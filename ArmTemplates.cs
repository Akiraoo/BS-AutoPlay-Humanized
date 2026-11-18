using System.Collections.Generic;
using UnityEngine;
using BotPlay.Core;

namespace BotPlay
{
    /// <summary>
    /// 手臂位置模板 (模型的 arm): 手腕之外, 手臂在一次揮刀裡多揮的位移.
    /// 真人資料: 往下揮時, 預揮階段手臂比"按指向推算"多往後拉約 8 cm, 收勢時多往前送約 9 cm 並往方塊方向伸 6~12 cm.
    /// 舊模型沒有 arm -> 回傳 0, 手的位置照舊只由指向推算.
    /// </summary>
    public static class ArmTemplates
    {
        static BotPlayModel _builtFor;
        static List<ArmTemplateData>[] _byDir;
        static float _tauStart, _tauStep;

        static bool Ensure()
        {
            var model = ModelManager.Current;
            if (model?.arm == null || model.arm.groups == null || model.arm.groups.Count == 0) return false;
            if (!ReferenceEquals(_builtFor, model))
            {
                _builtFor = model;
                _tauStart = model.arm.tauStart; _tauStep = model.arm.tauStep;
                _byDir = new[] { new List<ArmTemplateData>(), new List<ArmTemplateData>(), new List<ArmTemplateData>() };
                foreach (var g in model.arm.groups)
                    if (g.dir >= 0 && g.dir < 3 && g.along != null) _byDir[g.dir].Add(g);
                foreach (var l in _byDir) l.Sort((a, b) => (a.gap0 + a.gap1).CompareTo(b.gap0 + b.gap1));
            }
            return true;
        }

        public static bool Available => Ensure();

        static float Sample(float[] v, float tau)
        {
            float x = (tau - _tauStart) / _tauStep;
            if (x <= 0f) return v[0];
            if (x >= v.Length - 1) return v[v.Length - 1];
            int i = (int)x; float w = x - i;
            return v[i] + (v[i + 1] - v[i]) * w;
        }

        /// <summary>揮刀座標系裡的位移 (along, side, fwd), 間隔落在兩組之間時線性插值</summary>
        public static Vector3 Eval(int dirClass, float gap, float tau)
        {
            if (!Ensure()) return Vector3.zero;
            var list = _byDir[Mathf.Clamp(dirClass, 0, 2)];
            if (list.Count == 0)
            {
                // 這個方向沒有資料: 用其他方向的 (橫向少的時候常見)
                foreach (var l in _byDir) if (l.Count > 0) { list = l; break; }
                if (list.Count == 0) return Vector3.zero;
            }
            ArmTemplateData a = list[0], b = list[0];
            float w = 0f;
            float Center(ArmTemplateData g) => 0.5f * (g.gap0 + Mathf.Min(g.gap1, 1.2f));
            if (gap >= Center(list[list.Count - 1])) a = b = list[list.Count - 1];
            else if (gap > Center(list[0]))
                for (int i = 0; i + 1 < list.Count; i++)
                    if (gap <= Center(list[i + 1])) { a = list[i]; b = list[i + 1]; w = (gap - Center(a)) / (Center(b) - Center(a)); break; }
            return new Vector3(
                Mathf.Lerp(Sample(a.along, tau), Sample(b.along, tau), w),
                Mathf.Lerp(Sample(a.side, tau), Sample(b.side, tau), w),
                Mathf.Lerp(Sample(a.fwd, tau), Sample(b.fwd, tau), w));
        }
    }
}
