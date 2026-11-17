# Third-party notices — Lux desktop app

The app bundles these fonts. Each is licensed under the SIL Open Font License, Version 1.1; the full licence text ships beside the
fonts as `Assets/Fonts/Archivo-OFL.txt` and `Assets/Fonts/JetBrainsMono-OFL.txt` (in the source tree and in the built / published app).

| Font | Files | Copyright | Licence |
|---|---|---|---|
| Archivo | `Assets/Fonts/Archivo-Regular.ttf`, `Archivo-Medium.ttf`, `Archivo-SemiBold.ttf`, `Archivo-Bold.ttf` | Copyright 2020 The Archivo Project Authors (https://github.com/Omnibus-Type/Archivo) | SIL Open Font License 1.1 (https://openfontlicense.org) |
| JetBrains Mono | `Assets/Fonts/JetBrainsMono-Regular.ttf`, `JetBrainsMono-Medium.ttf`, `JetBrainsMono-Bold.ttf` | Copyright 2020 The JetBrains Mono Project Authors (https://github.com/JetBrains/JetBrainsMono) | SIL Open Font License 1.1 (https://openfontlicense.org) |

The fonts are used unmodified and are not sold by themselves. Inter, used as a fallback for characters the bundled faces lack, comes with
the Avalonia.Fonts.Inter package under its own SIL Open Font License 1.1.

## File formats

This product includes DNG technology under license by Adobe.

This product includes Gain Map technology under license by Adobe.

## Libraries

The published app also contains these libraries, each under its own licence:

| Library | Licence |
|---|---|
| .NET runtime and libraries (Microsoft) | MIT |
| Avalonia, Avalonia.Desktop, Avalonia.Themes.Fluent, Avalonia.Fonts.Inter | MIT |
| SkiaSharp and Skia (through Avalonia) | MIT / BSD 3-Clause |
| HarfBuzzSharp and HarfBuzz (through Avalonia) | MIT |
| CommunityToolkit.Mvvm | MIT |
| Microsoft.Data.Sqlite with SQLitePCLRaw and SQLite | MIT / Apache 2.0 / public domain |
| Google.Protobuf | BSD 3-Clause |
| Velopack (installer and updates of the installed builds) | MIT |

The mesh simplification of the 3D photo export follows the method of Fast Quadric Mesh Simplification by Sven Forstmann
(https://github.com/sp4cerat/Fast-Quadric-Mesh-Simplification, MIT license), written anew in C#.

## Refined depth: files that are downloaded, not bundled

The refined depth feature uses two files that are not part of the app. The app downloads them when you first use the feature
(or from Settings, Refined depth), into `models/depth` in the app's data folder, and you can remove them there. Each keeps its
own license:

| File | Source | Copyright | License |
|---|---|---|---|
| Depth Anything V2 Small (the depth network), converted to the ONNX format | The `onnx-community/depth-anything-v2-small` repository on Hugging Face (https://huggingface.co/onnx-community/depth-anything-v2-small); the model is from https://github.com/DepthAnything/Depth-Anything-V2 | The Depth Anything V2 authors | Apache License 2.0 (https://www.apache.org/licenses/LICENSE-2.0) |
| ONNX Runtime 1.23.2 (the library that runs the network) | The release archives at https://github.com/microsoft/onnxruntime/releases | Copyright (c) Microsoft Corporation | MIT |

The ONNX file is a converted form of the original model weights (a modified form in the sense of the Apache License). Lux uses
both files unchanged, as downloaded.
