using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace BotPlay
{
    /// <summary>生成好的動作: 每幀的頭 / 左手 / 右手姿態 (手的位置已經是 BeatLeader 記錄的手柄位置)</summary>
    public sealed class MotionFrames
    {
        public float t0, dt;
        public int count;
        public Vector3[] headPos, leftPos, rightPos;
        public Quaternion[] headRot, leftRot, rightRot;
    }

    /// <summary>
    /// 純動作生成 (揮刀規劃 + 頭 + 手), 不依賴 BeatLeader 和遊戲的譜面載入.
    /// 遊戲內由 BotPlayGenerator 呼叫; 遊戲外的 tools/ApModelTool generate 也直接編譯這個檔案, 兩邊結果一致.
    /// </summary>
    public static class MotionGenerator
    {
        public const float Fps = 90f;

        /// <summary>譜面沒填 NJS (0) 時遊戲按難度給的預設值</summary>
        public static float DefaultNjs(string difficulty) =>
            difficulty == "ExpertPlus" ? 16f : difficulty == "Expert" ? 12f : 10f;

        /// <summary>
        /// 反應時間 RT (方塊從出現到被切的時間) 和跳躍距離 JD, 公式同遊戲 (BeatmapObjectSpawnMovementData):
        /// 半跳拍數從 4 開始, 半跳距離超過 18 就減半, 再加上譜面的 offset, 最少 0.25 拍
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
        /// allNotes: 要切的方塊 (已按時間排序, 左手模式已映象); bombs / obstacles: 炸彈和牆.
        /// njs: 炸彈飛行速度; rt: 反應時間.
        /// </summary>
        public static MotionFrames Generate(List<NoteData> allNotes, List<NoteData> bombs, List<ObstacleData> obstacles,
                                            float njs, float rt, bool leftHanded, bool noArrows)
        {
            float startTime = 0f;
            float endTime = allNotes.Count > 0 ? allNotes.Max(n => n.time) + 2.5f : 60f;
            if (obstacles.Count > 0) endTime = Math.Max(endTime, obstacles.Max(o => o.time + o.duration) + 2.0f);

            var leftNotes = allNotes.Where(n => n.colorType == ColorType.ColorA).ToList();
            var rightNotes = allNotes.Where(n => n.colorType == ColorType.ColorB).ToList();

            // 炸彈飛行速度 = 這張譜的 NJS (米/歌曲秒); 揮刀時機要看得見方塊才行: 預揮最遠點不早於 (RT - 感知延遲)
            BombAvoider.BombSpeed = njs;
            NoteSwingPlanner.ReactionTime = rt;

            var leftSwings = NoteSwingPlanner.PlanSwings(leftNotes, bombs, BotPlayMath.LeftWristBase, true, noArrows);
            var rightSwings = NoteSwingPlanner.PlanSwings(rightNotes, bombs, BotPlayMath.RightWristBase, false, noArrows);

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

            // 頭: 目標姿態用臨界阻尼彈簧平滑 (原本躲牆時頭是瞬移的), 再疊加待機微動.
            // 先把整條頭部軌跡算出來, 手要跟著身體一起移動
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
                // 身體平移 = 頭相對基準位置的偏移 (躲牆往旁邊靠/蹲下時, 肩膀和手也一起移動, 只是幅度小一點)
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

            // 手: 整條軌道一次生成 (要做時間上的平滑, 不能逐幀獨立算)
            PoseCalculator.BuildHandTrack(startTime, dt, totalFrames, leftSwings, bombs, true, leftCtx, m.leftPos, m.leftRot);
            PoseCalculator.BuildHandTrack(startTime, dt, totalFrames, rightSwings, bombs, false, rightCtx, m.rightPos, m.rightRot);

            // 炸彈: 劍身離炸彈太近時以手腕為軸推開 (規劃時已經躲過, 這裡處理最後的手臂動作 / 晃動造成的誤差)
            (float t, Vector3 p)[] bl = null; float[] bt = null;
            void BombPass()
            {
                if (bl == null) return;
                for (int i = 0; i < totalFrames; i++)
                {
                    float t = startTime + i * dt;
                    m.leftRot[i] = AvoidBombs(t, m.leftPos[i], m.leftRot[i], bl, bt, leftCtx.hitTimes);
                    m.rightRot[i] = AvoidBombs(t, m.rightPos[i], m.rightRot[i], bl, bt, rightCtx.hitTimes);
                }
            }
            if (bombs != null && bombs.Count > 0)
            {
                bl = bombs.Select(b => (t: b.time, p: BotPlayMath.GetNotePos(b))).OrderBy(b => b.t).ToArray();
                bt = bl.Select(b => b.t).ToArray();
            }
            BombPass();

            // 劍別穿過頭: 劍身離頭心太近時, 以手腕為軸把劍往外推 (只動指向頭的那些幀, 其他方向的收勢幅度不受影響)
            // 推開的方向每隻手各自記住, 而且轉向有速度上限: 劍 / 劍柄剛好穿過頭心 / 眼睛時, 原始的推開方向會一幀內翻到另一側
            // (ranked 144 張批次測試: 一幀內 50~150° 的跳動幾乎都是這個), 這樣會改成從原本那側繞過去
            var headL = new PushState(); var headR = new PushState(); var eyeL = new PushState(); var eyeR = new PushState();
            for (int i = 0; i < totalFrames; i++)
            {
                Vector3 hc = m.headPos[i] - m.headRot[i] * new Vector3(0f, 0f, HeadCenterBack);
                m.leftRot[i] = AvoidHead(m.leftPos[i], m.leftRot[i], hc, ref headL, dt);
                m.rightRot[i] = AvoidHead(m.rightPos[i], m.rightRot[i], hc, ref headR, dt);
                // 劍柄別插進眼睛: 劍柄 (手腕 ~ 控制器前 5 cm) 離頭顯太近時, 把整隻手往外推
                m.leftPos[i] = AvoidEyes(m.leftPos[i], m.leftRot[i], m.headPos[i], ref eyeL, dt);
                m.rightPos[i] = AvoidEyes(m.rightPos[i], m.rightRot[i], m.headPos[i], ref eyeR, dt);
            }

            // 躲頭 / 躲眼可能把劍推向炸彈 (ranked 批次: 587555CF 劍離炸彈 10 cm): 再推一次炸彈
            BombPass();

            // 回放記錄的是手柄位置: 手腕往劍的方向前移 PommelOffset
            var pommelOffset = new Vector3(0f, 0f, HumanModel.PommelOffset);
            for (int i = 0; i < totalFrames; i++)
            {
                m.leftPos[i] = m.leftPos[i] + (m.leftRot[i] * pommelOffset);
                m.rightPos[i] = m.rightPos[i] + (m.rightRot[i] * pommelOffset);
            }
            return m;
        }

        public static float BombClear = 0.26f;       // 劍身離炸彈中心至少這麼遠 (炸彈 0.18 + 劍 0.02 = 0.20 算撞到)
        public static float BombClearSoft = 0.08f;

        static Quaternion AvoidBombs(float t, Vector3 wrist, Quaternion rot, (float t, Vector3 p)[] bl, float[] bt, float[] hits)
        {
            // 命中前後不動 (要切到方塊)
            int h = Array.BinarySearch(hits, t); if (h < 0) h = ~h;
            if ((h < hits.Length && hits[h] - t < 0.04f) || (h > 0 && t - hits[h - 1] < 0.04f)) return rot;
            float reach = BombAvoider.SaberLen + BombClear + BombClearSoft;
            float tMin = t - (BombAvoider.BombHitZ - wrist.z + reach) / BombAvoider.BombSpeed;   // 炸彈 z = HitZ + v (tb - t)
            float tMax = t + (wrist.z + reach - BombAvoider.BombHitZ) / BombAvoider.BombSpeed;
            int i0 = Array.BinarySearch(bt, tMin); if (i0 < 0) i0 = ~i0;
            for (int pass = 0; pass < 2; pass++)
                for (int i = i0; i < bl.Length && bl[i].t <= tMax; i++)
                {
                    Vector3 c = bl[i].p; c.z = BombAvoider.BombHitZ + BombAvoider.BombSpeed * (bl[i].t - t);
                    rot = PushAway(wrist, rot, c, BombAvoider.SaberStart, BombAvoider.SaberLen, BombClear, BombClearSoft);
                }
            return rot;
        }

        // 以手腕為軸轉動劍, 讓劍身 (手腕往前 s0..s1) 離 c 至少 clear (從 clear+soft 開始平滑地推)
        static Quaternion PushAway(Vector3 wrist, Quaternion rot, Vector3 c, float s0, float s1, float clear, float soft)
        {
            Vector3 f = rot * Vector3.forward;
            Vector3 v = c - wrist;
            float along = Vector3.Dot(v, f);
            if (along <= s0 || along >= s1 + clear + soft) return rot;
            Vector3 perp = v - f * along;
            float d = perp.magnitude, dist = v.magnitude;
            float past = Mathf.Max(0f, along - s1);                       // 在劍尖外面: 最近點是劍尖
            float dSeg = Mathf.Sqrt(d * d + past * past);
            if (dSeg >= clear + soft || dist < 1e-3f) return rot;
            float wantSeg = clear + soft * Mathf.Exp((dSeg - clear) / soft - 1f);
            float want = Mathf.Sqrt(Mathf.Max(0f, wantSeg * wantSeg - past * past));   // 需要的垂直距離
            if (want <= d) return rot;
            Vector3 away = d > 1e-5f ? -perp / d : Vector3.Cross(f, Vector3.up).normalized;
            float delta = Mathf.Asin(Mathf.Clamp01(want / dist)) - Mathf.Asin(Mathf.Clamp01(d / dist));
            Vector3 axis = Vector3.Cross(f, away);
            if (axis.sqrMagnitude < 1e-10f) return rot;
            return Quaternion.AngleAxis(delta * Mathf.Rad2Deg, axis.normalized) * rot;
        }

        public const float HeadCenterBack = 0.08f;   // 頭心在頭顯 (眼睛) 後方多遠
        public static float HeadClear = 0.20f;       // 劍身離頭心至少這麼遠 (頭半徑約 0.1 m; 真人上揮收勢時最近的 10% 約 21.6 cm)
        public static float HeadClearSoft = 0.06f;   // 從 HeadClear + 這個 開始平滑地推開
        const float BladeLen = 1.6f;                 // 手腕到劍尖

        public static float EyeClear = 0.19f;       // 劍柄離眼睛 (頭顯位置) 至少這麼遠 (生成的上揮收勢劍柄會停在臉正前方; 真人靠近時是在頭的旁邊)
        public static float EyeClearSoft = 0.05f;
        const float HiltFront = 0.05f;               // 劍柄算到控制器前方多遠

        public static float PushTurnRate = 720f;   // 推開方向最快每秒轉幾度 (180° 約 0.25 秒繞過去)
        public static float PushFarRate = 2880f;   // 已經離開頭 / 眼睛附近時追回原始方向的速度 (不然會一直扭著揮刀)

        /// <summary>躲頭 / 躲眼 正在推的方向 (單位向量); active = 還在推 (或推開方向還沒轉回原始方向)</summary>
        struct PushState { public bool active; public Vector3 dir; }

        // 把 cur 往 raw 轉, 每幀最多轉 maxDeg 度 (都垂直於 axis 時就是繞 axis 轉)
        static Vector3 TurnToward(Vector3 cur, Vector3 raw, float maxDeg)
        {
            float ang = Vector3.Angle(cur, raw);
            if (ang <= maxDeg || ang < 1e-3f) return raw;
            Vector3 ax = Vector3.Cross(cur, raw);
            if (ax.sqrMagnitude < 1e-10f) return cur;   // 正好相反: 這幀先不轉 (下一幀 raw 一動就有方向)
            return Quaternion.AngleAxis(maxDeg, ax.normalized) * cur;
        }

        static Vector3 AvoidEyes(Vector3 wrist, Quaternion rot, Vector3 eye, ref PushState st, float dt)
        {
            Vector3 f = rot * Vector3.forward;
            Vector3 ab = f * (HumanModel.PommelOffset + HiltFront);
            float c = EyeClear, z = EyeClearSoft;
            float s = Mathf.Clamp01(Vector3.Dot(eye - wrist, ab) / ab.sqrMagnitude);
            Vector3 v = wrist + ab * s - eye;
            float d = v.magnitude;
            bool near = d < c + z;
            Vector3 raw = d > 1e-4f ? v / d : (st.active ? st.dir : Vector3.down);
            if (!st.active)
            {
                if (!near) return wrist;
                st.active = true; st.dir = raw;
            }
            else st.dir = TurnToward(st.dir, raw, (near ? PushTurnRate : PushFarRate) * dt).normalized;
            if (!near && Vector3.Angle(st.dir, raw) < 2f) { st.active = false; return wrist; }
            float want = near ? c + z * Mathf.Exp((d - c) / z - 1f) : d;
            // 把劍柄最近點放到 eye + dir * want (dir = raw 時就是原本沿 v 推 want - d)
            wrist += st.dir * want - v;
            // 推完再檢查一次 (劍柄最近點可能換位置), 只沿目前方向補
            s = Mathf.Clamp01(Vector3.Dot(eye - wrist, ab) / ab.sqrMagnitude);
            v = wrist + ab * s - eye; d = v.magnitude;
            if (d < c + z && d > 1e-4f)
            {
                float want2 = c + z * Mathf.Exp((d - c) / z - 1f);
                wrist += v / d * (want2 - d);
            }
            return wrist;
        }

        static Quaternion AvoidHead(Vector3 wrist, Quaternion rot, Vector3 hc, ref PushState st, float dt)
        {
            Vector3 f = rot * Vector3.forward;
            Vector3 v = hc - wrist;
            float dist = v.magnitude;
            float along = Vector3.Dot(v, f);
            float c = HeadClear, z = HeadClearSoft;
            bool inSeg = along > 0f && along < BladeLen;              // 頭在劍的後面或劍尖外面: 線段最近點是端點, 不會更近
            Vector3 perp = v - f * along;
            float d = perp.magnitude;
            bool near = inSeg && d < c + z && dist > 1e-3f;
            if (!st.active && !near) return rot;
            if (dist < 1e-3f) return rot;
            Vector3 h = v / dist;
            // 推開方向 = 劍的指向在 "垂直於 手腕->頭心" 平面上的分量 (就是原本的 -perp 方向)
            Vector3 raw = f - h * Vector3.Dot(f, h);
            if (raw.sqrMagnitude > 1e-8f) raw.Normalize();
            else raw = st.active ? st.dir : Vector3.Cross(f, Vector3.up).normalized;
            if (!st.active) { st.active = true; st.dir = raw; }
            else
            {
                Vector3 pd = st.dir - h * Vector3.Dot(st.dir, h);   // 頭和手腕會動: 先投影回目前的平面
                st.dir = pd.sqrMagnitude > 1e-8f ? pd.normalized : raw;
                st.dir = TurnToward(st.dir, raw, (near ? PushTurnRate : PushFarRate) * dt).normalized;
            }
            if (!near && Vector3.Angle(st.dir, raw) < 2f) { st.active = false; return rot; }
            // 劍和 手腕->頭心 的夾角: 原始 theta, 需要 thetaWant (d = dist sin theta)
            float theta = Mathf.Acos(Mathf.Clamp(Vector3.Dot(f, h), -1f, 1f));
            float thetaOut = theta;
            if (near)
            {
                float want = c + z * Mathf.Exp((d - c) / z - 1f);       // d = c+z 時 want = d, 斜率 1 平滑接上; d -> 0 時 want -> c + z/e
                thetaOut = Mathf.Max(theta, Mathf.Asin(Mathf.Clamp01(want / dist)));   // 手腕離頭比 want 還近時 asin 取 90°
            }
            Vector3 fOut = (h * Mathf.Cos(thetaOut) + st.dir * Mathf.Sin(thetaOut)).normalized;
            return Quaternion.FromToRotation(f, fOut) * rot;
        }

        /// <summary>BeatLeader 用 "時間 + 音符 ID" 匹配事件: ID = (計分型別+2)*10000 + 列*1000 + 層*100 + 顏色*10 + 方向</summary>
        public static int NoteId(NoteData n, bool noArrows)
        {
            int dir = noArrows ? (int)NoteCutDirection.Any : (int)n.cutDirection;
            return ((int)n.scoringType + 2) * 10000 + n.lineIndex * 1000 + (int)n.noteLineLayer * 100 + (int)n.colorType * 10 + dir;
        }

        /// <summary>每種計分型別的滿分 (鏈條頭 85, 鏈條切片 20, 其餘 115)</summary>
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
