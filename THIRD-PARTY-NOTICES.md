# Third-party notices

BeatzOnKeys uses and adapts open-source software. Each component remains under its own license.

## MusicCatRGB

The Frost visual language, runtime theme/localization pattern, Mica helper, custom window structure, tray lifecycle, and low-level keyboard-hook approach are adapted from [MusicCatRGB](https://github.com/MusicCat-61/MusicCatRGB).

MusicCatRGB is licensed under the MIT License. The complete required notice is included in [`third-party/MusicCatRGB-LICENSE.txt`](third-party/MusicCatRGB-LICENSE.txt) and is reproduced in source and release distributions.

Copyright (c) 2026 Project GutsV<br>
Copyright (c) 2026 MusicCat161 (MusicCatRGB modification)

## Basic Pitch

[Basic Pitch](https://github.com/spotify/basic-pitch) is Copyright 2022 Spotify AB and is distributed under the Apache License 2.0. The portable Python environment retains Basic Pitch's package metadata and license files.

## CPython

The bundled embeddable Python runtime is distributed under the [Python Software Foundation License](https://docs.python.org/3/license.html). Its `LICENSE.txt` is retained inside the `python` folder.

## .NET dependencies

- [NAudio](https://github.com/naudio/NAudio) — MIT License; full text: [`third-party/NAudio-LICENSE.txt`](third-party/NAudio-LICENSE.txt).
- [Melanchall.DryWetMidi](https://github.com/melanchall/drywetmidi) — MIT License; full text: [`third-party/DryWetMidi-LICENSE.txt`](third-party/DryWetMidi-LICENSE.txt).
- [.NET](https://github.com/dotnet/runtime) — MIT License and associated notices.

## Python runtime dependencies

The release runtime includes ONNX Runtime (MIT; full text: [`third-party/ONNXRuntime-LICENSE.txt`](third-party/ONNXRuntime-LICENSE.txt)), NumPy, SciPy, librosa, scikit-learn, Numba, llvmlite, SoundFile, PrettyMIDI, mir_eval, resampy, and their transitive dependencies. Their package metadata and license files remain in `python/Lib/site-packages/*-info` or the corresponding package directory. See those files for exact terms.

No MusicCatRGB hardware drivers, vendor DLLs, fonts, logo, or screenshots are distributed with BeatzOnKeys.
