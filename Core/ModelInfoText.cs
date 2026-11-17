using System.Collections.Generic;
using System.Globalization;

namespace AutoReplay.Core
{
    /// <summary>把模型資訊排成幾行文字 (遊戲內設定頁和命令列工具共用)</summary>
    public static class ModelInfoText
    {
        static string F(float v, string fmt = "0.0") => v.ToString(fmt, CultureInfo.InvariantCulture);

        static string Star(StarStats s, string label) =>
            s == null ? null : $"{label} 中位 {F(s.median)} (P10–P90 {F(s.p10)}–{F(s.p90)})";

        public static List<string> Lines(AutoPlayModel m)
        {
            var L = new List<string>();
            var meta = m.meta;
            L.Add($"{meta.name}" + (string.IsNullOrEmpty(meta.author) ? "" : $" · 作者 {meta.author}") + $" · 建立 {meta.createdUtc}");
            var t = meta.training;
            if (t == null)
            {
                L.Add($"{meta.replayCount} 份回放 · {meta.swingCount} 次揮刀 · 訓練器 {meta.trainer}");
                if (!string.IsNullOrEmpty(meta.notes)) L.Add(meta.notes);
                L.Add("(舊版模型, 沒有詳細的訓練資訊)");
                return L;
            }

            L.Add($"訓練器 {meta.trainer} · AutoPlay {t.modVersion}" + (string.IsNullOrEmpty(t.playerName) ? "" : $" · 玩家 {t.playerName}"));
            string dev = string.Join(" · ", new[] { t.platform, t.hmd }.Where0());
            if (dev.Length > 0) L.Add($"裝置 {dev} · 遊戲 {t.gameVersions} · BeatLeader {t.beatLeaderVersions}");

            string range = t.mode == "auto"
                ? $"持續追蹤 (最近 {t.rollingDays} 天)"
                : (t.rangeFrom == "" && t.rangeTo == "" ? "不限日期" : $"{(t.rangeFrom == "" ? "最早" : t.rangeFrom)} ~ {(t.rangeTo == "" ? "現在" : t.rangeTo)}");
            L.Add($"範圍 {range} · 同譜最多 {t.perMapCap} 份 · 上限 {t.maxReplays} 份" + (t.minStars > 0 ? $" · 最低 {F(t.minStars)}★" : "")
                  + (t.rankedOnly ? " · 只用 ranked" : "") + (t.preferAcc ? " · acc 優先" : ""));
            if (!string.IsNullOrEmpty(t.selection)) L.Add(t.selection.Replace(" → 去重複後", "\n      → 去重複後"));
            L.Add($"採用 {meta.replayCount} 份 ({t.dataFrom} ~ {t.dataTo}) · {t.uniqueMaps} 張譜 · {meta.swingCount} 次揮刀 · 轉向≤90° {F(t.techShare * 100, "0")}%");
            L.Add($"NPS 中位 {F(t.npsMedian)} · P90 {F(t.npsP90)}" + (m.body != null
                ? $" · 身高設定 {F(m.body.height, "0.00")} m ({F(m.body.heightMin, "0.00")}–{F(m.body.heightMax, "0.00")}) · 實際頭高 {F(m.body.headHeight, "0.00")} m"
                : ""));

            string bl = Star(t.blStars, "BL ★");
            if (bl != null) L.Add(bl + $" · 有星數 {t.blStars.count}/{meta.replayCount}");
            var sub = new List<string>();
            if (t.blTech != null) sub.Add($"tech {F(t.blTech.median)}");
            if (t.blPass != null) sub.Add($"pass {F(t.blPass.median)}");
            if (t.blAcc != null) sub.Add($"acc {F(t.blAcc.median)}");
            if (sub.Count > 0) L.Add("BL 分項中位: " + string.Join(" · ", sub));
            if (t.ssRankedCount > 0) L.Add($"其中 SS ranked {t.ssRankedCount} 份 · SS ★ 中位 {F(t.ssStarsMedian)}");
            L.AddRange(StyleLines(m.style));
            return L;
        }

        /// <summary>個人習慣 (模型的 style); 沒有的專案註明用預設</summary>
        public static List<string> StyleLines(StyleParams st)
        {
            var L = new List<string>();
            string def = "樣本不夠, 用預設";
            var d = st?.dots;
            L.Add("點塊: " + (d == null ? def : $"{d.count} 個 · 下/中/上排往下揮 {Pct(d.downShare, 0)}/{Pct(d.downShare, 1)}/{Pct(d.downShare, 2)} · 往上平均外斜 {F(d.flowUp[0], "0")}°"));
            var l = st?.lean;
            L.Add("閃牆: " + (l == null ? def : $"{l.count} 次 · 最遠 {F(l.peak, "0.00")} m · 歪頭 {F(l.rollPerMeter * l.peak, "0")}° · {F(l.returnTime)} s 回原位"));
            var k = st?.duck;
            L.Add("蹲牆: " + (k == null ? def : $"{k.count} 次 · 下降 {F(k.depth, "0.00")} m · 牆到前 {F(-k.startTime)} s 開始 · 牆到後 {F(k.recoverTime)} s 站回"));
            var b = st?.bombs;
            L.Add("炸彈: " + (b == null ? def : $"{b.count} 顆 · 劍離炸彈 中位 {F(b.clearMedian, "0.00")} m (P10 {F(b.clearP10, "0.00")}) · 劍在上方 {F(b.upShare * 100, "0")}%"));
            return L;
        }

        static string Pct(float[] a, int i) => a != null && i < a.Length && a[i] >= 0f ? F(a[i] * 100, "0") + "%" : "?";

        static IEnumerable<string> Where0(this string[] xs)
        {
            foreach (var x in xs) if (!string.IsNullOrEmpty(x)) yield return x;
        }
    }
}
