using System;
using System.Collections.Generic;
using System.Linq;
using BeatLeader.Models.Replay;

namespace AutoReplay
{
    public static class AutoReplayGenerator
    {
        private static NoteData MirrorNote(NoteData note)
        {
            var copy = note.CopyWith();
            copy.Mirror(4);
            return copy;
        }

        // 與 BeatLeader ReplayBeatmapData.CreateSortedNoteDataList 相同的邏輯 (含鏈條切片)
        private static List<NoteData> CreateSortedNoteDataListFallback(IEnumerable<BeatmapDataItem> items)
        {
            var result = new List<NoteData>();
            foreach (var item in items)
            {
                switch (item)
                {
                    case NoteData data:
                        result.Add(data);
                        break;
                    case SliderData slider when slider.sliderType == SliderData.Type.Burst:
                        var sliceCount = slider.sliceCount;
                        for (var i = 1; i < sliceCount; ++i)
                        {
                            var lineIndex = i < sliceCount - 1 ? slider.headLineIndex : slider.tailLineIndex;
                            var lineLayer = i < sliceCount - 1 ? slider.headLineLayer : slider.tailLineLayer;
                            var time = UnityEngine.Mathf.LerpUnclamped(slider.time, slider.tailTime, (float)i / (sliceCount - 1));
                            result.Add(NoteData.CreateBurstSliderNoteData(
                                time, slider.beat, slider.rotation, lineIndex, lineLayer,
                                slider.headBeforeJumpLineLayer, slider.colorType, NoteCutDirection.Any, 1f));
                        }
                        break;
                }
            }
            result.Sort((a, b) => a.time.CompareTo(b.time));
            return result;
        }

        static float GetNoteJumpSpeed(BeatmapLevel level, BeatmapKey key)
        {
            float njs = 0f;
            try
            {
                var data = level.GetDifficultyBeatmapData(key.beatmapCharacteristic, key.difficulty);
                if (data != null) njs = data.noteJumpMovementSpeed;
            }
            catch (Exception ex)
            {
                Plugin.Log?.Warn($"Failed to read NJS: {ex.Message}");
            }
            // 譜面沒填 NJS (0) 時遊戲會按難度給預設值
            if (njs <= 0f) njs = MotionGenerator.DefaultNjs(key.difficulty.ToString());
            return njs > 0f ? njs : 19.25f;
        }

        // 反應時間 RT (方塊從出現到被切的時間) 和跳躍距離 JD, 公式同遊戲 (BeatmapObjectSpawnMovementData):
        //   半跳拍數從 4 開始, 半跳距離超過 18 就減半, 再加上譜面的 offset, 最少 0.25 拍
        static float GetReactionTime(BeatmapLevel level, BeatmapKey key, float njs, out float jd)
        {
            float offset = 0f;
            try
            {
                var data = level.GetDifficultyBeatmapData(key.beatmapCharacteristic, key.difficulty);
                if (data != null) offset = data.noteJumpStartBeatOffset;
            }
            catch { }
            return MotionGenerator.ReactionTime(njs, level.beatsPerMinute, offset, out jd);
        }

        public static Replay GenerateReplay(
            IReadonlyBeatmapData beatmapData,
            BeatmapLevel level,
            BeatmapKey key,
            string hash,
            GameplayModifiers modifiers = null,
            bool leftHanded = false)
        {
            var replay = new Replay();

            // 優先用 BeatLeader 自己的實現, 保證音符順序/鏈條切片與回放器完全一致.
            // 0.10.x 移到了 ReplayBeatmapData, 0.9.x 在 ReplayerScoreProcessor.
            IEnumerable<NoteData> sortedNotes = null;
            foreach (var target in new[]
            {
                "BeatLeader.Replayer.ReplayBeatmapData:CreateSortedNoteDataList",
                "BeatLeader.Replayer.Emulation.ReplayerScoreProcessor:CreateSortedNoteDataList",
            })
            {
                try
                {
                    var method = HarmonyLib.AccessTools.Method(target);
                    sortedNotes = method?.Invoke(null, new object[] { beatmapData.allBeatmapDataItems }) as IEnumerable<NoteData>;
                }
                catch (Exception ex)
                {
                    Plugin.Log?.Warn($"Failed to invoke {target}: {ex.Message}");
                }
                if (sortedNotes != null) break;
            }

            if (sortedNotes == null)
            {
                Plugin.Log?.Warn("BeatLeader CreateSortedNoteDataList not found, using local fallback.");
                sortedNotes = CreateSortedNoteDataListFallback(beatmapData.allBeatmapDataItems);
            }

            // 左手模式: 遊戲會把譜面左右映象 (軌道翻轉、紅藍互換、切割方向映象).
            // BeatLeader 用"時間 + 音符 ID"匹配事件, ID 來自映象後的譜面, 所以這裡也必須用映象後的音符.
            // 做法和 BeatLeader 自己映象音符的方式一致: CopyWith() 複製一份再 Mirror(4), 不改動原資料.
            // Noodle Extensions 的方塊座標 (方塊排到格子外 / 排成長條): 從原始音符讀, 映象的複本 x -> -x - 1
            sortedNotes = sortedNotes.ToList();
            AutoReplayMath.NoteCoords.Clear();
            foreach (var n in sortedNotes)
                if (NoodleCoords.TryGet(n, out var c)) AutoReplayMath.NoteCoords[n] = c;
            if (AutoReplayMath.NoteCoords.Count > 0) Plugin.Log?.Info($"Noodle coordinates: {AutoReplayMath.NoteCoords.Count} notes");

            if (leftHanded)
            {
                sortedNotes = sortedNotes.Select(n =>
                {
                    var m = MirrorNote(n);
                    if (AutoReplayMath.NoteCoords.TryGetValue(n, out var c)) AutoReplayMath.NoteCoords[m] = new UnityEngine.Vector2(-c.x - 1f, c.y);
                    return m;
                }).ToList();
            }

            var allNotes = sortedNotes
                .Where(n => n.scoringType != NoteData.ScoringType.Ignore &&
                            n.gameplayType != NoteData.GameplayType.Bomb &&
                            (n.colorType == ColorType.ColorA || n.colorType == ColorType.ColorB))
                .ToList();

            var obstacles = beatmapData.allBeatmapDataItems
                .OfType<ObstacleData>()
                .OrderBy(o => o.time)
                .ToList();

            var bombs = beatmapData.allBeatmapDataItems
                .OfType<NoteData>()
                .Where(n => n.gameplayType == NoteData.GameplayType.Bomb)
                .Select(b => leftHanded ? MirrorNote(b) : b)
                .OrderBy(b => b.time)
                .ToList();

            // NA(無箭頭)模式: 所有方塊當點塊處理, 上下交替揮, 不看譜面裡的切割方向
            bool noArrows = modifiers != null && modifiers.noArrows;

            float njs = GetNoteJumpSpeed(level, key);
            float rt = GetReactionTime(level, key, njs, out float jd);
            Plugin.Log?.Info($"NJS (bomb speed): {njs}  RT: {rt * 1000f:F0} ms  JD: {jd:F2}");
            var genWatch = System.Diagnostics.Stopwatch.StartNew();

            var motion = MotionGenerator.Generate(allNotes, bombs, obstacles, njs, rt, leftHanded, noArrows);
            int totalFrames = motion.count;

            var noteEvents = new List<NoteEvent>();
            int totalScore = 0;
            int combo = 0;
            foreach (var note in allNotes)
            {
                noteEvents.Add(NoteEventBuilder.Create115NoteEvent(note, noArrows));
                combo++;
                int mult = combo >= 14 ? 8 : (combo >= 6 ? 4 : (combo >= 2 ? 2 : 1));
                // 鏈條頭最高 85、鏈條切片最高 20, 不是每個塊都是 115
                totalScore += ScoreModel.GetNoteScoreDefinition(note.scoringType).maxCutScore * mult;
            }

            var frames = new List<Frame>(totalFrames);
            for (int i = 0; i < totalFrames; i++)
            {
                frames.Add(new Frame
                {
                    time = motion.t0 + i * motion.dt,
                    fps = 90,
                    head = new BeatLeader.Models.Replay.Transform
                    {
                        position = AutoReplayMath.ToBLVector(motion.headPos[i]),
                        rotation = AutoReplayMath.ToBLQuaternion(motion.headRot[i])
                    },
                    leftHand = new BeatLeader.Models.Replay.Transform
                    {
                        position = AutoReplayMath.ToBLVector(motion.leftPos[i]),
                        rotation = AutoReplayMath.ToBLQuaternion(motion.leftRot[i])
                    },
                    rightHand = new BeatLeader.Models.Replay.Transform
                    {
                        position = AutoReplayMath.ToBLVector(motion.rightPos[i]),
                        rotation = AutoReplayMath.ToBLQuaternion(motion.rightRot[i])
                    }
                });
            }

            Plugin.Log?.Info($"AutoPlay motion generated in {genWatch.ElapsedMilliseconds} ms ({totalFrames} frames)");

            replay.info = new ReplayInfo
            {
                version = "2.0.0",
                gameVersion = "1.40.8",
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
                playerID = "Auto Play",
                playerName = string.IsNullOrWhiteSpace(ModelManager.Bot.name) ? "Auto Play" : ModelManager.Bot.name,
                platform = "steam",
                trackingSytem = "OpenVR",
                hmd = "Oculus",
                controller = "Touch",
                hash = hash,
                songName = level.songName ?? "Unknown",
                mapper = level.allMappers != null && level.allMappers.Length > 0 ? level.allMappers[0] : "Unknown",
                difficulty = key.difficulty.ToString(),
                score = totalScore,
                mode = key.beatmapCharacteristic.serializedName,
                environment = level.GetEnvironmentName(key.beatmapCharacteristic, key.difficulty).ToString(),
                modifiers = AutoReplayMath.EncodeModifiers(modifiers),
                jumpDistance = 0f,
                leftHanded = leftHanded,
                height = AutoReplayMath.PlayerHeight,
                startTime = 0f,
                failTime = 0f,
                // BeatLeader 裡 speed != 0 會被當成"練習模式"設定(PracticeSettings), 回放就會以練習模式啟動.
                // 變速由 modifiers 裡的 FS/SF/SS 負責, 所以這裡保持 0, 和正常打出來的回放一致.
                speed = 0f
            };

            replay.frames = frames;
            replay.notes = noteEvents;
            replay.walls = new List<WallEvent>();
            replay.heights = new List<AutomaticHeight> { new AutomaticHeight { height = AutoReplayMath.PlayerHeight, time = 0f } };
            replay.pauses = new List<Pause>();
            replay.saberOffsets = new SaberOffsets();
            // 浮水印: 標明這是 AutoPlay 生成的回放, 以及用的是哪個模型 (BeatLeader 會原樣儲存 customData)
            replay.customData = new Dictionary<string, byte[]>
            {
                ["AutoPlay"] = System.Text.Encoding.UTF8.GetBytes(Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    generator = "AutoPlay-unofficial",
                    version = Plugin.Version,
                    model = ModelManager.CurrentId,
                    modelName = ModelManager.Current?.meta?.name,
                    modelFingerprint = ModelManager.CurrentFingerprint,
                    modelTrust = ModelManager.CurrentTrust.ToString(),
                    generatedUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                }))
            };

            return replay;
        }
    }

    /// <summary>
    /// 讀 Noodle Extensions 的方塊座標. 有裝 CustomJSONData 時音符是 CustomNoteData, 帶一個 customData (字典);
    /// 用反射讀, 不需要直接參考那個組件 (沒裝的話就什麼都讀不到, 照原本的格子位置)
    /// </summary>
    static class NoodleCoords
    {
        static readonly System.Collections.Generic.Dictionary<Type, Func<object, object>> _getters = new System.Collections.Generic.Dictionary<Type, Func<object, object>>();

        public static bool TryGet(NoteData note, out UnityEngine.Vector2 coords)
        {
            coords = default;
            try
            {
                var t = note.GetType();
                if (!_getters.TryGetValue(t, out var get))
                {
                    var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
                    var prop = t.GetProperty("customData", flags);
                    var field = prop == null ? t.GetField("customData", flags) ?? t.GetField("<customData>k__BackingField", flags) : null;
                    get = prop != null ? (o => prop.GetValue(o)) : field != null ? (Func<object, object>)(o => field.GetValue(o)) : null;
                    _getters[t] = get;
                }
                if (get == null || !(get(note) is System.Collections.IDictionary d)) return false;
                object v = d.Contains("coordinates") ? d["coordinates"] : d.Contains("_position") ? d["_position"] : null;
                if (!(v is System.Collections.IList l) || l.Count < 2 || l[0] == null || l[1] == null) return false;
                coords = new UnityEngine.Vector2(Convert.ToSingle(l[0], System.Globalization.CultureInfo.InvariantCulture), Convert.ToSingle(l[1], System.Globalization.CultureInfo.InvariantCulture));
                return true;
            }
            catch { return false; }
        }
    }
}
