import gc
import importlib.util
from importlib.metadata import PackageNotFoundError, version
import json
import os
import re
import subprocess
import sys
import threading
import time
import venv
from pathlib import Path

MODELS = {"xtts": ("XTTS-v2", "model.pth"), "qwen3": ("Qwen3-TTS-0.6B", "model.safetensors"), "chatterbox": ("Chatterbox-Multilingual", "ve.pt")}
PACKAGES = {"xtts": ("coqui-tts", "soundfile", "transformers==4.57.6"), "qwen3": ("qwen-tts", "soundfile"), "chatterbox": ("chatterbox-tts", "soundfile")}
CUDA_WHEEL_INDEX = "https://download.pytorch.org/whl/cu124"
CUDA_TORCH_PACKAGES = ["torch==2.6.0", "torchaudio==2.6.0"]
ACTIVE_STEP = None

def progress(step, fraction, part=None):
    global ACTIVE_STEP
    if ACTIVE_STEP and ACTIVE_STEP != step:
        print("AMPLE_PROGRESS " + json.dumps({"step": ACTIVE_STEP, "fraction": fraction, "stepFraction": 1.0}), flush=True)
    ACTIVE_STEP = step
    print("AMPLE_PROGRESS " + json.dumps({"step": step, "fraction": fraction, "stepFraction": part}), flush=True)

def with_heartbeat(label, fraction, action):
    result = []
    failure = []
    def run():
        try:
            result.append(action())
        except BaseException as error:
            failure.append(error)
    worker = threading.Thread(target=run, daemon=True)
    worker.start()
    while worker.is_alive():
        worker.join(15)
        if worker.is_alive():
            progress(label, fraction, None)
    if failure:
        raise failure[0]
    return result[0] if result else None

def environment_text(request):
    """Which Python the engine runs in and which setting to change, for the error messages."""
    source = request.get("pythonSetting")
    where = "the Python from %s" % source if source else "the default Python (no interpreter is set for it)"
    return "The %s engine runs in %s." % (request["model"], where), request.get("engineSetting") or "voice-clone.python"

def ensure_dependencies(model, auto_detect, request):
    progress("Voice Python: %s" % sys.executable, 0.04, 1.0)
    modules = {"xtts": ("TTS", "torch", "torchaudio", "soundfile"), "qwen3": ("qwen_tts", "soundfile", "torch", "torchaudio"), "chatterbox": ("chatterbox", "soundfile", "torch", "torchaudio")}[model]
    if model == "qwen3" and auto_detect:
        modules += ("faster_whisper",)
    transformers_compatible = model != "xtts"
    if model == "xtts":
        try:
            transformers_compatible = version("transformers") == "4.57.6"
        except (PackageNotFoundError, ValueError):
            transformers_compatible = False
        progress("Checking XTTS Transformers compatibility", 0.05, None)
    nvidia_gpu = has_nvidia_gpu()
    try:
        import torch
        cuda_ready = torch.cuda.is_available()
    except ImportError:
        cuda_ready = False
    if request.get("explicitPython"):
        missing = [name for name in modules if importlib.util.find_spec(name) is None]
        if missing:
            raise SystemExit("%s It is missing: %s. Set %s to a Python that has them (for example the voice-clone venv under your model root) or install them in that environment. Nothing was installed or switched automatically." % (environment_text(request)[0], ", ".join(missing), environment_text(request)[1]))
        expected_transformers = {"xtts": "4.57.6", "qwen3": "4.57.3"}.get(model)
        if expected_transformers and version("transformers") != expected_transformers:
            raise SystemExit("%s It needs transformers %s but has %s. Set %s to a Python with the right version, or fix it in that environment. No other environment was tried." % (environment_text(request)[0], expected_transformers, version("transformers"), environment_text(request)[1]))
        if nvidia_gpu and not cuda_ready:
            raise SystemExit("%s It cannot use CUDA although an NVIDIA GPU is available. Set %s to a Python with a CUDA build of torch. No CPU fallback was used." % (environment_text(request)[0], environment_text(request)[1]))
        try:
            if model == "qwen3":
                from qwen_tts import Qwen3TTSModel
            elif model == "xtts":
                from TTS.api import TTS
            else:
                from chatterbox.mtl_tts import ChatterboxMultilingualTTS
        except ImportError as error:
            raise SystemExit("%s Its engine import failed: %s. Set %s to a Python where it imports, or repair that environment. No other environment was tried." % (environment_text(request)[0], error, environment_text(request)[1])) from error
        progress("Configured voice environment verified", 0.1, 1.0)
        return
    if request.get("dependenciesCached") and all(importlib.util.find_spec(name) is not None for name in modules) and transformers_compatible and (not nvidia_gpu or cuda_ready):
        return
    if all(importlib.util.find_spec(name) is not None for name in modules) and transformers_compatible and (not nvidia_gpu or cuda_ready):
        progress("Voice dependencies are already installed", 0.1, 1.0)
        return
    base = Path(os.environ.get("LOCALAPPDATA", os.environ.get("XDG_DATA_HOME", Path.home() / ".local" / "share")))
    environment = base / "ample" / "voice-clone" / "venv"
    executable = environment / ("Scripts/python.exe" if os.name == "nt" else "bin/python")
    shared_environment = Path(request["modelRoot"]) / "venv"
    if Path(sys.prefix).resolve() != environment.resolve() and Path(sys.prefix).resolve() != shared_environment.resolve():
        if not executable.is_file():
            progress("Preparing an isolated Python environment", 0.04, None)
            venv.EnvBuilder(with_pip=True).create(environment)
        result = subprocess.run([str(executable), str(Path(__file__).resolve())], input=json.dumps(request), text=True, check=False)
        raise SystemExit(result.returncode)
    missing_torch = any(importlib.util.find_spec(name) is None for name in ("torch", "torchaudio"))
    if missing_torch or (nvidia_gpu and not cuda_ready):
        progress("Installing CUDA-enabled PyTorch and torchaudio (large download; this can take a while)" if nvidia_gpu else "Installing PyTorch and torchaudio (large download; this can take a while)", 0.06, None)
        install_dependencies(CUDA_TORCH_PACKAGES, ["--index-url", CUDA_WHEEL_INDEX]) if nvidia_gpu else install_dependencies(["torch", "torchaudio"], [])
    modules = {"coqui-tts": "TTS", "qwen-tts": "qwen_tts", "chatterbox-tts": "chatterbox", "soundfile": "soundfile"}
    packages = [package for package in PACKAGES[model] if package == "transformers==4.57.6" or importlib.util.find_spec(modules[package]) is None]
    if model == "qwen3" and auto_detect and importlib.util.find_spec("faster_whisper") is None:
        packages.append("faster-whisper")
    if packages:
        install_dependencies(packages, [])
    if model == "xtts" and version("transformers") != "4.57.6":
        raise SystemExit("XTTS needs transformers 4.57.6; dependency installation did not activate that version.")
    if nvidia_gpu:
        import torch
        if not torch.cuda.is_available():
            raise SystemExit("An NVIDIA GPU is available, but this voice environment still cannot access CUDA.")

def has_nvidia_gpu():
    try:
        result = subprocess.run(["nvidia-smi", "--query-gpu=name", "--format=csv,noheader"], capture_output=True, text=True, timeout=10, check=False)
        return result.returncode == 0 and bool(result.stdout.strip())
    except (OSError, subprocess.TimeoutExpired):
        return False

def install_dependencies(packages, index_arguments):
    progress("Resolving voice dependencies", 0.06, None)
    process = subprocess.Popen([sys.executable, "-m", "pip", "install", "--disable-pip-version-check", *index_arguments, *packages], stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, encoding="utf-8", errors="replace")
    discovered = set()
    error = ""
    for raw in process.stdout:
        line = raw.strip()
        if line.startswith("Collecting "):
            name = line[len("Collecting "):].split(" ", 1)[0]
            if name not in discovered:
                discovered.add(name)
                progress("Resolving dependency %d: %s" % (len(discovered), name), 0.06, None)
        elif line.startswith("Installing build dependencies"):
            progress("Installing dependency build tools", 0.06, None)
        elif line.startswith("Building wheel"):
            progress("Building a voice dependency", 0.06, None)
        elif line.startswith("Installing collected packages"):
            progress("Installing resolved voice packages", 0.06, None)
        elif line.startswith("Downloading "):
            progress("Downloading voice dependency files", 0.06, None)
        elif line.startswith("Successfully installed"):
            installed = line[len("Successfully installed"):].split()
            progress("Installed %d voice dependencies" % len(installed), 0.1, 1.0)
        elif line.startswith("ERROR:"):
            error = line[len("ERROR:"):].strip()
    result = process.wait()
    if result != 0:
        raise SystemExit("Voice dependency installation failed (pip exit %d)%s." % (result, ": " + error if error else ""))

# Warm cache: in --serve mode the loaded models stay in memory for a short time so a rebuild shortly after goes straight to synthesis.
# The cache is dropped when it has been idle too long or when video memory gets tight; only memory totals of this device are looked at
# (free, total and what this worker itself holds), never other programs, processes or their arguments.
CACHE = {}
KEEP = {"seconds": 0, "pressureMb": 1024, "minFreeMb": 1000}
BUSY = threading.Lock()
PRESSURE = {"reason": None}
MB = 1024 * 1024

def gpu_numbers():
    try:
        import torch
        if not torch.cuda.is_available():
            return None
        free, total = torch.cuda.mem_get_info()
        return free // MB, total // MB, torch.cuda.memory_reserved() // MB
    except Exception:
        return None

def others_mb():
    numbers = gpu_numbers()
    return None if numbers is None else (numbers[1] - numbers[0]) - numbers[2]

def pressure_reason(others0=None):
    """Why the device looks contended right now, or None. Totals of this device only: never processes, names or arguments."""
    numbers = gpu_numbers()
    if numbers is None:
        return None
    if numbers[0] < KEEP["minFreeMb"]:
        return "free video memory below %d MB" % KEEP["minFreeMb"]
    if others0 is not None and (numbers[1] - numbers[0]) - numbers[2] - others0 > KEEP["pressureMb"]:
        return "video memory use outside this worker grew by more than %d MB" % KEEP["pressureMb"]
    return None

def note(event, **details):
    print("AMPLE_CACHE " + json.dumps(dict(event=event, **details)), flush=True)

def unload(reason):
    held = len(CACHE)
    CACHE.clear()
    gc.collect()
    try:
        import torch
        if torch.cuda.is_available():
            torch.cuda.empty_cache()
    except Exception:
        pass
    numbers = gpu_numbers()
    note("evict", reason=reason, models=held, freeMb=None if numbers is None else numbers[0])

def cached(key, label, fraction, loader):
    entry = CACHE.get(key)
    if entry is not None:
        entry["last"] = time.time()
        progress("Using the voice model kept in memory", fraction, 1.0)
        note("hit", model=key[0])
        return entry["model"]
    if KEEP["seconds"] > 0 and CACHE:
        unload("another engine was requested")
    before = others_mb()
    model = with_heartbeat(label, fraction, loader)
    reason = pressure_reason(before)
    if reason:
        PRESSURE["reason"] = reason
        note("contended", model=key[0], reason=reason)
    if KEEP["seconds"] > 0:
        CACHE[key] = {"model": model, "others0": others_mb(), "last": time.time()}
        note("keep", model=key[0], seconds=KEEP["seconds"])
    return model

def watch():
    while True:
        time.sleep(5)
        if not CACHE and not BUSY.locked():
            continue
        if not BUSY.acquire(blocking=False):
            reason = pressure_reason(next((entry["others0"] for entry in CACHE.values()), None))
            if reason and not PRESSURE["reason"]:
                PRESSURE["reason"] = reason
                note("contended", reason=reason)
            continue
        if not CACHE:
            BUSY.release()
            continue
        try:
            idle = time.time() - max(entry["last"] for entry in CACHE.values())
            numbers = gpu_numbers()
            if idle > KEEP["seconds"]:
                unload("idle for %d s" % idle)
            elif numbers is not None and numbers[0] < KEEP["minFreeMb"]:
                unload("free video memory below %d MB" % KEEP["minFreeMb"])
            elif numbers is not None and any(entry["others0"] is not None and others_mb() - entry["others0"] > KEEP["pressureMb"] for entry in CACHE.values()):
                unload("video memory use outside this worker grew by more than %d MB" % KEEP["pressureMb"])
        finally:
            BUSY.release()

def serve():
    threading.Thread(target=watch, daemon=True).start()
    global ACTIVE_STEP
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        request = json.loads(line)
        KEEP["seconds"] = int(request.get("keepWarmS") or 0)
        KEEP["pressureMb"] = int(request.get("pressureMb") or 1024)
        KEEP["minFreeMb"] = int(request.get("minFreeMb") or 1000)
        with BUSY:
            try:
                run(request)
                result = {"ok": True}
            except SystemExit as error:
                result = {"ok": False, "error": str(error.code) if error.code not in (None, 0) else "Voice synthesis stopped."}
            except BaseException as error:
                result = {"ok": False, "error": "%s: %s" % (type(error).__name__, str(error)[:400])}
            ACTIVE_STEP = None
            if PRESSURE["reason"] and CACHE:
                unload("contention seen during the build: " + PRESSURE["reason"])
            PRESSURE["reason"] = None
            if not KEEP["seconds"]:
                CACHE.clear()
        print("AMPLE_RESULT " + json.dumps(result), flush=True)

def main():
    run(json.load(sys.stdin))

def seed_generators(seed):
    import random
    import numpy as np
    import torch
    random.seed(seed)
    np.random.seed(seed % (2 ** 32))
    torch.manual_seed(seed)
    if torch.cuda.is_available():
        torch.cuda.manual_seed_all(seed)
    torch.backends.cudnn.benchmark = False
    torch.backends.cudnn.deterministic = True
    torch.use_deterministic_algorithms(True, warn_only=True)

def run(request):
    model_dir = Path(request["modelRoot"]) / MODELS[request["model"]][0]
    progress("Checking voice model files", 0.02, None)
    if not (model_dir / MODELS[request["model"]][1]).is_file():
        raise SystemExit("Selected voice model files are not installed under the configured model root.")
    if request["model"] == "qwen3" and request["language"].lower() not in {"zh", "en", "ja", "ko", "de", "fr", "ru", "pt", "es", "it"}:
        raise SystemExit("Qwen3-TTS does not support this language (Dutch and Flemish are not supported).")
    progress("Checking Python dependencies", 0.04, None)
    ensure_dependencies(request["model"], request["autoDetect"], request)
    os.environ.setdefault("CUBLAS_WORKSPACE_CONFIG", ":4096:8")
    seed = int(request["seed"])
    seed_generators(seed)
    print("AMPLE_DEPENDENCIES_OK", flush=True)
    progress("Preparing the voice sample", 0.12, None)
    sample = Path(request["sample"])
    if sample.suffix.lower() != ".wav":
        progress("Converting the voice sample to WAV", 0.14, None)
        converted = sample.with_name("reference.wav")
        subprocess.run([request["ffmpeg"], "-y", "-i", str(sample), "-ar", "24000", "-ac", "1", str(converted)], check=True, capture_output=True)
        sample = converted
    reference = request["referenceText"]
    if request["model"] == "qwen3" and not reference and request["autoDetect"]:
        progress("Transcribing the voice sample with Whisper", 0.2, None)
        import torch
        from faster_whisper import WhisperModel
        whisper_device = "cuda" if torch.cuda.is_available() else "cpu"
        whisper = cached(("whisper", "small", whisper_device), "Loading the Whisper model on %s" % whisper_device, 0.2, lambda: WhisperModel("small", device=whisper_device, compute_type="float16" if whisper_device == "cuda" else "int8"))
        segments, _ = whisper.transcribe(str(sample))
        reference = " ".join(seg.text.strip() for seg in segments)
    if request["model"] == "qwen3" and not reference:
        raise SystemExit("Qwen3 requires a transcript of the sample, or successful Whisper auto detection.")
    import torch
    device = "cuda" if torch.cuda.is_available() else "cpu"
    device_label = "GPU (CUDA)" if device == "cuda" else "CPU"
    progress("Using %s for voice synthesis" % device_label, 0.27, 1.0)
    segment = 0
    generation = []
    def prepare_generation(method, model):
        nonlocal segment
        import inspect
        seed_generators((seed + segment) % (2 ** 64))
        segment += 1
        defaults = {key: value.default for key, value in inspect.signature(method).parameters.items() if value.default is not inspect.Parameter.empty}
        config = getattr(model, "generation_config", None) or getattr(getattr(model, "synthesizer", None), "tts_config", None)
        generation.append({"seed": str((seed + segment - 1) % (2 ** 64)), "samplingDefaults": defaults, "modelConfig": config.to_dict() if hasattr(config, "to_dict") else str(config)})
    def speak(text, output):
        if request["model"] == "xtts":
            from TTS.api import TTS
            from TTS.tts.models import xtts
            xtts.load_audio = load_xtts_audio
            def load_xtts():
                model = TTS(model_path=str(model_dir), config_path=str(model_dir / "config.json"))
                model.to(device)
                return model
            tts = cached(("xtts", str(model_dir), device), "Loading the XTTS voice model on %s" % device_label, 0.28, load_xtts)
            progress("Synthesizing speech with XTTS on %s" % device_label, 0.55, None)
            prepare_generation(tts.tts_to_file, tts)
            with_heartbeat("Synthesizing speech with XTTS on %s" % device_label, 0.55, lambda: tts.tts_to_file(text=text, speaker_wav=str(sample), language=request["language"], file_path=output))
        elif request["model"] == "qwen3":
            import soundfile as sf
            from qwen_tts import Qwen3TTSModel
            languages = {"zh":"Chinese","en":"English","ja":"Japanese","ko":"Korean","de":"German","fr":"French","ru":"Russian","pt":"Portuguese","es":"Spanish","it":"Italian"}
            tts = cached(("qwen3", str(model_dir), device), "Loading the Qwen3 voice model on %s" % device_label, 0.28, lambda: Qwen3TTSModel.from_pretrained(str(model_dir), device_map=device, dtype=torch.bfloat16))
            progress("Synthesizing speech with Qwen3-TTS on %s" % device_label, 0.55, None)
            prepare_generation(tts.generate_voice_clone, tts)
            wavs, rate = with_heartbeat("Synthesizing speech with Qwen3-TTS on %s" % device_label, 0.55, lambda: tts.generate_voice_clone(text=text, language=languages[request["language"].lower()], ref_audio=str(sample), ref_text=reference))
            sf.write(output, wavs[0], rate)
        else:
            import torchaudio
            from chatterbox.mtl_tts import ChatterboxMultilingualTTS
            tts = cached(("chatterbox", str(model_dir), device), "Loading the Chatterbox voice model on %s" % device_label, 0.28, lambda: ChatterboxMultilingualTTS.from_local(str(model_dir), device))
            progress("Synthesizing speech with Chatterbox on %s" % device_label, 0.55, None)
            prepare_generation(tts.generate, tts)
            wav = with_heartbeat("Synthesizing speech with Chatterbox on %s" % device_label, 0.55, lambda: tts.generate(text, audio_prompt_path=str(sample), language_id=request["language"]))
            torchaudio.save(output, wav, tts.sr)
    parts = split_pauses(request["text"])
    if all(kind == "text" for kind, _ in parts):
        speak(request["text"], request["output"])
    else:
        speak_with_pauses(parts, speak, request["output"])
    progress("Checking generated audio", 0.98, None)
    output_path = Path(request["output"])
    if not output_path.is_file():
        raise SystemExit("Voice synthesis returned successfully but did not create its audio file.")
    import numpy as np
    import soundfile as sf
    pcm16_path = output_path.with_name(output_path.stem + ".pcm16.wav")
    with sf.SoundFile(output_path) as generated:
        if generated.frames == 0:
            raise SystemExit("Voice synthesis created a WAV with 0 samples; the model returned no speech audio.")
        peak = 0.0
        audible_frames = 0
        with sf.SoundFile(pcm16_path, mode="w", samplerate=generated.samplerate, channels=generated.channels, format="WAV", subtype="PCM_16") as normalized:
            for samples in generated.blocks(blocksize=65536, dtype="float32", always_2d=True):
                if not np.isfinite(samples).all():
                    raise SystemExit("Voice synthesis created invalid audio containing NaN or infinity samples.")
                levels = np.max(np.abs(samples), axis=1)
                peak = max(peak, float(np.max(levels, initial=0.0)))
                audible_frames += int(np.count_nonzero(levels >= 1e-5))
                normalized.write(np.clip(samples, -1.0, 1.0))
        if peak < 1e-6:
            raise SystemExit("Voice synthesis created a silent WAV (peak below -120 dBFS); the model returned no audible speech.")
        if audible_frames < generated.samplerate // 4:
            raise SystemExit("Voice synthesis failed the sustained-audio check: duration %.3fs, active audio %.3fs (minimum 0.250s), peak %.1f dBFS, %d Hz, %d channel(s), model %s, device %s. This check measures signal activity, not whether it contains speech." % (generated.frames / generated.samplerate, audible_frames / generated.samplerate, 20 * float(np.log10(peak)), generated.samplerate, generated.channels, request["model"], device_label))
    os.replace(pcm16_path, output_path)
    import importlib.metadata
    packages = {}
    for package in ("torch", "numpy", "transformers", "coqui-tts", "qwen-tts", "chatterbox-tts", "soundfile"):
        try:
            packages[package] = importlib.metadata.version(package)
        except importlib.metadata.PackageNotFoundError:
            pass
    recipe = {"schema": 1, "seed": str(seed), "model": request["model"], "language": request["language"], "referenceText": reference, "generation": generation, "software": {"python": sys.version, "packages": packages, "device": device}, "warnings": ["Voice model file hashes and cross-device numerical determinism are unverified."]}
    with open(str(output_path) + ".recipe.json", "w", encoding="utf-8") as recipe_file:
        json.dump(recipe, recipe_file, default=str)
    progress("Voice synthesis complete", 1.0, 1.0)

PAUSE = re.compile(r"\[\s*pause\s+([0-9]*\.?[0-9]+)\s*(ms|s)\s*\]", re.IGNORECASE)
MAX_PAUSE_S = 10.0
MAX_TOTAL_PAUSE_S = 60.0

def split_pauses(text):
    """The text as ("text", words) and ("pause", seconds) parts; [pause 800 ms] or [pause 1.2 s] marks a silence between two pieces of speech."""
    parts = []
    position = 0
    total = 0.0
    for match in PAUSE.finditer(text):
        words = text[position:match.start()].strip()
        if words:
            parts.append(("text", words))
        seconds = float(match.group(1)) / (1000.0 if match.group(2).lower() == "ms" else 1.0)
        if seconds > MAX_PAUSE_S:
            raise SystemExit("A pause in the text may be at most %d seconds (it says %.1f)." % (MAX_PAUSE_S, seconds))
        total += seconds
        if total > MAX_TOTAL_PAUSE_S:
            raise SystemExit("The pauses in the text add up to more than %d seconds." % MAX_TOTAL_PAUSE_S)
        parts.append(("pause", seconds))
        position = match.end()
    tail = text[position:].strip()
    if tail:
        parts.append(("text", tail))
    if not any(kind == "text" for kind, _ in parts):
        raise SystemExit("The text has no words to speak, only pauses.")
    return parts

def speak_with_pauses(parts, speak, output):
    """Makes the voice per piece of text, with the same reference, and puts the silences between them in one WAV."""
    import numpy as np
    import soundfile as sf
    pieces = []
    rate = None
    channels = None
    folder = Path(output).parent
    count = sum(1 for kind, _ in parts if kind == "text")
    done = 0
    for index, (kind, value) in enumerate(parts):
        if kind == "pause":
            pieces.append(("pause", value))
            continue
        done += 1
        progress("Speaking part %d of %d" % (done, count), 0.5 + 0.45 * done / count, None)
        temp = folder / (Path(output).stem + ".part%d.wav" % index)
        speak(value, str(temp))
        data, sample_rate = sf.read(str(temp), dtype="float32", always_2d=True)
        temp.unlink(missing_ok=True)
        if rate is None:
            rate, channels = sample_rate, data.shape[1]
        elif sample_rate != rate or data.shape[1] != channels:
            raise SystemExit("The voice returned parts with different audio formats.")
        pieces.append(("audio", data))
    out = []
    for kind, value in pieces:
        out.append(value if kind == "audio" else np.zeros((int(round(value * rate)), channels), dtype="float32"))
    sf.write(output, np.concatenate(out, axis=0), rate, subtype="FLOAT")

def load_xtts_audio(audiopath, sampling_rate):
    import soundfile as sf
    import torch
    import torchaudio
    audio, source_rate = sf.read(audiopath, dtype="float32", always_2d=True)
    audio = torch.from_numpy(audio.T.copy())
    if audio.size(0) != 1:
        audio = torch.mean(audio, dim=0, keepdim=True)
    if source_rate != sampling_rate:
        audio = torchaudio.functional.resample(audio, source_rate, sampling_rate)
    return audio.clamp_(-1, 1)

if __name__ == "__main__":
    if "--serve" in sys.argv:
        serve()
    else:
        main()
