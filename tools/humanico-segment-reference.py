#!/usr/bin/env python3
"""Capture uncalibrated segment timings from the pinned Humanico Python sources."""

import argparse
import hashlib
import importlib
import json
import pathlib
import sys

parser = argparse.ArgumentParser()
parser.add_argument("--upstream", type=pathlib.Path, required=True)
parser.add_argument("--output", type=pathlib.Path, required=True)
args = parser.parse_args()
expected_hashes = {
    "tts.py": "e4c78253cc8f76c4cf20768afe77ba888f057575c1c6db4a7f17815bfd95d15d",
    "phones.py": "789e389dd0b2f4cb7a9683e1d336afbe949092b5dbba36bf4f0678fa62da08cf",
    "klatt80.py": "957173c95cf4f1e5b29bbd8bf39268f51e70e26d0abcef82cce43f50def6b412",
}
for name, expected in expected_hashes.items():
    actual = hashlib.sha256((args.upstream / name).read_bytes()).hexdigest()
    if actual != expected:
        raise SystemExit(f"Humanico reference hash mismatch: {name}")
sys.path.insert(0, str(args.upstream))
tts = importlib.import_module("tts")
tts.balance = lambda target, kind: target
symbols = sorted(tts.VOWEL_SET | set(tts.STOPS) | set(tts.AFFRICATES) | set(tts.FRICATIVES)
                 | set(tts.NASALS) | set(tts.LIQUIDS) | set(tts.GLIDES))
cases = []
for symbol in symbols:
    for stress in ([0, 1, 2] if symbol in tts.VOWEL_SET else [-1]):
        segments = tts.phones_to_segments([(symbol, stress)])
        cases.append({"phone": symbol, "stress": stress,
                      "segments": [{"kind": s["kind"], "duration": s["dur_ms"],
                                    "transition": s["trans_ms"]} for s in segments]})
result = {"upstreamRevision": "ce793bc9304cf34637199b0efde72c8afe130b1d", "cases": cases}
args.output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8", newline="\n")
