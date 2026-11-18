using UnityEngine;
using BotPlay.Core;

namespace BotPlay
{
    /// <summary>
    /// 手部運動學模型 (引數來自當前載入的 BotPlay 模型, 見 ModelManager / Core.BotPlayModel).
    /// 回放裡只有手柄(劍)和頭的姿態, 沒有手肘/前臂, 所以"手腕關節限位"用三件事近似:
    ///   1) 位置: 手的位置 = 頭 + b + A·f      (f = 劍的指向; 劍朝哪, 整條手臂就帶著手往哪移)
    ///   2) 扭轉: 劍繞自身軸的滾轉角 = 指向的平滑函式 (前臂旋前/旋後)
    ///   3) 限位: 每個方位上, 劍偏離正前方的角度不超過真人的 p99 (軟限位, 越接近越"推不動")
    /// 扭轉角的定義: 以 "+z 到 f 的最小旋轉" 把 +y 搬過去當參考上方向, 再繞 f 轉 roll 度.
    /// </summary>
    public static class HumanModel
    {
        // ---- 頭的基準位置 (BotPlay 的頭基本固定在這裡, 手的模型以它為原點) ----
        public static Vector3 HeadBase => new Vector3(0f, BotPlayMath.HeadHeight, 0f);

        // 回放記錄的是手柄位置; BotPlay 的旋轉中心(手腕)在它後面 PommelOffset 處 (和 BotPlayGenerator 的 pommelOffset 一致)
        public const float PommelOffset = 0.20f;

        // 手腕 z 方向最多往前伸多少 (A 的 z 行 - PommelOffset, 取上界), 炸彈檢測的"夠得著"範圍要用
        public const float MaxWristForwardZ = 0.15f;

        const float LimitSoftZone = 20f;   // 軟壓縮的寬度
        // 從上限 (真人 p99) 本身才開始軟壓縮, 超出部分最多再多 LimitSoftZone.
        // 原本在上限前 20° 就開始壓, 真人常到的角度也被壓掉: 收勢最遠只到 103° (真人 120°).
        // 遊戲外 12 張訓練外的譜: 收勢 103 -> 111°, 角速度 0.86 -> 0.94 倍真人, 方向誤差 32.0 -> 31.9°
        public static bool LimitKneeAtLimit = true;
        public static float LimitKneeBelow = 0f;   // 軟壓縮從上限前多少度開始
        public static float LimitOver = 20f;       // 最多超過上限多少度

        // 追蹤誤差修正: 真人資料裡劍偏離正前方的極限 (126~166°) 有一部分是手柄貼近頭顯時的追蹤跳變 ("劍戳腦袋"),
        // 這裡把限位上限壓到 MaxForwardAngle
        public const float MaxForwardAngle = 125f;

        static HandParams P(bool isLeft) => ModelManager.Require().Hand(isLeft);

        public static Vector3 HandBase(bool isLeft)
        {
            var b = P(isLeft).b;
            return HeadBase + new Vector3(b[0], b[1], b[2]);
        }

        /// <summary>手腕(旋轉中心)位置: 由劍的指向決定</summary>
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

        /// <summary>命中姿態: 劍從手腕出發正好穿過 target. 手腕又取決於指向 -> 不動點迭代</summary>
        public static Quaternion AimAt(bool isLeft, Vector3 target)
        {
            Quaternion q = Quaternion.LookRotation((target - HandBase(isLeft)).normalized, Vector3.up);
            for (int i = 0; i < 8; i++)
                q = Quaternion.LookRotation((target - WristFor(isLeft, q)).normalized, Vector3.up);
            return q;
        }

        /// <summary>軟限位: 劍偏離正前方的角度超過 knee 後被逐漸壓縮, 最多到 top (上限 + LimitOver). 防戳頭另外在 MotionGenerator.AvoidHead 按實際距離處理</summary>
        public static float LimitSideTurnRate = 1500f;   // 限位後往哪一側拉 (繞正前方的方位) 每秒最多轉幾度

        /// <summary>
        /// 有狀態的版本: 按時間順序逐幀呼叫. 指向接近正後方時, 原始的 "往哪一側拉" 一跨過正後方就從左邊換到右邊,
        /// 限位後的劍會一幀內甩過去 (ranked 批次測試: 一幀 60~140°). 這裡讓那個方位每秒最多轉 LimitSideTurnRate 度.
        /// side = 上一幀的方位 (z = 0 的單位向量; 零向量 = 還沒有)
        /// </summary>
        public static Vector3 LimitForward(bool isLeft, Vector3 f, ref Vector3 side, float dt)
        {
            Vector3 raw = new Vector3(f.x, f.y, 0f);
            raw = raw.sqrMagnitude > 1e-8f ? raw.normalized : (side.sqrMagnitude > 0.5f ? side : Vector3.up);
            if (side.sqrMagnitude < 0.5f) side = raw;
            else
            {
                float a = Mathf.Atan2(side.x * raw.y - side.y * raw.x, side.x * raw.x + side.y * raw.y) * Mathf.Rad2Deg, step = LimitSideTurnRate * dt;
                side = Mathf.Abs(a) <= step ? raw : Quaternion.AngleAxis(Mathf.Sign(a) * step, Vector3.forward) * side;
                side.z = 0f; side.Normalize();
            }
            // 剛超過 knee 的地方方位還是用原始的 (不然還沒追上時, 從限位裡出來的那一幀會跳)
            float ang = Mathf.Acos(Mathf.Clamp(f.z, -1f, 1f)) * Mathf.Rad2Deg;
            float kneeRaw = KneeAt(isLeft, raw);
            float k = BotPlayMath.SmoothStep((ang - kneeRaw) / 20f);
            Vector3 use = k >= 1f ? side : Vector3.Slerp(raw, side, k);
            use.z = 0f; use = use.sqrMagnitude > 1e-8f ? use.normalized : raw;
            return LimitForwardSide(isLeft, f, use);
        }

        static float KneeAt(bool isLeft, Vector3 side)
        {
            float lim = Mathf.Min(MaxForwardAngle, LimitAt(P(isLeft), Mathf.Atan2(side.y, side.x) * Mathf.Rad2Deg));
            return LimitKneeAtLimit ? lim - LimitKneeBelow : lim - LimitSoftZone;
        }

        public static Vector3 LimitForward(bool isLeft, Vector3 f)
        {
            Vector3 raw = new Vector3(f.x, f.y, 0f);
            if (raw.sqrMagnitude < 1e-8f) raw = Vector3.up;
            return LimitForwardSide(isLeft, f, raw.normalized);
        }

        // side: 往哪一側拉 (z = 0 的單位向量). 沒超過限位時原樣回傳 f
        static Vector3 LimitForwardSide(bool isLeft, Vector3 f, Vector3 side)
        {
            float ang = Mathf.Acos(Mathf.Clamp(f.z, -1f, 1f)) * Mathf.Rad2Deg;
            float az = Mathf.Atan2(side.y, side.x) * Mathf.Rad2Deg;
            float lim = Mathf.Min(MaxForwardAngle, LimitAt(P(isLeft), az));
            float knee = LimitKneeAtLimit ? lim - LimitKneeBelow : lim - LimitSoftZone;
            float top = LimitKneeAtLimit ? lim + LimitOver : lim;      // 最多到這裡
            if (ang <= knee) return f;

            float zone = Mathf.Max(1f, top - knee);
            float newAng = knee + zone * Tanh((ang - knee) / zone);
            float r = newAng * Mathf.Deg2Rad;
            return (Vector3.forward * Mathf.Cos(r) + side * Mathf.Sin(r)).normalized;
        }

        /// <summary>真人在這個指向下的扭轉趨勢 (未歸一化, 長度 = 信心; 長度接近 0 的方向真人怎麼轉都有)</summary>
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

        /// <summary>由指向 f 和扭轉角 roll(度) 組成旋轉</summary>
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
