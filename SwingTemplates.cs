using System.Collections.Generic;
using UnityEngine;
using AutoReplay.Core;

namespace AutoReplay
{
    /// <summary>
    /// 真人揮刀模板: 以"切到方塊的瞬間"為 0 點對齊, 統計真人 replay 裡劍的軌跡 (中位數曲線).
    ///   alpha(τ): 沿揮動方向的角度. τ&lt;0 時為負 (預揮, 劍還在後方), τ&gt;0 時為正 (後揮)
    ///   beta(τ) : 偏離揮動平面的側向角度 -> 來回揮時的 "8 字" / 同向復位時的繞圈
    /// 按 (和上一刀揮動方向的夾角 0/45/90/135/180°, 揮動方向, 和上一刀的間隔) 分組; 間隔落在兩組之間時線性插值.
    /// 夾角分組很重要: tech 譜常見的 90° 轉向 (例如 下->右) 預揮是從側邊繞進來的, 和來回揮(180°)、同向復位(0°) 都不一樣.
    /// 資料來自當前載入的 AutoPlay 模型 (ModelManager), 訓練見 Core/ModelTrainer.cs.
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
            // 優先: 同夾角同方向 -> 同夾角任意方向 -> 相鄰夾角 (先往大的找, 0°/45° 資料少)
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
        /// <summary>兩個揮動方向(2D)的夾角分組: 0=0° 1=45° 2=90° 3=135° 4=180°</summary>
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

        /// <summary>τ 時刻的 (alpha, beta), 單位: 度. beta 以右手為準</summary>
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

        // 超過 AmplitudeKnee 的揮動幅度按 AmplitudeSlope 壓縮, 只防極端角度 (追蹤跳變); 指向軟限位 (<=125°) 另外防戳到頭.
        // 原本 90° / 0.45 壓太多: 真人收勢會揮到離命中方向 115-122°, 生成只到 92°, 兩刀之間的速度只有真人一半.
        // 離線對比真人 replay: 110° / 0.8 的誤差和完全不壓縮一樣 (23.8° vs 原本 25.3°), 速度中位 213 -> 269°/s (真人 339)
        public static float AmplitudeKnee = 110f;
        public const float AmplitudeSlope = 0.8f;
        static float Compress(float a)
        {
            float m = Mathf.Abs(a);
            if (m <= AmplitudeKnee) return a;
            return Mathf.Sign(a) * (AmplitudeKnee + (m - AmplitudeKnee) * AmplitudeSlope);
        }

        /// <summary>預揮最遠點距離命中的時間 (正數, 秒) —— 用來和反應時間比較, 太早就壓縮</summary>
        public static float PreExtremeLead(int rel, int dir, float gap)
        {
            float best = 0f, bestT = 0.1f;
            Candidates(rel, dir);   // 確保 TauStart/TauStep 已按當前模型設定
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
