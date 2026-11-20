using System;
using System.Collections.Generic;
using UnityEngine;

namespace BotPlay
{
    /// <summary>
    /// 從生成好的劍軌跡算每一刀的切擊資料 (回放裡的 noteCutInfo): 前揮 / 後揮角度、切面離方塊中心的距離、提早多少切.
    /// 原本每刀都寫死 115 分 (時間偏移 0、正中心、前後揮 100%), 分數和動作無關, 一看就不是人.
    ///   切的瞬間 = 規劃的命中時間 (NoteSwingData.time), 劍速取離手 0.85 m 的點, 切面 = 劍身方向 × 劍速方向;
    ///   前揮 / 後揮照遊戲的算法: 從切的瞬間往前 / 往後逐幀累加劍身掃角, 每一步的轉軸和切的瞬間轉軸夾角超過 75° 開始打折, 超過 90° 停
    ///   (揮動換了方向). 遊戲的前揮一直算到劍穿過方塊中心的平面, 後揮從劍碰到方塊 (切的瞬間) 開始算, 中間差約 15° (CutLead).
    ///   生成器規劃的命中時刻劍正好在方塊中心, 所以前揮照量、後揮加 CutLead.
    ///   對真人回放校準 (8965 刀, 從遊戲記的切的瞬間量: 前揮 +CutLead、後揮照量): 前揮未滿 4.2% (遊戲記錄 3.4%), 後揮未滿 0.5% (0.3%);
    ///   舊的「命中後 0.1 s × 0.72」會讓連打的後揮一半以上未滿.
    /// 遊戲內和 tools/ApModelTool generate 共用.
    /// </summary>
    public static class CutScorer
    {
        public struct Cut
        {
            public float timeDev;      // 提早切為正 (秒)
            public float before, after;
            public float dist;         // 米
            public float speed;        // 劍速 (米/秒)
            public float dirDev;       // 揮動方向偏離箭頭 (度)
            public Vector3 saberDir, point, normal;
            public int cutScore;       // 這一刀的分數 (不含倍率)
            public int maxScore;
        }

        public const float TipDistance = 0.85f;
        public const float PreWindow = 1.0f, PostWindow = 0.5f, CutLead = 15f, NormalSoft = 75f, NormalMax = 90f;

        static readonly Dictionary<NoteData, Cut> _empty = new Dictionary<NoteData, Cut>();

        /// <summary>按音符查切擊資料 (以物件身分比對, 和 BotPlayMath.NoteCoords 一樣)</summary>
        public static Dictionary<NoteData, Cut> Score(MotionFrames m)
        {
            var res = new Dictionary<NoteData, Cut>(new BotPlayMath.RefEq());
            if (m == null || m.count < 8) return res;
            Score(m, m.leftSwings, m.leftPos, m.leftRot, res);
            Score(m, m.rightSwings, m.rightPos, m.rightRot, res);
            return res;
        }

        static void Score(MotionFrames m, List<NoteSwingData> swings, Vector3[] pos, Quaternion[] rot, Dictionary<NoteData, Cut> res)
        {
            if (swings == null) return;
            int n = m.count;
            var f = new Vector3[n];
            for (int i = 0; i < n; i++) f[i] = (rot[i] * Vector3.forward).normalized;
            Vector3 Tip(int i) => pos[i] + f[i] * TipDistance;
            foreach (var sw in swings)
            {
                if (sw.note == null) continue;
                int i = Mathf.Clamp(Mathf.RoundToInt((sw.time - m.t0) / m.dt), 2, n - 3);
                Vector3 v = (Tip(i + 1) - Tip(i - 1)) / (2f * m.dt);
                Vector3 nrm = Vector3.Cross(f[i], v);
                nrm = nrm.sqrMagnitude > 1e-10f ? nrm.normalized : Vector3.right;
                Vector3 c = sw.noteCenter;
                float signed = Vector3.Dot(c - pos[i], nrm);
                Vector3 axis = Vector3.Cross(f[i - 1], f[i + 1]);
                axis = axis.sqrMagnitude > 1e-12f ? axis.normalized : nrm;
                float before = SwingAngle(f, i, -1, axis, Mathf.RoundToInt(PreWindow / m.dt)) / 100f;
                float after = (SwingAngle(f, i, +1, axis, Mathf.RoundToInt(PostWindow / m.dt)) + CutLead) / 60f;

                float dev = 0f;
                float mx = v.x, my = v.y, ml = Mathf.Sqrt(mx * mx + my * my);
                Vector2 nd = ArrowDir(sw.cutDirection);
                if (ml > 1e-4f && (nd.x != 0f || nd.y != 0f))
                {
                    mx /= ml; my /= ml;
                    dev = Mathf.Atan2(nd.x * my - nd.y * mx, nd.x * mx + nd.y * my) * Mathf.Rad2Deg;
                    dev = Mathf.Clamp(dev, -59f, 59f);   // 超過 60° 遊戲會判成方向錯 (bad cut); 規劃的方向是對的, 偏差只來自量測
                }

                var cut = new Cut
                {
                    timeDev = sw.note.time - sw.time,
                    before = before, after = after,
                    dist = Mathf.Abs(signed),
                    speed = v.magnitude,
                    dirDev = dev,
                    saberDir = v.sqrMagnitude > 1e-10f ? v.normalized : Vector3.down,
                    point = c - nrm * signed,
                    normal = nrm,
                };
                CutScore(sw.note, before, after, cut.dist, out cut.cutScore, out cut.maxScore);
                res[sw.note] = cut;
            }
        }

        /// <summary>
        /// 從 i0 往前 (step=-1) 或往後 (step=+1) 累積劍身掃過的角度 (遊戲的 BeforeCut / AfterCutStepRating):
        /// 每一步的轉軸和 axisRef 夾角 nd, 權重 1 - clamp01((nd - 75) / 15), nd 超過 90° 就停
        /// </summary>
        static float SwingAngle(Vector3[] f, int i0, int step, Vector3 axisRef, int limit)
        {
            float tot = 0f; int i = i0;
            while (i > 0 && i < f.Length - 1 && Math.Abs(i - i0) < limit)
            {
                Vector3 a = f[i], b = f[i + step];
                Vector3 c = Vector3.Cross(a, b) * step;
                float sn = c.magnitude;
                float ang = Mathf.Atan2(sn, Vector3.Dot(a, b)) * Mathf.Rad2Deg;
                i += step;
                if (ang <= 0.02f) continue;
                float nd = Mathf.Acos(Mathf.Clamp(Vector3.Dot(c / sn, axisRef), -1f, 1f)) * Mathf.Rad2Deg;
                if (nd > NormalMax) break;
                tot += ang * (1f - Mathf.Clamp01((nd - NormalSoft) / (NormalMax - NormalSoft)));
            }
            return tot;
        }

        static Vector2 ArrowDir(int d)
        {
            const float s = 0.7071f;
            switch (d)
            {
                case 0: return new Vector2(0, 1);
                case 1: return new Vector2(0, -1);
                case 2: return new Vector2(-1, 0);
                case 3: return new Vector2(1, 0);
                case 4: return new Vector2(-s, s);
                case 5: return new Vector2(s, s);
                case 6: return new Vector2(-s, -s);
                case 7: return new Vector2(s, -s);
                default: return Vector2.zero;
            }
        }

        /// <summary>遊戲的計分: 前揮 0~70, 後揮 0~30, 準度 0~15 (離中心 0.3 m 以上 0 分); 滑條 / 鏈條的頭尾有固定分</summary>
        public static void CutScore(NoteData note, float before, float after, float dist, out int score, out int max)
        {
            int pre = Mathf.RoundToInt(70f * Mathf.Clamp01(before));
            int post = Mathf.RoundToInt(30f * Mathf.Clamp01(after));
            int acc = Mathf.RoundToInt(15f * (1f - Mathf.Clamp01(dist / 0.3f)));
            switch (note.scoringType.ToString())
            {
                case "ArcHead": score = pre + 30 + acc; max = 115; break;
                case "ArcTail": score = 70 + post + acc; max = 115; break;
                case "ArcHeadArcTail": score = 70 + 30 + acc; max = 115; break;
                case "ChainHead": score = pre + acc; max = 85; break;
                case "ChainHeadArcTail": score = 70 + acc; max = 85; break;
                case "ChainLink":
                case "ChainLinkArcHead": score = 20; max = 20; break;
                default: score = pre + post + acc; max = 115; break;
            }
        }
    }
}
