using System.Collections.Generic;
using UnityEngine;
using BLVector3 = BeatLeader.Models.Replay.Vector3;
using BLQuaternion = BeatLeader.Models.Replay.Quaternion;

namespace AutoReplay
{
    public static class AutoReplayMath
    {
        public const float BaseHeadHeight = 1.50f;   // 旧模型 (没有身高资料) 的默认值, 也是下面方块高度的基准身高
        public const float DuckHeadHeight = 1.00f;

        // 身高 (来自模型): 设定身高决定方块和墙的上下位置 (每高 1 m 上移 0.5 m, 真人回放实测), 头高是真人身体的高度
        public static float PlayerHeight { get { var b = ModelManager.Current?.body; return b != null && b.height > 1f ? b.height : BaseHeadHeight; } }
        public static float HeadHeight { get { var b = ModelManager.Current?.body; return b != null && b.headHeight > 1f ? b.headHeight : BaseHeadHeight; } }
        public static float NoteYOffset => 0.5f * (PlayerHeight - BaseHeadHeight);
        public static float DuckHeight => DuckHeadHeight + NoteYOffset;
        public const float CutDistanceZ = 1.50f;

        // 手的基准位置 (剑指向正前方附近时手柄所在处), 来自 HumanModel 的真人统计.
        // 实际手腕位置随剑的指向移动, 见 HumanModel.WristFor
        public static Vector3 LeftWristBase => HumanModel.HandBase(true);
        public static Vector3 RightWristBase => HumanModel.HandBase(false);

        // ================= 平滑噪声 (待机微动) =================
        // 几个不成倍数的正弦叠加, 输出约 [-1, 1], 连续且平滑, 不需要随机数状态
        public static float Noise(float t, float seed)
        {
            return 0.5f * Mathf.Sin(t * 1.37f + seed * 12.9898f)
                 + 0.3f * Mathf.Sin(t * 2.71f + seed * 78.233f)
                 + 0.2f * Mathf.Sin(t * 5.03f + seed * 37.719f);
        }

        // Cardinal resting poses (+10 degrees larger swing)
        public static readonly Quaternion PoseUp = Quaternion.Euler(-90f, 0f, 0f);
        public static readonly Quaternion PoseDown = Quaternion.Euler(90f, 0f, 0f);
        public static readonly Quaternion PoseLeft = Quaternion.Euler(0f, -90f, 0f);
        public static readonly Quaternion PoseRight = Quaternion.Euler(0f, 90f, 0f);

        // Diagonal resting poses (+10 degrees)
        public static readonly Quaternion PoseUpLeft = Quaternion.Euler(-70f, -70f, 0f);
        public static readonly Quaternion PoseUpRight = Quaternion.Euler(-70f, 70f, 0f);
        public static readonly Quaternion PoseDownLeft = Quaternion.Euler(70f, -70f, 0f);
        public static readonly Quaternion PoseDownRight = Quaternion.Euler(70f, 70f, 0f);

        public static float SmoothStep(float t)
        {
            t = Mathf.Clamp01(t);
            return t * t * (3f - 2f * t);
        }

        public static float GetCutAngle(NoteData note)
        {
            float baseAngle = 180f;
            switch (note.cutDirection)
            {
                case NoteCutDirection.Up: baseAngle = 0f; break;
                case NoteCutDirection.Down: baseAngle = 180f; break;
                case NoteCutDirection.Left: baseAngle = 270f; break;
                case NoteCutDirection.Right: baseAngle = 90f; break;
                case NoteCutDirection.UpLeft: baseAngle = 315f; break;
                case NoteCutDirection.UpRight: baseAngle = 45f; break;
                case NoteCutDirection.DownLeft: baseAngle = 225f; break;
                case NoteCutDirection.DownRight: baseAngle = 135f; break;
                case NoteCutDirection.Any:
                case NoteCutDirection.None:
                default:
                    baseAngle = 180f;
                    break;
            }
            return (baseAngle + note.cutDirectionAngleOffset) % 360f;
        }

        public static Vector3 GetNotePos(NoteData note)
        {
            float noteX = (note.lineIndex - 1.5f) * 0.60f;
            float noteY = 0.72f + ((int)note.noteLineLayer) * 0.53f + NoteYOffset; // 身高 1.50 时 Layer 0: 0.72, 1: 1.25, 2: 1.78
            return new Vector3(noteX, noteY, CutDistanceZ);
        }

        public static Vector3 RodriguesRotate(Vector3 v, Vector3 axis, float deg)
        {
            float rad = deg * Mathf.Deg2Rad;
            float c = Mathf.Cos(rad);
            float s = Mathf.Sin(rad);
            return (v * c + Vector3.Cross(axis, v) * s + axis * Vector3.Dot(axis, v) * (1f - c)).normalized;
        }

        public static (Quaternion start, Quaternion hit, Quaternion end) GetSwingPoses(NoteData note, NoteData prevNote, Vector3 wristPos, Quaternion currentRest)
        {
            Vector3 notePos = GetNotePos(note);
            Vector3 vCenter = (notePos - wristPos).normalized;

            Vector3 restFwd = currentRest * Vector3.forward;

            // Determine 2D cut direction
            Vector3 dCut = Vector3.down; // Default down
            switch (note.cutDirection)
            {
                case NoteCutDirection.Down: dCut = Vector3.down; break;
                case NoteCutDirection.Up: dCut = Vector3.up; break;
                case NoteCutDirection.Left: dCut = Vector3.left; break;
                case NoteCutDirection.Right: dCut = Vector3.right; break;
                case NoteCutDirection.UpLeft: dCut = new Vector3(-0.7071f, 0.7071f, 0f); break;
                case NoteCutDirection.UpRight: dCut = new Vector3(0.7071f, 0.7071f, 0f); break;
                case NoteCutDirection.DownLeft: dCut = new Vector3(-0.7071f, -0.7071f, 0f); break;
                case NoteCutDirection.DownRight: dCut = new Vector3(0.7071f, -0.7071f, 0f); break;
                case NoteCutDirection.Any:
                case NoteCutDirection.None:
                default:
                    // If following a note, inherit direction or natural flow
                    if (prevNote != null)
                    {
                        if (prevNote.cutDirection == NoteCutDirection.Up || prevNote.cutDirection == NoteCutDirection.UpLeft || prevNote.cutDirection == NoteCutDirection.UpRight)
                            dCut = Vector3.up;
                        else if (prevNote.cutDirection == NoteCutDirection.Down || prevNote.cutDirection == NoteCutDirection.DownLeft || prevNote.cutDirection == NoteCutDirection.DownRight)
                            dCut = Vector3.down;
                        else if (prevNote.cutDirection == NoteCutDirection.Left)
                            dCut = Vector3.left;
                        else if (prevNote.cutDirection == NoteCutDirection.Right)
                            dCut = Vector3.right;
                        else
                            dCut = (restFwd.y < -0.2f) ? Vector3.up : Vector3.down;
                    }
                    else
                    {
                        dCut = (restFwd.y < -0.2f) ? Vector3.up : Vector3.down;
                    }
                    break;
            }

            // Swing axis is perpendicular to line-of-sight and cut direction
            Vector3 swingAxis = Vector3.Cross(vCenter, dCut).normalized;
            if (swingAxis.sqrMagnitude < 0.01f)
            {
                swingAxis = Vector3.Cross(vCenter, Vector3.up).normalized;
            }

            // Pre-swing (-105 deg) and post-swing (+65 deg) directly in the cutting plane!
            Vector3 dPre = RodriguesRotate(vCenter, swingAxis, -105f);
            Vector3 dPost = RodriguesRotate(vCenter, swingAxis, 65f);

            Quaternion qStart = Quaternion.LookRotation(dPre, Vector3.up);
            Quaternion qHit = Quaternion.LookRotation(vCenter, Vector3.up);
            Quaternion qEnd = Quaternion.LookRotation(dPost, Vector3.up);

            return (qStart, qHit, qEnd);
        }

        public static Quaternion GetSafeBombPose(float tStart, float tEnd, List<NoteData> bombs, bool isLeft, Quaternion defaultRest)
        {
            if (bombs == null || bombs.Count == 0) return defaultRest;

            bool hasTopBomb = false;
            bool hasBottomBomb = false;

            for (int i = 0; i < bombs.Count; i++)
            {
                var b = bombs[i];
                if (b.time >= tStart - 0.25f && b.time <= tEnd + 0.15f)
                {
                    bool onHandSide = isLeft ? (b.lineIndex <= 1) : (b.lineIndex >= 2);
                    if (onHandSide)
                    {
                        if (b.noteLineLayer == NoteLineLayer.Top || (int)b.noteLineLayer >= 2)
                        {
                            hasTopBomb = true;
                        }
                        else if (b.noteLineLayer == NoteLineLayer.Base || (int)b.noteLineLayer <= 0)
                        {
                            hasBottomBomb = true;
                        }
                        else
                        {
                            hasTopBomb = true;
                        }
                    }
                }
            }

            // Both above and below -> horizontal middle between both!
            if (hasTopBomb && hasBottomBomb)
            {
                return Quaternion.Euler(0f, isLeft ? 10f : -10f, 0f);
            }
            // Bomb above -> diagonal down
            if (hasTopBomb)
            {
                return isLeft ? PoseDownLeft : PoseDownRight;
            }
            // Bomb below -> diagonal up
            if (hasBottomBomb)
            {
                return isLeft ? PoseUpLeft : PoseUpRight;
            }

            return defaultRest;
        }

        // 修饰符代码必须和 BeatLeader 的解析一致 (MapEnhancer 写入 / ReplayDataUtils.GetModifiersFromReplay 读取):
        //   SS=慢速  FS=快速  SF=超快  SA=严格角度  SC=小方块  CS=剑碰撞失败  NA=无箭头  NB=无炸弹  NO=无墙
        //   DA=消失箭头  GN=幽灵音符  PM=专业模式  NF=0能量不失败  IF=一击即死  BE=电池能量
        public static string EncodeModifiers(GameplayModifiers m)
        {
            if (m == null) return "";
            var list = new List<string>();
            if (m.energyType == GameplayModifiers.EnergyType.Battery) list.Add("BE");
            if (m.noFailOn0Energy) list.Add("NF");
            if (m.instaFail) list.Add("IF");
            if (m.failOnSaberClash) list.Add("CS");
            if (m.smallCubes) list.Add("SC");
            if (m.enabledObstacleType == GameplayModifiers.EnabledObstacleType.NoObstacles) list.Add("NO");
            if (m.noBombs) list.Add("NB");
            if (m.songSpeed == GameplayModifiers.SongSpeed.Slower) list.Add("SS");
            else if (m.songSpeed == GameplayModifiers.SongSpeed.Faster) list.Add("FS");
            else if (m.songSpeed == GameplayModifiers.SongSpeed.SuperFast) list.Add("SF");
            if (m.disappearingArrows) list.Add("DA");
            if (m.ghostNotes) list.Add("GN");
            if (m.noArrows) list.Add("NA");
            if (m.proMode) list.Add("PM");
            if (m.strictAngles) list.Add("SA");
            return string.Join(",", list);
        }

        public static BLVector3 ToBLVector(Vector3 v)
        {
            return new BLVector3(v.x, v.y, v.z);
        }

        public static BLQuaternion ToBLQuaternion(Quaternion q)
        {
            return new BLQuaternion(q.x, q.y, q.z, q.w);
        }
    }
}



