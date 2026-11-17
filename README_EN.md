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
- **In-game training** from your own local replays within a chosen date range (the player ID is checked, so only your own replays are used), with optional background retraining as new replays come in
- **Model info**: trainer version, date range, filters, BeatLeader / ScoreSaber star distribution and more, with no player ID
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

**Model tab**
- Switch between models in `Models/`; shows signature status, fingerprint and **model info**:
  - trainer and AutoPlay version, player name, device, game / BeatLeader version
  - date range and filters, how many replays survive each step, data period, map count, swing count
  - NPS, BeatLeader star distribution (unranked maps have AI estimates too) with tech / pass / acc ratings, ScoreSaber ranked count and stars
  - **No player ID is stored.** Info in unsigned models is for reference only
- Pack to `.apmodel` (obfuscated, unsigned), Refresh

**Training tab**
| Item | Description |
|---|---|
| From / To | Date range (slide fully right = no limit) |
| Max per map | Only the newest N replays of the same map + difficulty (default 3), so grinding one map doesn't dominate the model |
| Total limit | Above this, replays are **sampled evenly** across the range (default 300). Affects training time, not model size |
| Min BL stars | Filter by BeatLeader stars (maps without stars are kept) |
| Look up BL / SS stars | Public APIs, no account needed; cached in `UserData/AutoPlay/star-cache.json`; only queried while training |
| Ranked maps only | Only maps ranked on BeatLeader or ScoreSaber |
| Prefer acc | Take the highest-scoring plays of each map, and the highest-acc replays when over the total limit (acc = score / BL max score) |

- A live preview shows `in range → own → eligible → deduplicated → star filter → used`
- **Only your own replays**: the logged-in player ID is fetched from the platform and every replay must match
- Always excluded: non-Standard, left-handed, FS / SF / SS, below 1.5 average NPS, and replays whose **height setting** is more than 10 cm from the median
- Output is an unpacked, readable `Models/my-<date>/model.json` plus a training report

**Auto-training tab**
- With "Keep following new replays" on, `Models/my-auto/` is retrained in the background **while idle in the menu** after every N new replays (default 10)
- The rolling window uses the Training tab's "From" (e.g. last 30 days) and the same filters; it pauses when you start a song
- It only switches to the new version if you're already using `my-auto`; the previous one is kept in `my-auto.bak/`

**Height**: the model stores the training replays' **in-game height setting** (median) and the **actual head height**. The height setting decides where notes and walls sit (about 0.5 m higher per 1 m of height, measured from real replays), so generated replays use that setting to keep note positions identical to the recordings, while the head uses the actual head height. Older models without height data keep 1.50 m.

> More data isn't always better: in testing, 80 → 300 → 900 replays drifted further from real play (style changes over time), so prefer a recent window.

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
