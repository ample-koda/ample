#!/usr/bin/env python3
"""Capture a fixed-F0 Humanico word vector using the pinned Python implementation."""

import argparse
import hashlib
import importlib
import json
import math
import pathlib
import runpy
import sys
import types

parser = argparse.ArgumentParser()
parser.add_argument("--upstream", type=pathlib.Path, required=True)
parser.add_argument("--dictionary", type=pathlib.Path, required=True)
parser.add_argument("--output", type=pathlib.Path, required=True)
args = parser.parse_args()
support = runpy.run_path(str(pathlib.Path(__file__).with_name("humanico-calibration-reference.py")))
for name, expected in support["HASHES"].items():
    if hashlib.sha256((args.upstream / name).read_bytes()).hexdigest() != expected:
        raise SystemExit(f"Humanico reference hash mismatch: {name}")
dictionary_hash = "81917843c7f44ce2b094ac63873c2c7a4cf802040792c455ba3ca406891c3d22"
if hashlib.sha256(args.dictionary.read_bytes()).hexdigest() != dictionary_hash:
    raise SystemExit("Humanico reference dictionary hash mismatch")
numpy = types.ModuleType("numpy")
numpy.array = support["Array"]
numpy.sqrt = math.sqrt
sys.modules["numpy"] = numpy
sys.path.insert(0, str(args.upstream))
tts = importlib.import_module("tts")
klatt = importlib.import_module("klatt80")
tts._DICT = tts.load_cmudict(str(args.dictionary))
segments = tts.phones_to_segments(tts.word_to_phones("hello"))
frames = []
previous = None
for segment in segments:
    target = segment["target"]
    count = max(1, round(segment["dur_ms"] / 5))
    transition = max(0, round(min(segment["trans_ms"], segment["dur_ms"] * 0.6) / 5))
    if previous is None:
        previous = target
    for index in range(count):
        frame = dict(target)
        if index < transition and previous is not target:
            mix = (index + 1) / (transition + 1)
            for key in set(previous) | set(target):
                start = previous.get(key, 0.0)
                frame[key] = start + (target.get(key, 0.0) - start) * mix
        frames.append(frame)
    previous = target
voice = klatt.Voice()
voice._noise = types.SimpleNamespace(uniform=lambda a, b: 0.25)
voice.f0 = 105
pcm = []
for frame in frames:
    for key, value in frame.items():
        setattr(voice, key, value)
    pcm.extend(voice.frame(50))
indexes = [0, 49, 50, 199, 500, 1500, len(pcm) - 1]
result = {"word": "hello", "frames": len(frames), "noise": 0.25, "pitch": 105,
          "samples": [{"index": index, "value": pcm[index]} for index in indexes]}
args.output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8", newline="\n")
