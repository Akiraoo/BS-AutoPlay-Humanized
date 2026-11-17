// 遊戲外用的 UnityEngine 數學型別替代品: 只實作生成器用到的部分, 行為照 Unity 的公開定義.
// 遊戲內不會編譯這個檔案 (只有 tools/ApModelTool 編譯, 並定義 AUTOPLAY_STANDALONE).
using System;

namespace UnityEngine
{
    public static class Mathf
    {
        public const float PI = (float)Math.PI;
        public const float Deg2Rad = PI / 180f;
        public const float Rad2Deg = 180f / PI;
        public const float Epsilon = float.Epsilon;
        public const float Infinity = float.PositiveInfinity;

        public static float Sin(float f) => (float)Math.Sin(f);
        public static float Cos(float f) => (float)Math.Cos(f);
        public static float Tan(float f) => (float)Math.Tan(f);
        public static float Asin(float f) => (float)Math.Asin(f);
        public static float Acos(float f) => (float)Math.Acos(f);
        public static float Atan(float f) => (float)Math.Atan(f);
        public static float Atan2(float y, float x) => (float)Math.Atan2(y, x);
        public static float Sqrt(float f) => (float)Math.Sqrt(f);
        public static float Exp(float f) => (float)Math.Exp(f);
        public static float Pow(float f, float p) => (float)Math.Pow(f, p);
        public static float Abs(float f) => Math.Abs(f);
        public static int Abs(int v) => Math.Abs(v);
        public static float Sign(float f) => f >= 0f ? 1f : -1f;          // Unity: 0 也回傳 1
        public static float Min(float a, float b) => a < b ? a : b;
        public static float Max(float a, float b) => a > b ? a : b;
        public static int Min(int a, int b) => a < b ? a : b;
        public static int Max(int a, int b) => a > b ? a : b;
        public static float Min(params float[] v) { float m = v[0]; for (int i = 1; i < v.Length; i++) if (v[i] < m) m = v[i]; return m; }
        public static float Max(params float[] v) { float m = v[0]; for (int i = 1; i < v.Length; i++) if (v[i] > m) m = v[i]; return m; }
        public static float Clamp(float v, float min, float max) => v < min ? min : v > max ? max : v;
        public static int Clamp(int v, int min, int max) => v < min ? min : v > max ? max : v;
        public static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
        public static float Lerp(float a, float b, float t) => a + (b - a) * Clamp01(t);
        public static float LerpUnclamped(float a, float b, float t) => a + (b - a) * t;
        public static float InverseLerp(float a, float b, float v) => a != b ? Clamp01((v - a) / (b - a)) : 0f;
        public static float Round(float f) => (float)Math.Round(f);        // Unity 用 Math.Round (銀行家舍入)
        public static int RoundToInt(float f) => (int)Math.Round(f);
        public static int CeilToInt(float f) => (int)Math.Ceiling(f);
        public static int FloorToInt(float f) => (int)Math.Floor(f);
        public static float Floor(float f) => (float)Math.Floor(f);
        public static float Ceil(float f) => (float)Math.Ceiling(f);
        public static float Repeat(float t, float length) => Clamp(t - Floor(t / length) * length, 0f, length);
        public static float DeltaAngle(float current, float target)
        {
            float d = Repeat(target - current, 360f);
            if (d > 180f) d -= 360f;
            return d;
        }
        public static bool Approximately(float a, float b) =>
            Math.Abs(b - a) < Math.Max(1e-6f * Math.Max(Math.Abs(a), Math.Abs(b)), Epsilon * 8f);

        // 臨界阻尼彈簧 (Game Programming Gems 4, 和 Unity 的 SmoothDamp 同一個公式)
        public static float SmoothDamp(float current, float target, ref float currentVelocity, float smoothTime, float maxSpeed, float deltaTime)
        {
            smoothTime = Max(0.0001f, smoothTime);
            float omega = 2f / smoothTime;
            float x = omega * deltaTime;
            float exp = 1f / (1f + x + 0.48f * x * x + 0.235f * x * x * x);
            float change = current - target;
            float originalTo = target;
            float maxChange = maxSpeed * smoothTime;
            change = Clamp(change, -maxChange, maxChange);
            target = current - change;
            float temp = (currentVelocity + omega * change) * deltaTime;
            currentVelocity = (currentVelocity - omega * temp) * exp;
            float output = target + (change + temp) * exp;
            if (originalTo - current > 0f == output > originalTo)
            {
                output = originalTo;
                currentVelocity = (output - originalTo) / deltaTime;
            }
            return output;
        }
    }

    public struct Vector2
    {
        public float x, y;
        public Vector2(float x, float y) { this.x = x; this.y = y; }
        public static Vector2 zero => new Vector2(0f, 0f);
        public float magnitude => (float)Math.Sqrt(x * x + y * y);
        public float sqrMagnitude => x * x + y * y;
        public static Vector2 operator +(Vector2 a, Vector2 b) => new Vector2(a.x + b.x, a.y + b.y);
        public static Vector2 operator -(Vector2 a, Vector2 b) => new Vector2(a.x - b.x, a.y - b.y);
        public static Vector2 operator *(Vector2 a, float d) => new Vector2(a.x * d, a.y * d);
        public static Vector2 operator *(float d, Vector2 a) => new Vector2(a.x * d, a.y * d);
        public static Vector2 operator /(Vector2 a, float d) => new Vector2(a.x / d, a.y / d);
        public override string ToString() => $"({x:F2}, {y:F2})";
    }

    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public Vector3(float x, float y) { this.x = x; this.y = y; z = 0f; }

        public static Vector3 zero => new Vector3(0f, 0f, 0f);
        public static Vector3 one => new Vector3(1f, 1f, 1f);
        public static Vector3 forward => new Vector3(0f, 0f, 1f);
        public static Vector3 back => new Vector3(0f, 0f, -1f);
        public static Vector3 up => new Vector3(0f, 1f, 0f);
        public static Vector3 down => new Vector3(0f, -1f, 0f);
        public static Vector3 left => new Vector3(-1f, 0f, 0f);
        public static Vector3 right => new Vector3(1f, 0f, 0f);

        public float magnitude => (float)Math.Sqrt(x * x + y * y + z * z);
        public float sqrMagnitude => x * x + y * y + z * z;
        public Vector3 normalized => Normalize(this);
        public void Normalize() { this = Normalize(this); }
        public static Vector3 Normalize(Vector3 v)
        {
            float m = v.magnitude;
            return m > 1e-5f ? v / m : zero;
        }

        public static float Dot(Vector3 a, Vector3 b) => a.x * b.x + a.y * b.y + a.z * b.z;
        public static Vector3 Cross(Vector3 a, Vector3 b) => new Vector3(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x);
        public static float Distance(Vector3 a, Vector3 b) => (a - b).magnitude;
        public static Vector3 Lerp(Vector3 a, Vector3 b, float t) { t = Mathf.Clamp01(t); return new Vector3(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t, a.z + (b.z - a.z) * t); }
        public static Vector3 LerpUnclamped(Vector3 a, Vector3 b, float t) => new Vector3(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t, a.z + (b.z - a.z) * t);
        public static Vector3 Scale(Vector3 a, Vector3 b) => new Vector3(a.x * b.x, a.y * b.y, a.z * b.z);
        public static Vector3 ProjectOnPlane(Vector3 v, Vector3 n)
        {
            float sq = Dot(n, n);
            if (sq < float.Epsilon) return v;
            return v - n * (Dot(v, n) / sq);
        }

        public static float Angle(Vector3 from, Vector3 to)
        {
            float denom = (float)Math.Sqrt(from.sqrMagnitude * to.sqrMagnitude);
            if (denom < 1e-15f) return 0f;
            float dot = Mathf.Clamp(Dot(from, to) / denom, -1f, 1f);
            return (float)Math.Acos(dot) * Mathf.Rad2Deg;
        }

        /// <summary>方向按球面插值, 長度按線性插值 (和 Unity 的 Vector3.Slerp 一樣)</summary>
        public static Vector3 Slerp(Vector3 a, Vector3 b, float t)
        {
            t = Mathf.Clamp01(t);
            float ma = a.magnitude, mb = b.magnitude;
            if (ma < 1e-6f || mb < 1e-6f) return Lerp(a, b, t);
            Vector3 ua = a / ma, ub = b / mb;
            float dot = Mathf.Clamp(Dot(ua, ub), -1f, 1f);
            float len = ma + (mb - ma) * t;
            if (dot > 0.9995f) return Normalize(LerpUnclamped(ua, ub, t)) * len;
            float theta = (float)Math.Acos(dot);
            Vector3 axis;
            if (dot < -0.9995f)
            {
                // 反向: 隨便找一個垂直軸轉過去
                axis = Cross(ua, Math.Abs(ua.x) < 0.9f ? right : up).normalized;
            }
            else axis = Cross(ua, ub).normalized;
            return (Quaternion.AngleAxis(theta * t * Mathf.Rad2Deg, axis) * ua) * len;
        }

        public static Vector3 SmoothDamp(Vector3 current, Vector3 target, ref Vector3 currentVelocity, float smoothTime, float maxSpeed, float deltaTime)
        {
            smoothTime = Mathf.Max(0.0001f, smoothTime);
            float omega = 2f / smoothTime;
            float x = omega * deltaTime;
            float exp = 1f / (1f + x + 0.48f * x * x + 0.235f * x * x * x);
            Vector3 change = current - target;
            Vector3 originalTo = target;
            float maxChange = maxSpeed * smoothTime;
            float maxChangeSq = maxChange * maxChange;
            float sq = change.sqrMagnitude;
            if (sq > maxChangeSq)
            {
                float mag = (float)Math.Sqrt(sq);
                change = change / mag * maxChange;
            }
            target = current - change;
            Vector3 temp = (currentVelocity + change * omega) * deltaTime;
            currentVelocity = (currentVelocity - temp * omega) * exp;
            Vector3 output = target + (change + temp) * exp;
            Vector3 origMinusCurrent = originalTo - current, outMinusOrig = output - originalTo;
            if (Dot(origMinusCurrent, outMinusOrig) > 0f)
            {
                output = originalTo;
                currentVelocity = (output - originalTo) / deltaTime;
            }
            return output;
        }

        public static Vector3 operator +(Vector3 a, Vector3 b) => new Vector3(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new Vector3(a.x - b.x, a.y - b.y, a.z - b.z);
        public static Vector3 operator -(Vector3 a) => new Vector3(-a.x, -a.y, -a.z);
        public static Vector3 operator *(Vector3 a, float d) => new Vector3(a.x * d, a.y * d, a.z * d);
        public static Vector3 operator *(float d, Vector3 a) => new Vector3(a.x * d, a.y * d, a.z * d);
        public static Vector3 operator /(Vector3 a, float d) => new Vector3(a.x / d, a.y / d, a.z / d);
        // Unity 的 == 是近似相等
        public static bool operator ==(Vector3 a, Vector3 b) => (a - b).sqrMagnitude < 9.99999944e-11f;
        public static bool operator !=(Vector3 a, Vector3 b) => !(a == b);
        public override bool Equals(object o) => o is Vector3 v && x == v.x && y == v.y && z == v.z;
        public override int GetHashCode() => x.GetHashCode() ^ (y.GetHashCode() << 2) ^ (z.GetHashCode() >> 2);
        public override string ToString() => $"({x:F2}, {y:F2}, {z:F2})";
    }

    public struct Vector4
    {
        public float x, y, z, w;
        public Vector4(float x, float y, float z, float w) { this.x = x; this.y = y; this.z = z; this.w = w; }
        public static Vector4 zero => new Vector4(0f, 0f, 0f, 0f);
        public float magnitude => (float)Math.Sqrt(x * x + y * y + z * z + w * w);
        public static float Dot(Vector4 a, Vector4 b) => a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w;
        public void Normalize()
        {
            float m = magnitude;
            this = m > 1e-5f ? this / m : zero;
        }
        public static Vector4 operator +(Vector4 a, Vector4 b) => new Vector4(a.x + b.x, a.y + b.y, a.z + b.z, a.w + b.w);
        public static Vector4 operator -(Vector4 a, Vector4 b) => new Vector4(a.x - b.x, a.y - b.y, a.z - b.z, a.w - b.w);
        public static Vector4 operator -(Vector4 a) => new Vector4(-a.x, -a.y, -a.z, -a.w);
        public static Vector4 operator *(Vector4 a, float d) => new Vector4(a.x * d, a.y * d, a.z * d, a.w * d);
        public static Vector4 operator *(float d, Vector4 a) => new Vector4(a.x * d, a.y * d, a.z * d, a.w * d);
        public static Vector4 operator /(Vector4 a, float d) => new Vector4(a.x / d, a.y / d, a.z / d, a.w / d);
    }

    public struct Quaternion
    {
        public float x, y, z, w;
        public Quaternion(float x, float y, float z, float w) { this.x = x; this.y = y; this.z = z; this.w = w; }
        public static Quaternion identity => new Quaternion(0f, 0f, 0f, 1f);

        public static float Dot(Quaternion a, Quaternion b) => a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w;

        public Quaternion normalized
        {
            get
            {
                float m = (float)Math.Sqrt(Dot(this, this));
                return m < float.Epsilon ? identity : new Quaternion(x / m, y / m, z / m, w / m);
            }
        }
        public void Normalize() { this = normalized; }

        public static Quaternion operator *(Quaternion a, Quaternion b) => new Quaternion(
            a.w * b.x + a.x * b.w + a.y * b.z - a.z * b.y,
            a.w * b.y + a.y * b.w + a.z * b.x - a.x * b.z,
            a.w * b.z + a.z * b.w + a.x * b.y - a.y * b.x,
            a.w * b.w - a.x * b.x - a.y * b.y - a.z * b.z);

        public static Vector3 operator *(Quaternion q, Vector3 v)
        {
            float x2 = q.x * 2f, y2 = q.y * 2f, z2 = q.z * 2f;
            float xx = q.x * x2, yy = q.y * y2, zz = q.z * z2;
            float xy = q.x * y2, xz = q.x * z2, yz = q.y * z2;
            float wx = q.w * x2, wy = q.w * y2, wz = q.w * z2;
            return new Vector3(
                (1f - (yy + zz)) * v.x + (xy - wz) * v.y + (xz + wy) * v.z,
                (xy + wz) * v.x + (1f - (xx + zz)) * v.y + (yz - wx) * v.z,
                (xz - wy) * v.x + (yz + wx) * v.y + (1f - (xx + yy)) * v.z);
        }

        public static Quaternion Inverse(Quaternion q)
        {
            float n = Dot(q, q);
            if (n < float.Epsilon) return identity;
            return new Quaternion(-q.x / n, -q.y / n, -q.z / n, q.w / n);
        }

        public static Quaternion AngleAxis(float angle, Vector3 axis)
        {
            Vector3 a = axis.normalized;
            if (a.sqrMagnitude < 1e-12f) return identity;
            float h = angle * Mathf.Deg2Rad * 0.5f;
            float s = (float)Math.Sin(h);
            return new Quaternion(a.x * s, a.y * s, a.z * s, (float)Math.Cos(h));
        }

        /// <summary>Unity 的尤拉角順序: 先繞 z, 再繞 x, 最後繞 y (q = qy * qx * qz)</summary>
        public static Quaternion Euler(float x, float y, float z) =>
            AngleAxis(y, Vector3.up) * AngleAxis(x, Vector3.right) * AngleAxis(z, Vector3.forward);
        public static Quaternion Euler(Vector3 e) => Euler(e.x, e.y, e.z);

        public static float Angle(Quaternion a, Quaternion b)
        {
            float dot = Math.Min(Math.Abs(Dot(a, b)), 1f);
            return dot > 1f - 1e-6f ? 0f : (float)Math.Acos(dot) * 2f * Mathf.Rad2Deg;
        }

        public static Quaternion Slerp(Quaternion a, Quaternion b, float t) => SlerpUnclamped(a, b, Mathf.Clamp01(t));

        public static Quaternion SlerpUnclamped(Quaternion a, Quaternion b, float t)
        {
            float dot = Dot(a, b);
            if (dot < 0f) { b = new Quaternion(-b.x, -b.y, -b.z, -b.w); dot = -dot; }
            if (dot > 0.9995f)
            {
                var r = new Quaternion(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t, a.z + (b.z - a.z) * t, a.w + (b.w - a.w) * t);
                return r.normalized;
            }
            float theta = (float)Math.Acos(Mathf.Clamp(dot, -1f, 1f));
            float sin = (float)Math.Sin(theta);
            float wa = (float)Math.Sin((1f - t) * theta) / sin, wb = (float)Math.Sin(t * theta) / sin;
            return new Quaternion(a.x * wa + b.x * wb, a.y * wa + b.y * wb, a.z * wa + b.z * wb, a.w * wa + b.w * wb).normalized;
        }

        public static Quaternion Lerp(Quaternion a, Quaternion b, float t)
        {
            t = Mathf.Clamp01(t);
            if (Dot(a, b) < 0f) b = new Quaternion(-b.x, -b.y, -b.z, -b.w);
            return new Quaternion(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t, a.z + (b.z - a.z) * t, a.w + (b.w - a.w) * t).normalized;
        }

        public static Quaternion FromToRotation(Vector3 from, Vector3 to)
        {
            Vector3 f = from.normalized, t = to.normalized;
            float dot = Vector3.Dot(f, t);
            if (dot > 1f - 1e-6f) return identity;
            if (dot < -1f + 1e-6f)
            {
                Vector3 axis = Vector3.Cross(Vector3.right, f);
                if (axis.sqrMagnitude < 1e-6f) axis = Vector3.Cross(Vector3.up, f);
                return AngleAxis(180f, axis);
            }
            Vector3 c = Vector3.Cross(f, t);
            return new Quaternion(c.x, c.y, c.z, 1f + dot).normalized;
        }

        /// <summary>轉角 (0~360 度) 和轉軸; 幾乎沒轉時軸給 (1,0,0)</summary>
        public void ToAngleAxis(out float angle, out Vector3 axis)
        {
            Quaternion q = normalized;
            float cw = Mathf.Clamp(q.w, -1f, 1f);
            angle = 2f * (float)Math.Acos(cw) * Mathf.Rad2Deg;
            float s = (float)Math.Sqrt(Math.Max(0f, 1f - cw * cw));
            axis = s > 1e-6f ? new Vector3(q.x / s, q.y / s, q.z / s) : Vector3.right;
        }

        public static Quaternion LookRotation(Vector3 forward) => LookRotation(forward, Vector3.up);

        public static Quaternion LookRotation(Vector3 forward, Vector3 upwards)
        {
            Vector3 z = forward.normalized;
            if (z.sqrMagnitude < 1e-12f) return identity;
            Vector3 xAxis = Vector3.Cross(upwards, z);
            if (xAxis.sqrMagnitude < 1e-12f)
            {
                // forward 和 up 平行: 退回最小旋轉
                return FromToRotation(Vector3.forward, z);
            }
            xAxis = xAxis.normalized;
            Vector3 yAxis = Vector3.Cross(z, xAxis);
            return FromBasis(xAxis, yAxis, z);
        }

        // 旋轉矩陣 (列 = x, y, z 軸) 轉四元數
        static Quaternion FromBasis(Vector3 X, Vector3 Y, Vector3 Z)
        {
            float m00 = X.x, m01 = Y.x, m02 = Z.x;
            float m10 = X.y, m11 = Y.y, m12 = Z.y;
            float m20 = X.z, m21 = Y.z, m22 = Z.z;
            float trace = m00 + m11 + m22;
            Quaternion q;
            if (trace > 0f)
            {
                float s = (float)Math.Sqrt(trace + 1f) * 2f;
                q = new Quaternion((m21 - m12) / s, (m02 - m20) / s, (m10 - m01) / s, 0.25f * s);
            }
            else if (m00 > m11 && m00 > m22)
            {
                float s = (float)Math.Sqrt(1f + m00 - m11 - m22) * 2f;
                q = new Quaternion(0.25f * s, (m01 + m10) / s, (m02 + m20) / s, (m21 - m12) / s);
            }
            else if (m11 > m22)
            {
                float s = (float)Math.Sqrt(1f + m11 - m00 - m22) * 2f;
                q = new Quaternion((m01 + m10) / s, 0.25f * s, (m12 + m21) / s, (m02 - m20) / s);
            }
            else
            {
                float s = (float)Math.Sqrt(1f + m22 - m00 - m11) * 2f;
                q = new Quaternion((m02 + m20) / s, (m12 + m21) / s, 0.25f * s, (m10 - m01) / s);
            }
            return q.normalized;
        }

        public override string ToString() => $"({x:F3}, {y:F3}, {z:F3}, {w:F3})";
    }
}
