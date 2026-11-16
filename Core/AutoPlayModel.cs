using System;
using System.Collections.Generic;

namespace AutoReplay.Core
{
    /// <summary>
    /// AutoPlay 的动作模型: 从真人 replay 统计出来的全部参数.
    /// 未打包格式 = 资料夹里的 model.json (可读, 本机训练直接输出这个);
    /// 打包格式 = .apmodel 单档 (混淆 + 可选签名), 见 ApModelFile.
    /// 这个类不依赖 Unity, 游戏内和命令行工具 (tools/ApModelTool) 共用.
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

        public HandParams Hand(bool isLeft) => isLeft ? left : right;

        /// <summary>检查参数是否完整, 不完整时返回原因</summary>
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
        public string trainer = "";          // 训练工具/版本
        public int replayCount;               // 用了几份 replay
        public int swingCount;                // 统计到的挥刀次数
        public string notes = "";
        public TrainingInfo training;         // 训练资料的详细信息 (旧模型没有)
    }

    /// <summary>
    /// 模型资讯: 训练用的资料从哪来、长什么样. 不存任何玩家 ID.
    /// 有作者签名的模型, 这些内容在签名范围内; 未签名的只能参考.
    /// </summary>
    public sealed class TrainingInfo
    {
        public string modVersion = "";        // 训练时的 AutoPlay 版本
        public string playerName = "";        // 回放里的玩家名称 (排行榜上公开的名字)
        public string platform = "";          // steam / oculus ...
        public string hmd = "";               // 最常见的头盔
        public string gameVersions = "";      // 回放录制时的游戏版本 (可能多个)
        public string beatLeaderVersions = "";
        public string mode = "manual";        // manual = 固定范围; auto = 持续追踪最新回放

        // 筛选条件
        public string rangeFrom = "", rangeTo = "";   // 设定的日期范围 (空 = 不限)
        public int rollingDays;                        // auto 模式: 最近几天
        public int perMapCap, maxReplays;
        public float minStars;
        public string selection = "";                  // 每一步筛完剩几份

        // 实际采用的资料
        public string dataFrom = "", dataTo = "";      // 采用的回放最早 / 最晚日期
        public int uniqueMaps;
        public float npsMedian, npsP90;
        public StarStats blStars, blTech, blPass, blAcc;  // BeatLeader 评分 (unranked 也有 AI 估计)
        public int ssRankedCount;                         // 其中 ScoreSaber ranked 的份数
        public float ssStarsMedian;
        public float techShare;                           // 和上一刀转向 <= 90° 的挥刀比例 (tech 程度)
    }

    public sealed class StarStats
    {
        public int count;                     // 有数据的份数
        public float median, p10, p90;
    }

    /// <summary>一只手: 位置模型 手柄 - 头 = b + A·f; 扭转模型 [cos,sin](roll) = C·feats(f); 指向限位</summary>
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

    public sealed class TemplateSet
    {
        public float tauStart = -0.70f;
        public float tauStep = 0.02f;
        public List<SwingTemplateData> groups = new List<SwingTemplateData>();
    }

    /// <summary>挥刀模板: rel = 和上一刀挥动方向的夹角分组 (0=0° 1=45° 2=90° 3=135° 4=180°); dir: 0=下 1=上 2=横 3=任意</summary>
    public sealed class SwingTemplateData
    {
        public int rel;
        public int dir;
        public float gap0, gap1;
        public int n;
        public float[] alpha;   // 度, 沿挥动方向
        public float[] beta;    // 度, 侧向 (右手为准)
    }

    /// <summary>反手扭转偏移: data[手 0=左 1=右][切割方向 0..8][τ], 已乘一致性 R</summary>
    public sealed class RollOffsetSet
    {
        public float tauStart = -0.20f;
        public float tauStep = 0.02f;
        public float[][][] data;
    }
}
