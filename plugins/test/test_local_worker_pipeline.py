import json
import pathlib
import sys
import tempfile
import types
import unittest
from unittest.mock import patch

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1] / "analysis-local-transcription"))
import worker


class Audio:
    def __init__(self, count, channels, value):
        self.rows = [[value] * channels for _ in range(count)]
        self.shape = (count, channels)

    def __getitem__(self, key):
        rows, columns = key if isinstance(key, tuple) else (key, slice(None))
        selected = self.rows[rows]
        if isinstance(rows, int):
            selected = [selected]
        return Audio.from_rows([row[columns] for row in selected])

    @classmethod
    def from_rows(cls, rows):
        result = cls.__new__(cls)
        result.rows = rows
        result.shape = (len(rows), len(rows[0]) if rows else 0)
        return result

    def copy(self):
        return Audio.from_rows([row[:] for row in self.rows])

    def __iadd__(self, other):
        self.rows = [[left + right for left, right in zip(a, b)] for a, b in zip(self.rows, other.rows)]
        return self

    def __itruediv__(self, value):
        self.rows = [[sample / value for sample in row] for row in self.rows]
        return self


class Midi:
    def __init__(self):
        self.instruments = [types.SimpleNamespace(program=4, name="Electric Piano")]

    def write(self, filename):
        pathlib.Path(filename).write_bytes(b"MThd\x00\x00\x00\x06")


class LocalWorkerPipelineTests(unittest.TestCase):
    def test_component_stems_become_separate_gm_hinted_midi_and_drum_mode_is_explicit(self):
        with tempfile.TemporaryDirectory(prefix="ample-worker-") as temp:
            root = pathlib.Path(temp)
            source = root / "in" / "audio.wav"
            source.parent.mkdir()
            source.write_bytes(b"generated wav placeholder")
            model_dir = root / "models"
            model_dir.mkdir()
            (model_dir / "model.onnx").write_bytes(b"generated model marker")
            out = root / "out"
            written_audio = {}
            midi_written = []
            values = {"vocals": 0.4, "instrumental": 0.7, "bass": 0.2, "guitar": 0.3, "drums": 0.9}

            class FakeSeparator:
                def __init__(self, output_dir, **_kwargs):
                    self.output_dir = pathlib.Path(output_dir)

                def load_model(self, **_kwargs):
                    return None

                def separate(self, _source):
                    paths = []
                    for name in ("vocals", "instrumental", "bass", "guitar", "drums"):
                        item = self.output_dir / (name + ".wav")
                        item.write_bytes(b"generated stem")
                        paths.append(str(item))
                    return paths

            soundfile = types.ModuleType("soundfile")
            soundfile.info = lambda _path: types.SimpleNamespace(frames=80, samplerate=8000)
            soundfile.read = lambda path, **_kwargs: (Audio(80, 1, values.get(pathlib.Path(path).stem.split("_")[-1].lower(), 0.1)), 8000)

            def audio_write(path, audio, _rate, **_kwargs):
                written_audio[pathlib.Path(path).name] = audio.rows[0][0] if audio.rows else 0
                pathlib.Path(path).write_bytes(b"generated pcm")

            soundfile.write = audio_write
            numpy = types.ModuleType("numpy")
            numpy.clip = lambda value, _low, _high: value
            separator_pkg = types.ModuleType("audio_separator")
            separator_pkg.__path__ = []
            separator_module = types.ModuleType("audio_separator.separator")
            separator_module.Separator = FakeSeparator
            basic_pkg = types.ModuleType("basic_pitch")
            basic_pkg.__path__ = []
            basic_module = types.ModuleType("basic_pitch.inference")
            basic_module.predict = lambda path: (None, Midi(), [(0.0, 0.01, 60, 0.8, None)] if pathlib.Path(path).name == "vocals.wav" else [])

            def midi_write(path):
                midi_written.append(pathlib.Path(path).name)
                pathlib.Path(path).write_bytes(b"MThd\x00\x00\x00\x06")

            midi_instances = []

            class TrackingMidi(Midi):
                def __init__(self):
                    super().__init__()
                    midi_instances.append(self)

                def write(self, filename):
                    midi_write(filename)

            basic_module.predict = lambda path: (None, TrackingMidi(), [(0.0, 0.01, 60, 0.8, None)] if pathlib.Path(path).name == "vocals.wav" else [])
            scipy_pkg = types.ModuleType("scipy")
            scipy_pkg.__path__ = []
            scipy_signal = types.ModuleType("scipy.signal")
            modules = {
                "numpy": numpy,
                "scipy": scipy_pkg,
                "scipy.signal": scipy_signal,
                "soundfile": soundfile,
                "audio_separator": separator_pkg,
                "audio_separator.separator": separator_module,
                "basic_pitch": basic_pkg,
                "basic_pitch.inference": basic_module,
            }
            options = {"modelDirectory": str(model_dir), "separatorModel": "model.onnx", "modelLicense": "generated", "drumMode": "skip"}
            with patch.dict(sys.modules, modules), patch.object(worker, "disable_network"), patch.object(worker.importlib.metadata, "version", return_value="test"):
                worker.main(str(source), str(out), options)
            result = json.loads((out / "result.json").read_text(encoding="utf-8"))["analysis"]
            self.assertEqual(["bass", "guitar", "combined"], [item["label"] for item in result["midiArtifacts"]])
            self.assertIn("the drum stem was omitted", " ".join(result["warnings"]).lower())
            self.assertEqual(0.7, written_audio["backing.wav"])
            self.assertEqual(["vocal-midi.mid", "backing-midi-01.mid", "backing-midi-02.mid", "backing-midi-combined.mid"], midi_written)
            self.assertEqual((32, "bass"), (midi_instances[1].instruments[0].program, midi_instances[1].instruments[0].name))
            self.assertEqual((24, "guitar"), (midi_instances[2].instruments[0].program, midi_instances[2].instruments[0].name))
            self.assertEqual([32, 24], [instrument.program for instrument in midi_instances[1].instruments])


if __name__ == "__main__":
    unittest.main()
