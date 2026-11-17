using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AutoReplay.Core;
using Newtonsoft.Json;
using UnityEngine;

namespace AutoReplay.Tools
{
    /// <summary>
    /// 遊戲外生成回放: 讀譜面 + 模型 -> MotionGenerator (和遊戲內同一份程式) -> 寫 .bsor.
    /// 生成的檔案可以直接放進 BeatLeader 的回放資料夾在遊戲裡看.
    /// </summary>
    public static class StandaloneGenerate
    {
        public static int Run(string[] a, Func<string[], string, string, string> opt)
        {
            string mapArg = a[1], difficulty = a[2], modelPath = a[3], outPath = a[4];
            string gameDir = opt(a, "--game", @"D:\Steam\steamapps\common\Beat Saber");
            bool leftHanded = Array.IndexOf(a, "--left") >= 0;
            bool noArrows = Array.IndexOf(a, "--na") >= 0;
            Plugin.Log.verbose = Array.IndexOf(a, "--verbose") >= 0;
            if (Array.IndexOf(a, "--limit-knee") >= 0) HumanModel.LimitKneeAtLimit = true;   // debug
            if (Array.IndexOf(a, "--soft-limit") >= 0) HumanModel.LimitKneeAtLimit = false;   // debug: 限位前就開始軟壓縮 (舊版)
            string lkb = opt(a, "--lim-below", null); if (lkb != null) HumanModel.LimitKneeBelow = float.Parse(lkb, System.Globalization.CultureInfo.InvariantCulture);   // debug
            string lov = opt(a, "--lim-over", null); if (lov != null) HumanModel.LimitOver = float.Parse(lov, System.Globalization.CultureInfo.InvariantCulture);   // debug
            string hcl = opt(a, "--head-clear", null); if (hcl != null) MotionGenerator.HeadClear = float.Parse(hcl, System.Globalization.CultureInfo.InvariantCulture);   // debug
            string hcs = opt(a, "--head-soft", null); if (hcs != null) MotionGenerator.HeadClearSoft = float.Parse(hcs, System.Globalization.CultureInfo.InvariantCulture);   // debug
            string ec = opt(a, "--eye-clear", null); if (ec != null) MotionGenerator.EyeClear = float.Parse(ec, System.Globalization.CultureInfo.InvariantCulture);   // debug
            string bsw = opt(a, "--bomb-sway", null); if (bsw != null) PoseCalculator.BombSway = float.Parse(bsw, System.Globalization.CultureInfo.InvariantCulture);   // debug
            string bcl = opt(a, "--bomb-clear", null); if (bcl != null) MotionGenerator.BombClear = float.Parse(bcl, System.Globalization.CultureInfo.InvariantCulture);   // debug
            string knee = opt(a, "--amp-knee", null);   // debug: 揮動幅度壓縮的起點 (度)
            if (knee != null) SwingTemplates.AmplitudeKnee = float.Parse(knee, System.Globalization.CultureInfo.InvariantCulture);
            string bd = opt(a, "--blend-delay", null);   // debug
            if (bd != null) NoteSwingPlanner.BlendDelay = float.Parse(bd, System.Globalization.CultureInfo.InvariantCulture);
            if (Array.IndexOf(a, "--bomb-up") >= 0) BombAvoider.PreferUp = true;   // debug
            string span = opt(a, "--bomb-span", null);   // debug: 一串炸彈最長跨度 (秒)
            if (span != null) BombAvoider.MaxClusterSpan = float.Parse(span, System.Globalization.CultureInfo.InvariantCulture);
            NoteSwingPlanner.DebugDots = Array.IndexOf(a, "--debug-dots") >= 0;
            string dbc = opt(a, "--dot-bomb-cost", null);   // debug: 點塊方向規劃裡炸彈的代價 (0 = 不看炸彈)
            if (dbc != null) NoteSwingPlanner.DotBombCost = float.Parse(dbc, System.Globalization.CultureInfo.InvariantCulture);
            string upm = opt(a, "--up-margin", null);
            if (upm != null) BombAvoider.UpMargin = float.Parse(upm, System.Globalization.CultureInfo.InvariantCulture);
            string safety = opt(a, "--bomb-safety", null);   // debug: 炸彈安全餘量 (米)
            if (safety != null) BombAvoider.Safety = float.Parse(safety, System.Globalization.CultureInfo.InvariantCulture);
            string armSmooth = opt(a, "--arm-smooth", null);   // debug: 手臂跟隨指向的平滑 (秒)
            if (armSmooth != null) PoseCalculator.ArmSmoothSigma = float.Parse(armSmooth, System.Globalization.CultureInfo.InvariantCulture);

            LoadModel(modelPath);
            if (Array.IndexOf(a, "--no-arm") >= 0) ModelManager.Current.arm = null;   // debug: 不用手臂模板
            string folder = MapLoader.FindFolder(gameDir, mapArg);
            var map = MapLoader.Load(folder, difficulty, opt(a, "--mode", "Standard"));
            foreach (var w in map.warnings) Console.WriteLine("[warn] " + w);
            string hash = Directory.Exists(mapArg) ? map.hash : mapArg.ToUpperInvariant();

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var notes = map.notes;
            var bombs = map.bombs;
            if (leftHanded)
            {
                notes = notes.Select(n => { var c = n.CopyWith(); c.Mirror(4); return c; }).ToList();
                bombs = bombs.Select(n => { var c = n.CopyWith(); c.Mirror(4); return c; }).ToList();
            }
            var allNotes = notes.Where(n => n.scoringType != NoteData.ScoringType.Ignore && n.gameplayType != NoteData.GameplayType.Bomb &&
                                            (n.colorType == ColorType.ColorA || n.colorType == ColorType.ColorB)).ToList();
            string orderFrom = opt(a, "--order-from", null);
            if (orderFrom != null)
            {
                // debug: 照另一份回放 (例如遊戲內生成的) 的方塊事件順序排列, 用來排查順序造成的差異
                var ev = BsorReader.Read(orderFrom).notes;
                var pool = allNotes.ToList(); var ordered = new List<NoteData>();
                foreach (var e in ev)
                {
                    int k = pool.FindIndex(n => Math.Abs(n.time - e.time) < 1e-3f && MotionGenerator.NoteId(n, noArrows) == e.id);
                    if (k >= 0) { ordered.Add(pool[k]); pool.RemoveAt(k); }
                }
                ordered.AddRange(pool);
                allNotes = ordered;
            }
            string jitter = opt(a, "--jitter", null);
            if (jitter != null)
            {
                // debug: 給方塊時間加極小的擾動, 找出對數值誤差敏感的地方
                var rnd = new Random(int.Parse(jitter));
                foreach (var n in allNotes) n.time += (float)(rnd.NextDouble() - 0.5) * 2e-5f;
            }
            float rt = MotionGenerator.ReactionTime(map.njs, map.bpm, map.offset, out float jd);
            var motion = MotionGenerator.Generate(allNotes, bombs, map.obstacles, map.njs, rt, leftHanded, noArrows);

            int score = 0, combo = 0;
            foreach (var n in allNotes)
            {
                combo++;
                int mult = combo >= 14 ? 8 : combo >= 6 ? 4 : combo >= 2 ? 2 : 1;
                score += MotionGenerator.MaxCutScore(n.scoringType) * mult;
            }

            using (var fs = File.Create(outPath))
            using (var w = new BinaryWriter(fs))
                WriteBsor(w, map, hash, difficulty, leftHanded, noArrows, score, allNotes, motion);

            Console.WriteLine($"{map.songName} [{difficulty}] v{map.formatVersion}  BPM {map.bpm}  NJS {map.njs}  offset {map.offset}  RT {rt * 1000f:F0} ms  JD {jd:F2}");
            Console.WriteLine($"方塊 {allNotes.Count}  炸彈 {bombs.Count}  牆 {map.obstacles.Count}  幀 {motion.count}  模型 {ModelManager.CurrentId}  身高 {AutoReplayMath.PlayerHeight:F2}  ({sw.ElapsedMilliseconds} ms)");
            Console.WriteLine($"已寫入 {outPath}");
            return 0;
        }

        static void LoadModel(string path)
        {
            if (Directory.Exists(path)) path = Path.Combine(path, "model.json");
            string json; ModelTrust trust;
            if (path.EndsWith(ApModelFile.Extension, StringComparison.OrdinalIgnoreCase)) json = ApModelFile.Unpack(File.ReadAllBytes(path), out trust);
            else { json = File.ReadAllText(path); trust = ModelTrust.Unpacked; }
            var m = JsonConvert.DeserializeObject<AutoPlayModel>(json);
            string err = m == null ? "模型文件是空的" : m.Validate();
            if (err != null) throw new InvalidDataException(err);
            ModelManager.Current = m;
            ModelManager.CurrentTrust = trust;
            ModelManager.CurrentFingerprint = ApModelFile.Fingerprint(json);
            string name = Path.GetFileName(path) == "model.json" ? Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(path))) + "/" : Path.GetFileName(path);
            ModelManager.CurrentId = name;
        }

        // ---------------- .bsor (和 BeatLeader ReplayEncoder 同一個格式) ----------------
        static void Str(BinaryWriter w, string s) { var b = Encoding.UTF8.GetBytes(s ?? ""); w.Write(b.Length); w.Write(b); }
        static void V(BinaryWriter w, Vector3 v) { w.Write(v.x); w.Write(v.y); w.Write(v.z); }
        static void Q(BinaryWriter w, Quaternion q) { w.Write(q.x); w.Write(q.y); w.Write(q.z); w.Write(q.w); }

        static void WriteBsor(BinaryWriter w, LoadedMap map, string hash, string difficulty, bool leftHanded, bool noArrows,
                              int score, List<NoteData> notes, MotionFrames m)
        {
            w.Write(0x442d3d69);
            w.Write((byte)1);

            w.Write((byte)0);   // info
            Str(w, "2.0.0"); Str(w, "1.40.8"); Str(w, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());
            Str(w, "Auto Play"); Str(w, "Auto Play"); Str(w, "steam");
            Str(w, "OpenVR"); Str(w, "Oculus"); Str(w, "Touch");
            Str(w, hash); Str(w, map.songName); Str(w, map.mapper); Str(w, difficulty);
            w.Write(score);
            Str(w, map.characteristic); Str(w, "DefaultEnvironment"); Str(w, noArrows ? "NA" : "");
            w.Write(0f); w.Write(leftHanded); w.Write(AutoReplayMath.PlayerHeight);
            w.Write(0f); w.Write(0f); w.Write(0f);

            w.Write((byte)1);   // frames
            w.Write((uint)m.count);
            for (int i = 0; i < m.count; i++)
            {
                w.Write(m.t0 + i * m.dt); w.Write(90);
                V(w, m.headPos[i]); Q(w, m.headRot[i]);
                V(w, m.leftPos[i]); Q(w, m.leftRot[i]);
                V(w, m.rightPos[i]); Q(w, m.rightRot[i]);
            }

            w.Write((byte)2);   // notes (全部 115 good, 和遊戲內 NoteEventBuilder 一樣)
            w.Write((uint)notes.Count);
            foreach (var n in notes)
            {
                w.Write(MotionGenerator.NoteId(n, noArrows)); w.Write(n.time); w.Write(n.time); w.Write(0);
                CutVectors(n.cutDirection, out var dir, out var normal);
                w.Write(true); w.Write(true); w.Write(true); w.Write(false);
                w.Write(IncorporateBool(42f, true));
                V(w, dir);
                w.Write((int)n.colorType);
                w.Write(0f); w.Write(0f);
                V(w, AutoReplayMath.GetNotePos(n)); V(w, normal);
                w.Write(0f); w.Write(0f); w.Write(1f); w.Write(1f);
            }

            w.Write((byte)3); w.Write(0u);                                              // walls
            w.Write((byte)4); w.Write(1u); w.Write(AutoReplayMath.PlayerHeight); w.Write(0f);  // heights
            w.Write((byte)5); w.Write(0u);                                              // pauses
            w.Write((byte)6);                                                           // saber offsets
            for (int k = 0; k < 2; k++) { V(w, Vector3.zero); Q(w, new Quaternion(0f, 0f, 0f, 0f)); }
            w.Write((byte)7);                                                           // custom data: 浮水印
            var mark = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new
            {
                generator = "AutoPlay-unofficial (standalone)",
                version = Plugin.Version,
                model = ModelManager.CurrentId,
                modelName = ModelManager.Current?.meta?.name,
                modelFingerprint = ModelManager.CurrentFingerprint,
                modelTrust = ModelManager.CurrentTrust.ToString(),
                generatedUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            }));
            w.Write(1); Str(w, "AutoPlay"); w.Write(mark.Length); w.Write(mark);
        }

        static float IncorporateBool(float v, bool flag)
        {
            int i = BitConverter.ToInt32(BitConverter.GetBytes(v), 0);
            i = flag ? i | 1 : i & ~1;
            return BitConverter.ToSingle(BitConverter.GetBytes(i), 0);
        }

        static void CutVectors(NoteCutDirection d, out Vector3 dir, out Vector3 normal)
        {
            const float s = 0.7071f;
            switch (d)
            {
                case NoteCutDirection.Up: dir = Vector3.up; normal = Vector3.left; break;
                case NoteCutDirection.Left: dir = Vector3.left; normal = Vector3.up; break;
                case NoteCutDirection.Right: dir = Vector3.right; normal = Vector3.down; break;
                case NoteCutDirection.UpLeft: dir = new Vector3(-s, s, 0f); normal = new Vector3(s, s, 0f); break;
                case NoteCutDirection.UpRight: dir = new Vector3(s, s, 0f); normal = new Vector3(s, -s, 0f); break;
                case NoteCutDirection.DownLeft: dir = new Vector3(-s, -s, 0f); normal = new Vector3(-s, s, 0f); break;
                case NoteCutDirection.DownRight: dir = new Vector3(s, -s, 0f); normal = new Vector3(-s, -s, 0f); break;
                default: dir = Vector3.down; normal = Vector3.right; break;
            }
        }
    }
}
