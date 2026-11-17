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
        public const float HandIdleAngle = 4.0f;     // 手的旋转微动幅度(度): 空档停在原地时靠它模拟人手的小幅晃动
        public const float HandIdlePos = 0.02f;      // 手的位置微动幅度(米)
        public const float HitCalmInner = 0.05f;     // 命中前后这么近完全不加旋转微动 (保证剑尖准确穿过方块)
        public const float HitCalmRamp = 0.15f;      // 再往外这么长时间内微动逐渐恢复
        public const float BombCalmBefore = 0.40f;   // 炸弹到达前这么久开始收掉旋转微动 (躲避姿势不被打乱)
        public const float BombCalmAfter = 0.25f;    // 炸弹过去后这么久才恢复

        public const float HeadIdleX = 0.020f, HeadIdleY = 0.006f, HeadIdleZ = 0.015f;   // 头的位置微动(米)
        public const float HeadIdleYaw = 2.0f, HeadIdlePitch = 1.5f, HeadIdleRoll = 1.0f; // 头的旋转微动(度)

        // 墙提前多久开始躲 (之后由 AutoReplayGenerator 里的弹簧平滑过去, 不再瞬移)
        public const float WallLead = 0.30f;
        public const float WallTrail = 0.10f;
        // 躲墙时身体平移对手的影响: 头往旁边移 x, 手跟着移 x*BodyFollowX; 蹲下时手下降得少一点
        public const float BodyFollowX = 0.85f;
        public const float BodyFollowY = 0.70f;
        // 躲墙时头的歪斜角度: 原本 40°, 看回放会晕. 现在主要靠身体平移, 头只稍微歪一点
        public const float HeadTiltRoll = 12f;
        public const float DuckPitch = 5f;

        // 轨道编号(从左到右): 第1轨 = lineIndex 0, 第2轨 = 1, 第3轨 = 2, 第4轨 = 3
        // 规则:
        //   - 第1轨 / 第4轨 的掩体不管 (墙只占外侧轨道时直接忽略, 跨到中间的墙只看它在 2-3 轨的部分)
        //   - 顶部掩体 (天花板墙) 同时盖住第2轨和第3轨 -> 蹲下 (可以是同一面墙, 也可以是两面墙同时存在)
        //   - 其他情况只盖住一条中间轨道 -> 歪头躲, 不蹲
        // 返回的是目标姿态, 平滑由调用方负责
        public static HeadTarget CalculateHeadTarget(float t, List<ObstacleData> obstacles, bool mirror = false)
        {
            float targetX = 0f;
            float targetY = AutoReplayMath.HeadHeight;
            float targetRoll = 0f;
            float targetPitch = 0f;

            // 顶部掩体对第2轨/第3轨的覆盖情况 (多面墙同时存在时取并集)
            bool ceilingLane2 = false;
            bool ceilingLane3 = false;
            // 顶部掩体只盖住一条中间轨道时的歪头结果, 如果最后判定要蹲下就丢弃
            float ceilingTiltX = 0f;
            float ceilingTiltRoll = 0f;

            foreach (var obs in obstacles)
            {
                if (t < obs.time - WallLead || t > obs.time + obs.duration + WallTrail) continue;

                // 只保留墙在中间两条轨道(lineIndex 1~2)的部分, 第1轨/第4轨的部分不管
                // 左手模式下游戏会把谱面左右镜像, 墙的轨道也要跟着镜像 (共 4 轨)
                int lineIndex = mirror ? 4 - (obs.lineIndex + obs.width) : obs.lineIndex;
                int c0 = Mathf.Max(lineIndex, 1);
                int c1 = Mathf.Min(lineIndex + obs.width - 1, 2);
                if (c0 > c1) continue; // 墙只在第1轨或第4轨 -> 忽略

                bool coversLane2 = c0 <= 1 && c1 >= 1;
                bool coversLane3 = c0 <= 2 && c1 >= 2;
                bool isCeilingWall = obs.lineLayer != NoteLineLayer.Base || obs.height <= 3;

                // 这面墙单独需要的歪头动作
                float tiltX, tiltRoll;
                if (coversLane2 && coversLane3)
                {
                    // 盖住第2和第3轨 -> 向右靠
                    tiltX = 0.45f; tiltRoll = -HeadTiltRoll;
                }
                else if (coversLane2)
                {
                    // 只有第2轨(左中) -> 头向右歪
                    tiltX = 0.42f; tiltRoll = -HeadTiltRoll;
                }
                else
                {
                    // 只有第3轨(右中) -> 头向左歪
                    tiltX = -0.42f; tiltRoll = HeadTiltRoll;
                }

                if (isCeilingWall)
                {
                    ceilingLane2 |= coversLane2;
                    ceilingLane3 |= coversLane3;
                    if (tiltX > 0f) { ceilingTiltX = Mathf.Max(ceilingTiltX, tiltX); ceilingTiltRoll = Mathf.Min(ceilingTiltRoll, tiltRoll); }
                    else { ceilingTiltX = Mathf.Min(ceilingTiltX, tiltX); ceilingTiltRoll = Mathf.Max(ceilingTiltRoll, tiltRoll); }
                }
                else
                {
                    // 非顶部的墙: 一律歪头, 不蹲
                    if (tiltX > 0f) { targetX = Mathf.Max(targetX, tiltX); targetRoll = Mathf.Min(targetRoll, tiltRoll); }
                    else { targetX = Mathf.Min(targetX, tiltX); targetRoll = Mathf.Max(targetRoll, tiltRoll); }
                }
            }

            if (ceilingLane2 && ceilingLane3)
            {
                // 第2、3轨同时有顶部掩体 -> 蹲下
                targetY = Mathf.Min(targetY, AutoReplayMath.DuckHeight);
                targetPitch = Mathf.Max(targetPitch, DuckPitch);
            }
            else if (ceilingTiltX > 0f)
            {
                targetX = Mathf.Max(targetX, ceilingTiltX);
                targetRoll = Mathf.Min(targetRoll, ceilingTiltRoll);
            }
            else if (ceilingTiltX < 0f)
            {
                targetX = Mathf.Min(targetX, ceilingTiltX);
                targetRoll = Mathf.Max(targetRoll, ceilingTiltRoll);
            }

            return new HeadTarget { position = new Vector3(targetX, targetY, 0f), pitch = targetPitch, roll = targetRoll };
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
        public const float ArmSmoothSigma = 0.050f; // 手臂(位置)跟随剑指向的延迟: 手臂比手腕重, 快速甩剑主要是手腕在动
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
                wrist[i] = HumanModel.WristForDir(isLeft, dirSlow) + ctx.bodyOffset(t) + new Vector3(
                    HandIdlePos * AutoReplayMath.Noise(t * 0.7f, s + 3f),
                    HandIdlePos * AutoReplayMath.Noise(t * 0.6f, s + 4f),
                    HandIdlePos * AutoReplayMath.Noise(t * 0.5f, s + 5f));
            }
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
                    HandIdleAngle * w * AutoReplayMath.Noise(t, s + 1f),
                    HandIdleAngle * w * AutoReplayMath.Noise(t * 0.8f, s + 2f),
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
