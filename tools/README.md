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
ApModelTool keygen <私鑰.xml> <公鑰.xml>
```

私鑰請放在 repo 以外的地方，**不要提交**。
