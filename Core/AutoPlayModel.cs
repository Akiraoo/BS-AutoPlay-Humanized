using System;
using System.Collections.Generic;

namespace AutoReplay.Core
{
    /// <summary>
    /// AutoPlay 的動作模型: 從真人 replay 統計出來的全部引數.
    /// 未打包格式 = 資料夾裡的 model.json (可讀, 本機訓練直接輸出這個);
    /// 打包格式 = .apmodel 單檔 (混淆 + 可選簽名), 見 ApModelFile.
    /// 這個類不依賴 Unity, 遊戲內和命令列工具 (tools/ApModelTool) 共用.
    /// </summary>
    public sealed class AutoPlayModel
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

        public HandParams Hand(bool isLeft) => isLeft ? left : right;

        /// <summary>檢查引數是否完整, 不完整時返回原因</summary>
        public string Validate()
        {
            if (format > CurrentFormat) return $"模型格式版本 {format} 比這個模組新, 請更新 AutoPlay";
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
        public string modVersion = "";        // 訓練時的 AutoPlay 版本
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
}
