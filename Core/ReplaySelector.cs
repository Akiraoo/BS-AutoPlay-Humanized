using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BotPlay.Core
{
    /// <summary>挑訓練用 replay 的條件</summary>
    public sealed class SelectionOptions
    {
        public string replaysDir;
        public string playerId;              // 只用這個玩家的 replay (必須); null = 本機最常見的 (僅命令列工具)
        public DateTime? fromUtc;            // 日期範圍 (含), null = 不限
        public DateTime? toUtc;
        public int perMapCap = 3;            // 同一張譜同一難度最多幾份 (取最新)
        public int maxReplays = 300;         // 總數上限, 超過就在日期範圍內平均抽樣
        public float minStars = 0f;          // BL 星數下限 (0 = 不限; 沒有星數的譜不受影響)
        public float heightTolerance = 0.10f; // 身高設定和中位數差太多的回放不用 (方塊位置不同)
        public bool rankedOnly;               // 只用 BL 或 SS ranked 的譜 (需要星數查詢)
        public bool preferAcc;                // 超過上限時優先取 acc 高的 (而不是平均抽樣)
        public IEnumerable<string> excludeMaps;  // 另外不用的譜面 hash (使用者自訂; 內建的惡搞譜清單一律排除)
    }

    public sealed class ReplayEntry
    {
        public string path;
        public float acc;          // 0..1, 由 info.score / 譜面滿分算; 不知道滿分時為 0
        public BsorReplay info;
        public DateTime timeUtc;
        public StarInfo stars;
    }

    /// <summary>每一步篩完剩幾份, 給介面和訓練報告看</summary>
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
        /// <summary>純惡搞譜, 動作不代表平常打法, 一律不拿來訓練</summary>
        public static readonly HashSet<string> JokeMaps = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "6C9D463C1CF556246CF60BFAEF75FC4F8EA3053F",   // Mope Mope (4000+ 顆炸彈)
            "875F9F57103004ACA7C4BFAC57601D21402CDB10",   // DADADADA
        };

        // 只讀 info 的快取 (路徑 + 修改時間), 遊戲內反覆預覽時不用重讀 1000+ 個檔案
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
        /// 日期範圍 → 本人 → 基本條件 → 同譜上限 → 總數上限 (平均抽樣).
        /// stars 不為 null 時再按星數下限篩 (network=false 只看快取, 用於預覽).
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
                pid = inRange.Where(e => !string.IsNullOrEmpty(e.info.playerId) && e.info.playerId != "Bot Play" && e.info.playerId != "Auto Play")
                             .GroupBy(e => e.info.playerId).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
            r.playerId = pid;
            var own = inRange.Where(e => e.info.playerId == pid).ToList();
            r.own = own.Count;

            // Standard, 右手模式, 沒有改變速度的修飾符 (FS/SF/SS 會讓揮刀節奏不像平常)
            // 以及排除惡搞譜 / 使用者指定不用的譜
            var exclude = new HashSet<string>(JokeMaps, StringComparer.OrdinalIgnoreCase);
            if (o.excludeMaps != null) foreach (var h in o.excludeMaps) if (!string.IsNullOrWhiteSpace(h)) exclude.Add(h.Trim());
            var eligible = own.Where(e => !e.info.leftHanded && e.info.mode == "Standard" && !e.info.HasSpeedModifier("FS", "SF", "SS")
                                          && !exclude.Contains(e.info.hash ?? "")).ToList();
            r.eligible = eligible.Count;

            // 身高設定: 取中位數, 差太多的不用 (身高設定決定方塊的上下位置, 混在一起會學到兩套軌跡)
            var hs = eligible.Select(e => e.info.height).Where(h => h > 1f && h < 2.5f).OrderBy(h => h).ToList();
            r.heightMedian = hs.Count > 0 ? hs[hs.Count / 2] : 0f;
            if (r.heightMedian > 0f)
                eligible = eligible.Where(e => Math.Abs(e.info.height - r.heightMedian) <= o.heightTolerance).ToList();
            r.heightOk = eligible.Count;

            // 同一張譜 + 難度最多 perMapCap 份, 取最新 (避免刷同一張譜的人讓模型只學到那幾張)
            var deduped = eligible.GroupBy(e => (e.info.hash ?? "").ToUpperInvariant() + "|" + e.info.difficulty)
                                  .SelectMany(g => (o.preferAcc ? g.OrderByDescending(e => e.info.score) : g.OrderByDescending(e => e.timeUtc))
                                                   .Take(Math.Max(1, o.perMapCap)))   // acc 優先: 同一張譜取分數最高的
                                  .OrderBy(e => e.timeUtc).ToList();
            r.deduped = deduped.Count;

            // 星數 (查詢在抽樣之後做, 只查真正會用到的譜; 被星數刷掉的再從剩下的補)
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
                    if (network && !o.preferAcc && kept.Count >= target * 2) break;   // 夠抽樣用就停 (acc 優先要比較全部, 不能停)
                }
                pool = kept.OrderBy(e => e.timeUtc).ToList();
                r.starFiltered = pool.Count;
            }

            r.sampled = pool.Count > o.maxReplays;
            if (o.preferAcc && r.sampled)
            {
                // 優先 acc 高的: 按 acc 排序取前 N 份 (acc 不明的排最後), 再按時間排回去
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

        /// <summary>打散順序 (黃金比例): 中途停下時, 已處理的也均勻分佈在整段時間裡</summary>
        static IEnumerable<ReplayEntry> SpreadOrder(List<ReplayEntry> sorted) =>
            Enumerable.Range(0, sorted.Count).OrderBy(i => (i * 0.6180339887) % 1.0).Select(i => sorted[i]);

        /// <summary>按時間順序平均取 n 份 (不只取最新的, 讓整段時間都有代表)</summary>
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
