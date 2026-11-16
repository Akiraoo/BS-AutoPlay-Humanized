using UnityEngine;
using AutoReplay.Core;

namespace AutoReplay
{
    /// <summary>
    /// 反手 / 手腕翻转: 切横向、斜向方块时, 真人的前臂会在切到的那一瞬间大幅旋转 (例如右手往外切 R 约 -60°),
    /// 只看"剑指向哪"的扭转模型会把这些平均掉, 看起来就像永远只用正手.
    /// 这里按 (手, 切割方向) 给出命中前后 ±0.2s 的扭转偏移曲线, 叠加在扭转模型之上. 数据来自当前载入的模型.
    /// </summary>
    public static class RollOffsets
    {
        /// <param name="cutDirection">NoteCutDirection 的数值 (0..7 箭头, 8 = 点块/Any, 9 = None 也当点块)</param>
        public static float At(bool isLeft, int cutDirection, float tau)
        {
            var set = ModelManager.Require().rollOffsets;
            float[] v = set.data[isLeft ? 0 : 1][Mathf.Clamp(cutDirection, 0, 8)];
            if (v == null || v.Length < 2) return 0f;
            float u = (tau - set.tauStart) / set.tauStep;
            if (u <= 0f || u >= v.Length - 1) return 0f;
            int i = (int)u;
            // 窗口两端淡出, 避免在 ±0.2s 处突然跳回 0
            float taper = AutoReplayMath.SmoothStep((Window - Mathf.Abs(tau)) / 0.06f);
            return Mathf.Lerp(v[i], v[i + 1], u - i) * taper;
        }

        public const float Window = 0.2f;
    }
}
