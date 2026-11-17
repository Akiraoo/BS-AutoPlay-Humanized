using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace AutoReplay
{
    /// <summary>生成好的动作: 每帧的头 / 左手 / 右手姿态 (手的位置已经是 BeatLeader 记录的手柄位置)</summary>
    public sealed class MotionFrames
    {
        public float t0, dt;
        public int count;
        public Vector3[] headPos, leftPos, rightPos;
        public Quaternion[] headRot, leftRot, rightRot;
    }

    /// <summary>
    /// 纯动作生成 (揮刀规划 + 头 + 手), 不依赖 BeatLeader 和游戏的谱面载入.
    /// 游戏内由 AutoReplayGenerator 呼叫; 游戏外的 tools/ApModelTool generate 也直接编译这个文件, 两边结果一致.
    /// </summary>
    public static class MotionGenerator
    {
        public const float Fps = 90f;

        /// <summary>谱面没填 NJS (0) 时游戏按难度给的默认值</summary>
        public static float DefaultNjs(string difficulty) =>
            difficulty == "ExpertPlus" ? 16f : difficulty == "Expert" ? 12f : 10f;

        /// <summary>
        /// 反应时间 RT (方块从出现到被切的时间) 和跳跃距离 JD, 公式同游戏 (BeatmapObjectSpawnMovementData):
        /// 半跳拍数从 4 开始, 半跳距离超过 18 就减半, 再加上谱面的 offset, 最少 0.25 拍
        /// </summary>
        public static float ReactionTime(float njs, float bpm, float offset, out float jd)
        {
            if (bpm <= 0f) bpm = 120f;
            float spb = 60f / bpm;
            float hjd = 4f;
            while (njs * spb * hjd > 17.999f) hjd /= 2f;
            hjd += offset;
            if (hjd < 0.25f) hjd = 0.25f;
            float rt = hjd * spb;
            jd = 2f * njs * rt;
            return rt;
        }

        /// <summary>
        /// allNotes: 要切的方块 (已按时间排序, 左手模式已镜像); bombs / obstacles: 炸弹和墙.
        /// njs: 炸弹飞行速度; rt: 反应时间.
        /// </summary>
        public static MotionFrames Generate(List<NoteData> allNotes, List<NoteData> bombs, List<ObstacleData> obstacles,
                                            float njs, float rt, bool leftHanded, bool noArrows)
        {
            float startTime = 0f;
            float endTime = allNotes.Count > 0 ? allNotes.Max(n => n.time) + 2.5f : 60f;
            if (obstacles.Count > 0) endTime = Math.Max(endTime, obstacles.Max(o => o.time + o.duration) + 2.0f);

            var leftNotes = allNotes.Where(n => n.colorType == ColorType.ColorA).ToList();
            var rightNotes = allNotes.Where(n => n.colorType == ColorType.ColorB).ToList();

            // 炸弹飞行速度 = 这张谱的 NJS (米/歌曲秒); 挥刀时机要看得见方块才行: 预挥最远点不早于 (RT - 感知延迟)
            BombAvoider.BombSpeed = njs;
            NoteSwingPlanner.ReactionTime = rt;

            var leftSwings = NoteSwingPlanner.PlanSwings(leftNotes, bombs, AutoReplayMath.LeftWristBase, true, noArrows);
            var rightSwings = NoteSwingPlanner.PlanSwings(rightNotes, bombs, AutoReplayMath.RightWristBase, false, noArrows);

            var leftCtx = new HandMotionContext(leftSwings, bombs, 0.37f);
            var rightCtx = new HandMotionContext(rightSwings, bombs, 0.71f);

            float dt = 1f / Fps;
            int totalFrames = (int)Math.Ceiling((endTime - startTime) * Fps);
            var m = new MotionFrames
            {
                t0 = startTime, dt = dt, count = totalFrames,
                headPos = new Vector3[totalFrames], headRot = new Quaternion[totalFrames],
                leftPos = new Vector3[totalFrames], leftRot = new Quaternion[totalFrames],
                rightPos = new Vector3[totalFrames], rightRot = new Quaternion[totalFrames],
            };

            // 头: 目标姿态用临界阻尼弹簧平滑 (原本躲墙时头是瞬移的), 再叠加待机微动.
            // 先把整条头部轨迹算出来, 手要跟着身体一起移动
            var headTarget0 = PoseCalculator.CalculateHeadTarget(startTime, obstacles, leftHanded);
            Vector3 headPos = headTarget0.position, headPosVel = Vector3.zero;
            float headPitch = headTarget0.pitch, headPitchVel = 0f;
            float headRoll = headTarget0.roll, headRollVel = 0f;
            const float headSmooth = 0.07f;
            var bodyArr = new Vector3[totalFrames];
            for (int i = 0; i < totalFrames; i++)
            {
                float t = startTime + i * dt;
                var ht = PoseCalculator.CalculateHeadTarget(t, obstacles, leftHanded);
                headPos = Vector3.SmoothDamp(headPos, ht.position, ref headPosVel, headSmooth, float.PositiveInfinity, dt);
                headPitch = Mathf.SmoothDamp(headPitch, ht.pitch, ref headPitchVel, headSmooth, float.PositiveInfinity, dt);
                headRoll = Mathf.SmoothDamp(headRoll, ht.roll, ref headRollVel, headSmooth, float.PositiveInfinity, dt);
                var head = PoseCalculator.ApplyHeadIdle(t, headPos, headPitch, headRoll);
                m.headPos[i] = head.position;
                m.headRot[i] = head.rotation;
                // 身体平移 = 头相对基准位置的偏移 (躲墙往旁边靠/蹲下时, 肩膀和手也一起移动, 只是幅度小一点)
                var off = headPos - HumanModel.HeadBase;
                bodyArr[i] = new Vector3(off.x * PoseCalculator.BodyFollowX, off.y * PoseCalculator.BodyFollowY, off.z * PoseCalculator.BodyFollowX);
            }
            Vector3 BodyOffset(float t)
            {
                float u = (t - startTime) / dt;
                if (u <= 0f) return bodyArr[0];
                if (u >= totalFrames - 1) return bodyArr[totalFrames - 1];
                int i0 = (int)u;
                return Vector3.Lerp(bodyArr[i0], bodyArr[i0 + 1], u - i0);
            }
            leftCtx.bodyOffset = BodyOffset;
            rightCtx.bodyOffset = BodyOffset;

            // 手: 整条轨道一次生成 (要做时间上的平滑, 不能逐帧独立算)
            PoseCalculator.BuildHandTrack(startTime, dt, totalFrames, leftSwings, bombs, true, leftCtx, m.leftPos, m.leftRot);
            PoseCalculator.BuildHandTrack(startTime, dt, totalFrames, rightSwings, bombs, false, rightCtx, m.rightPos, m.rightRot);

            // 回放记录的是手柄位置: 手腕往剑的方向前移 PommelOffset
            var pommelOffset = new Vector3(0f, 0f, HumanModel.PommelOffset);
            for (int i = 0; i < totalFrames; i++)
            {
                m.leftPos[i] = m.leftPos[i] + (m.leftRot[i] * pommelOffset);
                m.rightPos[i] = m.rightPos[i] + (m.rightRot[i] * pommelOffset);
            }
            return m;
        }

        /// <summary>BeatLeader 用 "时间 + 音符 ID" 匹配事件: ID = (计分类型+2)*10000 + 列*1000 + 层*100 + 颜色*10 + 方向</summary>
        public static int NoteId(NoteData n, bool noArrows)
        {
            int dir = noArrows ? (int)NoteCutDirection.Any : (int)n.cutDirection;
            return ((int)n.scoringType + 2) * 10000 + n.lineIndex * 1000 + (int)n.noteLineLayer * 100 + (int)n.colorType * 10 + dir;
        }

        /// <summary>每种计分类型的满分 (链条头 85, 链条切片 20, 其余 115)</summary>
        public static int MaxCutScore(NoteData.ScoringType t)
        {
            switch (t)
            {
                case NoteData.ScoringType.ChainHead:
                case NoteData.ScoringType.ChainHeadArcTail: return 85;
                case NoteData.ScoringType.ChainLink:
                case NoteData.ScoringType.ChainLinkArcHead: return 20;
                case NoteData.ScoringType.Ignore:
                case NoteData.ScoringType.NoScore: return 0;
                default: return 115;
            }
        }
    }
}
