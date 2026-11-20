using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BotPlay.Core
{
    /// <summary>
    /// 個人習慣 (StyleParams) 的統計. 和 tools/ 下的分析指令碼同一套方法:
    ///   點塊角度 dot_angle_stats.py / 點塊上下 dot_layer_check.py / 閃牆 dodge_stats.py / 躲炸彈 bomb_eval.py; 蹲牆照同樣的做法 (以牆到達為 0 的頭高度曲線).
    /// 存的是量測值, 生成器自己換算成引數.
    /// </summary>
    public static partial class ModelTrainer
    {
        const int MinDots = 300, MinLean = 30, MinDuck = 15, MinBombs = 300, MinCuts = 2000, MinCutGroup = 150;
        const int MaxBombsPerReplay = 400;

        static string GameDirFrom(string replaysDir)
        {
            // <遊戲>/UserData/BeatLeader/Replays
            try { return string.IsNullOrEmpty(replaysDir) ? null : Directory.GetParent(replaysDir)?.Parent?.Parent?.FullName; }
            catch { return null; }
        }

        sealed class StyleAcc
        {
            readonly string gameDir;
            public StyleAcc(string gameDir) { this.gameDir = gameDir; }

            // 點塊: 往上 / 往下 各一組最小二乘
            readonly double[][,] dotXtX = { new double[5, 5], new double[5, 5] };
            readonly double[][,] dotXtY = { new double[5, 1], new double[5, 1] };
            readonly int[] layerDown = new int[3], layerAll = new int[3];
            int dotCount;

            static readonly double[] LeanTau = Enumerable.Range(0, 17).Select(i => -0.6 + 0.1 * i).ToArray();
            static readonly double[] DuckTau = Enumerable.Range(0, 19).Select(i => -0.6 + 0.1 * i).ToArray();
            readonly List<double[]> leanDx = new List<double[]>();
            readonly List<double> leanRoll = new List<double>();
            readonly List<double[]> duckDy = new List<double[]>();
            readonly List<double> duckHand = new List<double>();
            readonly List<double> bombDist = new List<double>();
            int bombUp, bombSide;

            public void Add(Frames fr, BsorReplay rep)
            {
                AddDots(fr, rep);
                if (string.IsNullOrEmpty(gameDir) || string.IsNullOrEmpty(rep.hash)) return;
                var map = MapReader.Load(gameDir, rep.hash, rep.difficulty, string.IsNullOrEmpty(rep.mode) ? "Standard" : rep.mode);
                if (map == null) return;
                double yoff = rep.height > 1f ? 0.5 * (rep.height - 1.5) : 0.0;
                double headH = MedianHeadY(fr);
                AddWalls(fr, map, yoff, headH);
                AddBombs(fr, map, yoff);
                AddCuts(rep, map, yoff);
            }

            // ---------------- 切擊: 深度 / 切點偏移 ----------------
            static readonly float[] CutQ = { 0.02f, 0.05f, 0.1f, 0.15f, 0.2f, 0.25f, 0.3f, 0.35f, 0.4f, 0.45f, 0.5f, 0.55f, 0.6f, 0.65f, 0.7f, 0.75f, 0.8f, 0.85f, 0.9f, 0.95f, 0.98f };
            readonly List<double>[] cutDepth = { new List<double>(), new List<double>() };
            readonly List<double>[] cutOff = Enumerable.Range(0, 18).Select(_ => new List<double>()).ToArray();
            double depSx, depSy, depSxx, depSyy, depSxy; int depPairs;
            int cutCount;

            void AddCuts(BsorReplay rep, MapObjects map, double yoff)
            {
                // 左手模式 / 變速: 方塊座標和時間尺度不同, 不統計
                if (rep.leftHanded || map.njs <= 0 || rep.HasSpeedModifier("FS", "SF", "SS")) return;
                for (int h = 0; h < 2; h++)
                {
                    var seq = rep.notes.Where(n => n.type == 0 && n.saberType == h && n.id / 10000 == 3).OrderBy(n => n.time).ToList();
                    double prevDepth = double.NaN, prevT = double.NegativeInfinity;
                    foreach (var n in seq)
                    {
                        int line = (n.id / 1000) % 10, layer = (n.id / 100) % 10;
                        if (line > 3 || layer > 2) continue;
                        double cx = (line - 1.5) * 0.6, cy = 0.72 + layer * 0.53 + yoff;
                        double nl = Math.Sqrt(n.normX * n.normX + n.normY * n.normY);
                        if (nl < 0.8) continue;   // 切面法線幾乎朝前後: 量不準
                        double s = ((cx - n.cutX) * n.normX + (cy - n.cutY) * n.normY) / nl;
                        if (Math.Abs(Math.Abs(s) - n.cutDist) > 0.03) continue;   // 和遊戲記的距離對不上 (方塊不在標準格子上)
                        s *= CutStyle.CanonSign(n.normX, n.normY, h == 1);
                        double depth = 0.72 + map.njs * n.timeDev;
                        if (depth < 0.2 || depth > 3.0) continue;
                        cutDepth[h].Add(depth);
                        cutOff[h * 9 + Math.Min(8, n.CutDirection)].Add(s);
                        cutCount++;
                        if (!double.IsNaN(prevDepth) && n.time - prevT < 1.0)
                        {
                            depSx += prevDepth; depSy += depth; depSxx += prevDepth * prevDepth; depSyy += depth * depth; depSxy += prevDepth * depth; depPairs++;
                        }
                        prevDepth = depth; prevT = n.time;
                    }
                }
            }

            static float[] Quantiles(List<double> v)
            {
                var a = v.ToArray(); Array.Sort(a);
                return CutQ.Select(q => (float)a[Math.Min(a.Length - 1, (int)Math.Round(q * (a.Length - 1)))]).ToArray();
            }

            // ---------------- 點塊 ----------------
            void AddDots(Frames fr, BsorReplay rep)
            {
                for (int h = 0; h < 2; h++)
                {
                    double m = h == 0 ? -1 : 1;
                    var seq = rep.notes.Where(n => n.type == 0 && n.Color == h && n.id / 10000 == 3).OrderBy(n => n.time).ToList();
                    for (int j = 1; j + 1 < seq.Count; j++)
                    {
                        var n = seq[j];
                        if (n.CutDirection != 8) continue;
                        int k = Array.BinarySearch(fr.t, n.time); if (k < 0) k = ~k;
                        if (k < 3 || k > fr.t.Length - 4) continue;
                        V3 Tip(int i) => fr.pos[h][i] + fr.fwd[h][i];
                        V3 v = Tip(k + 2) - Tip(k - 2);
                        if (Math.Sqrt(v.x * v.x + v.y * v.y) < 0.05) continue;
                        double ang = Math.Atan2(v.x * m, v.y) * Rad2Deg;
                        bool down = Math.Abs(ang) > 90;
                        double dev = down ? Math.Sign(ang) * (180 - Math.Abs(ang)) : ang;
                        (double x, double y) P(BsorNote q) => (((q.id / 1000) % 10 - 1.5) * 0.6 * m, ((q.id / 100) % 10) * 0.53);
                        var p0 = P(seq[j - 1]); var p1 = P(n); var p2 = P(seq[j + 1]);
                        double[] x = { 1, p1.x - p0.x, p2.x - p1.x, p1.y - p0.y, p2.y - p1.y };
                        int g = down ? 1 : 0;
                        for (int a = 0; a < 5; a++) { for (int b = 0; b < 5; b++) dotXtX[g][a, b] += x[a] * x[b]; dotXtY[g][a, 0] += x[a] * dev; }
                        int layer = Math.Min(2, Math.Max(0, (n.id / 100) % 10));
                        layerAll[layer]++; if (down) layerDown[layer]++;
                        dotCount++;
                    }
                }
            }

            // ---------------- 牆: 左右閃 / 蹲 ----------------
            void AddWalls(Frames fr, MapObjects map, double yoff, double headH)
            {
                var t = fr.t;
                // 會影響頭的牆 (蓋到頭的高度, 且佔到中間兩軌)
                var rel = new List<(double t0, double t1, int c0, int c1, double bottom)>();
                foreach (var w in map.walls)
                {
                    double bottom = 0.25 + 0.6 * w.y - 0.15 + yoff, top = bottom + 0.6 * w.h;
                    if (top < headH - 0.15 || bottom > headH + 0.1) continue;
                    int c0 = Math.Max(w.x, 1), c1 = Math.Min(w.x + w.w - 1, 2);
                    if (c0 > c1) continue;
                    rel.Add((w.t0, w.t1, c0, c1, bottom));
                }
                if (rel.Count == 0) return;
                int n = t.Length;
                var hx = new double[n]; var hy = new double[n]; var roll = new double[n]; var handY = new double[n];
                for (int i = 0; i < n; i++)
                {
                    hx[i] = fr.headPos[i].x; hy[i] = fr.headPos[i].y;
                    V3 r = fr.headRot[i].Rotate(new V3(1, 0, 0));
                    roll[i] = Math.Asin(Clamp(-r.y, -1, 1)) * Rad2Deg;
                    handY[i] = 0.5 * (fr.pos[0][i].y + fr.pos[1][i].y);
                }
                double bx = Median(hx), br = Median(roll), bh = Median(handY);
                double duckLine = 1.0 + yoff + 0.12;   // 頂牆: 牆底比蹲下的頭 (1.0 m + 身高偏移) 還高
                for (int e = 0; e < rel.Count; e++)
                {
                    var w = rel[e];
                    bool isolated = true;
                    for (int o = 0; o < rel.Count; o++)
                        if (o != e && Math.Abs(rel[o].t0 - w.t0) < 1.5 && Math.Abs(rel[o].t0 - w.t0) > 0.02) { isolated = false; break; }
                    if (!isolated) continue;
                    bool sameTimeOther = rel.Any(o => !ReferenceEquals(o, w) && Math.Abs(o.t0 - w.t0) <= 0.02 && (o.c0 != w.c0 || o.c1 != w.c1));
                    // 左右閃: 只蓋一條中間軌道, 從低處蓋過頭 (不能蹲)
                    if (w.c0 == w.c1 && w.bottom <= 1.0 && !sameTimeOther && w.t0 - 0.7 >= t[0] && w.t1 + 1.1 <= t[n - 1])
                    {
                        double side = w.c0 == 1 ? 1 : -1;
                        var dx = LeanTau.Select(tau => (Lerp(t, hx, w.t0 + tau) - bx) * side).ToArray();
                        var dr = LeanTau.Select(tau => Math.Abs((Lerp(t, roll, w.t0 + tau) - br))).ToArray();
                        leanDx.Add(dx); leanRoll.Add(dr.Max());
                    }
                    // 蹲: 中間兩軌都蓋住的頂牆 (單面牆), 牆不長
                    if (w.c0 == 1 && w.c1 == 2 && w.bottom >= duckLine && w.bottom <= headH - 0.05 && w.t1 - w.t0 <= 0.6
                        && w.t0 - 0.7 >= t[0] && w.t0 + 1.3 <= t[n - 1])
                    {
                        var dy = DuckTau.Select(tau => Lerp(t, hy, w.t0 + tau) - headH).ToArray();
                        duckDy.Add(dy);
                        int ib = Array.IndexOf(dy, dy.Min());
                        double headDrop = -dy[ib];
                        if (headDrop > 0.15) duckHand.Add((bh - Lerp(t, handY, w.t0 + DuckTau[ib])) / headDrop);
                    }
                }
            }

            // ---------------- 炸彈 ----------------
            void AddBombs(Frames fr, MapObjects map, double yoff)
            {
                var t = fr.t; int n = t.Length;
                if (map.bombs.Count == 0) return;
                int used = 0; double lastT = double.MinValue;
                foreach (var b in map.bombs)
                {
                    if (used >= MaxBombsPerReplay) break;
                    if (b.t - 0.25 < t[0] || b.t + 0.12 > t[n - 1]) continue;
                    var c0 = new V3((b.x - 1.5) * 0.6, 0.72 + b.y * 0.53 + yoff, 0);
                    double best = 9; V3 bestRel = new V3();
                    for (double g = b.t - 0.25; g <= b.t + 0.12; g += 1.0 / 120)
                    {
                        var c = new V3(c0.x, c0.y, 0.72 + map.njs * (b.t - g));
                        for (int h = 0; h < 2; h++)
                        {
                            V3 P = LerpV(t, fr.pos[h], g), f = LerpV(t, fr.fwd[h], g).Normalized();
                            V3 wr = P - f * 0.20, a = wr + f * 0.30, ab = f * 1.30;
                            double s = Clamp(V3.Dot(c - a, ab) / V3.Dot(ab, ab), 0, 1);
                            V3 q = a + ab * s; double d = (c - q).Length;
                            if (d < best) { best = d; bestRel = q - c; }
                        }
                    }
                    bombDist.Add(best);
                    bombSide++;
                    if (Math.Abs(bestRel.y) >= Math.Abs(bestRel.x) && bestRel.y > 0) bombUp++;
                    used++; lastT = b.t;
                }
            }

            public StyleParams Solve(Action<string> log)
            {
                var st = new StyleParams();
                if (dotCount >= MinDots)
                {
                    var up = SolveLS(dotXtX[0], dotXtY[0], 5, 1); var dn = SolveLS(dotXtX[1], dotXtY[1], 5, 1);
                    st.dots = new DotStyle
                    {
                        count = dotCount,
                        flowUp = Enumerable.Range(0, 5).Select(i => (float)up[i, 0]).ToArray(),
                        flowDown = Enumerable.Range(0, 5).Select(i => (float)dn[i, 0]).ToArray(),
                        downShare = Enumerable.Range(0, 3).Select(i => layerAll[i] > 20 ? layerDown[i] / (float)layerAll[i] : -1f).ToArray(),
                    };
                    log?.Invoke($"點塊習慣: {dotCount} 個, 往上 {Fmt(st.dots.flowUp)}, 往下 {Fmt(st.dots.flowDown)}, 下/中/上排往下揮 {Fmt(st.dots.downShare)}");
                }
                else log?.Invoke($"點塊樣本 {dotCount} 個, 不夠 ({MinDots}), 用預設");

                if (leanDx.Count >= MinLean)
                {
                    var peaks = leanDx.Select(d => d.Max()).ToList();
                    var med = LeanTau.Select((_, i) => Median(leanDx.Select(d => d[i]).ToArray())).ToArray();
                    int ip = Array.IndexOf(med, med.Max());
                    int ir = ip; while (ir < med.Length && med[ir] > 0.25 * med[ip]) ir++;
                    double peak = Pct(peaks, 0.5);
                    st.lean = new LeanStyle
                    {
                        count = leanDx.Count, peak = (float)peak, peakP10 = (float)Pct(peaks, 0.1), peakP90 = (float)Pct(peaks, 0.9),
                        peakTime = (float)LeanTau[ip], returnTime = (float)((ir < med.Length ? LeanTau[ir] : LeanTau[med.Length - 1] + 0.1) - LeanTau[ip]),
                        rollPerMeter = peak > 0.02 ? (float)(Pct(leanRoll, 0.5) / peak) : 0f,
                    };
                    log?.Invoke($"閃牆習慣: {leanDx.Count} 次, 最遠 {peak:F2} m (P10 {st.lean.peakP10:F2} ~ P90 {st.lean.peakP90:F2}), 最遠點 {st.lean.peakTime:+0.0;-0.0} s, 回來 {st.lean.returnTime:F1} s, 歪頭 {st.lean.rollPerMeter:F0}°/m");
                }
                else log?.Invoke($"閃牆樣本 {leanDx.Count} 次, 不夠 ({MinLean}), 用預設");

                if (duckDy.Count >= MinDuck)
                {
                    var med = DuckTau.Select((_, i) => Median(duckDy.Select(d => d[i]).ToArray())).ToArray();
                    int ib = Array.IndexOf(med, med.Min()); double depth = -med[ib];
                    int i0 = 0; while (i0 < ib && med[i0] > -0.1 * depth) i0++;
                    int i1 = ib; while (i1 < med.Length && med[i1] < -0.1 * depth) i1++;
                    st.duck = new DuckStyle
                    {
                        count = duckDy.Count, depth = (float)depth, startTime = (float)DuckTau[i0], bottomTime = (float)DuckTau[ib],
                        recoverTime = (float)(i1 < med.Length ? DuckTau[i1] : DuckTau[med.Length - 1] + 0.1),
                        handFollow = duckHand.Count >= 5 ? (float)Pct(duckHand, 0.5) : -1f,
                    };
                    log?.Invoke($"蹲牆習慣: {duckDy.Count} 次, 下降 {depth:F2} m, 開始 {st.duck.startTime:+0.0;-0.0} s, 最低 {st.duck.bottomTime:+0.0;-0.0} s, 回復 {st.duck.recoverTime:+0.0;-0.0} s, 手跟著 {st.duck.handFollow:F2}");
                }
                else log?.Invoke($"蹲牆樣本 {duckDy.Count} 次, 不夠 ({MinDuck}), 用預設");

                if (bombDist.Count >= MinBombs)
                {
                    st.bombs = new BombStyle
                    {
                        count = bombDist.Count, clearMedian = (float)Pct(bombDist, 0.5), clearP10 = (float)Pct(bombDist, 0.1),
                        upShare = bombSide > 0 ? bombUp / (float)bombSide : 0.5f,
                    };
                    log?.Invoke($"炸彈習慣: {bombDist.Count} 顆, 距離中位 {st.bombs.clearMedian:F2} m, P10 {st.bombs.clearP10:F2} m, 劍在上方 {st.bombs.upShare * 100:F0}%");
                }
                else log?.Invoke($"炸彈樣本 {bombDist.Count} 顆, 不夠 ({MinBombs}), 用預設");

                if (cutCount >= MinCuts && cutDepth[0].Count >= MinCutGroup && cutDepth[1].Count >= MinCutGroup)
                {
                    double corr = 0;
                    if (depPairs > 50)
                    {
                        double mx = depSx / depPairs, my = depSy / depPairs;
                        double vx = depSxx / depPairs - mx * mx, vy = depSyy / depPairs - my * my;
                        corr = vx > 1e-9 && vy > 1e-9 ? (depSxy / depPairs - mx * my) / Math.Sqrt(vx * vy) : 0;
                    }
                    st.cuts = new CutStyle
                    {
                        count = cutCount, q = (float[])CutQ.Clone(),
                        depth = new[] { Quantiles(cutDepth[0]), Quantiles(cutDepth[1]) },
                        depthCorr = (float)Clamp(corr, 0, 0.95),
                        offset = cutOff.Select(l => l.Count >= MinCutGroup ? Quantiles(l) : null).ToArray(),
                        offsetAll = Enumerable.Range(0, 2).Select(h => Quantiles(Enumerable.Range(0, 9).SelectMany(d => cutOff[h * 9 + d]).ToList())).ToArray(),
                    };
                    int mid = Array.IndexOf(CutQ, 0.5f);
                    log?.Invoke($"切擊習慣: {cutCount} 刀, 深度中位 左 {st.cuts.depth[0][mid]:F2} / 右 {st.cuts.depth[1][mid]:F2} m (相鄰相關 {st.cuts.depthCorr:F2}), " +
                                $"切點偏移中位 左 {st.cuts.offsetAll[0][mid] * 100:F1} / 右 {st.cuts.offsetAll[1][mid] * 100:F1} cm");
                }
                else log?.Invoke($"切擊樣本 {cutCount} 刀, 不夠 ({MinCuts}), 用預設 (不加偏移)");
                return st.dots == null && st.lean == null && st.duck == null && st.bombs == null && st.cuts == null ? null : st;
            }

            static string Fmt(float[] a) => "[" + string.Join(", ", a.Select(x => x.ToString("0.##"))) + "]";
        }

        static double Median(double[] a) { if (a.Length == 0) return 0; var b = (double[])a.Clone(); Array.Sort(b); return b[b.Length / 2]; }

        static double Lerp(double[] t, double[] v, double tt)
        {
            if (tt <= t[0]) return v[0];
            int n = t.Length; if (tt >= t[n - 1]) return v[n - 1];
            int i = Array.BinarySearch(t, tt); if (i >= 0) return v[i];
            i = ~i; double u = (tt - t[i - 1]) / (t[i] - t[i - 1]);
            return v[i - 1] + (v[i] - v[i - 1]) * u;
        }

        static V3 LerpV(double[] t, V3[] v, double tt)
        {
            if (tt <= t[0]) return v[0];
            int n = t.Length; if (tt >= t[n - 1]) return v[n - 1];
            int i = Array.BinarySearch(t, tt); if (i >= 0) return v[i];
            i = ~i; double u = (tt - t[i - 1]) / (t[i] - t[i - 1]);
            return v[i - 1] + (v[i] - v[i - 1]) * u;
        }
    }
}
