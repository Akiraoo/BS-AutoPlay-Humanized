using System;
using System.Collections.Generic;
using UnityEngine;
using Vector3 = UnityEngine.Vector3;
using Quaternion = UnityEngine.Quaternion;

namespace AutoReplay
{
    /// <summary>頭部目標姿態 (還沒平滑): 位置 + 俯仰/滾轉角</summary>
    public struct HeadTarget
    {
        public Vector3 position;
        public float pitch;
        public float roll;
    }

    /// <summary>
    /// 一隻手的待機微動所需的時間資訊: 命中時刻和炸彈時刻附近要"穩住", 不加微動.
    /// </summary>
    public class HandMotionContext
    {
        public readonly float[] hitTimes;
        public readonly Vector3[] hitTargets;   // 和 hitTimes 一一對應: 方塊中心
        public readonly int[] hitDirs;          // 和 hitTimes 一一對應: 切割方向 (反手扭轉用)
        public readonly float[] bombTimes;
        public readonly float seed;
        // 手臂模板用: 每一刀 (疊塊/幾乎同時的合成一刀) 的時間、揮刀座標系、方向分類、和前後一刀的較短間隔
        public readonly float[] armTimes;
        public readonly Vector3[] armD, armN, armF;
        public readonly int[] armDir;
        public readonly float[] armGap;
        // 身體平移 (躲牆時整個人往旁邊/往下移, 手也跟著走). 由 AutoReplayGenerator 根據頭的軌跡提供
        public Func<float, Vector3> bodyOffset = t => Vector3.zero;

        public HandMotionContext(List<NoteSwingData> swings, List<NoteData> bombs, float seed)
        {
            int n = swings?.Count ?? 0;
            hitTimes = new float[n];
            hitTargets = new Vector3[n];
            hitDirs = new int[n];
            for (int i = 0; i < n; i++) { hitTimes[i] = swings[i].time; hitTargets[i] = swings[i].target; hitDirs[i] = swings[i].cutDirection; }
            // swings 本來就按時間排好序

            var at = new List<int>();
            for (int i = 0; i < n; i++)
                if (at.Count == 0 || swings[i].time - swings[at[at.Count - 1]].time > 0.06f) at.Add(i);
            int m = at.Count;
            armTimes = new float[m]; armD = new Vector3[m]; armN = new Vector3[m]; armF = new Vector3[m]; armDir = new int[m]; armGap = new float[m];
            for (int k = 0; k < m; k++)
            {
                var sw = swings[at[k]];
                Vector3 fc = (sw.hitPose * Vector3.forward).normalized;
                Vector3 d = sw.swingDir;
                if (d.sqrMagnitude < 1e-6f) d = Vector3.Cross(fc, Vector3.right);
                d = (d - fc * Vector3.Dot(d, fc)).normalized;
                armTimes[k] = sw.time; armF[k] = fc; armD[k] = d; armN[k] = Vector3.Cross(fc, d);
                armDir[k] = d.y < -0.5f ? 0 : d.y > 0.5f ? 1 : 2;
            }
            for (int k = 0; k < m; k++)
            {
                float gp = k > 0 ? armTimes[k] - armTimes[k - 1] : 3f, gn = k + 1 < m ? armTimes[k + 1] - armTimes[k] : 3f;
                armGap[k] = Mathf.Min(gp, gn);
            }

            bombTimes = new float[bombs?.Count ?? 0];
            for (int i = 0; i < bombTimes.Length; i++) bombTimes[i] = bombs[i].time;
            Array.Sort(bombTimes);

            this.seed = seed;
        }
    }

    public static class PoseCalculator
    {
        // ================= 待機微動引數 =================
        // 真人 replay: 手幾乎從不完全靜止 (角速度 <20°/s 的時間約 1%), 頭的朝向也有約 2° 的晃動.
        // 原本 4° / 2 cm 而且頻率太低: 長間隔裡劍幾乎靜止 (3~5°/s), 真人是 30~50°/s. 現在按真人 replay 校準 (見 AutoReplayMath.HandNoise)
        public const float HandIdleAngle = 9.0f;     // 手的旋轉微動幅度(度)
        public const float HandIdlePos = 0.05f;      // 手的位置微動幅度(米)
        public const float HitCalmInner = 0.05f;     // 命中前後這麼近完全不加旋轉微動 (保證劍尖準確穿過方塊)
        public const float HitCalmRamp = 0.15f;      // 再往外這麼長時間內微動逐漸恢復
        public const float BombCalmBefore = 0.40f;   // 炸彈到達前這麼久開始收掉旋轉微動 (躲避姿勢不被打亂)
        public static float BombSway = 18f;          // 炸彈經過時劍在躲避姿勢附近慢慢晃的幅度 (度)
        public const float BombCalmAfter = 0.25f;    // 炸彈過去後這麼久才恢復

        public const float HeadIdleX = 0.020f, HeadIdleY = 0.006f, HeadIdleZ = 0.015f;   // 頭的位置微動(米)
        public const float HeadIdleYaw = 2.0f, HeadIdlePitch = 1.5f, HeadIdleRoll = 1.0f; // 頭的旋轉微動(度)

        // 躲牆時身體平移對手的影響: 頭往旁邊移 x, 手跟著移 x*BodyFollowX
        public const float BodyFollowX = 0.85f;
        public const float BodyFollowY = 0.85f;   // 真人蹲牆時手的下降約是頭的 0.81~0.86 倍
        // 躲牆的時機和幅度見下面的 DuckAmount (蹲) / LeanOffset (左右閃), 都照真人回放的曲線
        public const float DuckPitch = 1.5f;      // 真人蹲牆幾乎不低頭 (1~2°), 原本 5°

        // 軌道編號(從左到右): 第1軌 = lineIndex 0, 第2軌 = 1, 第3軌 = 2, 第4軌 = 3
        // 規則:
        //   - 第1軌 / 第4軌 的掩體不管 (牆只佔外側軌道時直接忽略, 跨到中間的牆只看它在 2-3 軌的部分)
        //   - 頂部掩體 (天花板牆) 同時蓋住第2軌和第3軌 -> 蹲下 (可以是同一面牆, 也可以是兩面牆同時存在)
        //   - 其他情況只蓋住一條中間軌道 -> 歪頭躲, 不蹲
        // 返回的是目標姿態, 平滑由呼叫方負責
        public static HeadTarget CalculateHeadTarget(float t, List<ObstacleData> obstacles, bool mirror = false)
        {
            float targetY = AutoReplayMath.HeadHeight;
            float targetPitch = 0f;

            // 蹲下: 照真人的蹲牆曲線 (DuckAmount)
            float duck = DuckAmount(t, obstacles, mirror);
            if (duck > 0f)
            {
                targetY = Mathf.Lerp(targetY, Mathf.Min(targetY, AutoReplayMath.DuckHeight), duck);
                targetPitch = Mathf.Max(targetPitch, DuckPitch * duck);
            }

            // 左右閃: 照真人的閃牆曲線 (LeanOffset)
            float leanX = LeanOffset(t, obstacles, mirror);
            float roll = Mathf.Clamp(-leanX * LeanRollPerMeter, -LeanMaxRoll, LeanMaxRoll);
            return new HeadTarget { position = new Vector3(leanX, targetY, 0f), pitch = targetPitch, roll = roll };
        }

        // ================= 左右閃牆曲線 =================
        // 真人 60 份回放 / 410 次孤立的閃牆 (只蓋住中間一條軌道的全高牆), 以牆到達為 0, 往牆的反方向為正:
        //   橫移 -0.3s +0.02 | -0.1 +0.06 | 0 +0.09 | +0.2 +0.15 | +0.3 +0.15 (最遠) | +0.6 +0.11 | +1.0 +0.04 m
        //   歪頭 最大中位 6.8° (P10 3.8 ~ P90 11.0); 最遠橫移中位 0.16 m (P10 0.10 ~ P90 0.21), 每次都不一樣.
        // 原本: 牆到前 0.3 秒目標瞬間切到 0.42~0.45 m / 12°, 牆一過就彈回 -> 幅度是真人的 2.7 倍, 而且每次一樣, 牆多就暈.
        public const float LeanBase = 0.16f;           // 真人最遠橫移中位數
        public const float LeanVariation = 0.25f;      // 每次隨機 ±25% (按牆的時間決定, 每次生成一樣)
        public const float LeanClear = 0.12f;          // 頭離牆邊至少要這麼遠
        public const float LeanStart = 0.35f;          // 牆到前多久開始移
        public const float LeanRise = 0.45f;           // 移到最遠要多久 (牆到時已移了約 90%: 真人最遠點在牆過後 0.2~0.3 s,
                                                       //  但牆到時只移了 9 cm, 照抄會擦到牆邊)
        public const float LeanHold = 0.12f;           // 牆過去後保持多久
        public const float LeanReturn = 0.90f;         // 回到原位要多久
        public const float LeanRollPerMeter = 40f;     // 歪頭角度 = 橫移 * 這個 (0.16 m -> 6.4°)
        public const float LeanMaxRoll = 14f;

        static List<ObstacleData> _leanSrc;
        static bool _leanMirror;
        static List<(float s, float e, float amp)> _leanEvents;   // amp > 0 往右, < 0 往左

        public static float LeanOffset(float t, List<ObstacleData> obstacles, bool mirror)
        {
            if (!ReferenceEquals(_leanSrc, obstacles) || _leanMirror != mirror)
            {
                _leanSrc = obstacles; _leanMirror = mirror;
                _leanEvents = BuildLeanEvents(obstacles, mirror);
            }
            // 按時間順序: 新的牆從上一道牆留下的位置接手 (左右交替的牆 -> 頭左右來回擺; 原本兩邊相加會互相抵消, 頭停在中間撞牆)
            float v = 0f;
            foreach (var (s0, e0, amp) in _leanEvents)
            {
                if (t < s0 - LeanStart) break;
                float up = AutoReplayMath.SmoothStep((t - (s0 - LeanStart)) / LeanRise);
                v = Mathf.Lerp(v, Mathf.Abs(v) > Mathf.Abs(amp) && Mathf.Sign(v) == Mathf.Sign(amp) ? v : amp, up);
                if (t > e0 + LeanHold) v *= 1f - AutoReplayMath.SmoothStep((t - (e0 + LeanHold)) / LeanReturn);
            }
            return v;
        }

        // 要閃的牆: 擋到頭 (不是矮牆、也不是能蹲過去的頂牆), 蓋住中間的第 2 或第 3 軌
        static List<(float, float, float)> BuildLeanEvents(List<ObstacleData> obstacles, bool mirror)
        {
            var res = new List<(float, float, float)>();
            if (obstacles == null) return res;
            foreach (var obs in obstacles)
            {
                if (WallBelowHead(obs)) continue;
                int lineIndex = mirror ? 4 - (obs.lineIndex + obs.width) : obs.lineIndex;
                int c0 = Mathf.Max(lineIndex, 1), c1 = Mathf.Min(lineIndex + obs.width - 1, 2);
                if (c0 > c1) continue;
                bool lane2 = c0 <= 1 && c1 >= 1, lane3 = c0 <= 2 && c1 >= 2;
                if (IsCeilingWall(obs) && lane2 && lane3) continue;           // 蹲 (DuckAmount)
                // 牆邊離中線多遠: 只蓋第 2 軌 -> 牆的右邊在 x=0; 兩軌都蓋 -> 牆的右邊在 x=0.6
                float edge = lane2 && lane3 ? 0.6f : 0f;
                float dir = lane2 ? 1f : -1f;                                // 牆在左中 -> 往右閃
                // 整數雜湊 (牆的時間取到 1/100 秒): 原本 sin(大數) 對浮點誤差太敏感, 遊戲內和遊戲外算出的牆時間差一點點就變成完全不同的幅度
                uint hk = (uint)Mathf.RoundToInt(obs.time * 100f) * 2654435761u ^ (uint)(obs.lineIndex + 7) * 40503u;
                hk ^= hk >> 15; hk *= 2246822519u; hk ^= hk >> 13;
                float h = (hk & 0xFFFFu) / 65535f;
                float amp = Mathf.Max(edge + LeanClear, LeanBase * (1f + LeanVariation * (2f * h - 1f)));
                res.Add((obs.time, obs.time + obs.duration, dir * amp));
            }
            res.Sort((x, y) => x.Item1.CompareTo(y.Item1));
            return res;
        }

        // ================= 牆的高度 =================
        // 原本把 "不在最底層 或 高度 <= 3" 都當成頂牆要蹲: 從地板往上只到約 1.3 m 的矮牆 (y=0, h=2) 也蹲,
        // 結果頭反而蹲進牆裡 (WIP 譜 M.C.ドナルド 36.8 s: 頭 1.09 m, 牆頂約 1.3 m). 現在照牆實際佔的高度判斷:
        //   牆頂比站著的頭低 -> 不用躲;  牆底比蹲下的頭還高 -> 頂牆, 蹲;  其餘 (從低處一路蓋過頭) -> 只能往旁邊閃
        // 高度規則: 底邊 = 層高 (0.25 + 0.6*層) - 0.15 + 身高偏移, 高 = h * 0.6 (tools/wall_eval.py 用同一套, 和真人回放對得上)
        public const float HeadClear = 0.15f;   // 頭 (連同頭顯) 和牆之間要留的距離

        public static float WallBottom(ObstacleData o) => 0.25f + 0.6f * (int)o.lineLayer - 0.15f + AutoReplayMath.NoteYOffset;
        public static float WallTop(ObstacleData o) => WallBottom(o) + 0.6f * o.height;
        public static bool WallBelowHead(ObstacleData o) => WallTop(o) <= AutoReplayMath.HeadHeight - HeadClear;
        public static bool IsCeilingWall(ObstacleData o) => WallBottom(o) >= AutoReplayMath.DuckHeight + HeadClear * 0.8f;

        // ================= 蹲牆曲線 =================
        // 真人 17 份回放 / 146 次獨立蹲牆 (蓋住中間兩軌的頂牆), 以牆到達為 0:
        //   頭 Δy  -0.3s -0.04 | -0.2 -0.15 | -0.1 -0.32 | 0 -0.46 | +0.1 -0.54 | +0.2 -0.56 (最低) | +0.4 -0.45 | +0.6 -0.20 | +0.8 -0.06 m
        // 也就是: 牆到前約 0.3 秒開始蹲, 牆過去後 0.1~0.2 秒最低, 站起來比蹲下慢 (約 0.6~0.8 秒); 頭幾乎不低 (1~2°).
        // 原本: 牆到前 0.3 秒目標瞬間切到蹲, 彈簧 0.07 秒 -> 0.1 秒內猛蹲 42 cm, 牆一過 0.2 秒內彈回, 還低頭 5°.
        public const float DuckDownStart = 0.40f;   // 牆到前多久開始蹲 (含頭部彈簧約 0.05 秒的延遲)
        public const float DuckDownEnd = 0.05f;     // 牆到後多久蹲到底
        public const float DuckHoldAfter = 0.07f;   // 牆過去後還保持多久
        public const float DuckUpTime = 0.65f;      // 站起來的時間

        static List<ObstacleData> _duckSrc;
        static bool _duckMirror;
        static List<(float s, float e)> _duckIntervals;
        static List<(float s, float e, float cap)> _lowWalls;   // 矮牆經過時最多能蹲多少 (0 = 不能蹲)
        public const float LowWallRise = 0.25f;     // 矮牆到前多久要站到夠高 (真人先蹲後站時會很快站起來)

        /// <summary>0 = 站著, 1 = 蹲到底</summary>
        public static float DuckAmount(float t, List<ObstacleData> obstacles, bool mirror)
        {
            if (!ReferenceEquals(_duckSrc, obstacles) || _duckMirror != mirror)
            {
                _duckSrc = obstacles; _duckMirror = mirror;
                _duckIntervals = BuildDuckIntervals(obstacles, mirror);
                _lowWalls = BuildLowWalls(obstacles, mirror);
            }
            float best = 0f;
            foreach (var (s0, e0) in _duckIntervals)
            {
                if (t < s0 - DuckDownStart || t > e0 + DuckHoldAfter + DuckUpTime) continue;
                float down = AutoReplayMath.SmoothStep((t - (s0 - DuckDownStart)) / (DuckDownStart + DuckDownEnd));
                float up = 1f - AutoReplayMath.SmoothStep((t - (e0 + DuckHoldAfter)) / DuckUpTime);
                best = Mathf.Max(best, Mathf.Min(down, up));
            }
            // 矮牆 (先蹲後站的組合): 矮牆經過時頭要高過牆頂, 來不及就站快一點
            if (best > 0f)
                foreach (var (s0, e0, cap) in _lowWalls)
                {
                    if (t < s0 - LowWallRise || t > e0 + 0.15f) continue;
                    float rise = AutoReplayMath.SmoothStep((t - (s0 - LowWallRise)) / (LowWallRise - 0.03f));
                    float release = 1f - AutoReplayMath.SmoothStep((t - (e0 + 0.03f)) / 0.12f);
                    float w = Mathf.Min(rise, release);
                    best = Mathf.Min(best, Mathf.Lerp(1f, cap, w));
                }
            return Mathf.Max(0f, best);
        }

        // 蓋住中間軌道 (第 2 或 3 軌) 的矮牆: 頭要高過牆頂 + HeadClear
        static List<(float, float, float)> BuildLowWalls(List<ObstacleData> obstacles, bool mirror)
        {
            var res = new List<(float, float, float)>();
            if (obstacles == null) return res;
            float range = AutoReplayMath.HeadHeight - AutoReplayMath.DuckHeight;
            foreach (var obs in obstacles)
            {
                if (!WallBelowHead(obs)) continue;
                int lineIndex = mirror ? 4 - (obs.lineIndex + obs.width) : obs.lineIndex;
                int c0 = Mathf.Max(lineIndex, 1), c1 = Mathf.Min(lineIndex + obs.width - 1, 2);
                if (c0 > c1) continue;
                float need = WallTop(obs) + HeadClear;                         // 頭至少要這麼高
                if (need <= AutoReplayMath.DuckHeight) continue;               // 蹲到底也碰不到
                float cap = range > 1e-3f ? Mathf.Clamp01((AutoReplayMath.HeadHeight - need) / range) : 0f;
                res.Add((obs.time, obs.time + obs.duration, cap));
            }
            return res;
        }

        // 需要蹲的時間段: 頂部的牆 (或幾面頂牆合起來) 同時蓋住第 2、3 軌
        static List<(float, float)> BuildDuckIntervals(List<ObstacleData> obstacles, bool mirror)
        {
            var l2 = new List<(float, float)>(); var l3 = new List<(float, float)>();
            if (obstacles != null)
                foreach (var obs in obstacles)
                {
                    if (WallBelowHead(obs) || !IsCeilingWall(obs)) continue;
                    int lineIndex = mirror ? 4 - (obs.lineIndex + obs.width) : obs.lineIndex;
                    int c0 = Mathf.Max(lineIndex, 1), c1 = Mathf.Min(lineIndex + obs.width - 1, 2);
                    if (c0 > c1) continue;
                    var iv = (obs.time, obs.time + obs.duration);
                    if (c0 <= 1 && c1 >= 1) l2.Add(iv);
                    if (c0 <= 2 && c1 >= 2) l3.Add(iv);
                }
            var res = new List<(float, float)>();
            foreach (var a in l2)
                foreach (var b in l3)
                {
                    float s0 = Mathf.Max(a.Item1, b.Item1), e0 = Mathf.Min(a.Item2, b.Item2);
                    if (e0 >= s0 - 1e-4f) res.Add((s0, e0));
                }
            return res;
        }

        /// <summary>頭的待機微動 (疊加在平滑後的姿態上)</summary>
        public static (Vector3 position, Quaternion rotation) ApplyHeadIdle(float t, Vector3 pos, float pitch, float roll)
        {
            pos += new Vector3(
                HeadIdleX * AutoReplayMath.Noise(t * 0.6f, 11f),
                HeadIdleY * AutoReplayMath.Noise(t * 0.9f, 12f),
                HeadIdleZ * AutoReplayMath.Noise(t * 0.5f, 13f));
            var rot = Quaternion.Euler(
                pitch + HeadIdlePitch * AutoReplayMath.Noise(t * 0.7f, 14f),
                HeadIdleYaw * AutoReplayMath.Noise(t * 0.45f, 15f),
                roll + HeadIdleRoll * AutoReplayMath.Noise(t * 0.55f, 16f));
            return (pos, rot);
        }

        // ================= 平滑引數 =================
        public const int Oversample = 4;            // 內部按 4 倍幀率取樣, 平滑後再抽回輸出幀率
        public const float RotSmoothSigma = 0.010f; // 最終姿態的高斯平滑 (秒): 去掉關鍵幀折線的稜角
        public const float RollSmoothSigma = 0.030f;// 扭轉趨勢先在時間上平均
        public const float RollTau = 0.035f;        // 扭轉角跟隨真人模型的時間常數 (彈簧)
        public const float MaxRollSpeed = 1500f;    // 扭轉角速度上限 (度/秒): 前臂旋前/旋後轉不了更快 -> 不會再"一幀翻半圈"
        public const float RollConfidence = 0.35f;  // 真人模型的扭轉向量長度低於這個值 -> 信心不足, 彈簧變軟, 保持當前扭轉
        public static float ArmSmoothSigma = 0.050f; // 手臂(位置)跟隨劍指向的延遲: 手臂比手腕重, 快速甩劍主要是手腕在動
        public const float HitPinWindow = 0.040f;   // 命中前後這麼長時間內把劍拉回精確對準方塊
        public const int AimIterations = 3;         // 手臂位置 <-> 命中對準 的迭代次數 (多算幾次換更準的軌跡)

        /// <summary>
        /// 生成一隻手每一幀的姿態. 流程:
        ///   模板軌道 -> 待機微動 -> 指向軟限位 -> 扭轉(彈簧跟隨真人模型) -> 高斯平滑
        ///   -> [手臂位置(延遲跟隨 + 身體平移) -> 命中幀對準方塊] x AimIterations
        /// </summary>
        public static void BuildHandTrack(float t0, float dt, int count, List<NoteSwingData> swings, List<NoteData> bombs,
                                          bool isLeft, HandMotionContext ctx, Vector3[] outPos, Quaternion[] outRot)
        {
            float sdt = dt / Oversample;
            int pad = Mathf.CeilToInt(0.25f / sdt);
            int ns = count * Oversample + 2 * pad;

            var fwd = new Vector3[ns];
            var rv = new Vector2[ns];
            for (int i = 0; i < ns; i++)
            {
                Vector3 f = RawForward(t0 + (i - pad) * sdt, swings, bombs, isLeft, ctx);
                fwd[i] = f;
                rv[i] = HumanModel.RollVector(isLeft, f);
            }

            // ---- 扭轉: 真人模型給目標, 彈簧跟隨 (正向+反向平均, 無延遲), 限速 ----
            var rvs = SmoothVec2(rv, RollSmoothSigma / sdt);
            var rollF = RollSpring(rvs, sdt, false);
            var rollB = RollSpring(rvs, sdt, true);
            var q = new Quaternion[ns];
            int hc = 0;
            for (int i = 0; i < ns; i++)
            {
                float t = t0 + (i - pad) * sdt;
                float roll = rollF[i] + 0.5f * Mathf.DeltaAngle(rollF[i], rollB[i]);
                // 反手: 切橫向/斜向方塊的瞬間前臂會大幅旋轉, 按 (手, 切割方向) 疊加真人的扭轉偏移曲線
                while (hc < ctx.hitTimes.Length && ctx.hitTimes[hc] < t - RollOffsets.Window) hc++;
                float best = 0f, bestD = float.MaxValue;
                for (int h = hc; h < ctx.hitTimes.Length && ctx.hitTimes[h] <= t + RollOffsets.Window; h++)
                {
                    float d = Mathf.Abs(t - ctx.hitTimes[h]);
                    if (d < bestD) { bestD = d; best = RollOffsets.At(isLeft, ctx.hitDirs[h], t - ctx.hitTimes[h]); }
                }
                q[i] = HumanModel.Compose(fwd[i], roll + best);
            }
            q = SmoothQuat(q, RotSmoothSigma / sdt);

            // ---- 手臂位置 <-> 命中對準 迭代 ----
            var wrist = new Vector3[ns];
            int pinR = Mathf.CeilToInt(HitPinWindow / sdt) + 1;
            for (int it = 0; it < AimIterations; it++)
            {
                ComputeWrists(q, isLeft, ctx, t0, pad, sdt, wrist);
                for (int h = 0; h < ctx.hitTimes.Length; h++)
                {
                    float th = ctx.hitTimes[h];
                    float u = (th - t0) / sdt + pad;
                    int i0 = Mathf.FloorToInt(u);
                    if (i0 < 0 || i0 + 1 >= ns) continue;
                    float fr = u - i0;
                    Vector3 curF = (Quaternion.Slerp(q[i0], q[i0 + 1], fr) * Vector3.forward).normalized;
                    Vector3 w = Vector3.Lerp(wrist[i0], wrist[i0 + 1], fr);
                    Vector3 exactF = (ctx.hitTargets[h] - w).normalized;
                    Quaternion corr = Quaternion.FromToRotation(curF, exactF);
                    for (int i = Mathf.Max(0, i0 - pinR); i <= Mathf.Min(ns - 1, i0 + 1 + pinR); i++)
                    {
                        float d = Mathf.Abs((i - u) * sdt) / HitPinWindow;
                        if (d >= 1f) continue;
                        float k = 1f - AutoReplayMath.SmoothStep(d);
                        q[i] = Quaternion.Slerp(Quaternion.identity, corr, k) * q[i];
                    }
                }
            }
            ComputeWrists(q, isLeft, ctx, t0, pad, sdt, wrist);

            for (int j = 0; j < count; j++)
            {
                int i = j * Oversample + pad;
                outPos[j] = wrist[i];
                outRot[j] = q[i];
            }
        }

        // 手腕位置 = 人體模型(延遲跟隨的劍指向) + 身體平移 + 位置微動
        static void ComputeWrists(Quaternion[] q, bool isLeft, HandMotionContext ctx, float t0, int pad, float sdt, Vector3[] wrist)
        {
            int ns = q.Length;
            var f = new Vector3[ns];
            for (int i = 0; i < ns; i++) f[i] = q[i] * Vector3.forward;
            var fs = SmoothVec3(f, ArmSmoothSigma / sdt);
            float s = ctx.seed;
            for (int i = 0; i < ns; i++)
            {
                float t = t0 + (i - pad) * sdt;
                Vector3 dirSlow = fs[i].sqrMagnitude > 1e-6f ? fs[i].normalized : f[i];
                wrist[i] = HumanModel.WristForDir(isLeft, dirSlow) + ctx.bodyOffset(t) + ArmOffset(t, isLeft, ctx) + new Vector3(
                    HandIdlePos * AutoReplayMath.HandPosNoise(t, s + 3f),
                    HandIdlePos * AutoReplayMath.HandPosNoise(t * 0.9f, s + 4f),
                    HandIdlePos * AutoReplayMath.HandPosNoise(t * 0.8f, s + 5f));
            }
        }

        /// <summary>
        /// 手臂模板的位移: 上一刀的後半段和下一刀的前半段, 按在間隔裡的位置平滑交接 (和指向的模板一樣)
        /// </summary>
        static Vector3 ArmOffset(float t, bool isLeft, HandMotionContext ctx)
        {
            int m = ctx.armTimes.Length;
            if (m == 0 || !ArmTemplates.Available) return Vector3.zero;
            float sgn = isLeft ? -1f : 1f;
            Vector3 At(int k)
            {
                Vector3 r = ArmTemplates.Eval(ctx.armDir[k], ctx.armGap[k], t - ctx.armTimes[k]);
                return ctx.armD[k] * r.x + ctx.armN[k] * (r.y * sgn) + ctx.armF[k] * r.z;
            }
            if (t <= ctx.armTimes[0]) return At(0);
            if (t >= ctx.armTimes[m - 1]) return At(m - 1);
            int lo = 0, hi = m - 1;
            while (hi - lo > 1) { int mid = (lo + hi) >> 1; if (ctx.armTimes[mid] <= t) lo = mid; else hi = mid; }
            float w = AutoReplayMath.SmoothStep((t - ctx.armTimes[lo]) / Mathf.Max(1e-4f, ctx.armTimes[hi] - ctx.armTimes[lo]));
            return At(lo) * (1f - w) + At(hi) * w;
        }

        // 扭轉角彈簧: 目標 = 真人模型的扭轉; 模型信心低時彈簧變軟 (保持當前扭轉), 速度有上限
        static float[] RollSpring(Vector2[] target, float sdt, bool backward)
        {
            int n = target.Length;
            var o = new float[n];
            float omega0 = 2f / RollTau;
            int first = backward ? n - 1 : 0, step = backward ? -1 : 1;
            float roll = Mathf.Atan2(target[first].y, target[first].x) * Mathf.Rad2Deg, vel = 0f;
            for (int c = 0, i = first; c < n; c++, i += step)
            {
                Vector2 v = target[i];
                float conf = Mathf.Clamp01(v.magnitude / RollConfidence);
                float omega = omega0 * conf * conf;
                float err = Mathf.DeltaAngle(roll, Mathf.Atan2(v.y, v.x) * Mathf.Rad2Deg);
                float damp = 2f * Mathf.Max(omega, 0.35f * omega0);
                vel += (omega * omega * err - damp * vel) * sdt;
                vel = Mathf.Clamp(vel, -MaxRollSpeed, MaxRollSpeed);
                roll += vel * sdt;
                o[i] = roll;
            }
            return o;
        }

        static Vector3[] SmoothVec3(Vector3[] v, float sigma)
        {
            var k = Kernel(sigma); int r = k.Length / 2, n = v.Length;
            var o = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                Vector3 acc = Vector3.zero;
                for (int j = -r; j <= r; j++) acc += v[Mathf.Clamp(i + j, 0, n - 1)] * k[j + r];
                o[i] = acc;
            }
            return o;
        }

        // 規劃軌道 + 待機微動 + 軟限位 之後的劍指向
        static Vector3 RawForward(float t, List<NoteSwingData> swings, List<NoteData> bombs, bool isLeft, HandMotionContext ctx)
        {
            Quaternion rot = NoteSwingPlanner.SampleHandRotation(t, swings, bombs, isLeft);
            float w = IdleWeight(t, ctx, out float bombNear);
            if (w > 0f)
            {
                float s = ctx.seed;
                var jitter = Quaternion.Euler(
                    HandIdleAngle * w * AutoReplayMath.HandNoise(t, s + 1f),
                    HandIdleAngle * w * AutoReplayMath.HandNoise(t * 0.83f, s + 2f),
                    0f);
                rot = jitter * rot;
            }
            if (bombNear > 0f && BombSway > 0f)
            {
                // 炸彈經過時劍不要僵住: 慢慢在躲避姿勢附近晃 (真人炸彈經過時劍角速度中位約 54°/s; 原本是 0).
                // 晃到太靠近炸彈的部分, 最後由 MotionGenerator.AvoidBombs 推開
                float s = ctx.seed;
                float wb = bombNear * AutoReplayMath.SmoothStep((NearestDistance(ctx.hitTimes, t) - HitCalmInner) / HitCalmRamp);
                var sway = Quaternion.Euler(
                    BombSway * wb * AutoReplayMath.HandNoise(t * 1.7f, s + 21f),
                    BombSway * wb * AutoReplayMath.HandNoise(t * 1.5f, s + 22f),
                    0f);
                rot = sway * rot;
            }
            return HumanModel.LimitForward(isLeft, rot * Vector3.forward);
        }

        static float[] Kernel(float sigma)
        {
            int r = Mathf.Max(1, Mathf.CeilToInt(3f * sigma));
            var k = new float[2 * r + 1];
            float sum = 0f;
            for (int i = -r; i <= r; i++) { k[i + r] = Mathf.Exp(-0.5f * i * i / (sigma * sigma)); sum += k[i + r]; }
            for (int i = 0; i < k.Length; i++) k[i] /= sum;
            return k;
        }

        static Vector2[] SmoothVec2(Vector2[] v, float sigma)
        {
            var k = Kernel(sigma); int r = k.Length / 2, n = v.Length;
            var o = new Vector2[n];
            for (int i = 0; i < n; i++)
            {
                Vector2 acc = Vector2.zero;
                for (int j = -r; j <= r; j++) acc += v[Mathf.Clamp(i + j, 0, n - 1)] * k[j + r];
                o[i] = acc;
            }
            return o;
        }

        static Quaternion[] SmoothQuat(Quaternion[] q, float sigma)
        {
            int n = q.Length;
            var a = new Vector4[n];
            for (int i = 0; i < n; i++)
            {
                var v = new Vector4(q[i].x, q[i].y, q[i].z, q[i].w);
                if (i > 0 && Vector4.Dot(v, a[i - 1]) < 0f) v = -v;
                a[i] = v;
            }
            var k = Kernel(sigma); int r = k.Length / 2;
            var o = new Quaternion[n];
            for (int i = 0; i < n; i++)
            {
                Vector4 acc = Vector4.zero;
                for (int j = -r; j <= r; j++) acc += a[Mathf.Clamp(i + j, 0, n - 1)] * k[j + r];
                acc.Normalize();
                o[i] = new Quaternion(acc.x, acc.y, acc.z, acc.w);
            }
            return o;
        }

        static float IdleWeight(float t, HandMotionContext ctx, out float bombNear)
        {
            float w = 1f;
            bombNear = 0f;

            float dHit = NearestDistance(ctx.hitTimes, t);
            w *= AutoReplayMath.SmoothStep((dHit - HitCalmInner) / HitCalmRamp);

            if (ctx.bombTimes.Length > 0)
            {
                // 炸彈: 到達前 BombCalmBefore ~ 過去後 BombCalmAfter 之間為 0, 兩側 0.15s 漸變
                int i = LowerBound(ctx.bombTimes, t - BombCalmAfter - 0.15f);
                for (; i < ctx.bombTimes.Length && ctx.bombTimes[i] <= t + BombCalmBefore + 0.15f; i++)
                {
                    float b = ctx.bombTimes[i];
                    float d = t < b - BombCalmBefore ? (b - BombCalmBefore) - t
                            : t > b + BombCalmAfter ? t - (b + BombCalmAfter)
                            : 0f;
                    bombNear = Mathf.Max(bombNear, 1f - AutoReplayMath.SmoothStep(d / 0.15f));
                }
                w *= 1f - bombNear;
            }
            return w;
        }

        static float NearestDistance(float[] sorted, float t)
        {
            if (sorted.Length == 0) return float.MaxValue;
            int i = LowerBound(sorted, t);
            float d = float.MaxValue;
            if (i < sorted.Length) d = sorted[i] - t;
            if (i > 0) d = Mathf.Min(d, t - sorted[i - 1]);
            return d;
        }

        static int LowerBound(float[] v, float t)
        {
            int lo = 0, hi = v.Length;
            while (lo < hi) { int mid = (lo + hi) >> 1; if (v[mid] < t) lo = mid + 1; else hi = mid; }
            return lo;
        }
    }
}
