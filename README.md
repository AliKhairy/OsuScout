<img src="branding/scoutsu_256.png" width="96" alt="Scoutsu icon">

# Scoutsu

Scoutsu (formerly OsuScout) is a WPF-based tool designed for osu! players to analyze, track, and extract features from beatmaps and live gameplay using Machine Learning.

## Features
- Works with both osu!stable and osu!lazer
- Real-time memory data reading from osu!
- Machine learning integration via ONNX for beatmap classification and feature extraction
- Intuitive user interface built with MahApps.Metro
- Automatic background updates via GitHub Releases

## osu!stable and osu!lazer
Scoutsu finds, tags and searches the maps of either client. On first launch it picks whichever one is installed, or asks if you have both. **STABLE / LAZER** at the top switches between them, and each client keeps its own library, so switching never mixes them.

- **Finding your maps.** For stable, Scoutsu reads your `Songs` folder. For lazer, it reads lazer's data folder (`%APPDATA%\osu`, or wherever you moved it in lazer's settings). Scoutsu only ever reads from it and never changes your lazer install. If either isn't found, pick it with **DIR**. The first lazer scan checks every file lazer stores and takes a minute or two; later launches take seconds.
- **New maps** show up while Scoutsu is open, on both clients.
- **While you play**, the overlay hides itself and comes back in song select. **Alt+S** shows or hides it any time, and the pin keeps it on top of the game (turn it off if you play in exclusive fullscreen).
- **Copy & Switch to osu!** puts the map's search on your clipboard and switches to the game; press Ctrl+V in song select. On lazer that's the beatmap ID, which song select matches to that map. On stable, and for maps with no online ID, it's artist, title and difficulty.

osu!lazer support started in [FrasierGH's fork](https://github.com/FrasierGH/OsuScout).

## Installation
1. Go to the [Releases](https://github.com/AliKhairy/OsuScout/releases) page.
2. Download `Scoutsu-Setup.exe` from the latest release.
3. Run the setup file. The application will automatically install and launch!
4. A shortcut will be created on your desktop and start menu.

## License
Copyright (C) 2026 AliKhairy

Scoutsu is free software, licensed under the [GNU General Public License v3.0](LICENSE). You can use, study, change and share it; if you distribute a modified version, it has to stay under the same license with its source available.

It's GPL because it bundles [OsuMemoryDataProvider](https://github.com/Piotrekol/ProcessMemoryDataFinder) (GPL-3.0-or-later), which it uses for one thing: noticing when you start playing on osu!stable so the window can hide itself. On osu!lazer it reads the game's window title instead, where lazer shows the map you're playing.

Other libraries in the installer: [MahApps.Metro](https://github.com/MahApps/MahApps.Metro) and ControlzEx (MIT), [ONNX Runtime](https://github.com/microsoft/onnxruntime) (MIT), [Entity Framework Core](https://github.com/dotnet/efcore) (MIT), [Velopack](https://github.com/velopack/velopack) (MIT), [SQLitePCL.raw](https://github.com/ericsink/SQLitePCL.raw) (Apache-2.0) and Rosu.Net, a wrapper around [rosu-pp](https://github.com/MaxOhn/rosu-pp) (MIT) for star ratings.

The tagging model was trained on community tags from [echosu](https://echosu.com).
