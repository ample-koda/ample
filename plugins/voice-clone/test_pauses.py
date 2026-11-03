"""Tests of the pause markers in the text of a voice-clone block (stdlib unittest; the speech itself is stood in for by a tone).

Run: python -m unittest plugins/voice-clone/test_pauses.py   (needs numpy and soundfile for the join test)
"""
import importlib.util
import tempfile
import unittest
from pathlib import Path

spec = importlib.util.spec_from_file_location("voice_worker", Path(__file__).with_name("worker.py"))
worker = importlib.util.module_from_spec(spec)
spec.loader.exec_module(worker)


class SplitPauses(unittest.TestCase):
    def test_text_without_markers_is_one_part(self):
        self.assertEqual(worker.split_pauses("I'm walking down this road"), [("text", "I'm walking down this road")])

    def test_markers_split_the_text_and_carry_their_seconds(self):
        parts = worker.split_pauses("I'm walking [pause 800 ms] down [PAUSE 1.5 s] this road")
        self.assertEqual(parts, [("text", "I'm walking"), ("pause", 0.8), ("text", "down"), ("pause", 1.5), ("text", "this road")])

    def test_a_pause_at_the_start_or_end_is_kept(self):
        parts = worker.split_pauses("[pause 500 ms] hello [pause 1 s]")
        self.assertEqual(parts, [("pause", 0.5), ("text", "hello"), ("pause", 1.0)])

    def test_only_pauses_or_too_long_pauses_are_refused(self):
        with self.assertRaises(SystemExit):
            worker.split_pauses("[pause 1 s]")
        with self.assertRaises(SystemExit):
            worker.split_pauses("a [pause 11 s] b")
        with self.assertRaises(SystemExit):
            worker.split_pauses(" ".join("a [pause 9 s]" for _ in range(8)) + " z")


class SpeakWithPauses(unittest.TestCase):
    def test_the_pieces_are_joined_with_silence_of_the_right_length(self):
        import numpy as np
        import soundfile as sf
        rate = 24000

        def speak(text, output):
            seconds = 0.2 * len(text.split())
            t = np.arange(int(rate * seconds)) / rate
            sf.write(output, (0.3 * np.sin(2 * np.pi * 440 * t)).astype("float32"), rate)

        with tempfile.TemporaryDirectory() as folder:
            output = str(Path(folder) / "voice.wav")
            parts = worker.split_pauses("one two [pause 500 ms] three")
            worker.speak_with_pauses(parts, speak, output)
            data, got_rate = sf.read(output, dtype="float32", always_2d=True)
            self.assertEqual(got_rate, rate)
            expected = int(rate * 0.4) + int(round(0.5 * rate)) + int(rate * 0.2)
            self.assertEqual(data.shape[0], expected)
            gap = data[int(rate * 0.4):int(rate * 0.4) + int(round(0.5 * rate)), 0]
            self.assertTrue((gap == 0).all())
            self.assertFalse(Path(folder, "voice.part0.wav").exists())


if __name__ == "__main__":
    unittest.main()
