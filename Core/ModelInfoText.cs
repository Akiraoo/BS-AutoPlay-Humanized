using System.Collections.Generic;
using System.Globalization;

namespace AutoReplay.Core
{
    /// <summary>把模型资讯排成几行文字 (游戏内设定页和命令行工具共用)</summary>
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
            L.Add($"範圍 {range} · 同譜最多 {t.perMapCap} 份 · 上限 {t.maxReplays} 份" + (t.minStars > 0 ? $" · 最低 {F(t.minStars)}★" : ""));
            if (!string.IsNullOrEmpty(t.selection)) L.Add(t.selection);
            L.Add($"採用 {meta.replayCount} 份 ({t.dataFrom} ~ {t.dataTo}) · {t.uniqueMaps} 張譜 · {meta.swingCount} 次揮刀 · 轉向≤90° {F(t.techShare * 100, "0")}%");
            L.Add($"NPS 中位 {F(t.npsMedian)} · P90 {F(t.npsP90)}");

            string bl = Star(t.blStars, "BL ★");
            if (bl != null) L.Add(bl + $" · 有星數 {t.blStars.count}/{meta.replayCount}");
            var sub = new List<string>();
            if (t.blTech != null) sub.Add($"tech {F(t.blTech.median)}");
            if (t.blPass != null) sub.Add($"pass {F(t.blPass.median)}");
            if (t.blAcc != null) sub.Add($"acc {F(t.blAcc.median)}");
            if (sub.Count > 0) L.Add("BL 分項中位: " + string.Join(" · ", sub));
            if (t.ssRankedCount > 0) L.Add($"其中 SS ranked {t.ssRankedCount} 份 · SS ★ 中位 {F(t.ssStarsMedian)}");
            return L;
        }

        static IEnumerable<string> Where0(this string[] xs)
        {
            foreach (var x in xs) if (!string.IsNullOrEmpty(x)) yield return x;
        }
    }
}
