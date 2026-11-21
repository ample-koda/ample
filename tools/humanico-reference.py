#!/usr/bin/env python3
"""Run the pinned Humanico Python reference without modifying upstream source."""

import argparse
import hashlib
import importlib.util
import json
import pathlib
import sys
from typing import Any

PINNED_REVISION = "ce793bc9304cf34637199b0efde72c8afe130b1d"


def load_tts(upstream: pathlib.Path, dictionary: pathlib.Path | None) -> Any:
    sys.path.insert(0, str(upstream))
    spec = importlib.util.spec_from_file_location("humanico_reference_tts", upstream / "tts.py")
    if spec is None or spec.loader is None:
        raise RuntimeError(f"Cannot load pinned tts.py from {upstream}")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    if dictionary is not None:
        module._DICT = module.load_cmudict(str(dictionary))
    return module


def phone_text(phone: tuple[str, int]) -> str:
    symbol, stress = phone
    return f"{symbol}{stress}" if stress >= 0 else symbol


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--upstream", type=pathlib.Path, required=True)
    parser.add_argument("--dictionary", type=pathlib.Path)
    parser.add_argument("--text", required=True)
    parser.add_argument("--rate", type=float, default=1.0)
    parser.add_argument("--pitch", type=float, default=1.0)
    parser.add_argument("--strict", action="store_true")
    parser.add_argument("--output", type=pathlib.Path, required=True)
    args = parser.parse_args()

    result: dict[str, Any] = {"fixtureVersion": 1, "upstreamRevision": PINNED_REVISION, "text": args.text, "rate": args.rate, "pitch": args.pitch, "pcmAvailable": False}
    try:
        tts = load_tts(args.upstream, args.dictionary)
        tokens = tts.tokenize(args.text)
        words = [value for kind, value in tokens if kind == "word"]
        result["tokens"] = [{"kind": kind, "value": value} for kind, value in tokens]
        result["words"] = words
        result["phones"] = {word: [phone_text(phone) for phone in tts.word_to_phones(word)] for word in words}
        try:
            segments = [tts.phones_to_segments(tts.word_to_phones(word)) for word in words]
            result["segments"] = [[{"kind": segment["kind"], "durationMs": segment["dur_ms"], "transitionMs": segment["trans_ms"]} for segment in word_segments] for word_segments in segments]
            frames = tts.utterance_frames(args.text, rate=args.rate, pitch=args.pitch)
            result["frameCount"] = len(frames)
            result["frameKeys"] = sorted(frames[0].keys()) if frames else []
        except Exception as error:
            result["frameError"] = f"{type(error).__name__}: {error}"
        try:
            pcm = tts.render(args.text, rate=args.rate, pitch=args.pitch)
            result["pcmAvailable"] = True
            result["pcmSamples"] = len(pcm)
            result["pcmJsonSha256"] = hashlib.sha256(json.dumps(pcm, separators=(",", ":")).encode("utf-8")).hexdigest()
        except Exception as error:
            result["pcmError"] = f"{type(error).__name__}: {error}"
    except Exception as error:
        result["loadError"] = f"{type(error).__name__}: {error}"

    args.output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8", newline="\n")
    return 2 if args.strict and not result["pcmAvailable"] else 0


if __name__ == "__main__":
    raise SystemExit(main())
