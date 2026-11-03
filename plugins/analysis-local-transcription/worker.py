import hashlib
import importlib.metadata
import json
import math
import re
import socket
import sys
import urllib.request
from pathlib import Path


def no_network(*_args, **_kwargs):
    raise RuntimeError("network is disabled for local transcription")


def disable_network():
    socket.create_connection = no_network
    socket.socket.connect = no_network
    socket.socket.connect_ex = no_network
    urllib.request.urlopen = no_network


def progress(fraction, step, step_fraction):
    print("AMPLE_PROGRESS " + json.dumps({"fraction": fraction, "step": step, "stepFraction": step_fraction}), flush=True)


def sha256_file(path):
    digest = hashlib.sha256()
    with open(path, "rb") as source:
        for chunk in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def stem_label(path, index):
    words = set(re.split(r"[^a-z0-9]+", path.stem.lower()))
    for needles, label in ((('drums', 'drum', 'percussion'), 'drums'), (('bass',), 'bass'), (('guitar',), 'guitar'), (('piano', 'keys'), 'keys'), (('strings', 'string', 'violin', 'cello'), 'strings'), (('synth',), 'synth')):
        if words.intersection(needles):
            return label
    return "stem-%02d" % (index + 1)


def suggested_program(label):
    return {"backing": 0, "drums": 0, "bass": 32, "guitar": 24, "keys": 4, "strings": 48, "synth": 80}.get(label, 0)


def transcribe_stem(label, drum_mode):
    return label != "drums" or drum_mode == "pitched"


def main(source_path, output_dir, options):
    disable_network()
    import numpy as np
    import scipy.signal
    import soundfile as sf
    from audio_separator.separator import Separator
    from basic_pitch.inference import predict

    source = Path(source_path)
    out = Path(output_dir)
    temp = out / "separated"
    temp.mkdir(parents=True, exist_ok=True)
    source_info = sf.info(source)
    if source_info.frames <= 0 or source_info.frames > source_info.samplerate * 600:
        raise ValueError("source duration outside limit")

    progress(0.1, "separation", 0)
    separator = Separator(output_dir=str(temp), output_format="WAV", model_file_dir=options["modelDirectory"], log_level="WARNING")
    separator.load_model(model_filename=options["separatorModel"])
    produced = separator.separate(str(source))
    if not produced or len(produced) > 8:
        raise ValueError("separator output count outside limit")
    generated = [Path(item).resolve() for item in produced]
    if any(temp.resolve() not in item.parents or not item.is_file() for item in generated):
        raise ValueError("separator wrote outside its private output folder")
    vocal = next((item for item in generated if "vocal" in item.name.lower()), None)
    if vocal is None:
        raise ValueError("selected model did not produce a vocal stem")
    whole_backing = next((item for item in generated if item != vocal and ("instrumental" in item.name.lower() or "backing" in item.name.lower())), None)
    components = [item for item in generated if item != vocal and item != whole_backing]
    backing_sources = components if components else [whole_backing or next((item for item in generated if item != vocal), None)]
    if not backing_sources or backing_sources[0] is None:
        raise ValueError("selected model did not produce an accompaniment stem")
    mix_sources = [whole_backing] if whole_backing is not None else backing_sources

    reference, rate = sf.read(source, dtype="float32", always_2d=True)
    channels = min(2, reference.shape[1])
    frame_count = reference.shape[0]
    warnings = ["Separated stems and neural MIDI are estimates; review leakage, timing and notes before use."]
    aligned = {}
    alignment_sources = list(dict.fromkeys(backing_sources + mix_sources))
    source_keys = {item: "stem-%02d" % (index + 1) for index, item in enumerate(alignment_sources)}
    for role, item in [("vocals", vocal)] + [(source_keys[item], item) for item in alignment_sources]:
        data, stem_rate = sf.read(item, dtype="float32", always_2d=True)
        if stem_rate != rate:
            divisor = math.gcd(stem_rate, rate)
            data = scipy.signal.resample_poly(data, rate // divisor, stem_rate // divisor, axis=0)
        if data.shape[1] == 1 and channels == 2:
            data = np.repeat(data, 2, axis=1)
        if data.shape[1] < channels:
            raise ValueError("stem channel count is invalid")
        data = data[:, :channels]
        if data.shape[0] != frame_count:
            warnings.append(role + " stem was trimmed or padded to the source timeline.")
            data = data[:frame_count]
            if data.shape[0] < frame_count:
                data = np.pad(data, ((0, frame_count - data.shape[0]), (0, 0)))
        aligned[role] = data
    sf.write(out / "vocals.wav", np.clip(aligned["vocals"], -1.0, 1.0), rate, subtype="PCM_16")
    backing_mix = aligned[source_keys[mix_sources[0]]].copy()
    for item in mix_sources[1:]:
        backing_mix += aligned[source_keys[item]]
    if len(mix_sources) > 1:
        backing_mix /= len(mix_sources)
    sf.write(out / "backing.wav", np.clip(backing_mix, -1.0, 1.0), rate, subtype="PCM_16")

    progress(0.5, "transcribe-vocals", 0)
    vocal_midi = out / "vocal-midi.mid"
    _, vocal_data, vocal_notes = predict(str(out / "vocals.wav"))
    vocal_data.write(str(vocal_midi))
    progress(0.72, "transcribe-backing", 0)
    midi_artifacts = []
    combined_midi = None
    for index, source_stem in enumerate(backing_sources):
        label = stem_label(source_stem, index) if components else "backing"
        if not transcribe_stem(label, options["drumMode"]):
            warnings.append("The drum stem was omitted from MIDI; Basic Pitch estimates pitches and does not map drum hits. Set drumMode to pitched for a rough pitched estimate.")
            continue
        midi_input = out / ("backing-source-%02d.wav" % (index + 1))
        sf.write(midi_input, np.clip(aligned[source_keys[source_stem]], -1.0, 1.0), rate, subtype="PCM_16")
        _, backing_data, _ = predict(str(midi_input))
        for instrument in backing_data.instruments:
            instrument.program = suggested_program(label)
            instrument.name = label
        if combined_midi is None:
            combined_midi = backing_data
        else:
            combined_midi.instruments.extend(backing_data.instruments)
        midi_path = "out/backing-midi-%02d.mid" % (index + 1)
        backing_data.write(str(out / Path(midi_path).name))
        midi_artifacts.append({"label": label, "path": midi_path})
    if len(midi_artifacts) > 1:
        midi_path = "out/backing-midi-combined.mid"
        combined_midi.write(str(out / Path(midi_path).name))
        midi_artifacts.append({"label": "combined", "path": midi_path})
    if len(backing_sources) > 1:
        warnings.append("Separate accompaniment stems are rough MIDI estimates; stem labels do not identify instruments reliably.")
    if any(item["label"] == "drums" for item in midi_artifacts):
        warnings.append("A drum stem is pitch-transcribed and uses a pitched GM program; this is not drum-hit mapping.")

    hop = 0.02
    duration = frame_count / rate
    count = min(30000, math.ceil(duration / hop))
    active = [None] * count
    for start, end, pitch, amplitude, *_ in vocal_notes:
        low = max(0, int(float(start) / hop))
        high = min(count, math.ceil(float(end) / hop))
        activity = max(0.0, min(1.0, float(amplitude)))
        for frame in range(low, high):
            if active[frame] is None or activity > active[frame][1]:
                active[frame] = (int(pitch), activity)
    frames = []
    for index, note in enumerate(active):
        if note is None:
            continue
        pitch, activity = note
        hz = 440.0 * (2.0 ** ((pitch - 69) / 12.0))
        frames.append({"t": round(index * hop, 3), "hz": round(hz, 3), "midi": pitch, "confidence": round(activity, 4)})
    tool_version = "audio-separator " + importlib.metadata.version("audio-separator") + "; basic-pitch " + importlib.metadata.version("basic-pitch")
    result = {
        "tool": "audio-separator + basic-pitch",
        "toolVersion": tool_version[:64],
        "model": options["separatorModel"],
        "modelHash": sha256_file(Path(options["modelDirectory"], options["separatorModel"])),
        "modelLicense": options["modelLicense"],
        "drumMode": options["drumMode"],
        "sampleRate": rate,
        "sourceSamples": frame_count,
        "warnings": warnings,
        "midiArtifacts": midi_artifacts,
        "vocalPitch": {"hopSeconds": hop, "voicedFrames": len(frames), "frames": frames},
    }
    (out / "result.json").write_text(json.dumps({"analysis": result}, separators=(",", ":")), encoding="utf-8")
    progress(1.0, "complete", 1.0)


if __name__ == "__main__":
    try:
        main(sys.argv[1], sys.argv[2], json.loads(sys.argv[3]))
    except Exception as error:
        sys.stderr.write(type(error).__name__ + "\n")
        raise SystemExit(1)
