using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AutoReplay.Core
{
    /// <summary>挑训练用 replay 的条件</summary>
    public sealed class SelectionOptions
    {
        public string replaysDir;
        public string playerId;              // 只用这个玩家的 replay (必须); null = 本机最常见的 (仅命令行工具)
        public DateTime? fromUtc;            // 日期范围 (含), null = 不限
        public DateTime? toUtc;
        public int perMapCap = 3;            // 同一张谱同一难度最多几份 (取最新)
        public int maxReplays = 300;         // 总数上限, 超过就在日期范围内平均抽样
        public float minStars = 0f;          // BL 星数下限 (0 = 不限; 没有星数的谱不受影响)
        public float heightTolerance = 0.10f; // 身高设定和中位数差太多的回放不用 (方块位置不同)
        public bool rankedOnly;               // 只用 BL 或 SS ranked 的谱 (需要星数查询)
        public bool preferAcc;                // 超过上限时优先取 acc 高的 (而不是平均抽样)
    }

    public sealed class ReplayEntry
    {
        public string path;
        public float acc;          // 0..1, 由 info.score / 谱面满分算; 不知道满分时为 0
        public BsorReplay info;
        public DateTime timeUtc;
        public StarInfo stars;
    }

    /// <summary>每一步筛完剩几份, 给界面和训练报告看</summary>
    public sealed class SelectionResult
    {
        public string playerId;
        public int total, inRange, own, eligible, heightOk, deduped, starFiltered = -1;
        public float heightMedian;
        public bool sampled;
        public List<ReplayEntry> picked = new List<ReplayEntry>();

        public string Summary()
        {
            string s = $"範圍內 {inRange} 份 → 本人 {own} → 符合條件 {eligible} → 身高一致 {heightOk} → 去重複後 {deduped}";
            if (starFiltered >= 0) s += $" → 星數篩選後 {starFiltered}";
            s += $" → 採用 {picked.Count}" + (sampled ? " (平均抽樣)" : "");
            return s;
        }
    }

    public static class ReplaySelector
    {
        // 只读 info 的缓存 (路径 + 修改时间), 游戏内反复预览时不用重读 1000+ 个文件
        static readonly Dictionary<string, (DateTime mtime, BsorReplay info)> _headers = new Dictionary<string, (DateTime, BsorReplay)>();
        static readonly object _lock = new object();

        public static List<ReplayEntry> ScanHeaders(string replaysDir)
        {
            if (!Directory.Exists(replaysDir)) throw new DirectoryNotFoundException("找不到 replay 資料夾: " + replaysDir);
            var list = new List<ReplayEntry>();
            foreach (var f in new DirectoryInfo(replaysDir).GetFiles("*.bsor"))
            {
                BsorReplay info = null;
                lock (_lock)
                    if (_headers.TryGetValue(f.FullName, out var c) && c.mtime == f.LastWriteTimeUtc) info = c.info;
                if (info == null)
                {
                    try { info = BsorReader.ReadInfo(f.FullName); } catch { continue; }
                    lock (_lock) _headers[f.FullName] = (f.LastWriteTimeUtc, info);
                }
                var t = info.timestamp > 0 ? DateTimeOffset.FromUnixTimeSeconds(info.timestamp).UtcDateTime : f.LastWriteTimeUtc;
                list.Add(new ReplayEntry { path = f.FullName, info = info, timeUtc = t });
            }
            return list;
        }

        /// <summary>
        /// 日期范围 → 本人 → 基本条件 → 同谱上限 → 总数上限 (平均抽样).
        /// stars 不为 null 时再按星数下限筛 (network=false 只看缓存, 用于预览).
        /// </summary>
        public static SelectionResult Select(SelectionOptions o, StarService stars = null, bool network = false,
                                             Action<string> progress = null, System.Threading.CancellationToken cancel = default)
        {
            var all = ScanHeaders(o.replaysDir);
            var r = new SelectionResult { total = all.Count };

            var inRange = all.Where(e => (o.fromUtc == null || e.timeUtc >= o.fromUtc) && (o.toUtc == null || e.timeUtc <= o.toUtc)).ToList();
            r.inRange = inRange.Count;

            string pid = o.playerId;
            if (string.IsNullOrEmpty(pid))
                pid = inRange.Where(e => !string.IsNullOrEmpty(e.info.playerId) && e.info.playerId != "Auto Play")
                             .GroupBy(e => e.info.playerId).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
            r.playerId = pid;
            var own = inRange.Where(e => e.info.playerId == pid).ToList();
            r.own = own.Count;

            // Standard, 右手模式, 没有改变速度的修饰符 (FS/SF/SS 会让挥刀节奏不像平常)
            var eligible = own.Where(e => !e.info.leftHanded && e.info.mode == "Standard" && !e.info.HasSpeedModifier("FS", "SF", "SS")).ToList();
            r.eligible = eligible.Count;

            // 身高设定: 取中位数, 差太多的不用 (身高设定决定方块的上下位置, 混在一起会学到两套轨迹)
            var hs = eligible.Select(e => e.info.height).Where(h => h > 1f && h < 2.5f).OrderBy(h => h).ToList();
            r.heightMedian = hs.Count > 0 ? hs[hs.Count / 2] : 0f;
            if (r.heightMedian > 0f)
                eligible = eligible.Where(e => Math.Abs(e.info.height - r.heightMedian) <= o.heightTolerance).ToList();
            r.heightOk = eligible.Count;

            // 同一张谱 + 难度最多 perMapCap 份, 取最新 (避免刷同一张谱的人让模型只学到那几张)
            var deduped = eligible.GroupBy(e => (e.info.hash ?? "").ToUpperInvariant() + "|" + e.info.difficulty)
                                  .SelectMany(g => (o.preferAcc ? g.OrderByDescending(e => e.info.score) : g.OrderByDescending(e => e.timeUtc))
                                                   .Take(Math.Max(1, o.perMapCap)))   // acc 优先: 同一张谱取分数最高的
                                  .OrderBy(e => e.timeUtc).ToList();
            r.deduped = deduped.Count;

            // 星数 (查询在抽样之后做, 只查真正会用到的谱; 被星数刷掉的再从剩下的补)
            var pool = deduped;
            if (stars != null && (o.minStars > 0f || o.rankedOnly || o.preferAcc))
            {
                var kept = new List<ReplayEntry>();
                int target = o.maxReplays;
                foreach (var e in SpreadOrder(pool))
                {
                    cancel.ThrowIfCancellationRequested();
                    e.stars = stars.Get(e.info.hash, e.info.difficulty, network, cancel);
                    if (e.stars?.blMaxScore > 0 && e.info.score > 0) e.acc = Math.Min(1f, e.info.score / (float)e.stars.blMaxScore);
                    if (Keep(e.stars, o)) kept.Add(e);
                    if (network) progress?.Invoke($"查詢星數… {kept.Count}/{Math.Min(target, pool.Count)}");
                    if (network && !o.preferAcc && kept.Count >= target * 2) break;   // 够抽样用就停 (acc 优先要比较全部, 不能停)
                }
                pool = kept.OrderBy(e => e.timeUtc).ToList();
                r.starFiltered = pool.Count;
            }

            r.sampled = pool.Count > o.maxReplays;
            if (o.preferAcc && r.sampled)
            {
                // 优先 acc 高的: 按 acc 排序取前 N 份 (acc 不明的排最后), 再按时间排回去
                r.picked = pool.OrderByDescending(e => e.acc).Take(o.maxReplays).OrderBy(e => e.timeUtc).ToList();
            }
            else r.picked = EvenSample(pool, o.maxReplays);
            return r;
        }

        static bool Keep(StarInfo s, SelectionOptions o)
        {
            if (o.rankedOnly && (s == null || !(s.blStatus == 3 || s.ssRanked))) return false;   // 查不到就不算 ranked
            if (o.minStars > 0f && s?.blStars != null && s.blStars < o.minStars) return false;
            return true;
        }

        /// <summary>打散顺序 (黄金比例): 中途停下时, 已处理的也均匀分布在整段时间里</summary>
        static IEnumerable<ReplayEntry> SpreadOrder(List<ReplayEntry> sorted) =>
            Enumerable.Range(0, sorted.Count).OrderBy(i => (i * 0.6180339887) % 1.0).Select(i => sorted[i]);

        /// <summary>按时间顺序平均取 n 份 (不只取最新的, 让整段时间都有代表)</summary>
        static List<ReplayEntry> EvenSample(List<ReplayEntry> sorted, int n)
        {
            if (sorted.Count <= n) return sorted.ToList();
            var res = new List<ReplayEntry>(n);
            if (n <= 0) return res;
            if (n == 1) { res.Add(sorted[sorted.Count - 1]); return res; }
            for (int i = 0; i < n; i++) res.Add(sorted[(int)Math.Round(i * (sorted.Count - 1) / (double)(n - 1))]);
            return res;
        }
    }
}
