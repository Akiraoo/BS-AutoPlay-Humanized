using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace AutoReplay.Core
{
    public sealed class TrainOptions
    {
        public SelectionOptions selection = new SelectionOptions();
        public int minReplays = 10;          // 少于这个数就不训练
        public string modelName = "my-model";
        public string author = "";
        public StarService stars;            // null = 不查星数
        public bool network = true;          // 允许连网查星数
        public string modVersion = "";
        public string mode = "manual";       // manual / auto
        public int rollingDays;              // auto 模式的滚动天数
        public Action<string> progress;
        public CancellationToken cancel;
    }

    /// <summary>
    /// 从本机 BeatLeader replay 训练 AutoPlay 模型. 算法和 tools/ 下的 Python 脚本一致:
    ///   wrist_fit.py (手的位置/扭转/限位), tech_templates.py (按转向角度分组的挥刀模板), roll_offset_templates.py (反手扭转)
    /// 纯 C#, 不依赖 Unity, 可以在后台线程跑.
    /// </summary>
    public static class ModelTrainer
    {
        public const string TrainerVersion = "AutoPlay-trainer/1";

        // ---- 和 Python 版一致的常量 ----
        const double MergeGap = 0.06;                                    // 0.06s 内的切块算同一刀
        static readonly double[] GapBins = { 0.10, 0.18, 0.26, 0.36, 0.50, 0.75, 1.20, 3.0 };
        static readonly double[] RelEdges = { 30, 67.5, 112.5, 157.5 };
        const double TauStart = -0.70, TauStepFine = 0.01; const int TauFine = 111;   // -0.70..0.40, 输出隔一个取 -> 56
        const double RollTauStart = -0.20, RollTauStep = 0.02; const int RollTau = 21;

        sealed class Row { public int rel, dir; public double gap; public float[] alpha, beta; }

        public static AutoPlayModel Train(TrainOptions o, out string report)
        {
            var log = new System.Text.StringBuilder();
            void P(string s) { log.AppendLine(s); o.progress?.Invoke(s); }

            P("掃描回放…");
            var sel = ReplaySelector.Select(o.selection, o.stars, o.network, P, o.cancel);
            P(sel.Summary());
            var files = sel.picked.Select(e => e.path).ToList();
            var entryOf = sel.picked.ToDictionary(e => e.path);
            if (files.Count < o.minReplays)
                throw new InvalidOperationException($"符合條件的 replay 只有 {files.Count} 份, 至少需要 {o.minReplays} 份 (可以放寬日期範圍)");

            // ---------- 第 1 轮: 手部模型 + 挥刀模板 ----------
            var hand = new HandAcc[] { new HandAcc(), new HandAcc() };
            var rows = new List<Row>();
            var used = new List<string>();
            var npsList = new List<double>();
            int done = 0;
            foreach (var f in files)
            {
                o.cancel.ThrowIfCancellationRequested();
                P($"[1/2] 分析 {++done}/{files.Count}: {Path.GetFileName(f)}");
                BsorReplay rep;
                try { rep = BsorReader.Read(f); } catch (Exception ex) { P($"   跳過 (讀取失敗: {ex.Message})"); continue; }
                double nps = Nps(rep);
                if (nps < MinNps) { P($"   跳過 (太簡單: {nps:F1} NPS)"); continue; }
                var fr = Prepare(rep);
                if (fr == null) continue;
                AccumulateHands(fr, rep, hand);
                CollectSwings(fr, rep, rows);
                used.Add(f);
                npsList.Add(nps);
            }
            files = used;
            if (files.Count < o.minReplays)
                throw new InvalidOperationException($"可用的 replay 只有 {files.Count} 份, 至少需要 {o.minReplays} 份");

            var model = new AutoPlayModel();
            model.left = hand[0].Solve();
            model.right = hand[1].Solve();
            int minCount = Math.Max(30, Math.Min(80, rows.Count / 500));
            model.templates = BuildTemplates(rows, minCount);
            P($"挥刀样本 {rows.Count} 次, 模板 {model.templates.groups.Count} 组 (每组至少 {minCount} 次)");

            // ---------- 第 2 轮: 反手扭转偏移 (要用到刚训练好的扭转模型) ----------
            var roll = new RollAcc();
            done = 0;
            foreach (var f in files)
            {
                o.cancel.ThrowIfCancellationRequested();
                P($"[2/2] 反手扭转 {++done}/{files.Count}");
                BsorReplay rep;
                try { rep = BsorReader.Read(f); } catch { continue; }
                var fr = Prepare(rep);
                if (fr == null) continue;
                roll.Accumulate(fr, rep, model);
            }
            model.rollOffsets = roll.Solve();

            model.meta = new ModelMeta
            {
                name = o.modelName,
                author = o.author ?? "",
                createdUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                trainer = TrainerVersion,
                replayCount = files.Count,
                swingCount = rows.Count,
                training = BuildInfo(o, sel, files.Select(f => entryOf[f]).ToList(), npsList, rows, P),
            };
            string err = model.Validate();
            if (err != null) throw new InvalidOperationException("訓練結果不完整: " + err);
            P($"完成: {files.Count} 份 replay, {rows.Count} 次挥刀");
            report = log.ToString();
            return model;
        }

        const double MinNps = 1.5;   // 平均每秒音符数低于这个的回放太简单, 挥刀不像平常

        static double Nps(BsorReplay rep)
        {
            float t0 = float.MaxValue, t1 = float.MinValue; int n = 0;
            foreach (var x in rep.notes) if (x.type != 3) { n++; if (x.time < t0) t0 = x.time; if (x.time > t1) t1 = x.time; }
            return n < 2 || t1 <= t0 ? 0 : n / (t1 - t0);
        }

        // =====================================================================
        //  模型资讯: 训练资料的统计 (不含玩家 ID)
        // =====================================================================
        static TrainingInfo BuildInfo(TrainOptions o, SelectionResult sel, List<ReplayEntry> used, List<double> nps, List<Row> rows, Action<string> P)
        {
            var so = o.selection;
            string Most(IEnumerable<string> xs) => xs.Where(x => !string.IsNullOrEmpty(x)).GroupBy(x => x).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault() ?? "";
            string All(IEnumerable<string> xs) => string.Join(", ", xs.Where(x => !string.IsNullOrEmpty(x)).GroupBy(x => x).OrderByDescending(g => g.Count()).Select(g => g.Key).Take(4));
            var ti = new TrainingInfo
            {
                modVersion = o.modVersion ?? "",
                playerName = Most(used.Select(e => e.info.playerName)),
                platform = Most(used.Select(e => e.info.platform)),
                hmd = CleanHmd(Most(used.Select(e => e.info.hmd))),
                gameVersions = All(used.Select(e => e.info.gameVersion)),
                beatLeaderVersions = All(used.Select(e => e.info.modVersion)),
                mode = o.mode ?? "manual",
                rollingDays = o.rollingDays,
                rangeFrom = so.fromUtc?.ToString("yyyy-MM-dd") ?? "",
                rangeTo = so.toUtc?.ToString("yyyy-MM-dd") ?? "",
                perMapCap = so.perMapCap,
                maxReplays = so.maxReplays,
                minStars = so.minStars,
                selection = sel.Summary(),
                dataFrom = used.Count > 0 ? used.Min(e => e.timeUtc).ToString("yyyy-MM-dd") : "",
                dataTo = used.Count > 0 ? used.Max(e => e.timeUtc).ToString("yyyy-MM-dd") : "",
                uniqueMaps = used.Select(e => (e.info.hash ?? "").ToUpperInvariant()).Distinct().Count(),
                npsMedian = (float)Pct(nps, 0.5),
                npsP90 = (float)Pct(nps, 0.9),
                techShare = rows.Count > 0 ? rows.Count(r => r.rel <= 2) / (float)rows.Count : 0f,   // 和上一刀转向 <= 90 度
            };

            if (o.stars != null)
            {
                int k = 0;
                foreach (var e in used)
                {
                    o.cancel.ThrowIfCancellationRequested();
                    if (e.stars == null || !e.stars.blChecked || !e.stars.ssChecked)
                        e.stars = o.stars.Get(e.info.hash, e.info.difficulty, o.network, o.cancel);
                    if (++k % 10 == 0) P($"查詢星數 {k}/{used.Count}" + (o.stars.Offline ? " (離線, 只用快取)" : ""));
                }
                o.stars.Save();
                var st = used.Select(e => e.stars).Where(x => x != null).ToList();
                ti.blStars = Stats(st.Select(x => x.blStars));
                ti.blTech = Stats(st.Select(x => x.blTech));
                ti.blPass = Stats(st.Select(x => x.blPass));
                ti.blAcc = Stats(st.Select(x => x.blAcc));
                var ss = st.Where(x => x.ssRanked && x.ssStars != null).Select(x => (double)x.ssStars.Value).ToList();
                ti.ssRankedCount = ss.Count;
                ti.ssStarsMedian = (float)Pct(ss, 0.5);
            }
            return ti;
        }

        // BL 的 hmd 字段有时是 ""Oculus""""Oculus Quest2"" 这种拼接, 取最后一段
        static string CleanHmd(string h)
        {
            if (string.IsNullOrEmpty(h)) return "";
            var parts = h.Split('"');
            for (int i = parts.Length - 1; i >= 0; i--) if (parts[i].Trim().Length > 0) return parts[i].Trim();
            return h;
        }

        static StarStats Stats(IEnumerable<float?> xs)
        {
            var v = xs.Where(x => x != null && x > 0).Select(x => (double)x.Value).ToList();
            if (v.Count == 0) return null;
            return new StarStats { count = v.Count, median = (float)Pct(v, 0.5), p10 = (float)Pct(v, 0.1), p90 = (float)Pct(v, 0.9) };
        }

        static double Pct(List<double> v, double q)
        {
            if (v.Count == 0) return 0;
            var s = v.OrderBy(x => x).ToList();
            double pos = q * (s.Count - 1); int i = (int)Math.Floor(pos);
            return i + 1 < s.Count ? s[i] + (s[i + 1] - s[i]) * (pos - i) : s[i];
        }

        // =====================================================================
        //  帧预处理: 去掉时间不递增的帧, 按头的平均朝向把房间偏航转正
        // =====================================================================
        sealed class Frames
        {
            public double[] t;
            public V3[] headPos;
            public V3[][] pos = new V3[2][];
            public Q4[][] rot = new Q4[2][];
            public V3[][] fwd = new V3[2][];
        }

        static Frames Prepare(BsorReplay rep)
        {
            int n = rep.time?.Length ?? 0;
            if (n < 100) return null;
            var keep = new List<int>(n) { 0 };
            for (int i = 1; i < n; i++) if (rep.time[i] > rep.time[i - 1]) keep.Add(i);
            int m = keep.Count;

            double sx = 0, sz = 0;
            foreach (int i in keep) { var hf = Q4.From(rep.head[i]).Rotate(V3.Z); sx += hf.x; sz += hf.z; }
            double psi = Math.Atan2(sx / m, sz / m);
            double c = Math.Cos(-psi), s = Math.Sin(-psi);
            var yaw = new Q4(0, Math.Sin(-psi / 2), 0, Math.Cos(-psi / 2));
            V3 RY(V3 v) => new V3(c * v.x + s * v.z, v.y, -s * v.x + c * v.z);

            var fr = new Frames { t = new double[m], headPos = new V3[m] };
            for (int h = 0; h < 2; h++) { fr.pos[h] = new V3[m]; fr.rot[h] = new Q4[m]; fr.fwd[h] = new V3[m]; }
            for (int k = 0; k < m; k++)
            {
                int i = keep[k];
                fr.t[k] = rep.time[i];
                fr.headPos[k] = RY(V3.From(rep.head[i]));
                for (int h = 0; h < 2; h++)
                {
                    float[] src = h == 0 ? rep.left[i] : rep.right[i];
                    fr.pos[h][k] = RY(V3.From(src));
                    var q = (yaw * Q4.From(src)).Normalized();
                    fr.rot[h][k] = q;
                    fr.fwd[h][k] = q.Rotate(V3.Z);
                }
            }
            return fr;
        }

        // =====================================================================
        //  手部模型 (wrist_fit.py): 位置 = b + A f 最小二乘; 扭转 [cos,sin] = C feats(f); 限位 = 每个方位的 p99
        // =====================================================================
        sealed class HandAcc
        {
            readonly double[,] xtx = new double[4, 4];
            readonly double[,] xty = new double[4, 3];
            readonly double[,] ftf = new double[10, 10];
            readonly double[,] fty = new double[10, 2];
            readonly int[][] hist = Enumerable.Range(0, 12).Select(_ => new int[361]).ToArray();   // 方位 x 0.5° 角度
            public long count;

            public void Add(V3 f, V3 rel, double roll)
            {
                double[] x = { 1, f.x, f.y, f.z };
                double[] y = { rel.x, rel.y, rel.z };
                for (int a = 0; a < 4; a++) { for (int b = 0; b < 4; b++) xtx[a, b] += x[a] * x[b]; for (int b = 0; b < 3; b++) xty[a, b] += x[a] * y[b]; }
                double[] F = Feats(f);
                double cr = Math.Cos(roll), sr = Math.Sin(roll);
                for (int a = 0; a < 10; a++) { for (int b = 0; b < 10; b++) ftf[a, b] += F[a] * F[b]; fty[a, 0] += F[a] * cr; fty[a, 1] += F[a] * sr; }
                double ang = Math.Acos(Clamp(f.z, -1, 1)) * Rad2Deg;
                double az = Math.Atan2(f.y, f.x) * Rad2Deg;
                int bin = Math.Min(11, Math.Max(0, (int)Math.Floor((az + 180) / 30)));
                hist[bin][Math.Min(360, (int)(ang * 2))]++;
                count++;
            }

            public HandParams Solve()
            {
                var h = new HandParams();
                var W = SolveLS(xtx, xty, 4, 3);       // 4x3: 行0 = b, 行1..3 = A 的列
                for (int k = 0; k < 3; k++) h.b[k] = (float)W[0, k];
                for (int r = 0; r < 3; r++) for (int col = 0; col < 3; col++) h.A[r * 3 + col] = (float)W[1 + col, r];
                var C = SolveLS(ftf, fty, 10, 2);
                for (int k = 0; k < 10; k++) { h.rollCos[k] = (float)C[k, 0]; h.rollSin[k] = (float)C[k, 1]; }
                h.limitFirstCenter = -165f; h.limitStep = 30f;
                for (int b = 0; b < 12; b++)
                {
                    int tot = hist[b].Sum();
                    if (tot <= 100) { h.limitDeg[b] = 150f; continue; }
                    int acc = 0, target = (int)Math.Ceiling(tot * 0.99);
                    for (int k = 0; k <= 360; k++) { acc += hist[b][k]; if (acc >= target) { h.limitDeg[b] = k * 0.5f; break; } }
                }
                return h;
            }
        }

        static void AccumulateHands(Frames fr, BsorReplay rep, HandAcc[] hand)
        {
            double t0 = double.MaxValue, t1 = double.MinValue;
            foreach (var n in rep.notes) if (n.type == 0) { t0 = Math.Min(t0, n.time); t1 = Math.Max(t1, n.time); }
            if (t0 >= t1) return;
            for (int k = 0; k < fr.t.Length; k += 2)
            {
                if (fr.t[k] < t0 || fr.t[k] > t1) continue;
                for (int h = 0; h < 2; h++)
                {
                    V3 f = fr.fwd[h][k];
                    hand[h].Add(f, fr.pos[h][k] - fr.headPos[k], RollOf(fr.rot[h][k], f));
                }
            }
        }

        // =====================================================================
        //  挥刀模板 (tech_templates.py)
        // =====================================================================
        static void CollectSwings(Frames fr, BsorReplay rep, List<Row> rows)
        {
            for (int h = 0; h < 2; h++)
            {
                var cuts = rep.notes.Where(n => n.type == 0 && n.saberType == h).Select(n => (double)n.time).OrderBy(x => x).ToList();
                if (cuts.Count < 20) continue;
                var sw = new List<double> { cuts[0] };
                foreach (var x in cuts.Skip(1)) if (x - sw[sw.Count - 1] > MergeGap) sw.Add(x);
                int m = sw.Count;
                var fc = new V3[m]; var d = new V3[m]; var nn = new V3[m];
                for (int i = 0; i < m; i++)
                {
                    fc[i] = Interp(fr, h, sw[i]);
                    var dd = (Interp(fr, h, sw[i] + 0.008) - Interp(fr, h, sw[i] - 0.008)).Normalized();
                    dd = (dd - fc[i] * V3.Dot(dd, fc[i])).Normalized();
                    d[i] = dd;
                    nn[i] = V3.Cross(fc[i], dd);
                }
                double sgn = h == 1 ? 1 : -1;
                for (int i = 1; i < m - 1; i++)
                {
                    double g = sw[i] - sw[i - 1];
                    if (g > 3.0) continue;
                    var a = new double[TauFine];
                    var b = new double[TauFine];
                    for (int k = 0; k < TauFine; k++)
                    {
                        V3 F = Interp(fr, h, sw[i] + TauStart + k * TauStepFine);
                        a[k] = Math.Atan2(V3.Dot(F, d[i]), V3.Dot(F, fc[i]));
                        b[k] = Math.Asin(Clamp(V3.Dot(F, nn[i]), -1, 1)) * Rad2Deg * sgn;
                    }
                    Unwrap(a);
                    int i0 = (int)Math.Round(-TauStart / TauStepFine);
                    double shift = 2 * Math.PI * Math.Round(a[i0] / (2 * Math.PI));
                    var row = new Row { gap = g, alpha = new float[(TauFine + 1) / 2], beta = new float[(TauFine + 1) / 2] };
                    for (int k = 0, j = 0; k < TauFine; k += 2, j++) { row.alpha[j] = (float)((a[k] - shift) * Rad2Deg); row.beta[j] = (float)b[k]; }
                    double rel = Math.Acos(Clamp(V3.Dot(d[i], d[i - 1]), -1, 1)) * Rad2Deg;
                    row.rel = 0; while (row.rel < RelEdges.Length && rel >= RelEdges[row.rel]) row.rel++;
                    row.dir = d[i].y < -0.5 ? 0 : d[i].y > 0.5 ? 1 : 2;
                    rows.Add(row);
                }
            }
        }

        static TemplateSet BuildTemplates(List<Row> rows, int minCount)
        {
            var set = new TemplateSet { tauStart = (float)TauStart, tauStep = (float)(TauStepFine * 2) };
            for (int rel = 0; rel < 5; rel++)
                for (int dir = 0; dir < 4; dir++)
                    for (int gi = 0; gi + 1 < GapBins.Length; gi++)
                    {
                        double g0 = GapBins[gi], g1 = GapBins[gi + 1];
                        var sel = rows.Where(r => r.rel == rel && r.gap >= g0 && r.gap < g1 && (dir == 3 || r.dir == dir)).ToList();
                        if (sel.Count < minCount) continue;
                        int len = sel[0].alpha.Length;
                        var t = new SwingTemplateData { rel = rel, dir = dir, gap0 = (float)g0, gap1 = (float)g1, n = sel.Count, alpha = new float[len], beta = new float[len] };
                        var buf = new float[sel.Count];
                        for (int k = 0; k < len; k++)
                        {
                            for (int j = 0; j < sel.Count; j++) buf[j] = sel[j].alpha[k];
                            t.alpha[k] = (float)Math.Round(Median(buf), 1);
                            for (int j = 0; j < sel.Count; j++) buf[j] = sel[j].beta[k];
                            t.beta[k] = (float)Math.Round(Median(buf), 1);
                        }
                        set.groups.Add(t);
                    }
            return set;
        }

        // =====================================================================
        //  反手扭转 (roll_offset_templates.py): 切块前后真人扭转 - 指向模型扭转 的圆平均, 乘一致性 R
        // =====================================================================
        sealed class RollAcc
        {
            readonly double[,,] re = new double[2, 9, RollTau], im = new double[2, 9, RollTau];
            readonly int[,] cnt = new int[2, 9];

            public void Accumulate(Frames fr, BsorReplay rep, AutoPlayModel model)
            {
                int n = fr.t.Length;
                for (int h = 0; h < 2; h++)
                {
                    var hp = model.Hand(h == 0);
                    var zr = new double[n]; var zi = new double[n];
                    for (int k = 0; k < n; k++)
                    {
                        V3 f = fr.fwd[h][k];
                        double[] F = Feats(f);
                        double pc = 0, ps = 0;
                        for (int j = 0; j < 10; j++) { pc += hp.rollCos[j] * F[j]; ps += hp.rollSin[j] * F[j]; }
                        double dr = RollOf(fr.rot[h][k], f) - Math.Atan2(ps, pc);
                        zr[k] = Math.Cos(dr); zi[k] = Math.Sin(dr);
                    }
                    foreach (var note in rep.notes)
                    {
                        if (note.type != 0 || note.Color != h) continue;
                        int cd = note.CutDirection;
                        if (cd < 0 || cd > 8) continue;
                        double ts = note.time + RollTauStart, te = note.time + RollTauStart + (RollTau - 1) * RollTauStep;
                        if (ts < fr.t[0] || te > fr.t[n - 1]) continue;
                        for (int k = 0; k < RollTau; k++)
                        {
                            double tt = note.time + RollTauStart + k * RollTauStep;
                            double a = InterpScalar(fr.t, zr, tt), b = InterpScalar(fr.t, zi, tt);
                            double len = Math.Sqrt(a * a + b * b);
                            if (len < 1e-9) continue;
                            re[h, cd, k] += a / len; im[h, cd, k] += b / len;
                        }
                        cnt[h, cd]++;
                    }
                }
            }

            public RollOffsetSet Solve()
            {
                var s = new RollOffsetSet { tauStart = (float)RollTauStart, tauStep = (float)RollTauStep, data = new float[2][][] };
                for (int h = 0; h < 2; h++)
                {
                    s.data[h] = new float[9][];
                    for (int cd = 0; cd < 9; cd++)
                    {
                        s.data[h][cd] = new float[RollTau];
                        if (cnt[h, cd] < 20) continue;
                        for (int k = 0; k < RollTau; k++)
                        {
                            double mr = re[h, cd, k] / cnt[h, cd], mi = im[h, cd, k] / cnt[h, cd];
                            double R = Math.Sqrt(mr * mr + mi * mi);
                            s.data[h][cd][k] = (float)Math.Round(Math.Atan2(mi, mr) * Rad2Deg * R, 1);
                        }
                    }
                }
                return s;
            }
        }

        // =====================================================================
        //  数学工具
        // =====================================================================
        const double Rad2Deg = 180.0 / Math.PI;
        static double Clamp(double v, double a, double b) => v < a ? a : v > b ? b : v;

        static double[] Feats(V3 f) => new[] { 1, f.x, f.y, f.z, f.x * f.x, f.y * f.y, f.z * f.z, f.x * f.y, f.y * f.z, f.x * f.z };

        // 以 "+z 到 f 的最小旋轉" 搬运过去的 +y 为参考, 手柄 up 绕 f 的角度
        static double RollOf(Q4 q, V3 f)
        {
            V3 u = q.Rotate(V3.Y);
            V3 ax = new V3(-f.y, f.x, 0);           // cross(z, f)
            double s = ax.Length, c = f.z;
            ax = s > 1e-9 ? ax * (1 / s) : new V3(0, 0, 0);
            V3 r = V3.Y * c + V3.Cross(ax, V3.Y) * s + ax * (V3.Dot(ax, V3.Y) * (1 - c));
            return Math.Atan2(V3.Dot(V3.Cross(r, u), f), V3.Dot(r, u));
        }

        static V3 Interp(Frames fr, int h, double tt)
        {
            double[] t = fr.t;
            var f = fr.fwd[h];
            if (tt <= t[0]) return f[0];
            if (tt >= t[t.Length - 1]) return f[t.Length - 1];
            int i = Array.BinarySearch(t, tt);
            if (i >= 0) return f[i];
            i = ~i;
            double w = (tt - t[i - 1]) / (t[i] - t[i - 1]);
            return (f[i - 1] * (1 - w) + f[i] * w).Normalized();
        }

        static double InterpScalar(double[] t, double[] v, double tt)
        {
            if (tt <= t[0]) return v[0];
            if (tt >= t[t.Length - 1]) return v[t.Length - 1];
            int i = Array.BinarySearch(t, tt);
            if (i >= 0) return v[i];
            i = ~i;
            double w = (tt - t[i - 1]) / (t[i] - t[i - 1]);
            return v[i - 1] * (1 - w) + v[i] * w;
        }

        static void Unwrap(double[] a)
        {
            for (int i = 1; i < a.Length; i++)
            {
                double d = a[i] - a[i - 1];
                if (d > Math.PI) a[i] -= 2 * Math.PI * Math.Round(d / (2 * Math.PI));
                else if (d < -Math.PI) a[i] -= 2 * Math.PI * Math.Round(d / (2 * Math.PI));
            }
        }

        static double Median(float[] v)
        {
            var c = (float[])v.Clone();
            Array.Sort(c);
            int n = c.Length;
            return n % 2 == 1 ? c[n / 2] : 0.5 * (c[n / 2 - 1] + c[n / 2]);
        }

        // 解正规方程 (XtX) W = XtY, 高斯消元 + 部分主元
        static double[,] SolveLS(double[,] xtx, double[,] xty, int n, int m)
        {
            var A = new double[n, n + m];
            for (int i = 0; i < n; i++) { for (int j = 0; j < n; j++) A[i, j] = xtx[i, j]; for (int j = 0; j < m; j++) A[i, n + j] = xty[i, j]; }
            for (int i = 0; i < n; i++) A[i, i] += 1e-9;       // 防止奇异
            for (int col = 0; col < n; col++)
            {
                int piv = col;
                for (int r = col + 1; r < n; r++) if (Math.Abs(A[r, col]) > Math.Abs(A[piv, col])) piv = r;
                if (piv != col) for (int j = 0; j < n + m; j++) { var tmp = A[col, j]; A[col, j] = A[piv, j]; A[piv, j] = tmp; }
                double d = A[col, col];
                if (Math.Abs(d) < 1e-15) continue;
                for (int j = 0; j < n + m; j++) A[col, j] /= d;
                for (int r = 0; r < n; r++)
                {
                    if (r == col) continue;
                    double k = A[r, col];
                    if (k == 0) continue;
                    for (int j = 0; j < n + m; j++) A[r, j] -= k * A[col, j];
                }
            }
            var W = new double[n, m];
            for (int i = 0; i < n; i++) for (int j = 0; j < m; j++) W[i, j] = A[i, n + j];
            return W;
        }
    }

    // ---- 双精度小向量/四元数 (训练用, 不依赖 Unity) ----
    public struct V3
    {
        public double x, y, z;
        public V3(double x, double y, double z) { this.x = x; this.y = y; this.z = z; }
        public static readonly V3 Y = new V3(0, 1, 0), Z = new V3(0, 0, 1);
        public static V3 From(float[] p) => new V3(p[0], p[1], p[2]);
        public static V3 operator +(V3 a, V3 b) => new V3(a.x + b.x, a.y + b.y, a.z + b.z);
        public static V3 operator -(V3 a, V3 b) => new V3(a.x - b.x, a.y - b.y, a.z - b.z);
        public static V3 operator *(V3 a, double k) => new V3(a.x * k, a.y * k, a.z * k);
        public static double Dot(V3 a, V3 b) => a.x * b.x + a.y * b.y + a.z * b.z;
        public static V3 Cross(V3 a, V3 b) => new V3(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x);
        public double Length => Math.Sqrt(x * x + y * y + z * z);
        public V3 Normalized() { double l = Length; return l > 1e-12 ? this * (1 / l) : this; }
    }

    public struct Q4
    {
        public double x, y, z, w;
        public Q4(double x, double y, double z, double w) { this.x = x; this.y = y; this.z = z; this.w = w; }
        public static Q4 From(float[] p) => new Q4(p[3], p[4], p[5], p[6]);
        public Q4 Normalized() { double l = Math.Sqrt(x * x + y * y + z * z + w * w); return l > 1e-12 ? new Q4(x / l, y / l, z / l, w / l) : this; }
        public static Q4 operator *(Q4 a, Q4 b) => new Q4(
            a.w * b.x + a.x * b.w + a.y * b.z - a.z * b.y,
            a.w * b.y - a.x * b.z + a.y * b.w + a.z * b.x,
            a.w * b.z + a.x * b.y - a.y * b.x + a.z * b.w,
            a.w * b.w - a.x * b.x - a.y * b.y - a.z * b.z);
        public V3 Rotate(V3 v)
        {
            var u = new V3(x, y, z);
            var uv = V3.Cross(u, v); var uuv = V3.Cross(u, uv);
            return v + (uv * w + uuv) * 2;
        }
    }
}
