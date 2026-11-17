using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace AutoReplay.Tools
{
    /// <summary>
    /// 游戏外读谱面: Info.dat + 难度文件 (v2 / v3 / v4), BPM 变化, 弧线 / 链条的计分类型.
    /// 结果和游戏 + BeatLeader 的 CreateSortedNoteDataList 对齐 (链条切片用同样的规则).
    /// </summary>
    public sealed class LoadedMap
    {
        public string folder, hash, songName = "", mapper = "", characteristic = "Standard", difficulty = "";
        public float bpm, njs, offset;
        public List<NoteData> notes = new List<NoteData>();      // 要切的方块 (含链条切片), 已按时间排序
        public List<NoteData> bombs = new List<NoteData>();
        public List<ObstacleData> obstacles = new List<ObstacleData>();
        public string formatVersion = "";
        public List<string> warnings = new List<string>();
    }

    public static class MapLoader
    {
        // ---------------- 找谱面: 资料夹路径, 或用 hash 查 SongCore 的快取 ----------------
        public static string FindFolder(string gameDir, string hashOrPath)
        {
            if (Directory.Exists(hashOrPath)) return hashOrPath;
            string cache = Path.Combine(gameDir, "UserData", "SongCore", "SongHashData.dat");
            if (File.Exists(cache))
            {
                var o = JObject.Parse(File.ReadAllText(cache));
                foreach (var p in o.Properties())
                    if (string.Equals((string)p.Value["songHash"], hashOrPath, StringComparison.OrdinalIgnoreCase))
                    {
                        string rel = p.Name.StartsWith(".\\") || p.Name.StartsWith("./") ? p.Name.Substring(2) : p.Name;
                        string full = Path.IsPathRooted(rel) ? rel : Path.Combine(gameDir, rel);
                        if (Directory.Exists(full)) return full;
                    }
            }
            // 快取里没有 (例如 CustomWIPLevels 的 WIP 谱): 逐个资料夹算 hash
            string want = hashOrPath.Replace(" WIP", "").Trim();
            foreach (var root in new[] { "CustomWIPLevels", "CustomLevels" })
            {
                string dir = Path.Combine(gameDir, "Beat Saber_Data", root);
                if (!Directory.Exists(dir)) continue;
                foreach (var d in Directory.GetDirectories(dir))
                {
                    try { if (string.Equals(ComputeHash(d), want, StringComparison.OrdinalIgnoreCase)) return d; }
                    catch { }
                }
            }
            throw new DirectoryNotFoundException("找不到譜面 (資料夾或 hash): " + hashOrPath);
        }

        /// <summary>谱面 hash = SHA1(Info.dat + 各难度文件, 按 Info 里列出的顺序), 和 SongCore 一样</summary>
        public static string ComputeHash(string folder)
        {
            string infoPath = Path.Combine(folder, "Info.dat");
            if (!File.Exists(infoPath)) infoPath = Path.Combine(folder, "info.dat");
            var infoBytes = File.ReadAllBytes(infoPath);
            var info = JObject.Parse(System.Text.Encoding.UTF8.GetString(infoBytes).TrimStart('﻿'));
            var files = new List<string>();
            if (info["difficultyBeatmaps"] is JArray v4)
            {
                foreach (var d in v4) { string f = (string)d["beatmapDataFilename"]; if (f != null && !files.Contains(f)) files.Add(f); }
            }
            else
            {
                foreach (var s in info["_difficultyBeatmapSets"] ?? new JArray())
                    foreach (var d in s["_difficultyBeatmaps"]) files.Add((string)d["_beatmapFilename"]);
            }
            using (var sha = System.Security.Cryptography.SHA1.Create())
            using (var ms = new MemoryStream())
            {
                ms.Write(infoBytes, 0, infoBytes.Length);
                foreach (var f in files)
                {
                    string p = Path.Combine(folder, f);
                    if (File.Exists(p)) { var b = File.ReadAllBytes(p); ms.Write(b, 0, b.Length); }
                }
                return BitConverter.ToString(sha.ComputeHash(ms.ToArray())).Replace("-", "");
            }
        }

        sealed class BpmMap
        {
            readonly List<(float beat, float bpm, float time)> regions = new List<(float, float, float)>();
            public BpmMap(float bpm, IEnumerable<(float beat, float bpm)> changes)
            {
                regions.Add((0f, bpm, 0f));
                foreach (var c in changes.Where(c => c.bpm > 0f).OrderBy(c => c.beat))
                {
                    var last = regions[regions.Count - 1];
                    if (c.beat <= last.beat) { regions[regions.Count - 1] = (last.beat, c.bpm, last.time); continue; }
                    float t = last.time + (c.beat - last.beat) * 60f / last.bpm;
                    regions.Add((c.beat, c.bpm, t));
                }
            }
            public float Time(float beat)
            {
                int i = regions.Count - 1;
                while (i > 0 && regions[i].beat > beat) i--;
                var r = regions[i];
                return r.time + (beat - r.beat) * 60f / r.bpm;
            }
        }

        public static LoadedMap Load(string folder, string difficulty, string characteristic = "Standard")
        {
            var map = new LoadedMap { folder = folder, difficulty = difficulty, characteristic = characteristic };
            string infoPath = Path.Combine(folder, "Info.dat");
            if (!File.Exists(infoPath)) infoPath = Path.Combine(folder, "info.dat");
            var infoBytes = File.ReadAllBytes(infoPath);
            var info = JObject.Parse(System.Text.Encoding.UTF8.GetString(infoBytes).TrimStart('﻿'));

            string diffFile = null;
            var hashFiles = new List<string>();
            if (info["version"] != null && ((string)info["version"]).StartsWith("4"))
            {
                map.songName = (string)info["song"]?["title"] ?? "";
                map.mapper = string.Join(", ", (info["difficultyBeatmaps"]?.FirstOrDefault()?["beatmapAuthors"]?["mappers"] as JArray)?.Select(x => (string)x) ?? new string[0]);
                map.bpm = (float?)info["audio"]?["bpm"] ?? 120f;
                foreach (var d in info["difficultyBeatmaps"])
                {
                    string f = (string)d["beatmapDataFilename"];
                    if (!hashFiles.Contains(f)) hashFiles.Add(f);
                    if ((string)d["characteristic"] == characteristic && (string)d["difficulty"] == difficulty)
                    {
                        diffFile = f;
                        map.njs = (float?)d["noteJumpMovementSpeed"] ?? 0f;
                        map.offset = (float?)d["noteJumpStartBeatOffset"] ?? 0f;
                    }
                }
            }
            else
            {
                map.songName = (string)info["_songName"] ?? "";
                map.mapper = (string)info["_levelAuthorName"] ?? "";
                map.bpm = (float?)info["_beatsPerMinute"] ?? 120f;
                foreach (var s in info["_difficultyBeatmapSets"])
                    foreach (var d in s["_difficultyBeatmaps"])
                    {
                        string f = (string)d["_beatmapFilename"];
                        hashFiles.Add(f);
                        if ((string)s["_beatmapCharacteristicName"] == characteristic && (string)d["_difficulty"] == difficulty)
                        {
                            diffFile = f;
                            map.njs = (float?)d["_noteJumpMovementSpeed"] ?? 0f;
                            map.offset = (float?)d["_noteJumpStartBeatOffset"] ?? 0f;
                        }
                    }
            }
            if (diffFile == null)
            {
                // 没指定模式又没有 Standard: 改用唯一有这个难度的模式 (例如 OneSaber 谱)
                var modes = info["difficultyBeatmaps"] is JArray v4
                    ? v4.Where(d => (string)d["difficulty"] == difficulty).Select(d => (string)d["characteristic"]).Distinct().ToList()
                    : (info["_difficultyBeatmapSets"] ?? new JArray()).Where(s => s["_difficultyBeatmaps"].Any(d => (string)d["_difficulty"] == difficulty))
                                                                   .Select(s => (string)s["_beatmapCharacteristicName"]).ToList();
                if (characteristic == "Standard" && modes.Count == 1)
                {
                    var m2 = Load(folder, difficulty, modes[0]);
                    m2.warnings.Add($"沒有 Standard 模式, 改用 {modes[0]}");
                    return m2;
                }
                throw new InvalidOperationException($"譜面沒有 {characteristic} {difficulty} 難度 (有: {string.Join(", ", modes)})");
            }
            if (map.njs <= 0f) map.njs = MotionGenerator.DefaultNjs(difficulty);
            map.hash = ComputeHash(folder);

            var diff = JObject.Parse(File.ReadAllText(Path.Combine(folder, diffFile)).TrimStart('﻿'));
            string ver = (string)diff["version"] ?? (string)diff["_version"] ?? "2.0.0";
            map.formatVersion = ver;
            if (ver.StartsWith("4")) LoadV4(map, diff);
            else if (ver.StartsWith("3")) LoadV3(map, diff);
            else LoadV2(map, diff);

            map.notes.Sort((a, b) => a.time.CompareTo(b.time));
            SnapStackAngles(map.notes);
            map.bombs.Sort((a, b) => a.time.CompareTo(b.time));
            map.obstacles.Sort((a, b) => a.time.CompareTo(b.time));
            return map;
        }

        static NoteLineLayer Layer(int y) => (NoteLineLayer)y;

        // ---------------- v2 ----------------
        static void LoadV2(LoadedMap map, JObject d)
        {
            var bpmChanges = new List<(float, float)>();
            foreach (var e in d["_events"] ?? new JArray())
                if ((int?)e["_type"] == 100) bpmChanges.Add(((float)e["_time"], (float?)e["_floatValue"] ?? 0f));
            foreach (var e in d["_BPMChanges"] ?? new JArray())
                bpmChanges.Add(((float)e["_time"], (float?)e["_BPM"] ?? (float?)e["_bpm"] ?? 0f));
            var bpm = new BpmMap(map.bpm, bpmChanges);

            var color = new List<NoteData>();
            foreach (var n in d["_notes"] ?? new JArray())
            {
                int type = (int)n["_type"];
                float beat = (float)n["_time"];
                var nd = new NoteData
                {
                    beat = beat, time = bpm.Time(beat),
                    lineIndex = (int)n["_lineIndex"], noteLineLayer = Layer((int)n["_lineLayer"]),
                    cutDirection = (NoteCutDirection)((int?)n["_cutDirection"] ?? 1),
                };
                if (type == 3)
                {
                    nd.colorType = ColorType.None; nd.gameplayType = NoteData.GameplayType.Bomb; nd.scoringType = NoteData.ScoringType.NoScore;
                    nd.cutDirection = NoteCutDirection.None;
                    map.bombs.Add(nd);
                }
                else if (type == 0 || type == 1)
                {
                    nd.colorType = type == 0 ? ColorType.ColorA : ColorType.ColorB;
                    color.Add(nd);
                }
            }
            // v2.6 的 _sliders (弧线)
            var arcs = new List<(float hb, int hx, int hy, int c, float tb, int tx, int ty)>();
            foreach (var s in d["_sliders"] ?? new JArray())
                arcs.Add(((float)s["_headTime"], (int)s["_headLineIndex"], (int)s["_headLineLayer"], (int)s["_colorType"],
                          (float)s["_tailTime"], (int)s["_tailLineIndex"], (int)s["_tailLineLayer"]));
            ApplyArcs(color, arcs);
            map.notes.AddRange(color);

            foreach (var o in d["_obstacles"] ?? new JArray())
            {
                float b = (float)o["_time"], dur = (float)o["_duration"];
                int type = (int?)o["_type"] ?? 0;
                float t0 = bpm.Time(b), t1 = bpm.Time(b + dur);
                map.obstacles.Add(new ObstacleData
                {
                    time = t0, duration = t1 - t0, lineIndex = (int)o["_lineIndex"], width = (int)o["_width"],
                    lineLayer = type == 1 ? NoteLineLayer.Top : NoteLineLayer.Base, height = type == 1 ? 3 : 5,
                });
            }
        }

        // ---------------- v3 ----------------
        static void LoadV3(LoadedMap map, JObject d)
        {
            var bpm = new BpmMap(map.bpm, (d["bpmEvents"] ?? new JArray()).Select(e => ((float?)e["b"] ?? 0f, (float?)e["m"] ?? 0f)));
            var color = new List<NoteData>();
            foreach (var n in d["colorNotes"] ?? new JArray())
            {
                float beat = (float?)n["b"] ?? 0f;
                color.Add(new NoteData
                {
                    beat = beat, time = bpm.Time(beat),
                    lineIndex = (int?)n["x"] ?? 0, noteLineLayer = Layer((int?)n["y"] ?? 0),
                    colorType = ((int?)n["c"] ?? 0) == 0 ? ColorType.ColorA : ColorType.ColorB,
                    cutDirection = (NoteCutDirection)((int?)n["d"] ?? 0),
                    cutDirectionAngleOffset = (float?)n["a"] ?? 0f,
                });
            }
            foreach (var n in d["bombNotes"] ?? new JArray())
            {
                float beat = (float?)n["b"] ?? 0f;
                map.bombs.Add(new NoteData
                {
                    beat = beat, time = bpm.Time(beat), lineIndex = (int?)n["x"] ?? 0, noteLineLayer = Layer((int?)n["y"] ?? 0),
                    colorType = ColorType.None, cutDirection = NoteCutDirection.None,
                    gameplayType = NoteData.GameplayType.Bomb, scoringType = NoteData.ScoringType.NoScore,
                });
            }
            var arcs = (d["sliders"] ?? new JArray()).Select(s => ((float?)s["b"] ?? 0f, (int?)s["x"] ?? 0, (int?)s["y"] ?? 0, (int?)s["c"] ?? 0,
                                                                  (float?)s["tb"] ?? 0f, (int?)s["tx"] ?? 0, (int?)s["ty"] ?? 0)).ToList();
            var chains = (d["burstSliders"] ?? new JArray()).Select(s => (hb: (float?)s["b"] ?? 0f, hx: (int?)s["x"] ?? 0, hy: (int?)s["y"] ?? 0, c: (int?)s["c"] ?? 0,
                                                                          tb: (float?)s["tb"] ?? 0f, tx: (int?)s["tx"] ?? 0, ty: (int?)s["ty"] ?? 0, sc: (int?)s["sc"] ?? 2)).ToList();
            AddChains(map, color, chains, bpm);
            ApplyArcs(color, arcs);
            map.notes.AddRange(color);

            foreach (var o in d["obstacles"] ?? new JArray())
            {
                float b = (float?)o["b"] ?? 0f, dur = (float?)o["d"] ?? 0f;
                float t0 = bpm.Time(b), t1 = bpm.Time(b + dur);
                map.obstacles.Add(new ObstacleData
                {
                    time = t0, duration = t1 - t0, lineIndex = (int?)o["x"] ?? 0, lineLayer = Layer((int?)o["y"] ?? 0),
                    width = (int?)o["w"] ?? 1, height = (int?)o["h"] ?? 5,
                });
            }
        }

        // ---------------- v4 (对象和数据分开存, 用索引对应) ----------------
        static void LoadV4(LoadedMap map, JObject d)
        {
            map.warnings.Add("v4 譜面: BPM 變化存在另外的音訊檔, 目前只用 Info 的固定 BPM");
            var bpm = new BpmMap(map.bpm, new (float, float)[0]);
            JToken At(string arr, JToken idx) { var a = d[arr] as JArray; int i = (int?)idx ?? 0; return a != null && i >= 0 && i < a.Count ? a[i] : new JObject(); }
            var color = new List<NoteData>();
            foreach (var n in d["colorNotes"] ?? new JArray())
            {
                var x = At("colorNotesData", n["i"]);
                float beat = (float?)n["b"] ?? 0f;
                color.Add(new NoteData
                {
                    beat = beat, time = bpm.Time(beat), lineIndex = (int?)x["x"] ?? 0, noteLineLayer = Layer((int?)x["y"] ?? 0),
                    colorType = ((int?)x["c"] ?? 0) == 0 ? ColorType.ColorA : ColorType.ColorB,
                    cutDirection = (NoteCutDirection)((int?)x["d"] ?? 0), cutDirectionAngleOffset = (float?)x["a"] ?? 0f,
                });
            }
            foreach (var n in d["bombNotes"] ?? new JArray())
            {
                var x = At("bombNotesData", n["i"]);
                float beat = (float?)n["b"] ?? 0f;
                map.bombs.Add(new NoteData
                {
                    beat = beat, time = bpm.Time(beat), lineIndex = (int?)x["x"] ?? 0, noteLineLayer = Layer((int?)x["y"] ?? 0),
                    colorType = ColorType.None, cutDirection = NoteCutDirection.None,
                    gameplayType = NoteData.GameplayType.Bomb, scoringType = NoteData.ScoringType.NoScore,
                });
            }
            var arcs = new List<(float, int, int, int, float, int, int)>();
            foreach (var a in d["arcs"] ?? new JArray())
            {
                var x = At("arcsData", a["ai"]); var h = At("colorNotesData", a["hi"]); var t = At("colorNotesData", a["ti"]);
                arcs.Add(((float?)a["hb"] ?? 0f, (int?)h["x"] ?? 0, (int?)h["y"] ?? 0, (int?)h["c"] ?? 0, (float?)a["tb"] ?? 0f, (int?)t["x"] ?? 0, (int?)t["y"] ?? 0));
            }
            var chains = new List<(float hb, int hx, int hy, int c, float tb, int tx, int ty, int sc)>();
            foreach (var c in d["chains"] ?? new JArray())
            {
                var x = At("chainsData", c["ci"]); var h = At("colorNotesData", c["i"]);
                chains.Add(((float?)c["hb"] ?? 0f, (int?)h["x"] ?? 0, (int?)h["y"] ?? 0, (int?)h["c"] ?? 0,
                            (float?)c["tb"] ?? 0f, (int?)x["tx"] ?? 0, (int?)x["ty"] ?? 0, (int?)x["c"] ?? 2));
            }
            AddChains(map, color, chains, bpm);
            ApplyArcs(color, arcs);
            map.notes.AddRange(color);
            foreach (var o in d["obstacles"] ?? new JArray())
            {
                var x = At("obstaclesData", o["i"]);
                float b = (float?)o["b"] ?? 0f, dur = (float?)x["d"] ?? 0f;
                float t0 = bpm.Time(b), t1 = bpm.Time(b + dur);
                map.obstacles.Add(new ObstacleData
                {
                    time = t0, duration = t1 - t0, lineIndex = (int?)x["x"] ?? 0, lineLayer = Layer((int?)x["y"] ?? 0),
                    width = (int?)x["w"] ?? 1, height = (int?)x["h"] ?? 5,
                });
            }
        }

        /// <summary>
        /// 游戏的"同排方块"处理: 同一时间、同颜色、同方向的两个箭头块, 连线和箭头方向接近时,
        /// 两个箭头都转到沿连线的方向, 存在 cutDirectionAngleOffset (逆时针为正, 用格子单位算连线).
        /// 对照游戏内生成的 Break Me (V2): 18.4° 的斜向叠块完全一致. 门槛 40° 是印象中的游戏数值, 18.4°~90° 之间还没有实测确认.
        /// </summary>
        const float SnapMaxDeg = 40f;

        static void SnapStackAngles(List<NoteData> notes)
        {
            for (int i = 0; i < notes.Count;)
            {
                int e = i;
                while (e + 1 < notes.Count && Math.Abs(notes[e + 1].time - notes[i].time) < 1e-4f) e++;
                foreach (var color in new[] { ColorType.ColorA, ColorType.ColorB })
                {
                    var row = new List<NoteData>();
                    for (int k = i; k <= e; k++)
                        if (notes[k].colorType == color && notes[k].gameplayType != NoteData.GameplayType.BurstSliderElement) row.Add(notes[k]);
                    if (row.Count != 2) continue;
                    var a = row[0]; var b = row[1];
                    if (a.cutDirection != b.cutDirection || a.cutDirection == NoteCutDirection.Any || a.cutDirection == NoteCutDirection.None) continue;
                    float dx = b.lineIndex - a.lineIndex, dy = (int)b.noteLineLayer - (int)a.noteLineLayer;
                    // 顺时针角度 (0=上, 90=右), 和 AutoReplayMath.GetCutAngle 相同; 先去掉谱面原有的偏移
                    float cut = AutoReplayMath.GetCutAngle(a) + a.cutDirectionAngleOffset;
                    float line = (float)(Math.Atan2(dx, dy) * 180.0 / Math.PI);
                    float d = DeltaDeg(cut, line);
                    if (Math.Abs(d) > 90f) d = DeltaDeg(cut, line + 180f);    // 连线方向取和箭头同向的那一端
                    if (Math.Abs(d) > SnapMaxDeg) continue;
                    a.cutDirectionAngleOffset = -d;                            // 逆时针为正
                    b.cutDirectionAngleOffset = -d;
                }
                i = e + 1;
            }
        }

        static float DeltaDeg(float from, float to)
        {
            float d = (to - from) % 360f;
            if (d > 180f) d -= 360f;
            if (d < -180f) d += 360f;
            return d;
        }

        static bool Same(NoteData n, float beat, int x, int y, int c) =>
            Math.Abs(n.beat - beat) < 1e-3f && n.lineIndex == x && (int)n.noteLineLayer == y && (int)n.colorType == c;

        /// <summary>
        /// 链条: 头方块变成 ChainHead; 切片和 BeatLeader 的 CreateSortedNoteDataList 一样产生
        /// (i = 1..sc-1, 时间在头尾之间线性分布, 位置除最后一片外用头的位置, 方向 Any, 计分 ChainLink)
        /// </summary>
        static void AddChains(LoadedMap map, List<NoteData> color, List<(float hb, int hx, int hy, int c, float tb, int tx, int ty, int sc)> chains, BpmMap bpm)
        {
            foreach (var ch in chains)
            {
                var head = color.FirstOrDefault(n => Same(n, ch.hb, ch.hx, ch.hy, ch.c));
                if (head != null)
                {
                    head.gameplayType = NoteData.GameplayType.BurstSliderHead;
                    head.scoringType = NoteData.ScoringType.ChainHead;
                }
                float t0 = bpm.Time(ch.hb), t1 = bpm.Time(ch.tb);
                int sc = Math.Max(2, ch.sc);
                for (int i = 1; i < sc; i++)
                {
                    color.Add(new NoteData
                    {
                        beat = ch.hb, time = t0 + (t1 - t0) * i / (sc - 1),
                        lineIndex = i < sc - 1 ? ch.hx : ch.tx, noteLineLayer = Layer(i < sc - 1 ? ch.hy : ch.ty),
                        colorType = ch.c == 0 ? ColorType.ColorA : ColorType.ColorB, cutDirection = NoteCutDirection.Any,
                        gameplayType = NoteData.GameplayType.BurstSliderElement, scoringType = NoteData.ScoringType.ChainLink,
                    });
                }
            }
        }

        /// <summary>弧线: 头 / 尾方块的计分类型 (同时是链条头时组合成 ChainHeadArcTail 等)</summary>
        static void ApplyArcs(List<NoteData> color, List<(float hb, int hx, int hy, int c, float tb, int tx, int ty)> arcs)
        {
            foreach (var a in arcs)
            {
                var head = color.FirstOrDefault(n => n.gameplayType != NoteData.GameplayType.BurstSliderElement && Same(n, a.hb, a.hx, a.hy, a.c));
                // 长度为 0 的弧线 (头尾同一拍): 游戏只标记头, 尾端的方块不算弧线尾 (对照 LESSON - HARPY HARE 的游戏内结果)
                var tail = a.tb <= a.hb + 1e-3f ? null
                         : color.FirstOrDefault(n => n.gameplayType != NoteData.GameplayType.BurstSliderElement && Same(n, a.tb, a.tx, a.ty, a.c));
                if (head != null)
                {
                    switch (head.scoringType)
                    {
                        case NoteData.ScoringType.Normal: head.scoringType = NoteData.ScoringType.ArcHead; break;
                        case NoteData.ScoringType.ArcTail: head.scoringType = NoteData.ScoringType.ArcHeadArcTail; break;
                        case NoteData.ScoringType.ChainLink: head.scoringType = NoteData.ScoringType.ChainLinkArcHead; break;
                    }
                }
                if (tail != null)
                {
                    switch (tail.scoringType)
                    {
                        case NoteData.ScoringType.Normal: tail.scoringType = NoteData.ScoringType.ArcTail; break;
                        case NoteData.ScoringType.ArcHead: tail.scoringType = NoteData.ScoringType.ArcHeadArcTail; break;
                        case NoteData.ScoringType.ChainHead: tail.scoringType = NoteData.ScoringType.ChainHeadArcTail; break;
                    }
                }
            }
        }
    }
}
