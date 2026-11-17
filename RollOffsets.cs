using UnityEngine;
using AutoReplay.Core;

namespace AutoReplay
{
    /// <summary>
    /// 反手 / 手腕翻轉: 切橫向、斜向方塊時, 真人的前臂會在切到的那一瞬間大幅旋轉 (例如右手往外切 R 約 -60°),
    /// 只看"劍指向哪"的扭轉模型會把這些平均掉, 看起來就像永遠只用正手.
    /// 這裡按 (手, 切割方向) 給出命中前後 ±0.2s 的扭轉偏移曲線, 疊加在扭轉模型之上. 資料來自當前載入的模型.
    /// </summary>
    public static class RollOffsets
    {
        /// <param name="cutDirection">NoteCutDirection 的數值 (0..7 箭頭, 8 = 點塊/Any, 9 = None 也當點塊)</param>
        public static float At(bool isLeft, int cutDirection, float tau)
        {
            var set = ModelManager.Require().rollOffsets;
            float[] v = set.data[isLeft ? 0 : 1][Mathf.Clamp(cutDirection, 0, 8)];
            if (v == null || v.Length < 2) return 0f;
            float u = (tau - set.tauStart) / set.tauStep;
            if (u <= 0f || u >= v.Length - 1) return 0f;
            int i = (int)u;
            // 視窗兩端淡出, 避免在 ±0.2s 處突然跳回 0
            float taper = AutoReplayMath.SmoothStep((Window - Mathf.Abs(tau)) / 0.06f);
            return Mathf.Lerp(v[i], v[i + 1], u - i) * taper;
        }

        public const float Window = 0.2f;
    }
}
