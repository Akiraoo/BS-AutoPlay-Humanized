# BS-AutoPlay-Humanized

[English](README_EN.md) | [中文](README.md)

[![Game Version](https://img.shields.io/badge/Beat%20Saber-1.40.8-blue.svg)](https://beatsaber.com/)
[![BSIPA](https://img.shields.io/badge/BSIPA-^4.3.0-orange.svg)](https://github.com/bsmg/BeatSaber-IPA-Reloaded)
[![BeatLeader](https://img.shields.io/badge/BeatLeader-^0.10.666-red.svg)](https://beatleader.com/)
[![BSML](https://img.shields.io/badge/BSML-^1.12.0-purple.svg)](https://github.com/monkeymanboy/BeatSaberMarkupLanguage)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

> **非官方分支**：原作是 haha6ha 的 [AutoPlay](https://github.com/haha6ha/AutoPlay)。本分支適配 BeatLeader **0.10.666**，並把揮刀動作改成「從真人回放訓練出來的模型」驅動。

為 **Beat Saber** 任意選中的譜面一鍵生成擬真 `.bsor` 回放，並直接在遊戲內用 BeatLeader 回放器播放。

## ✨ 和原版的差異

- **適配 BeatLeader 0.10.666**（含譜面成績上傳修正的版本）
- **真人動作模型**：揮刀軌跡、手腕扭轉、反手、8 字形回揮、停頓時的手部小幅晃動，全部從真人 BeatLeader 回放統計而來
  - 依照 NJS / JD / RT 決定預備動作的時機，不是到最後一刻才猛砍
  - 依「前後兩刀的轉向角度」分組的揮刀模板，tech 譜也能走出合理的路徑
  - 無方向方塊用動態規劃挑最順的方向，不打亂節奏
  - 手腕指向限位（模擬人體關節的活動範圍）、扭轉彈簧、手臂延遲，避免瞬移和翻轉
  - 躲牆時光劍會跟著身體移動，頭部傾斜角度收斂，觀看不暈
- **模型是獨立檔案**（`.apmodel`），可以換、可以自己訓練、可以分享
- **遊戲內訓練**：用你自己最近 80 份本機回放訓練出你的動作風格（會驗證玩家 ID，只用你本人的回放）
- **簽章**：作者發布的模型有 RSA 簽名，遊戲內會顯示「官方 ✓」；被改過的模型會被標示出來
- **浮水印**：生成的 `.bsor` 會在 `customData["AutoPlay"]` 記錄產生器版本、模型名稱與指紋

## ⚖️ 免責聲明

- 本模組僅用於**譜面演示、回放觀賞、演算法學習與研究**。
- 生成的 `.bsor` 回放，玩家都會被標記為 "Auto Play"，並帶有上述浮水印。
- 請勿用於任何破壞排行榜或多人對戰公平性的行為，尊重 BeatLeader 與 Beat Saber 社群規範。

## 🚀 安裝

1. 先裝好前置模組：**BSIPA**、**BeatLeader 0.10.666+**、**BeatSaberMarkupLanguage (BSML)**
2. 把 `auto_replay.dll` 放進 `Beat Saber/Plugins/`（從 [Releases](../../releases) 下載，或自己從原始碼建置）
3. 放一個模型到 `Beat Saber/UserData/AutoPlay/Models/`：
   - 直接用作者的範例模型：[`sample-model/Akiraoo-v2.apmodel`](sample-model/Akiraoo-v2.apmodel)
   - 或是進遊戲後用自己的回放訓練（見下方）
4. 啟動遊戲

> 沒有任何模型時，按 PEPE 按鈕不會生成，會提示你先放模型或訓練。

## 🎮 使用

1. 進入 Solo，選好曲子與難度（可以加修飾符、左手模式）
2. 按歌單和排行榜中間的 **PEPE 蛙按鈕**，生成 `.bsor` 回放
3. 模組會自動用 BeatLeader 回放器播放

### 模型設定（設定 → 模組設定 → AutoPlay）

| 項目 | 說明 |
|---|---|
| 使用中的模型 | 切換 `Models/` 裡的模型，下方顯示簽章狀態、回放數、模板數、指紋 |
| 用我的回放訓練 | 讀取 `UserData/BeatLeader/Replays` 裡**你本人**最近 80 份回放（Standard、Hard 以上、無加速修飾符；不夠會放寬，最少 10 份），在背景訓練，約數秒完成。輸出為未打包的 `Models/<名稱>/model.json`（可讀）與訓練報告 |
| 打包成 .apmodel | 把本機訓練的模型打包成單一 `.apmodel`（混淆、不簽名），方便分享 |
| 重新整理 | 重新掃描 `Models/` 資料夾 |

BeatLeader 每打完一首歌都會自動存回放到本機，所以重新訓練時會自動用上你最新的遊玩紀錄。

## 📦 模型格式

- **未打包**：`Models/<名稱>/model.json`，純文字參數（手部位置模型、扭轉模型、指向限位、揮刀模板、反手扭轉偏移）
- **打包**：`Models/*.apmodel`，`APMD` 標頭 + 混淆後的 gzip 內容 + 可選的 RSA-2048 / SHA-256 簽章
  - 混淆只是讓參數不能一眼讀出，**不是加密**
  - 遊戲內只有公鑰（`Core/OfficialKey.cs`），用來驗證作者簽名

## 👨‍💻 從原始碼建置

```bash
git clone https://github.com/Akiraoo/BS-AutoPlay-Humanized.git
cd BS-AutoPlay-Humanized
dotnet build auto_replay.csproj -c Release -p:BeatSaberDir="你的BeatSaber路徑"
```

- 預設會把 dll 複製到遊戲的 `Plugins/`；加 `-p:CopyToGame=false` 可以關掉
- 命令列模型工具：`tools/ApModelTool`（train / pack / sign / unpack / inspect / keygen），和遊戲內共用 `Core/` 的訓練器
- 分析與離線模擬用的 Python 腳本：見 [`tools/README.md`](tools/README.md)

### 📝 結構

```
├── Core/                     # 不依賴 Unity：模型格式、.apmodel 打包/簽章、bsor 讀取、訓練器
├── UI/                       # BSML 設定頁
├── AutoReplayGenerator.cs    # 回放生成核心（含浮水印）
├── AutoReplayManager.cs      # 取得選定譜面並驅動生成與播放
├── NoteSwingPlanner.cs       # 揮刀規劃：模板、無方向方塊 DP、躲炸彈
├── PoseCalculator.cs         # 頭部躲牆、手部軌跡（扭轉彈簧、反手、手臂延遲、停頓晃動）
├── HumanModel.cs             # 手部位置 / 扭轉 / 限位模型
├── SwingTemplates.cs         # 揮刀模板查詢
├── RollOffsets.cs            # 反手扭轉偏移
├── ModelManager.cs           # 模型載入、選擇、儲存、打包
├── sample-model/             # 作者的範例模型（已簽名）
└── tools/                    # ApModelTool 與 Python 分析工具
```

## 🙏 致謝

- 原作：[haha6ha/AutoPlay](https://github.com/haha6ha/AutoPlay)
- 範例模型只使用作者 Akiraoo 本人的 BeatLeader 回放訓練

## 📄 授權

[MIT License](LICENSE)
