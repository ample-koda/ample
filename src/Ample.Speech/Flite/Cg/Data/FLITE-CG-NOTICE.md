# Flite clustergen voice data in this folder

The `flite-cg-*.bin` files are converted from the generated data tables of the clustergen voices `cmu_us_slt`, `cmu_us_rms` and `cmu_us_awb` of Flite
(Carnegie Mellon University), pinned at commit `6c9f20dc915b17f5619340069889db0aa007fcdc`: the cluster vectors, the trees that choose a cluster for each
frame, the duration model, the states of each phone and the constants of the voice.

**These files are modified.** A tool (`tools/flite-data --cg`) read the C arrays and wrote the same values in a compact binary form; no value was changed.
The conversion is checked against the compiled C program (`tests/Ample.Speech.Flite.Tests/Cg`).

The original notice and licence are in `LICENSES/flite.txt` (Copyright (c) 1999-2017 Carnegie Mellon University, with the exceptions listed there). This data is
not endorsed by Carnegie Mellon University or the authors of Flite.

**Licence of the recordings.** The three voices are built from the recordings of the speakers slt (US English, female), rms (US English, male) and awb
(Scottish English, male) of the CMU ARCTIC speech databases (John Kominek and Alan W Black, Carnegie Mellon University, 2003, "free software": use, copy, modify and
license for any purpose, with the notice kept, modifications marked and the authors' names kept; all voice talents signed a waiver for distribution under these terms).
The text of those terms, of the Festvox COPYING (Carnegie Mellon University, Alan W Black and Kevin A. Lenzo, 1998-2020) and the statement of the Flite COPYING about
the files of the distribution are in `LICENSES/cmu-arctic.txt`. The 2003 report names four speakers (bdl, slt, jmk, awb): for rms the Flite distribution terms apply.

These files are modified (compact binary form, no value changed) and are not endorsed by Carnegie Mellon University or the authors of the databases or of Flite.
They are part of the public repository. The files are not embedded in the assembly yet (`Ample.Speech.csproj` embeds only `Flite/Data/*.bin`): the engine that
reads them is built step by step (plan/speech/flite/cg-architecture.md).
