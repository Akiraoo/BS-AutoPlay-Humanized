using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace AutoReplay.Core
{
    /// <summary>訓練用的譜面資料: 只要牆、炸彈、NJS (時間已換成秒)</summary>
    public sealed class MapObjects
    {
        public float njs;
        public List<(float t, int x, int y)> bombs = new List<(float, int, int)>();
        public List<(float t0, float t1, int x, int y, int w, int h)> walls = new List<(float, float, int, int, int, int)>();
    }

    /// <summary>
    /// 訓練時讀譜面 (學躲牆 / 躲炸彈的習慣要知道牆和炸彈在哪). 和 tools/ApModelTool/Standalone/MapLoader 的解析規則一致, 只讀需要的部分.
    /// 找不到譜面或讀不了就回傳 null (那份回放只是不拿來學這幾項).
    /// </summary>
    public static class MapReader
    {
        static readonly object _lock = new object();
        static string _gameDir;
        static Dictionary<string, string> _byHash;
        static readonly Dictionary<string, MapObjects> _cache = new Dictionary<string, MapObjects>();

        public static MapObjects Load(string gameDir, string hash, string difficulty, string characteristic = "Standard")
        {
            if (string.IsNullOrEmpty(gameDir) || string.IsNullOrEmpty(hash)) return null;
            string key = hash.ToUpperInvariant() + "|" + characteristic + "|" + difficulty;
            lock (_lock)
                if (_cache.TryGetValue(key, out var c)) return c;
            MapObjects m = null;
            try
            {
                string folder = FindFolder(gameDir, hash);
                if (folder != null) m = Read(folder, difficulty, characteristic);
            }
            catch { m = null; }
            lock (_lock) _cache[key] = m;
            return m;
        }

        static string FindFolder(string gameDir, string hash)
        {
            lock (_lock)
            {
                if (_byHash == null || _gameDir != gameDir)
                {
                    _gameDir = gameDir;
                    _byHash = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    string cache = Path.Combine(gameDir, "UserData", "SongCore", "SongHashData.dat");
                    if (File.Exists(cache))
                    {
                        var o = JObject.Parse(File.ReadAllText(cache));
                        foreach (var p in o.Properties())
                        {
                            string h = (string)p.Value["songHash"];
                            if (string.IsNullOrEmpty(h)) continue;
                            string rel = p.Name.StartsWith(".\\") || p.Name.StartsWith("./") ? p.Name.Substring(2) : p.Name;
                            _byHash[h] = Path.IsPathRooted(rel) ? rel : Path.Combine(gameDir, rel);
                        }
                    }
                }
                return _byHash.TryGetValue(hash, out var f) && Directory.Exists(f) ? f : null;
            }
        }

        sealed class Bpm
        {
            readonly List<(float beat, float bpm, float time)> r = new List<(float, float, float)>();
            public Bpm(float bpm, IEnumerable<(float beat, float bpm)> changes)
            {
                r.Add((0f, bpm, 0f));
                foreach (var c in changes.Where(c => c.bpm > 0f).OrderBy(c => c.beat))
                {
                    var last = r[r.Count - 1];
                    if (c.beat <= last.beat) { r[r.Count - 1] = (last.beat, c.bpm, last.time); continue; }
                    r.Add((c.beat, c.bpm, last.time + (c.beat - last.beat) * 60f / last.bpm));
                }
            }
            public float Time(float beat)
            {
                int i = r.Count - 1;
                while (i > 0 && r[i].beat > beat) i--;
                return r[i].time + (beat - r[i].beat) * 60f / r[i].bpm;
            }
        }

        static MapObjects Read(string folder, string difficulty, string characteristic)
        {
            string infoPath = Path.Combine(folder, "Info.dat");
            if (!File.Exists(infoPath)) infoPath = Path.Combine(folder, "info.dat");
            var info = JObject.Parse(File.ReadAllText(infoPath).TrimStart('﻿'));
            var m = new MapObjects();
            string file = null; float bpm;
            if (info["version"] != null && ((string)info["version"]).StartsWith("4"))
            {
                bpm = (float?)info["audio"]?["bpm"] ?? 120f;
                foreach (var d in info["difficultyBeatmaps"] ?? new JArray())
                    if ((string)d["characteristic"] == characteristic && (string)d["difficulty"] == difficulty)
                    { file = (string)d["beatmapDataFilename"]; m.njs = (float?)d["noteJumpMovementSpeed"] ?? 0f; }
            }
            else
            {
                bpm = (float?)info["_beatsPerMinute"] ?? 120f;
                foreach (var s in info["_difficultyBeatmapSets"] ?? new JArray())
                    if ((string)s["_beatmapCharacteristicName"] == characteristic)
                        foreach (var d in s["_difficultyBeatmaps"])
                            if ((string)d["_difficulty"] == difficulty)
                            { file = (string)d["_beatmapFilename"]; m.njs = (float?)d["_noteJumpMovementSpeed"] ?? 0f; }
            }
            if (file == null || !File.Exists(Path.Combine(folder, file))) return null;
            if (m.njs <= 0f) m.njs = difficulty == "ExpertPlus" ? 16f : difficulty == "Expert" ? 12f : 10f;

            var d0 = JObject.Parse(File.ReadAllText(Path.Combine(folder, file)).TrimStart('﻿'));
            string ver = (string)d0["version"] ?? (string)d0["_version"] ?? "2.0.0";
            if (ver.StartsWith("4"))
            {
                var b = new Bpm(bpm, new (float, float)[0]);
                JToken At(string arr, JToken idx) { var a = d0[arr] as JArray; int i = (int?)idx ?? 0; return a != null && i >= 0 && i < a.Count ? a[i] : new JObject(); }
                foreach (var n in d0["bombNotes"] ?? new JArray())
                { var x = At("bombNotesData", n["i"]); m.bombs.Add((b.Time((float?)n["b"] ?? 0f), (int?)x["x"] ?? 0, (int?)x["y"] ?? 0)); }
                foreach (var o in d0["obstacles"] ?? new JArray())
                {
                    var x = At("obstaclesData", o["i"]); float bt = (float?)o["b"] ?? 0f, dur = (float?)x["d"] ?? 0f;
                    m.walls.Add((b.Time(bt), b.Time(bt + dur), (int?)x["x"] ?? 0, (int?)x["y"] ?? 0, (int?)x["w"] ?? 1, (int?)x["h"] ?? 5));
                }
            }
            else if (ver.StartsWith("3"))
            {
                var b = new Bpm(bpm, (d0["bpmEvents"] ?? new JArray()).Select(e => ((float?)e["b"] ?? 0f, (float?)e["m"] ?? 0f)));
                foreach (var n in d0["bombNotes"] ?? new JArray())
                    m.bombs.Add((b.Time((float?)n["b"] ?? 0f), (int?)n["x"] ?? 0, (int?)n["y"] ?? 0));
                foreach (var o in d0["obstacles"] ?? new JArray())
                {
                    float bt = (float?)o["b"] ?? 0f, dur = (float?)o["d"] ?? 0f;
                    m.walls.Add((b.Time(bt), b.Time(bt + dur), (int?)o["x"] ?? 0, (int?)o["y"] ?? 0, (int?)o["w"] ?? 1, (int?)o["h"] ?? 5));
                }
            }
            else
            {
                var ch = new List<(float, float)>();
                foreach (var e in d0["_events"] ?? new JArray())
                    if ((int?)e["_type"] == 100) ch.Add(((float?)e["_time"] ?? 0f, (float?)e["_floatValue"] ?? 0f));
                foreach (var e in d0["_BPMChanges"] ?? new JArray())
                    ch.Add(((float?)e["_time"] ?? 0f, (float?)e["_BPM"] ?? (float?)e["_bpm"] ?? 0f));
                var b = new Bpm(bpm, ch);
                foreach (var n in d0["_notes"] ?? new JArray())
                    if ((int?)n["_type"] == 3)
                        m.bombs.Add((b.Time((float?)n["_time"] ?? 0f), (int?)n["_lineIndex"] ?? 0, (int?)n["_lineLayer"] ?? 0));
                foreach (var o in d0["_obstacles"] ?? new JArray())
                {
                    float bt = (float?)o["_time"] ?? 0f, dur = (float?)o["_duration"] ?? 0f; int type = (int?)o["_type"] ?? 0;
                    m.walls.Add((b.Time(bt), b.Time(bt + dur), (int?)o["_lineIndex"] ?? 0, type == 1 ? 2 : 0, (int?)o["_width"] ?? 1, type == 1 ? 3 : 5));
                }
            }
            m.bombs.Sort((p, q) => p.t.CompareTo(q.t));
            m.walls.RemoveAll(w => w.w <= 0 || w.h <= 0);
            m.walls.Sort((p, q) => p.t0.CompareTo(q.t0));
            return m;
        }
    }
}
