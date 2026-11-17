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
  - 躲牆（蹲下、左右閃）、躲炸彈、點塊斜切都照真人的習慣；劍不會穿過頭或戳到眼睛
- **個人習慣存進模型**：點塊斜切角度、上下排揮向、閃牆 / 蹲牆曲線、躲炸彈的距離，訓練時從你的回放（和對應譜面）量出來
- **模型是獨立檔案**（`.apmodel`），可以換、可以自己訓練、可以分享
- **遊戲內訓練**：選日期範圍，用你自己的本機回放訓練出你的動作風格（會驗證玩家 ID，只用你本人的回放）；可以開啟持續追蹤，在背景自動重訓
- **模型資訊**：訓練版本、日期範圍、篩選條件、BeatLeader / ScoreSaber 星數分佈等，不含任何玩家 ID
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
   - 直接用作者的範例模型：[`sample-model/Akiraoo-ranked30-acc-v3.apmodel`](sample-model/Akiraoo-ranked30-acc-v3.apmodel)
   - 或是進遊戲後用自己的回放訓練（見下方）
4. 啟動遊戲

> 沒有任何模型時，按 PEPE 按鈕不會生成，會提示你先放模型或訓練。

## 🎮 使用

1. 進入 Solo，選好曲子與難度（可以加修飾符、左手模式）
2. 按歌單和排行榜中間的 **PEPE 蛙按鈕**，生成 `.bsor` 回放
3. 模組會自動用 BeatLeader 回放器播放

### 模型設定（設定 → 模組設定 → AutoPlay）

**「模型」頁**
- 切換 `Models/` 裡的模型，顯示簽章狀態、指紋和**模型資訊**：
  - 訓練器與 AutoPlay 版本、玩家名稱、裝置、遊戲 / BeatLeader 版本
  - 日期範圍與篩選條件、每一步篩完剩幾份、採用的回放期間、譜面數、揮刀數
  - NPS、BeatLeader 星數分佈（unranked 也有 AI 估計）與 tech / pass / acc 分項、ScoreSaber ranked 份數與星數
  - **不存任何玩家 ID**。未簽名的模型資訊僅供參考
- 打包成 `.apmodel`（混淆、不簽名）、重新整理

**「訓練」頁**
| 項目 | 說明 |
|---|---|
| 從 / 到 | 日期範圍（拉到最右邊 = 不限日期） |
| 同一張譜最多 | 同譜同難度只取最新幾份，避免刷同一張譜讓模型只學到那幾張（預設 3） |
| 總數上限 | 超過時在日期範圍內**平均抽樣**（預設 300）。不影響模型大小，只影響訓練時間 |
| 最低 BL 星數 | 依 BeatLeader 星數篩選（查不到星數的譜不受影響） |
| 查詢 BL / SS 星數 | 用公開 API 查詢（不需要帳號），快取在 `UserData/AutoPlay/star-cache.json`；只在訓練時連網 |
| 只用 ranked 譜 | 只用 BeatLeader 或 ScoreSaber 上 ranked 的譜 |
| acc 優先 | 同一張譜取分數最高的；超過總數上限時取 acc 最高的（acc = 分數 / BL 記錄的滿分） |

- 調整時會即時預覽：`範圍內 → 本人 → 符合條件 → 去重複 → 星數篩選 → 採用`
- **只使用本人的回放**：先向平台取得目前登入的玩家 ID，每份回放都要相符
- 自動排除：非 Standard、左手模式、FS / SF / SS、平均 NPS < 1.5，以及**身高設定**和中位數差超過 10 cm 的回放
- 輸出為未打包、可讀的 `Models/my-日期/model.json` 與訓練報告

**「自動訓練」頁**
- 開啟「持續追蹤最新回放」後，每多 N 份新回放（預設 10）就在**選單閒置時**於背景重新訓練 `Models/my-auto/`
- 滾動範圍使用「訓練」頁的「從」（例如最近 30 天），其他條件相同；開始打歌會暫停
- 只有正在使用 `my-auto` 時才會自動切換到新版本，舊版保留在 `my-auto.bak/`

**身高**：模型會記錄訓練回放的**遊戲內身高設定**（中位數）和**實際頭高**。身高設定決定方塊和牆的上下位置（實測每高 1 m 上移約 0.5 m），所以生成時回放身高就用這個設定值，方塊位置才會和錄製時一樣；頭的位置則用實際頭高。舊模型沒有身高資料，維持 1.50 m。

> 資料不是越多越好：實測 80 份 → 300 份 → 900 份，和真人的偏差反而越來越大（打法會隨時間改變），建議用最近一段時間的回放。

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
