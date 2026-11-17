using System;
using System.Collections.Generic;
using UnityEngine;
using Vector3 = UnityEngine.Vector3;
using Quaternion = UnityEngine.Quaternion;

namespace AutoReplay
{
    /// <summary>头部目标姿态 (还没平滑): 位置 + 俯仰/滚转角</summary>
    public struct HeadTarget
    {
        public Vector3 position;
        public float pitch;
        public float roll;
    }

    /// <summary>
    /// 一只手的待机微动所需的时间信息: 命中时刻和炸弹时刻附近要"稳住", 不加微动.
    /// </summary>
    public class HandMotionContext
    {
        public readonly float[] hitTimes;
        public readonly Vector3[] hitTargets;   // 和 hitTimes 一一对应: 方块中心
        public readonly int[] hitDirs;          // 和 hitTimes 一一对应: 切割方向 (反手扭转用)
        public readonly float[] bombTimes;
        public readonly float seed;
        // 手臂模板用: 每一刀 (叠块/几乎同时的合成一刀) 的时间、挥刀坐标系、方向分类、和前后一刀的较短间隔
        public readonly float[] armTimes;
        public readonly Vector3[] armD, armN, armF;
        public readonly int[] armDir;
        public readonly float[] armGap;
        // 身体平移 (躲墙时整个人往旁边/往下移, 手也跟着走). 由 AutoReplayGenerator 根据头的轨迹提供
        public Func<float, Vector3> bodyOffset = t => Vector3.zero;

        public HandMotionContext(List<NoteSwingData> swings, List<NoteData> bombs, float seed)
        {
            int n = swings?.Count ?? 0;
            hitTimes = new float[n];
            hitTargets = new Vector3[n];
            hitDirs = new int[n];
            for (int i = 0; i < n; i++) { hitTimes[i] = swings[i].time; hitTargets[i] = swings[i].target; hitDirs[i] = swings[i].cutDirection; }
            // swings 本来就按时间排好序

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
        // ================= 待机微动参数 =================
        // 真人 replay: 手几乎从不完全静止 (角速度 <20°/s 的时间约 1%), 头的朝向也有约 2° 的晃动.
        // 原本 4° / 2 cm 而且频率太低: 长间隔里剑几乎静止 (3~5°/s), 真人是 30~50°/s. 现在按真人 replay 校准 (见 AutoReplayMath.HandNoise)
        public const float HandIdleAngle = 9.0f;     // 手的旋转微动幅度(度)
        public const float HandIdlePos = 0.05f;      // 手的位置微动幅度(米)
        public const float HitCalmInner = 0.05f;     // 命中前后这么近完全不加旋转微动 (保证剑尖准确穿过方块)
        public const float HitCalmRamp = 0.15f;      // 再往外这么长时间内微动逐渐恢复
        public const float BombCalmBefore = 0.40f;   // 炸弹到达前这么久开始收掉旋转微动 (躲避姿势不被打乱)
        public const float BombCalmAfter = 0.25f;    // 炸弹过去后这么久才恢复

        public const float HeadIdleX = 0.020f, HeadIdleY = 0.006f, HeadIdleZ = 0.015f;   // 头的位置微动(米)
        public const float HeadIdleYaw = 2.0f, HeadIdlePitch = 1.5f, HeadIdleRoll = 1.0f; // 头的旋转微动(度)

        // 躲墙时身体平移对手的影响: 头往旁边移 x, 手跟着移 x*BodyFollowX
        public const float BodyFollowX = 0.85f;
        public const float BodyFollowY = 0.85f;   // 真人蹲墙时手的下降约是头的 0.81~0.86 倍
        // 躲墙的时机和幅度见下面的 DuckAmount (蹲) / LeanOffset (左右闪), 都照真人回放的曲线
        public const float DuckPitch = 1.5f;      // 真人蹲墙几乎不低头 (1~2°), 原本 5°

        // 轨道编号(从左到右): 第1轨 = lineIndex 0, 第2轨 = 1, 第3轨 = 2, 第4轨 = 3
        // 规则:
        //   - 第1轨 / 第4轨 的掩体不管 (墙只占外侧轨道时直接忽略, 跨到中间的墙只看它在 2-3 轨的部分)
        //   - 顶部掩体 (天花板墙) 同时盖住第2轨和第3轨 -> 蹲下 (可以是同一面墙, 也可以是两面墙同时存在)
        //   - 其他情况只盖住一条中间轨道 -> 歪头躲, 不蹲
        // 返回的是目标姿态, 平滑由调用方负责
        public static HeadTarget CalculateHeadTarget(float t, List<ObstacleData> obstacles, bool mirror = false)
        {
            float targetY = AutoReplayMath.HeadHeight;
            float targetPitch = 0f;

            // 蹲下: 照真人的蹲墙曲线 (DuckAmount)
            float duck = DuckAmount(t, obstacles, mirror);
            if (duck > 0f)
            {
                targetY = Mathf.Lerp(targetY, Mathf.Min(targetY, AutoReplayMath.DuckHeight), duck);
                targetPitch = Mathf.Max(targetPitch, DuckPitch * duck);
            }

            // 左右闪: 照真人的闪墙曲线 (LeanOffset)
            float leanX = LeanOffset(t, obstacles, mirror);
            float roll = Mathf.Clamp(-leanX * LeanRollPerMeter, -LeanMaxRoll, LeanMaxRoll);
            return new HeadTarget { position = new Vector3(leanX, targetY, 0f), pitch = targetPitch, roll = roll };
        }

        // ================= 左右闪墙曲线 =================
        // 真人 60 份回放 / 410 次孤立的闪墙 (只盖住中间一条轨道的全高墙), 以墙到达为 0, 往墙的反方向为正:
        //   横移 -0.3s +0.02 | -0.1 +0.06 | 0 +0.09 | +0.2 +0.15 | +0.3 +0.15 (最远) | +0.6 +0.11 | +1.0 +0.04 m
        //   歪头 最大中位 6.8° (P10 3.8 ~ P90 11.0); 最远横移中位 0.16 m (P10 0.10 ~ P90 0.21), 每次都不一样.
        // 原本: 墙到前 0.3 秒目标瞬间切到 0.42~0.45 m / 12°, 墙一过就弹回 -> 幅度是真人的 2.7 倍, 而且每次一样, 墙多就晕.
        public const float LeanBase = 0.16f;           // 真人最远横移中位数
        public const float LeanVariation = 0.25f;      // 每次随机 ±25% (按墙的时间决定, 每次生成一样)
        public const float LeanClear = 0.12f;          // 头离墙边至少要这么远
        public const float LeanStart = 0.35f;          // 墙到前多久开始移
        public const float LeanRise = 0.45f;           // 移到最远要多久 (墙到时已移了约 90%: 真人最远点在墙过后 0.2~0.3 s,
                                                       //  但墙到时只移了 9 cm, 照抄会擦到墙边)
        public const float LeanHold = 0.12f;           // 墙过去后保持多久
        public const float LeanReturn = 0.90f;         // 回到原位要多久
        public const float LeanRollPerMeter = 40f;     // 歪头角度 = 横移 * 这个 (0.16 m -> 6.4°)
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
            // 按时间顺序: 新的墙从上一道墙留下的位置接手 (左右交替的墙 -> 头左右来回摆; 原本两边相加会互相抵消, 头停在中间撞墙)
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

        // 要闪的墙: 挡到头 (不是矮墙、也不是能蹲过去的顶墙), 盖住中间的第 2 或第 3 轨
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
                // 墙边离中线多远: 只盖第 2 轨 -> 墙的右边在 x=0; 两轨都盖 -> 墙的右边在 x=0.6
                float edge = lane2 && lane3 ? 0.6f : 0f;
                float dir = lane2 ? 1f : -1f;                                // 墙在左中 -> 往右闪
                float h = Mathf.Abs(Mathf.Sin(obs.time * 12.9898f + obs.lineIndex * 78.233f) * 43758.5453f) % 1f;
                float amp = Mathf.Max(edge + LeanClear, LeanBase * (1f + LeanVariation * (2f * h - 1f)));
                res.Add((obs.time, obs.time + obs.duration, dir * amp));
            }
            res.Sort((x, y) => x.Item1.CompareTo(y.Item1));
            return res;
        }

        // ================= 墙的高度 =================
        // 原本把 "不在最底层 或 高度 <= 3" 都当成顶墙要蹲: 从地板往上只到约 1.3 m 的矮墙 (y=0, h=2) 也蹲,
        // 结果头反而蹲进墙里 (WIP 谱 M.C.ドナルド 36.8 s: 头 1.09 m, 墙顶约 1.3 m). 现在照墙实际占的高度判断:
        //   墙顶比站着的头低 -> 不用躲;  墙底比蹲下的头还高 -> 顶墙, 蹲;  其余 (从低处一路盖过头) -> 只能往旁边闪
        // 高度规则: 底边 = 层高 (0.25 + 0.6*层) - 0.15 + 身高偏移, 高 = h * 0.6 (tools/wall_eval.py 用同一套, 和真人回放对得上)
        public const float HeadClear = 0.15f;   // 头 (连同头显) 和墙之间要留的距离

        public static float WallBottom(ObstacleData o) => 0.25f + 0.6f * (int)o.lineLayer - 0.15f + AutoReplayMath.NoteYOffset;
        public static float WallTop(ObstacleData o) => WallBottom(o) + 0.6f * o.height;
        public static bool WallBelowHead(ObstacleData o) => WallTop(o) <= AutoReplayMath.HeadHeight - HeadClear;
        public static bool IsCeilingWall(ObstacleData o) => WallBottom(o) >= AutoReplayMath.DuckHeight + HeadClear * 0.8f;

        // ================= 蹲墙曲线 =================
        // 真人 17 份回放 / 146 次独立蹲墙 (盖住中间两轨的顶墙), 以墙到达为 0:
        //   头 Δy  -0.3s -0.04 | -0.2 -0.15 | -0.1 -0.32 | 0 -0.46 | +0.1 -0.54 | +0.2 -0.56 (最低) | +0.4 -0.45 | +0.6 -0.20 | +0.8 -0.06 m
        // 也就是: 墙到前约 0.3 秒开始蹲, 墙过去后 0.1~0.2 秒最低, 站起来比蹲下慢 (约 0.6~0.8 秒); 头几乎不低 (1~2°).
        // 原本: 墙到前 0.3 秒目标瞬间切到蹲, 弹簧 0.07 秒 -> 0.1 秒内猛蹲 42 cm, 墙一过 0.2 秒内弹回, 还低头 5°.
        public const float DuckDownStart = 0.40f;   // 墙到前多久开始蹲 (含头部弹簧约 0.05 秒的延迟)
        public const float DuckDownEnd = 0.05f;     // 墙到后多久蹲到底
        public const float DuckHoldAfter = 0.07f;   // 墙过去后还保持多久
        public const float DuckUpTime = 0.65f;      // 站起来的时间

        static List<ObstacleData> _duckSrc;
        static bool _duckMirror;
        static List<(float s, float e)> _duckIntervals;
        static List<(float s, float e, float cap)> _lowWalls;   // 矮墙经过时最多能蹲多少 (0 = 不能蹲)
        public const float LowWallRise = 0.25f;     // 矮墙到前多久要站到够高 (真人先蹲后站时会很快站起来)

        /// <summary>0 = 站着, 1 = 蹲到底</summary>
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
            // 矮墙 (先蹲后站的组合): 矮墙经过时头要高过墙顶, 来不及就站快一点
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

        // 盖住中间轨道 (第 2 或 3 轨) 的矮墙: 头要高过墙顶 + HeadClear
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
                float need = WallTop(obs) + HeadClear;                         // 头至少要这么高
                if (need <= AutoReplayMath.DuckHeight) continue;               // 蹲到底也碰不到
                float cap = range > 1e-3f ? Mathf.Clamp01((AutoReplayMath.HeadHeight - need) / range) : 0f;
                res.Add((obs.time, obs.time + obs.duration, cap));
            }
            return res;
        }

        // 需要蹲的时间段: 顶部的墙 (或几面顶墙合起来) 同时盖住第 2、3 轨
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

        /// <summary>头的待机微动 (叠加在平滑后的姿态上)</summary>
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

        // ================= 平滑参数 =================
        public const int Oversample = 4;            // 内部按 4 倍帧率采样, 平滑后再抽回输出帧率
        public const float RotSmoothSigma = 0.010f; // 最终姿态的高斯平滑 (秒): 去掉关键帧折线的棱角
        public const float RollSmoothSigma = 0.030f;// 扭转趋势先在时间上平均
        public const float RollTau = 0.035f;        // 扭转角跟随真人模型的时间常数 (弹簧)
        public const float MaxRollSpeed = 1500f;    // 扭转角速度上限 (度/秒): 前臂旋前/旋后转不了更快 -> 不会再"一帧翻半圈"
        public const float RollConfidence = 0.35f;  // 真人模型的扭转向量长度低于这个值 -> 信心不足, 弹簧变软, 保持当前扭转
        public static float ArmSmoothSigma = 0.050f; // 手臂(位置)跟随剑指向的延迟: 手臂比手腕重, 快速甩剑主要是手腕在动
        public const float HitPinWindow = 0.040f;   // 命中前后这么长时间内把剑拉回精确对准方块
        public const int AimIterations = 3;         // 手臂位置 <-> 命中对准 的迭代次数 (多算几次换更准的轨迹)

        /// <summary>
        /// 生成一只手每一帧的姿态. 流程:
        ///   模板轨道 -> 待机微动 -> 指向软限位 -> 扭转(弹簧跟随真人模型) -> 高斯平滑
        ///   -> [手臂位置(延迟跟随 + 身体平移) -> 命中帧对准方块] x AimIterations
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

            // ---- 扭转: 真人模型给目标, 弹簧跟随 (正向+反向平均, 无延迟), 限速 ----
            var rvs = SmoothVec2(rv, RollSmoothSigma / sdt);
            var rollF = RollSpring(rvs, sdt, false);
            var rollB = RollSpring(rvs, sdt, true);
            var q = new Quaternion[ns];
            int hc = 0;
            for (int i = 0; i < ns; i++)
            {
                float t = t0 + (i - pad) * sdt;
                float roll = rollF[i] + 0.5f * Mathf.DeltaAngle(rollF[i], rollB[i]);
                // 反手: 切横向/斜向方块的瞬间前臂会大幅旋转, 按 (手, 切割方向) 叠加真人的扭转偏移曲线
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

            // ---- 手臂位置 <-> 命中对准 迭代 ----
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

        // 手腕位置 = 人体模型(延迟跟随的剑指向) + 身体平移 + 位置微动
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
        /// 手臂模板的位移: 上一刀的后半段和下一刀的前半段, 按在间隔里的位置平滑交接 (和指向的模板一样)
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

        // 扭转角弹簧: 目标 = 真人模型的扭转; 模型信心低时弹簧变软 (保持当前扭转), 速度有上限
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

        // 规划轨道 + 待机微动 + 软限位 之后的剑指向
        static Vector3 RawForward(float t, List<NoteSwingData> swings, List<NoteData> bombs, bool isLeft, HandMotionContext ctx)
        {
            Quaternion rot = NoteSwingPlanner.SampleHandRotation(t, swings, bombs, isLeft);
            float w = IdleWeight(t, ctx);
            if (w > 0f)
            {
                float s = ctx.seed;
                var jitter = Quaternion.Euler(
                    HandIdleAngle * w * AutoReplayMath.HandNoise(t, s + 1f),
                    HandIdleAngle * w * AutoReplayMath.HandNoise(t * 0.83f, s + 2f),
                    0f);
                rot = jitter * rot;
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

        static float IdleWeight(float t, HandMotionContext ctx)
        {
            float w = 1f;

            float dHit = NearestDistance(ctx.hitTimes, t);
            w *= AutoReplayMath.SmoothStep((dHit - HitCalmInner) / HitCalmRamp);

            if (ctx.bombTimes.Length > 0)
            {
                // 炸弹: 到达前 BombCalmBefore ~ 过去后 BombCalmAfter 之间为 0, 两侧 0.15s 渐变
                int i = LowerBound(ctx.bombTimes, t - BombCalmAfter - 0.15f);
                for (; i < ctx.bombTimes.Length && ctx.bombTimes[i] <= t + BombCalmBefore + 0.15f; i++)
                {
                    float b = ctx.bombTimes[i];
                    float d = t < b - BombCalmBefore ? (b - BombCalmBefore) - t
                            : t > b + BombCalmAfter ? t - (b + BombCalmAfter)
                            : 0f;
                    w *= AutoReplayMath.SmoothStep(d / 0.15f);
                    if (w <= 0f) break;
                }
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
