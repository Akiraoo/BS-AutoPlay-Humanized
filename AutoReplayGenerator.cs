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

        // 与 BeatLeader ReplayBeatmapData.CreateSortedNoteDataList 相同的逻辑 (含链条切片)
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
            // 谱面没填 NJS (0) 时游戏会按难度给默认值
            if (njs <= 0f) njs = MotionGenerator.DefaultNjs(key.difficulty.ToString());
            return njs > 0f ? njs : 19.25f;
        }

        // 反应时间 RT (方块从出现到被切的时间) 和跳跃距离 JD, 公式同游戏 (BeatmapObjectSpawnMovementData):
        //   半跳拍数从 4 开始, 半跳距离超过 18 就减半, 再加上谱面的 offset, 最少 0.25 拍
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

            // 优先用 BeatLeader 自己的实现, 保证音符顺序/链条切片与回放器完全一致.
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

            // 左手模式: 游戏会把谱面左右镜像 (轨道翻转、红蓝互换、切割方向镜像).
            // BeatLeader 用"时间 + 音符 ID"匹配事件, ID 来自镜像后的谱面, 所以这里也必须用镜像后的音符.
            // 做法和 BeatLeader 自己镜像音符的方式一致: CopyWith() 复制一份再 Mirror(4), 不改动原数据.
            if (leftHanded)
            {
                sortedNotes = sortedNotes.Select(MirrorNote).ToList();
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

            // NA(无箭头)模式: 所有方块当点块处理, 上下交替挥, 不看谱面里的切割方向
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
                // 链条头最高 85、链条切片最高 20, 不是每个块都是 115
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
                playerName = "Auto Play",
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
                // BeatLeader 里 speed != 0 会被当成"练习模式"设置(PracticeSettings), 回放就会以练习模式启动.
                // 变速由 modifiers 里的 FS/SF/SS 负责, 所以这里保持 0, 和正常打出来的回放一致.
                speed = 0f
            };

            replay.frames = frames;
            replay.notes = noteEvents;
            replay.walls = new List<WallEvent>();
            replay.heights = new List<AutomaticHeight> { new AutomaticHeight { height = AutoReplayMath.PlayerHeight, time = 0f } };
            replay.pauses = new List<Pause>();
            replay.saberOffsets = new SaberOffsets();
            // 浮水印: 标明这是 AutoPlay 生成的回放, 以及用的是哪个模型 (BeatLeader 会原样保存 customData)
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
}
