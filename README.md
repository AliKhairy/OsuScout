<img src="branding/scoutsu_256.png" width="96" alt="Scoutsu icon">

# Scoutsu

Scoutsu (formerly OsuScout) is a WPF-based tool designed for osu! players to analyze, track, and extract features from beatmaps and live gameplay using Machine Learning.

## Features
- Real-time memory data reading from osu!
- Machine learning integration via ONNX for beatmap classification and feature extraction
- Intuitive user interface built with MahApps.Metro
- Automatic background updates via GitHub Releases

## Installation
1. Go to the [Releases](https://github.com/AliKhairy/OsuScout/releases) page.
2. Download `Scoutsu-Setup.exe` from the latest release.
3. Run the setup file. The application will automatically install and launch!
4. A shortcut will be created on your desktop and start menu.

## License
Copyright (C) 2026 AliKhairy

Scoutsu is free software, licensed under the [GNU General Public License v3.0](LICENSE). You can use, study, change and share it; if you distribute a modified version, it has to stay under the same license with its source available.

It's GPL because it bundles [OsuMemoryDataProvider](https://github.com/Piotrekol/ProcessMemoryDataFinder) (GPL-3.0-or-later), which it uses for one thing: noticing when you start playing so the window can hide itself.

Other libraries in the installer: [MahApps.Metro](https://github.com/MahApps/MahApps.Metro) and ControlzEx (MIT), [ONNX Runtime](https://github.com/microsoft/onnxruntime) (MIT), [Entity Framework Core](https://github.com/dotnet/efcore) (MIT), [Velopack](https://github.com/velopack/velopack) (MIT), [SQLitePCL.raw](https://github.com/ericsink/SQLitePCL.raw) (Apache-2.0) and Rosu.Net, a wrapper around [rosu-pp](https://github.com/MaxOhn/rosu-pp) (MIT) for star ratings.

The tagging model was trained on community tags from [echosu](https://echosu.com).
