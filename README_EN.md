# BS-AutoPlay-Humanized

[English](README_EN.md) | [中文](README.md)

[![Game Version](https://img.shields.io/badge/Beat%20Saber-1.40.8-blue.svg)](https://beatsaber.com/)
[![BSIPA](https://img.shields.io/badge/BSIPA-^4.3.0-orange.svg)](https://github.com/bsmg/BeatSaber-IPA-Reloaded)
[![BeatLeader](https://img.shields.io/badge/BeatLeader-^0.10.666-red.svg)](https://beatleader.com/)
[![BSML](https://img.shields.io/badge/BSML-^1.12.0-purple.svg)](https://github.com/monkeymanboy/BeatSaberMarkupLanguage)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

> **Unofficial fork** of [haha6ha/AutoPlay](https://github.com/haha6ha/AutoPlay). Updated for BeatLeader **0.10.666**, with saber motion driven by a model trained on real player replays.

Generate a human-like `.bsor` replay for any selected map in one click and watch it right away in BeatLeader's in-game replayer.

## ✨ What's different from the original

- **BeatLeader 0.10.666 support** (the version with the score upload fixes)
- **Human motion model**: swing paths, wrist roll, backhand, figure-8 recovery and idle sway are all learned from real BeatLeader replays
  - Wind-up timing follows NJS / JD / RT instead of snapping at the last moment
  - Swing templates grouped by the turn angle between consecutive swings, so tech maps get sensible paths
  - Dot notes pick the smoothest direction with dynamic programming, keeping the flow intact
  - Wrist aim limits (human joint range), a roll spring and arm lag prevent teleports and flips
  - Sabers follow the body when dodging walls; head tilt is kept small so it's comfortable to watch
- **Models are separate files** (`.apmodel`): swap them, train your own, share them
- **In-game training** from your own 80 most recent local replays (the player ID is checked, so only your own replays are used)
- **Signing**: author-released models carry an RSA signature and show as "Official ✓"; modified models are flagged
- **Watermark**: generated `.bsor` files record generator version, model name and fingerprint in `customData["AutoPlay"]`

## ⚖️ Disclaimer

- For **map showcases, replay viewing, and algorithm study/research** only.
- Generated replays are marked as "Auto Play" and carry the watermark above.
- Do not use this to undermine leaderboard or multiplayer fairness. Respect the BeatLeader and Beat Saber communities.

## 🚀 Installation

1. Install the dependencies: **BSIPA**, **BeatLeader 0.10.666+**, **BeatSaberMarkupLanguage (BSML)**
2. Put `auto_replay.dll` into `Beat Saber/Plugins/` (from [Releases](../../releases), or build it yourself)
3. Put a model into `Beat Saber/UserData/AutoPlay/Models/`:
   - the author's sample model: [`sample-model/Akiraoo-v2.apmodel`](sample-model/Akiraoo-v2.apmodel)
   - or train one from your own replays in-game (see below)
4. Start the game

> Without any model, the PEPE button won't generate anything and will ask you to add or train a model first.

## 🎮 Usage

1. Go to Solo and pick a song and difficulty (modifiers and left-handed mode are supported)
2. Press the **PEPE frog button** between the song list and the leaderboard to generate a `.bsor`
3. The mod plays it in BeatLeader's replayer automatically

### Model settings (Settings → Mod Settings → AutoPlay)

| Item | Description |
|---|---|
| Active model | Switch between models in `Models/`; shows signature status, replay count, template count and fingerprint |
| Train with my replays | Reads **your own** 80 most recent replays from `UserData/BeatLeader/Replays` (Standard, Hard or above, no speed modifiers; relaxed if there aren't enough, minimum 10) and trains in the background in a few seconds. Output is an unpacked, readable `Models/<name>/model.json` plus a training report |
| Pack to .apmodel | Packs a locally trained model into a single obfuscated, unsigned `.apmodel` for sharing |
| Refresh | Rescans the `Models/` folder |

BeatLeader saves a local replay after every play, so retraining always picks up your latest sessions.

## 📦 Model format

- **Unpacked**: `Models/<name>/model.json`, plain-text parameters (hand position model, roll model, aim limits, swing templates, backhand roll offsets)
- **Packed**: `Models/*.apmodel`, an `APMD` header + obfuscated gzip payload + optional RSA-2048 / SHA-256 signature
  - Obfuscation only keeps the parameters from being read at a glance — **it is not encryption**
  - The game only contains the public key (`Core/OfficialKey.cs`) to verify the author's signature

## 👨‍💻 Building from source

```bash
git clone https://github.com/Akiraoo/BS-AutoPlay-Humanized.git
cd BS-AutoPlay-Humanized
dotnet build auto_replay.csproj -c Release -p:BeatSaberDir="path/to/Beat Saber"
```

- The dll is copied into the game's `Plugins/` by default; pass `-p:CopyToGame=false` to disable
- Command-line model tool: `tools/ApModelTool` (train / pack / sign / unpack / inspect / keygen), sharing the trainer in `Core/` with the game
- Python scripts for analysis and offline simulation: see [`tools/README.md`](tools/README.md)

## 🙏 Credits

- Original mod: [haha6ha/AutoPlay](https://github.com/haha6ha/AutoPlay)
- The sample model is trained only on the author's (Akiraoo) own BeatLeader replays

## 📄 License

[MIT License](LICENSE)
