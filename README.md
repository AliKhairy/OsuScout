
# OsuScout

A high-performance, multi-threaded local beatmap overlay and injector for osu!. 

OsuScout reads your game's memory in real-time, automatically classifies your beatmaps using an ONNX neural network, and allows you to instantly filter and inject maps directly into your game client via Win32 hooks.

![OsuScout Screenshot](link-to-an-image-you-will-upload-later.png)

## For Players: How to Use
1. Go to the **[Releases](../../releases)** tab on the right side of this page.
2. Download `OsuScout_Release.zip` and extract it anywhere on your PC.
3. Run `OsuScoutNew.exe`.
4. Press `Alt + S` at any time while osu! is open to toggle the overlay. Double-click a map to instantly focus the game and copy the exact search query to your clipboard.

## For Developers: Under the Hood
OsuScout is built to bypass the limitations of standard third-party beatmap tools. 

* **The AI Brain:** Utilizes `Microsoft.ML.OnnxRuntime` to run a custom Python-trained neural network locally, classifying maps into tags (Tech, Jumps, Streams) without relying on external API calls.
* **The Memory Hook:** Uses `OsuMemoryDataProvider` to track the active game state, automatically hiding the WPF overlay during gameplay and restoring it during song select.
* **The Math Engine:** Integrates the native Rust-based `rosu-pp` library via C# bindings to calculate live Star Ratings for local and unsubmitted maps natively, bypassing the bloated `osu.Game` dependencies.
* **The Data Layer:** Powered by an in-memory `ICollectionView` mapped to an Entity Framework SQLite database, allowing zero-latency filtering of 50,000+ maps simultaneously.

## Tech Stack
* **Framework:** .NET 10.0 (WPF)
* **Language:** C# / XAML
* **Database:** SQLite (Entity Framework Core)
* **Machine Learning:** ONNX Runtime
