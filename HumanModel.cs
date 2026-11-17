using UnityEngine;
using AutoReplay.Core;

namespace AutoReplay
{
    /// <summary>
    /// 手部运动学模型 (参数来自当前载入的 AutoPlay 模型, 见 ModelManager / Core.AutoPlayModel).
    /// 回放里只有手柄(剑)和头的姿态, 没有手肘/前臂, 所以"手腕关节限位"用三件事近似:
    ///   1) 位置: 手的位置 = 头 + b + A·f      (f = 剑的指向; 剑朝哪, 整条手臂就带着手往哪移)
    ///   2) 扭转: 剑绕自身轴的滚转角 = 指向的平滑函数 (前臂旋前/旋后)
    ///   3) 限位: 每个方位上, 剑偏离正前方的角度不超过真人的 p99 (软限位, 越接近越"推不动")
    /// 扭转角的定义: 以 "+z 到 f 的最小旋转" 把 +y 搬过去当参考上方向, 再绕 f 转 roll 度.
    /// </summary>
    public static class HumanModel
    {
        // ---- 头的基准位置 (AutoPlay 的头基本固定在这里, 手的模型以它为原点) ----
        public static Vector3 HeadBase => new Vector3(0f, AutoReplayMath.HeadHeight, 0f);

        // 回放记录的是手柄位置; AutoPlay 的旋转中心(手腕)在它后面 PommelOffset 处 (和 AutoReplayGenerator 的 pommelOffset 一致)
        public const float PommelOffset = 0.20f;

        // 手腕 z 方向最多往前伸多少 (A 的 z 行 - PommelOffset, 取上界), 炸弹检测的"够得着"范围要用
        public const float MaxWristForwardZ = 0.15f;

        const float LimitSoftZone = 20f;   // 软压缩的宽度
        // 从上限 (真人 p99) 本身才开始软压缩, 超出部分最多再多 LimitSoftZone.
        // 原本在上限前 20° 就开始压, 真人常到的角度也被压掉: 收势最远只到 103° (真人 120°).
        // 游戏外 12 张训练外的谱: 收势 103 -> 111°, 角速度 0.86 -> 0.94 倍真人, 方向误差 32.0 -> 31.9°
        public static bool LimitKneeAtLimit = true;
        public static float LimitKneeBelow = 0f;   // 软压缩从上限前多少度开始
        public static float LimitOver = 20f;       // 最多超过上限多少度

        // 追踪误差修正: 真人数据里剑偏离正前方的极限 (126~166°) 有一部分是手柄贴近头显时的追踪跳变 ("剑戳脑袋"),
        // 这里把限位上限压到 MaxForwardAngle
        public const float MaxForwardAngle = 125f;

        static HandParams P(bool isLeft) => ModelManager.Require().Hand(isLeft);

        public static Vector3 HandBase(bool isLeft)
        {
            var b = P(isLeft).b;
            return HeadBase + new Vector3(b[0], b[1], b[2]);
        }

        /// <summary>手腕(旋转中心)位置: 由剑的指向决定</summary>
        public static Vector3 WristFor(bool isLeft, Quaternion q) => WristForDir(isLeft, q * Vector3.forward);

        public static Vector3 WristForDir(bool isLeft, Vector3 f)
        {
            var p = P(isLeft);
            float[] a = p.A;
            Vector3 af = new Vector3(
                a[0] * f.x + a[1] * f.y + a[2] * f.z,
                a[3] * f.x + a[4] * f.y + a[5] * f.z,
                a[6] * f.x + a[7] * f.y + a[8] * f.z);
            return HeadBase + new Vector3(p.b[0], p.b[1], p.b[2]) + af - f * PommelOffset;
        }

        /// <summary>命中姿态: 剑从手腕出发正好穿过 target. 手腕又取决于指向 -> 不动点迭代</summary>
        public static Quaternion AimAt(bool isLeft, Vector3 target)
        {
            Quaternion q = Quaternion.LookRotation((target - HandBase(isLeft)).normalized, Vector3.up);
            for (int i = 0; i < 8; i++)
                q = Quaternion.LookRotation((target - WristFor(isLeft, q)).normalized, Vector3.up);
            return q;
        }

        /// <summary>软限位: 剑偏离正前方的角度超过 knee 后被逐渐压缩, 最多到 top (上限 + LimitOver). 防戳头另外在 MotionGenerator.AvoidHead 按实际距离处理</summary>
        public static Vector3 LimitForward(bool isLeft, Vector3 f)
        {
            float ang = Mathf.Acos(Mathf.Clamp(f.z, -1f, 1f)) * Mathf.Rad2Deg;
            float az = Mathf.Atan2(f.y, f.x) * Mathf.Rad2Deg;
            float lim = Mathf.Min(MaxForwardAngle, LimitAt(P(isLeft), az));
            float knee = LimitKneeAtLimit ? lim - LimitKneeBelow : lim - LimitSoftZone;
            float top = LimitKneeAtLimit ? lim + LimitOver : lim;      // 最多到这里
            if (ang <= knee) return f;

            float zone = Mathf.Max(1f, top - knee);
            float newAng = knee + zone * Tanh((ang - knee) / zone);
            Vector3 side = new Vector3(f.x, f.y, 0f);
            if (side.sqrMagnitude < 1e-8f) side = Vector3.up;
            side.Normalize();
            float r = newAng * Mathf.Deg2Rad;
            return (Vector3.forward * Mathf.Cos(r) + side * Mathf.Sin(r)).normalized;
        }

        /// <summary>真人在这个指向下的扭转趋势 (未归一化, 长度 = 信心; 长度接近 0 的方向真人怎么转都有)</summary>
        public static Vector2 RollVector(bool isLeft, Vector3 f)
        {
            var p = P(isLeft);
            float[] c = p.rollCos, s = p.rollSin;
            float x = f.x, y = f.y, z = f.z;
            float f0 = 1f, f1 = x, f2 = y, f3 = z, f4 = x * x, f5 = y * y, f6 = z * z, f7 = x * y, f8 = y * z, f9 = x * z;
            return new Vector2(
                c[0] * f0 + c[1] * f1 + c[2] * f2 + c[3] * f3 + c[4] * f4 + c[5] * f5 + c[6] * f6 + c[7] * f7 + c[8] * f8 + c[9] * f9,
                s[0] * f0 + s[1] * f1 + s[2] * f2 + s[3] * f3 + s[4] * f4 + s[5] * f5 + s[6] * f6 + s[7] * f7 + s[8] * f8 + s[9] * f9);
        }

        /// <summary>由指向 f 和扭转角 roll(度) 组成旋转</summary>
        public static Quaternion Compose(Vector3 f, float rollDeg)
        {
            return Quaternion.AngleAxis(rollDeg, f) * Quaternion.FromToRotation(Vector3.forward, f);
        }

        static float LimitAt(HandParams p, float az)
        {
            float[] table = p.limitDeg;
            float u = (az - p.limitFirstCenter) / p.limitStep;
            int n = table.Length;
            int i0 = Mathf.FloorToInt(u);
            float w = u - i0;
            int a = ((i0 % n) + n) % n, b = (a + 1) % n;
            return Mathf.Lerp(table[a], table[b], w);
        }

        static float Tanh(float x)
        {
            float e = Mathf.Exp(2f * x);
            return (e - 1f) / (e + 1f);
        }
    }
}
