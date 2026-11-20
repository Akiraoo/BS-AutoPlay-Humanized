using System;
using System.Collections.Generic;

namespace BotPlay.Core
{
    /// <summary>
    /// BotPlay 的動作模型: 從真人 replay 統計出來的全部引數.
    /// 未打包格式 = 資料夾裡的 model.json (可讀, 本機訓練直接輸出這個);
    /// 打包格式 = .apmodel 單檔 (混淆 + 可選簽名), 見 ApModelFile.
    /// 這個類不依賴 Unity, 遊戲內和命令列工具 (tools/ApModelTool) 共用.
    /// </summary>
    public sealed class BotPlayModel
    {
        public const int CurrentFormat = 1;

        public int format = CurrentFormat;
        public ModelMeta meta = new ModelMeta();
        public HandParams left = new HandParams();
        public HandParams right = new HandParams();
        public TemplateSet templates = new TemplateSet();
        public RollOffsetSet rollOffsets = new RollOffsetSet();
        public BodyParams body;      // 身高 (舊模型沒有 -> 用 1.50 m)
        public ArmTemplateSet arm;   // 手臂位置模板 (舊模型沒有 -> 手的位置只由劍的指向推算)
        public StyleParams style;    // 個人習慣 (點塊角度、閃牆、蹲牆、躲炸彈); 舊模型沒有 -> 用內建的預設值

        public HandParams Hand(bool isLeft) => isLeft ? left : right;

        /// <summary>檢查引數是否完整, 不完整時返回原因</summary>
        public string Validate()
        {
            if (format > CurrentFormat) return $"模型格式版本 {format} 比這個模組新, 請更新 BotPlay";
            foreach (var h in new[] { left, right })
            {
                if (h == null) return "缺少手部參數";
                if (h.b?.Length != 3 || h.A?.Length != 9) return "手部位置參數長度不對";
                if (h.rollCos?.Length != 10 || h.rollSin?.Length != 10) return "扭轉參數長度不對";
                if (h.limitDeg == null || h.limitDeg.Length < 4) return "限位參數長度不對";
            }
            if (templates?.groups == null || templates.groups.Count == 0) return "沒有揮刀模板";
            if (templates.tauStep <= 0f) return "揮刀模板時間步長不對";
            foreach (var g in templates.groups)
                if (g.alpha == null || g.beta == null || g.alpha.Length != g.beta.Length || g.alpha.Length < 2) return "揮刀模板數據不完整";
            if (rollOffsets?.data == null || rollOffsets.data.Length != 2) return "缺少反手扭轉數據";
            return null;
        }
    }

    public sealed class ModelMeta
    {
        public string name = "";
        public string author = "";
        public string createdUtc = "";
        public string trainer = "";          // 訓練工具/版本
        public int replayCount;               // 用了幾份 replay
        public int swingCount;                // 統計到的揮刀次數
        public string notes = "";
        public TrainingInfo training;         // 訓練資料的詳細資訊 (舊模型沒有)
    }

    /// <summary>
    /// 模型資訊: 訓練用的資料從哪來、長什麼樣. 不存任何玩家 ID.
    /// 有作者簽名的模型, 這些內容在簽名範圍內; 未簽名的只能參考.
    /// </summary>
    public sealed class TrainingInfo
    {
        public string modVersion = "";        // 訓練時的 BotPlay 版本
        public string playerName = "";        // 回放裡的玩家名稱 (排行榜上公開的名字)
        public string platform = "";          // steam / oculus ...
        public string hmd = "";               // 最常見的頭盔
        public string gameVersions = "";      // 回放錄製時的遊戲版本 (可能多個)
        public string beatLeaderVersions = "";
        public string mode = "manual";        // manual = 固定範圍; auto = 持續追蹤最新回放

        // 篩選條件
        public string rangeFrom = "", rangeTo = "";   // 設定的日期範圍 (空 = 不限)
        public int rollingDays;                        // auto 模式: 最近幾天
        public int perMapCap, maxReplays;
        public float minStars;
        public bool rankedOnly, preferAcc;
        public string selection = "";                  // 每一步篩完剩幾份

        // 實際採用的資料
        public string dataFrom = "", dataTo = "";      // 採用的回放最早 / 最晚日期
        public int uniqueMaps;
        public float npsMedian, npsP90;
        public StarStats blStars, blTech, blPass, blAcc;  // BeatLeader 評分 (unranked 也有 AI 估計)
        public int ssRankedCount;                         // 其中 ScoreSaber ranked 的份數
        public float ssStarsMedian;
        public float techShare;                           // 和上一刀轉向 <= 90° 的揮刀比例 (tech 程度)
    }

    public sealed class StarStats
    {
        public int count;                     // 有資料的份數
        public float median, p10, p90;
    }

    /// <summary>一隻手: 位置模型 手柄 - 頭 = b + A·f; 扭轉模型 [cos,sin](roll) = C·feats(f); 指向限位</summary>
    public sealed class HandParams
    {
        public float[] b = new float[3];
        public float[] A = new float[9];        // 3x3, 按行
        public float[] rollCos = new float[10];
        public float[] rollSin = new float[10];
        public float limitFirstCenter = -165f;  // 方位角 (0=右 90=上 180=左 -90=下), 第一格中心
        public float limitStep = 30f;
        public float[] limitDeg = new float[12];
    }

    /// <summary>
    /// 身高: height = BeatLeader 記錄的遊戲內身高設定 (決定方塊的上下位置: 每高 1 m 方塊上移 0.5 m);
    /// headHeight = 實際頭高 (真人的身體高度, 和設定無關). 生成時兩個都照用, 才能還原錄製時的軌跡.
    /// </summary>
    public sealed class BodyParams
    {
        public float height;        // 設定身高中位數
        public float heightMin, heightMax;
        public float headHeight;    // 打歌時頭的高度中位數
    }

    /// <summary>
    /// 手臂位置模板: 以切塊瞬間對齊, 真人手的位置 - (b + A·指向) 的殘差, 也就是"手臂在手腕之外多揮的部分".
    /// 座標系: along = 沿揮動方向, side = 側向 (右手為準), fwd = 沿命中時劍的指向 (往方塊伸).
    /// 按 (揮動方向 0=下 1=上 2=橫, 和前後一刀的較短間隔) 分組. 單位: 米
    /// </summary>
    public sealed class ArmTemplateSet
    {
        public float tauStart = -0.5f;
        public float tauStep = 0.02f;
        public List<ArmTemplateData> groups = new List<ArmTemplateData>();
    }

    public sealed class ArmTemplateData
    {
        public int dir;
        public float gap0, gap1;
        public int n;
        public float[] along, side, fwd;
    }

    public sealed class TemplateSet
    {
        public bool peakAligned;    // 模板是否先對齊每次揮刀各自的最遠點再取中位數 (保留真實幅度)
        public float tauStart = -0.70f;
        public float tauStep = 0.02f;
        public List<SwingTemplateData> groups = new List<SwingTemplateData>();
    }

    /// <summary>揮刀模板: rel = 和上一刀揮動方向的夾角分組 (0=0° 1=45° 2=90° 3=135° 4=180°); dir: 0=下 1=上 2=橫 3=任意</summary>
    public sealed class SwingTemplateData
    {
        public int rel;
        public int dir;
        public float gap0, gap1;
        public int n;
        public float[] alpha;   // 度, 沿揮動方向
        public float[] beta;    // 度, 側向 (右手為準)
    }

    /// <summary>反手扭轉偏移: data[手 0=左 1=右][切割方向 0..8][τ], 已乘一致性 R</summary>
    public sealed class RollOffsetSet
    {
        public float tauStart = -0.20f;
        public float tauStep = 0.02f;
        public float[][][] data;
    }

    /// <summary>
    /// 從訓練回放統計出來的個人習慣. 存的是量測值本身 (不是生成器的引數); 生成器怎麼換算見各處引用 (PoseCalculator / NoteSwingPlanner / BombAvoider).
    /// 任何一項樣本不夠時那一項是 null, 生成器改用內建預設值.
    /// </summary>
    public sealed class StyleParams
    {
        public DotStyle dots;
        public LeanStyle lean;
        public DuckStyle duck;
        public BombStyle bombs;
        public CutStyle cuts;
    }

    /// <summary>
    /// 切擊: 切的時候方塊離玩家多遠 (深度), 以及切面偏離方塊中心多少 (有號). 都是分位數表, 生成器照表抽樣.
    /// 深度 = 0.72 + NJS × 提早的時間 (米, 方塊在方塊時間時的 z 是 0.72). 真人的深度和 NJS 幾乎無關 (時間偏移才和 1/NJS 成正比), 所以存深度不存毫秒.
    /// 切點偏移 = (方塊中心 - 切點) · n, n 是切面法線, 方向統一成: 法線偏左右時朝身體外側 (左手朝左、右手朝右), 偏上下時朝上 (見 CanonSign).
    ///   正 = 方塊中心在外側 / 上方, 也就是切在內側 / 下方.
    /// </summary>
    public sealed class CutStyle
    {
        public int count;
        public float[] q;                 // 分位點 (遞增, 0~1), 下面每張表都在這些分位點取值
        public float[][] depth;           // [手 0=左 1=右]
        public float depthCorr;           // 同一隻手相鄰兩刀深度的相關係數 (節奏感: 一段時間內偏早 / 偏晚)
        public float[][] offset;          // [手 * 9 + 切割方向 0..8]; 樣本不夠的組是 null (生成器改用同一隻手的 offsetAll)
        public float[][] offsetAll;       // [手] 不分方向

        /// <summary>切面法線 (nx, ny) 要乘上的正負號, 讓它指向 外側 (偏左右時) / 上方 (偏上下時)</summary>
        public static float CanonSign(float nx, float ny, bool isRight)
        {
            if (Math.Abs(nx) >= Math.Abs(ny)) return (nx >= 0f) == isRight ? 1f : -1f;
            return ny >= 0f ? 1f : -1f;
        }

        /// <summary>分位數表 table 在 u (0~1) 的值, 線性插值, 兩端外面取端點</summary>
        public float Sample(float[] table, float u)
        {
            if (table == null || q == null || table.Length != q.Length || q.Length == 0) return 0f;
            if (u <= q[0]) return table[0];
            int n = q.Length;
            if (u >= q[n - 1]) return table[n - 1];
            int i = 1; while (i < n - 1 && q[i] < u) i++;
            float w = (u - q[i - 1]) / Math.Max(1e-6f, q[i] - q[i - 1]);
            return table[i - 1] + (table[i] - table[i - 1]) * w;
        }
    }

    /// <summary>點塊: 實際切割角度 (偏離正上 / 正下, 往外為正, 度) = c0 + c1*dx_prev + c2*dx_next + c3*dy_prev + c4*dy_next (位移單位: 米, 往外為正)</summary>
    public sealed class DotStyle
    {
        public int count;
        public float[] flowUp;          // 5 個係數
        public float[] flowDown;
        public float[] downShare;       // 最下 / 中間 / 最上排 往下揮的比例
    }

    /// <summary>左右閃牆 (只蓋住中間一條軌道的全高牆), 以牆到達為 0</summary>
    public sealed class LeanStyle
    {
        public int count;
        public float peak, peakP10, peakP90;   // 最遠橫移 (米): 中位 / P10 / P90
        public float peakTime;                  // 中位曲線最遠點的時間 (秒)
        public float returnTime;                // 從最遠點回到 25% 的時間 (秒)
        public float rollPerMeter;              // 歪頭最大角度 / 最遠橫移 (度/米)
    }

    /// <summary>蹲牆 (蓋住中間兩軌的頂牆), 以牆到達為 0; 時間都取自頭高度的中位曲線</summary>
    public sealed class DuckStyle
    {
        public int count;
        public float depth;          // 最低點頭下降多少 (米)
        public float startTime;      // 下降到 10% 的時間 (秒, 負 = 牆到前)
        public float bottomTime;     // 最低點的時間
        public float recoverTime;    // 最低點之後回到 10% 的時間
        public float handFollow;     // 手的下降 / 頭的下降
    }

    /// <summary>炸彈: 每顆炸彈經過時劍身離炸彈中心最近的距離</summary>
    public sealed class BombStyle
    {
        public int count;
        public float clearMedian, clearP10;   // 米
        public float upShare;                  // 最近時劍在炸彈上方的比例
    }
}
