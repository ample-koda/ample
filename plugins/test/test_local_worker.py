import pathlib
import sys
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1] / "analysis-local-transcription"))
from worker import stem_label, suggested_program, transcribe_stem


class LocalWorkerMidiTests(unittest.TestCase):
    def test_separator_stem_names_get_safe_labels_without_matching_song_title_substrings(self):
        self.assertEqual("bass", stem_label(pathlib.Path("Song_(Bass).wav"), 0))
        self.assertEqual("drums", stem_label(pathlib.Path("mix_percussion.wav"), 1))
        self.assertEqual("stem-03", stem_label(pathlib.Path("Song_Basshunter.wav"), 2))

    def test_stem_labels_choose_editable_general_midi_program_hints(self):
        self.assertEqual(32, suggested_program("bass"))
        self.assertEqual(24, suggested_program("guitar"))
        self.assertEqual(48, suggested_program("strings"))
        self.assertEqual(80, suggested_program("synth"))
        self.assertEqual(0, suggested_program("drums"))
        self.assertEqual(0, suggested_program("stem-01"))

    def test_drum_pitch_analysis_requires_explicit_opt_in(self):
        self.assertFalse(transcribe_stem("drums", "skip"))
        self.assertTrue(transcribe_stem("drums", "pitched"))
        self.assertTrue(transcribe_stem("bass", "skip"))


if __name__ == "__main__":
    unittest.main()
