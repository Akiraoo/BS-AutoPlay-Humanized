# 分析工具（Python 3 + numpy）

用真人 BeatLeader 回放（`UserData/BeatLeader/Replays/*.bsor`）統計人體動作模型，產生 C# 常數。

| 腳本 | 用途 |
|---|---|
| `bsor_stats.py <bsor...>` | 單份回放的動作統計（手/頭移動幅度、角速度、靜止比例） |
| `wrist_fit.py <Replays資料夾> <份數> model.json` | 擬合手的位置模型、扭轉模型、指向限位 → `HumanModel.cs` |
| `export_model.py model.json` | 印出可貼進 `HumanModel.cs` 的係數 |
| `swing_templates.py <Replays資料夾> <份數> templates.json` | 以切塊瞬間對齊，統計揮刀模板（時機、幅度、8 字側偏） |
| `gen_cs.py templates.json ../SwingTemplates.Data.cs` | 把模板轉成 C# |
| `glitch2.py <bsor...>` | 找瞬移 / 扭轉翻轉 |
| `rollcheck.py model.json <bsor...>` | 和真人扭轉模型比對 |
| `compare_map.py <autoplay.bsor> <真人.bsor...>` | 同一張譜：逐音符比對揮砍方向、找出劍的指向和真人差很多的時段 |
| `dot_rest_stats.py <Replays資料夾> <份數>` | 點塊方向習慣、長間隔休息姿勢 |
| `dot_dp_check.py <autoplay.bsor> <真人.bsor...>` | 離線驗證點塊方向規劃（Python 版）和真人的吻合率 |
| `roll_offset_templates.py <Replays資料夾> <份數> roll_offsets.json` | 反手：切塊前後的扭轉偏移曲線 |
| `gen_roll_cs.py roll_offsets.json ../RollOffsets.Data.cs` | 把扭轉偏移轉成 C# |
| `cut_pose_diff.py <bsor...>` / `backhand_check.py <bsor...>` | 切塊瞬間的指向/位置/扭轉和模型的差異 |
| `roll_motion_fit.py` | 測試扭轉是否和揮動方向有關 |
| `tech_templates.py <Replays資料夾> <份數> rel_templates.json [額外bsor...]` | 依「轉向角度 0/45/90/135/180°」分組的揮刀模板（目前 C# 用的就是這份） |
| `sim_planner.py rel_templates.json all_slerp <真人.bsor...>` | **離線模擬** AutoPlay 的劍指向軌跡，和真人回放逐幀比較（不用開遊戲） |
| `human_baseline.py <真人A.bsor> <真人B.bsor>` | 同一個人兩次遊玩之間的差異，當作「像人」的目標 |

## ApModelTool（C#，和遊戲內共用 `Core/` 訓練器）

```bash
dotnet build tools/ApModelTool -c Release
ApModelTool train <Replays資料夾> <輸出 model.json> [--player ID] [--from yyyy-MM-dd] [--to yyyy-MM-dd] [--days N]
                  [--per-map 3] [--max 300] [--min-stars 0] [--ranked] [--prefer-acc] [--stars] [--offline] [--name 名稱] [--author 作者]
ApModelTool pack <model.json> <輸出.apmodel> [--sign 私鑰.xml]
ApModelTool inspect <檔案.apmodel>
ApModelTool generate <譜面資料夾或hash> <難度> <模型> <輸出.bsor> [--left] [--na] [--mode Standard] [--game 遊戲路徑]
ApModelTool keygen <私鑰.xml> <公鑰.xml>
```

私鑰請放在 repo 以外的地方，**不要提交**。

### 遊戲外生成回放（debug 用）

`generate` 和模組共用同一份生成器原始碼（`MotionGenerator` / `NoteSwingPlanner` / `PoseCalculator` / 人體模型），
Unity 的數學型別和遊戲的譜面型別換成 `Standalone/` 裡的替代品，譜面自己讀（v2 / v3 / v4、BPM 變化、弧線、鏈條）。
產生的 `.bsor` 可以直接放進 BeatLeader 的回放資料夾在遊戲裡看。

- 譜面可以給資料夾路徑，或譜面 hash（先查 SongCore 的快取，找不到再逐個資料夾計算，包括 `CustomWIPLevels`）
- 驗證：33 張已在遊戲內生成過的譜（v2.0–v3.3，含 BPM 變化 / 弧線 / 鏈條 / OneSaber），方塊 ID 和時間全部和遊戲內一致；
  同一張譜的動作，除了疊塊（同時間同顏色）以外逐幀一致，疊塊的順序現在由揮刀規劃自己決定，兩邊結果相同

```bash
python eval_replay.py <生成.bsor> <真人.bsor> [更多真人.bsor]
```
和真人回放比較：方向誤差、手的高度、依間隔 × 位置拆開的角速度、收勢 / 預備幅度；多份真人回放時順便印出真人彼此的差異當基準。

```bash
python eval_batch.py variants.json --maps 12 --before 2026-10-18
```
批次評估：從玩家在 `--before` 之前（訓練範圍外）的回放挑譜，每個變體用遊戲外生成器生成一次並和真人比較，
輸出方向誤差 / >75° 比例 / 手的位置誤差 / 角速度與手速相對真人的倍數 / 收勢與預備最遠角度。
`variants.json` 例：`[{"name": "old", "model": "舊模型"}, {"name": "new", "model": "新模型", "args": ["--no-arm"]}]`

```bash
python bomb_eval.py <譜面hash或資料夾> <難度> <回放.bsor> [更多回放]
```
每顆炸彈飛過時光劍離它最近的距離（真人 / 生成都能算）、近距離時光劍在炸彈的哪一側。幾何和生成器的躲炸彈一致
（偏保守：炸彈緊貼方塊的譜，真人擦過也會被算成碰撞）。

`generate` 的除錯選項：`--no-arm`（不用手臂模板）、`--arm-smooth 秒`、`--amp-knee 度`、`--blend-delay 比例`、
`--bomb-safety 米`、`--bomb-span 秒`、`--up-margin 米`、`--order-from 回放`（照另一份回放的方塊順序）、`--jitter 種子`（方塊時間加極小擾動）
