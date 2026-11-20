// 遊戲外用的 Beat Saber 譜面型別替代品 (只有生成器用到的欄位). 列舉數值和遊戲 BeatmapCore.dll 一致.
using System;
using BotPlay.Core;

public enum ColorType { ColorA = 0, ColorB = 1, None = -1 }
public enum NoteCutDirection { Up = 0, Down = 1, Left = 2, Right = 3, UpLeft = 4, UpRight = 5, DownLeft = 6, DownRight = 7, Any = 8, None = 9 }
public enum NoteLineLayer { Base = 0, Upper = 1, Top = 2 }

public class NoteData
{
    public enum GameplayType { Normal = 0, Bomb = 1, BurstSliderHead = 2, BurstSliderElement = 3 }
    public enum ScoringType { Ignore = -1, NoScore = 0, Normal = 1, ArcHead = 2, ArcTail = 3, ChainHead = 4, ChainLink = 5, ArcHeadArcTail = 6, ChainHeadArcTail = 7, ChainLinkArcHead = 8 }

    public float time;
    public float beat;
    public int lineIndex;
    public NoteLineLayer noteLineLayer;
    public ColorType colorType;
    public NoteCutDirection cutDirection;
    public float cutDirectionAngleOffset;
    public GameplayType gameplayType;
    public ScoringType scoringType = ScoringType.Normal;

    public NoteData CopyWith() => (NoteData)MemberwiseClone();

    /// <summary>左手模式: 和遊戲一樣左右映象 (軌道翻轉、紅藍互換、切割方向映象)</summary>
    public void Mirror(int lineCount)
    {
        lineIndex = lineCount - 1 - lineIndex;
        if (colorType == ColorType.ColorA) colorType = ColorType.ColorB;
        else if (colorType == ColorType.ColorB) colorType = ColorType.ColorA;
        cutDirection = MirrorDir(cutDirection);
        cutDirectionAngleOffset = -cutDirectionAngleOffset;
    }

    static NoteCutDirection MirrorDir(NoteCutDirection d)
    {
        switch (d)
        {
            case NoteCutDirection.Left: return NoteCutDirection.Right;
            case NoteCutDirection.Right: return NoteCutDirection.Left;
            case NoteCutDirection.UpLeft: return NoteCutDirection.UpRight;
            case NoteCutDirection.UpRight: return NoteCutDirection.UpLeft;
            case NoteCutDirection.DownLeft: return NoteCutDirection.DownRight;
            case NoteCutDirection.DownRight: return NoteCutDirection.DownLeft;
            default: return d;
        }
    }
}

public class ObstacleData
{
    public float time, duration;
    public int lineIndex, width, height;
    public NoteLineLayer lineLayer;
    public float endTime => time + duration;
}

namespace BotPlay
{
    /// <summary>遊戲外: 日誌直接印到終端 (Info 預設不印, 太吵)</summary>
    public sealed class StandaloneLog
    {
        public bool verbose;
        public void Info(string s) { if (verbose) Console.WriteLine("[info] " + s); }
        public void Debug(string s) { if (verbose) Console.WriteLine("[debug] " + s); }
        public void Warn(string s) => Console.WriteLine("[warn] " + s);
        public void Error(string s) => Console.WriteLine("[error] " + s);
    }

    public static class Plugin
    {
        public const string Version = "1.4.0-standalone";
        public static StandaloneLog Log = new StandaloneLog();
    }

    /// <summary>遊戲外: 只有一個"目前的模型", 由命令列指定</summary>
    public static class ModelManager
    {
        public static BotPlayModel Current { get; set; }
        public static string CurrentId { get; set; } = "";
        public static string CurrentFingerprint { get; set; } = "";
        public static ModelTrust CurrentTrust { get; set; }

        public static BotPlayModel Require() => Current ?? throw new InvalidOperationException("尚未載入 BotPlay 模型");
    }
}
