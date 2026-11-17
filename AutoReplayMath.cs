using System.Collections.Generic;
using UnityEngine;
#if !AUTOPLAY_STANDALONE
using BLVector3 = BeatLeader.Models.Replay.Vector3;
using BLQuaternion = BeatLeader.Models.Replay.Quaternion;
#endif

namespace AutoReplay
{
    public static class AutoReplayMath
    {
        public const float BaseHeadHeight = 1.50f;   // 舊模型 (沒有身高資料) 的預設值, 也是下面方塊高度的基準身高
        public const float DuckHeadHeight = 1.00f;

        // 身高 (來自模型): 設定身高決定方塊和牆的上下位置 (每高 1 m 上移 0.5 m, 真人回放實測), 頭高是真人身體的高度
        public static float PlayerHeight { get { var b = ModelManager.Current?.body; return b != null && b.height > 1f ? b.height : BaseHeadHeight; } }
        public static float HeadHeight { get { var b = ModelManager.Current?.body; return b != null && b.headHeight > 1f ? b.headHeight : BaseHeadHeight; } }
        public static float NoteYOffset => 0.5f * (PlayerHeight - BaseHeadHeight);
        public static float DuckHeight => DuckHeadHeight + NoteYOffset;
        public const float CutDistanceZ = 1.50f;

        // 手的基準位置 (劍指向正前方附近時手柄所在處), 來自 HumanModel 的真人統計.
        // 實際手腕位置隨劍的指向移動, 見 HumanModel.WristFor
        public static Vector3 LeftWristBase => HumanModel.HandBase(true);
        public static Vector3 RightWristBase => HumanModel.HandBase(false);

        // ================= 平滑噪聲 (待機微動) =================
        // 幾個不成倍數的正弦疊加, 輸出約 [-1, 1], 連續且平滑, 不需要隨機數狀態
        public static float Noise(float t, float seed)
        {
            return 0.5f * Mathf.Sin(t * 1.37f + seed * 12.9898f)
                 + 0.3f * Mathf.Sin(t * 2.71f + seed * 78.233f)
                 + 0.2f * Mathf.Sin(t * 5.03f + seed * 37.719f);
        }

        /// <summary>
        /// 手的待機微動: 頻率比 Noise 高 (0.5~3.7 Hz). 按真人 replay 長間隔中段校準:
        /// 真人劍指向角速度中位 40°/s, 偏離平均方向 5°; 手的移動 0.20 m/s, 偏離 3.6 cm.
        /// 振幅 9° 時角速度中位 41°/s, 偏離 4°; 位置振幅 5 cm 時 0.21 m/s, 3 cm.
        /// </summary>
        public static float HandNoise(float t, float seed)
        {
            return 0.30f * Mathf.Sin(t * 2.9f + seed * 12.9898f)
                 + 0.30f * Mathf.Sin(t * 7.1f + seed * 78.233f)
                 + 0.22f * Mathf.Sin(t * 13.7f + seed * 37.719f)
                 + 0.18f * Mathf.Sin(t * 23.3f + seed * 93.1f);
        }

        public static float HandPosNoise(float t, float seed)
        {
            return 0.35f * Mathf.Sin(t * 2.6f + seed * 12.9898f)
                 + 0.30f * Mathf.Sin(t * 5.9f + seed * 78.233f)
                 + 0.20f * Mathf.Sin(t * 11.1f + seed * 37.719f)
                 + 0.15f * Mathf.Sin(t * 19.3f + seed * 93.1f);
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
            // 這裡的角度是順時針 (0=上 90=右), 遊戲的 cutDirectionAngleOffset 是逆時針為正, 所以要減.
            // (原本寫成加: 斜向疊塊被遊戲轉到沿連線方向時 (例如 18°), 揮刀方向反而偏了兩倍 (36°);
            //  對照遊戲內外生成的同一張譜確認)
            float a = (baseAngle - note.cutDirectionAngleOffset) % 360f;
            return a < 0f ? a + 360f : a;
        }

        public static Vector3 GetNotePos(NoteData note)
        {
            float noteX = (note.lineIndex - 1.5f) * 0.60f;
            float noteY = 0.72f + ((int)note.noteLineLayer) * 0.53f + NoteYOffset; // 身高 1.50 時 Layer 0: 0.72, 1: 1.25, 2: 1.78
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

#if !AUTOPLAY_STANDALONE  // 遊戲外的工具 (tools/ApModelTool) 沒有 BeatLeader / 遊戲的修飾符型別
        // 修飾符程式碼必須和 BeatLeader 的解析一致 (MapEnhancer 寫入 / ReplayDataUtils.GetModifiersFromReplay 讀取):
        //   SS=慢速  FS=快速  SF=超快  SA=嚴格角度  SC=小方塊  CS=劍碰撞失敗  NA=無箭頭  NB=無炸彈  NO=無牆
        //   DA=消失箭頭  GN=幽靈音符  PM=專業模式  NF=0能量不失敗  IF=一擊即死  BE=電池能量
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
#endif
    }
}



