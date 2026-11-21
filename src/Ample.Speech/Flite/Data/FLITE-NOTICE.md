# Flite data in this folder

The `flite-*.bin` files are converted from the generated data tables of Flite (Carnegie Mellon University), pinned at commit
`6c9f20dc915b17f5619340069889db0aa007fcdc`: the diphone voice `cmu_us_kal16`, the CMU lexicon with its letter-to-sound rules and
the US English front end (classification trees, phone set, F0 and duration models, word lists, regular expressions).

**These files are modified.** A tool (`tools/flite-data`) read the C arrays and wrote the same values in a compact binary form; no value was changed.
The conversion is checked against the compiled C program (`tests/Ample.Speech.Flite.Tests`).

The original notice and licence are in `LICENSES/flite.txt` (Copyright (c) 1999-2017 Carnegie Mellon University, with the
exceptions listed there). This data is not endorsed by Carnegie Mellon University or the authors of Flite.

The recorded diphone data of the voice is under the Festival licence: Copyright (c) 1998 Alan W Black and Kevin Lenzo, All Rights Reserved; Copyright (c) 1996-1997 University of Edinburgh, UK. The terms (use, distribute, modify and sell without restriction; keep the notice and the disclaimer, mark modifications, keep the authors' names, no endorsement) are repeated at the end of `LICENSES/flite.txt`.
