#!/usr/bin/env python3
"""Capture upstream calibrated single-phone targets without a NumPy installation.

The checked source files are executed as-is. A small in-memory NumPy-compatible RMS
adapter supplies only the operations used by tts._unit_rms; the generated targets
are reference data, not production calibration or a promise of bit identity.
"""

import argparse
import hashlib
import importlib
import json
import math
import pathlib
import sys
import types

HASHES = {
    "tts.py": "e4c78253cc8f76c4cf20768afe77ba888f057575c1c6db4a7f17815bfd95d15d",
    "phones.py": "789e389dd0b2f4cb7a9683e1d336afbe949092b5dbba36bf4f0678fa62da08cf",
    "klatt80.py": "957173c95cf4f1e5b29bbd8bf39268f51e70e26d0abcef82cce43f50def6b412",
}


class Array:
    def __init__(self, values):
        self.values = values

    def __pow__(self, exponent):
        return Array([value ** exponent for value in self.values])

    def mean(self):
        return sum(self.values) / len(self.values)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--upstream", type=pathlib.Path, required=True)
    parser.add_argument("--output", type=pathlib.Path, required=True)
    parser.add_argument("--fix-hh", action="store_true", help="Calibrate HH as aspiration for the AMPLE candidate")
    args = parser.parse_args()
    for name, expected in HASHES.items():
        if hashlib.sha256((args.upstream / name).read_bytes()).hexdigest() != expected:
            raise SystemExit(f"Humanico reference hash mismatch: {name}")
    numpy = types.ModuleType("numpy")
    numpy.array = Array
    numpy.sqrt = math.sqrt
    sys.modules["numpy"] = numpy
    sys.path.insert(0, str(args.upstream))
    tts = importlib.import_module("tts")
    def prepare(symbol, segments):
        if args.fix_hh and symbol == "HH":
            for segment in segments:
                target = segment["target"]
                target["af"] = 0.0
                tts._set_level(target, "ah", "aspir")
        return segments

    symbols = sorted(tts.VOWEL_SET | set(tts.STOPS) | set(tts.AFFRICATES) | set(tts.FRICATIVES)
                     | set(tts.NASALS) | set(tts.LIQUIDS) | set(tts.GLIDES))
    cases = []
    for symbol in symbols:
        stress = 1 if symbol in tts.VOWEL_SET else -1
        segments = prepare(symbol, tts.phones_to_segments([(symbol, stress)]))
        cases.append({"phone": symbol, "stress": stress,
                      "segments": [{"kind": segment["kind"], "target": segment["target"]}
                                   for segment in segments]})
        if symbol in tts.STOPS or symbol == "HH":
            for following in sorted(tts.VOWEL_SET):
                contextual = prepare(symbol, tts.phones_to_segments([(symbol, stress), (following, 0)])[:len(segments)])
                cases.append({"phone": symbol, "stress": stress, "following": following,
                              "segments": [{"kind": segment["kind"], "target": segment["target"]}
                                           for segment in contextual]})
    result = {"upstreamRevision": "ce793bc9304cf34637199b0efde72c8afe130b1d",
              "rmsBackend": "python-float-sum", "hhAspirationFix": args.fix_hh, "cases": cases}
    args.output.write_text(json.dumps(result, indent=2, allow_nan=False) + "\n", encoding="utf-8", newline="\n")


if __name__ == "__main__":
    main()
