using System.Collections.Generic;
using UnityEngine;
using AutoReplay.Core;

namespace AutoReplay
{
    /// <summary>
    /// 真人挥刀模板: 以"切到方块的瞬间"为 0 点对齐, 统计真人 replay 里剑的轨迹 (中位数曲线).
    ///   alpha(τ): 沿挥动方向的角度. τ&lt;0 时为负 (预挥, 剑还在后方), τ&gt;0 时为正 (后挥)
    ///   beta(τ) : 偏离挥动平面的侧向角度 -> 来回挥时的 "8 字" / 同向复位时的绕圈
    /// 按 (和上一刀挥动方向的夹角 0/45/90/135/180°, 挥动方向, 和上一刀的间隔) 分组; 间隔落在两组之间时线性插值.
    /// 夹角分组很重要: tech 谱常见的 90° 转向 (例如 下->右) 预挥是从侧边绕进来的, 和来回挥(180°)、同向复位(0°) 都不一样.
    /// 数据来自当前载入的 AutoPlay 模型 (ModelManager), 训练见 Core/ModelTrainer.cs.
    /// </summary>
    public static class SwingTemplates
    {
        public sealed class Template
        {
            public readonly int rel;
            public readonly int dir;
            public readonly float gap0, gap1;
            public readonly int count;
            public readonly float[] alpha, beta;
            public float Center => 0.5f * (gap0 + gap1);

            public Template(int rel, int dir, float gap0, float gap1, int count, float[] alpha, float[] beta)
            {
                this.rel = rel; this.dir = dir; this.gap0 = gap0; this.gap1 = gap1; this.count = count;
                this.alpha = alpha; this.beta = beta;
            }
        }

        public const int DirDown = 0, DirUp = 1, DirSide = 2, DirAny = 3;

        static Dictionary<int, List<Template>> _byKey;
        static AutoPlayModel _builtFor;
        static float TauStart, TauStep;
        static int TauCount;

        static List<Template> Candidates(int rel, int dir)
        {
            var model = ModelManager.Require();
            if (_byKey == null || !ReferenceEquals(_builtFor, model))
            {
                _builtFor = model;
                TauStart = model.templates.tauStart;
                TauStep = model.templates.tauStep;
                TauCount = 0;
                _byKey = new Dictionary<int, List<Template>>();
                foreach (var g in model.templates.groups)
                {
                    var t = new Template(g.rel, g.dir, g.gap0, g.gap1, g.n, g.alpha, g.beta);
                    TauCount = Mathf.Max(TauCount, g.alpha.Length);
                    int k = Key(t.rel, t.dir);
                    if (!_byKey.TryGetValue(k, out var list)) _byKey[k] = list = new List<Template>();
                    list.Add(t);
                }
                foreach (var list in _byKey.Values) list.Sort((a, b) => a.Center.CompareTo(b.Center));
            }
            // 优先: 同夹角同方向 -> 同夹角任意方向 -> 相邻夹角 (先往大的找, 0°/45° 数据少)
            for (int d = 0; d <= 4; d++)
            {
                foreach (int r in d == 0 ? new[] { rel } : new[] { rel + d, rel - d })
                {
                    if (r < 0 || r > 4) continue;
                    if (_byKey.TryGetValue(Key(r, dir), out var l1) && l1.Count > 0) return l1;
                    if (_byKey.TryGetValue(Key(r, DirAny), out var l2) && l2.Count > 0) return l2;
                }
            }
            return null;
        }

        static int Key(int rel, int dir) => rel * 10 + dir;

        public const int RelBack = 4, Rel135 = 3, Rel90 = 2, Rel45 = 1, RelSame = 0;
        /// <summary>两个挥动方向(2D)的夹角分组: 0=0° 1=45° 2=90° 3=135° 4=180°</summary>
        public static int RelBin(Vector3 a, Vector3 b)
        {
            float ang = Vector3.Angle(a, b);
            return ang < 30f ? 0 : ang < 67.5f ? 1 : ang < 112.5f ? 2 : ang < 157.5f ? 3 : 4;
        }

        public static int DirClass(Vector3 cutDir2D)
        {
            if (cutDir2D.y < -0.5f) return DirDown;
            if (cutDir2D.y > 0.5f) return DirUp;
            return DirSide;
        }

        /// <summary>τ 时刻的 (alpha, beta), 单位: 度. beta 以右手为准</summary>
        public static void Eval(int rel, int dir, float gap, float tau, out float alpha, out float beta)
        {
            var list = Candidates(rel, dir);
            alpha = 0f; beta = 0f;
            if (list == null) return;

            Template a = list[0], b = list[0];
            float w = 0f;
            if (gap >= list[list.Count - 1].Center) a = b = list[list.Count - 1];
            else if (gap > list[0].Center)
            {
                for (int i = 0; i + 1 < list.Count; i++)
                {
                    if (gap <= list[i + 1].Center)
                    {
                        a = list[i]; b = list[i + 1];
                        w = (gap - a.Center) / (b.Center - a.Center);
                        break;
                    }
                }
            }
            alpha = Compress(Mathf.Lerp(Sample(a.alpha, tau), Sample(b.alpha, tau), w));
            beta = Mathf.Lerp(Sample(a.beta, tau), Sample(b.beta, tau), w);
        }

        // 真人数据里 90° 以外的挥动幅度有一部分是追踪误差 (手柄靠近头显时跳变, 看起来像剑戳到脑袋),
        // 超过 AmplitudeKnee 的部分按 AmplitudeSlope 压缩: 例如 -150° -> -117°, +120° -> +103°
        public const float AmplitudeKnee = 90f;
        public const float AmplitudeSlope = 0.45f;
        static float Compress(float a)
        {
            float m = Mathf.Abs(a);
            if (m <= AmplitudeKnee) return a;
            return Mathf.Sign(a) * (AmplitudeKnee + (m - AmplitudeKnee) * AmplitudeSlope);
        }

        /// <summary>预挥最远点距离命中的时间 (正数, 秒) —— 用来和反应时间比较, 太早就压缩</summary>
        public static float PreExtremeLead(int rel, int dir, float gap)
        {
            float best = 0f, bestT = 0.1f;
            Candidates(rel, dir);   // 确保 TauStart/TauStep 已按当前模型设置
            for (int i = 0; i < TauCount; i++)
            {
                float tau = TauStart + i * TauStep;
                if (tau >= 0f) break;
                Eval(rel, dir, gap, tau, out float al, out _);
                if (al < best) { best = al; bestT = -tau; }
            }
            return bestT;
        }

        static float Sample(float[] v, float tau)
        {
            float u = (tau - TauStart) / TauStep;
            if (u <= 0f) return v[0];
            if (u >= v.Length - 1) return v[v.Length - 1];
            int i = (int)u;
            return Mathf.Lerp(v[i], v[i + 1], u - i);
        }
    }
}
