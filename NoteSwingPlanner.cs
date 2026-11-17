using System;
using System.Collections.Generic;
using UnityEngine;

namespace AutoReplay
{
    public struct PoseKey
    {
        public float t;
        public Quaternion q;
        public byte tag; // 0=普通, 1=命中幀(躲炸彈時絕不能動), 2=一筆畫結束幀
        public PoseKey(float t, Quaternion q) { this.t = t; this.q = q; this.tag = 0; }
    }

    public struct NoteSwingData
    {
        public float time;
        public float tStart;
        public float tEnd;
        public Quaternion startPose;
        public Quaternion hitPose;
        public Quaternion endPose;
        public bool isChainedWithPrev;
        public Vector3 target;         // 方塊中心 (命中時劍要穿過這裡)
        public int cutDirection;       // 譜面上的切割方向 (0..7 箭頭, 8 點塊), 用於反手扭轉
        public Vector3 swingDir;       // 命中時劍的揮動方向 (3D, 垂直於命中指向; 點塊是規劃出來的方向), 手臂模板用

        // 關鍵幀: 時間落在 (上一個音符命中時刻, 本音符命中時刻] 內, 最後一個就是命中幀(時間≈note.time)
        public PoseKey[] keys;
        // 只有最後一個音符才有: 命中之後的後揮/停靠關鍵幀
        public PoseKey[] tail;
    }

    public static class NoteSwingPlanner
    {
        // ================= 可調引數 =================
        public const float PreAngle = 105f;          // 預揮角度(度): 115分要求 >=100, 留 +5 餘量
        public const float PostAngle = 65f;          // 後揮角度(度): 115分要求 >=60, 留 +5 餘量
        public const float MaxAvgSpeed = 2400f;      // 單段揮動平均角速度上限(度/秒), 間隔太短時用它壓縮角度
        public const float NominalPreTime = 0.09f;   // 間隔充足時的預揮時長
        public const float NominalPostTime = 0.07f;  // 間隔充足時的後揮時長
        public const float ChainGap = 0.16f;         // 方向一致且間隔 <= 此值 -> 一刀連打(滑塊/疊塊)
        public const float DotChainGap = 0.02f;      // 點塊(無箭頭): 間隔 <= 此值才沿用上一刀方向(疊塊), 否則上下交替
        public const float ForceChainGap = 0.02f;    // 間隔小於此值 -> 不管方向, 強制一刀打完
        public const float ChainAlign = 0.8f;        // 兩塊切割方向點積 >= 此值才算"同向"
        public const float SliderAlign = 0.65f;      // 滑塊: 兩塊連線方向 和 上一刀切割方向 點積 >= 此值(約49度內, 直箭頭接斜對角滑塊=45度也算)
        public const float SliderDirAlign = 0.5f;    // 滑塊自帶箭頭時: 它的方向和上一刀方向點積 >= 此值(60度內, 不能是反向)
        public const float AlternateDot = -0.3f;     // 點積 <= 此值 -> 反向交替揮(上下上下), 回程即下一刀預揮
        public const float MaxConnectTime = 0.2f;    // 兩刀之間"復位"動作最長時長
        public const float AlternateMaxGap = 0.36f;  // 反向交替只用於間隔 <= 此值(來不及復位)的連續快刀; 間隔更長時 上一刀只做正常後揮(PostAngle),
                                                     // 手停在這個位置(劍朝上), 到下一刀前 MaxConnectTime 才轉到預揮位 -> 不會一揮完就把劍翻到腦後並停在那裡
        public const float LeadInTime = 0.3f;        // 第一刀之前從初始姿態過渡的時間
        const int ArcSteps = 10;                     // 每段弧線的關鍵幀數
        public const float TrackStep = 1f / 120f;    // 模板軌道的關鍵幀間隔
        public const float IdleGap = 2.0f;           // 第一刀之前/最後一刀之後 當作"間隔很長"
        public const float MinPreLead = 0.10f;       // 預揮最遠點至少提前這麼久
        public const float PerceptionDelay = 0.20f;  // 方塊出現後到開始反應的時間
        public static float ReactionTime = 0.60f;
        // 點塊方向規劃: 和上一刀同向(要復位)的代價 = ResetScale * exp(-間隔/ResetTau) + ResetBase
        // 真人資料: 不管間隔多長, 點塊 93% 和上一刀反向; 但一整串點塊的起始方向由後面的箭頭決定, 需要時在長間隔處復位
        public const float ResetScale = 3.0f;
        public const float ResetTau = 0.45f;
        public const float ResetBase = 0.4f;
        public const float StackGap = 0.06f;         // 間隔 <= 此值的兩塊, 真人當成同一刀 (統計模板時也是這樣合併的)
        // 長間隔: 超過這麼久, 後揮完就停在原地, 到預揮視窗才開始下一刀 (不再按來回揮的模板硬插值)
        public const float RestGap = 1.2f;
        public static float BlendDelay = 0f;     // 收勢 -> 下一刀預揮 的交接從間隔的這個比例才開始 (試過 0.15/0.3: 收勢多 2~4° 但誤差變大, 所以維持 0)
        public const float RestFade = 0.35f;    // 方塊從出現到被切的時間(RT), 由 AutoReplayGenerator 按 NJS/JD 設定

        // ================= 規劃 =================
        // noArrows = true (NA 無箭頭模式): 所有方塊一律當點塊處理, 完全不看譜面裡的切割方向(tech 之類的方向設計都不管),
        /// <summary>
        /// 同一時間的方塊 (疊塊 / 滑塊) 排成固定順序: 點塊在前 (後面的方向塊會決定它們的方向), 方向塊按揮刀方向先後
        /// (例如往下揮: 上面的先切). 遊戲給的同時間方塊順序不固定, 不排的話"一刀的第一塊/最後一塊"會變, 方向差約 30°.
        /// </summary>
        static List<NoteData> SortStacks(List<NoteData> notes)
        {
            var res = new List<NoteData>(notes.Count);
            for (int i = 0; i < notes.Count;)
            {
                int e = i;
                while (e + 1 < notes.Count && Mathf.Abs(notes[e + 1].time - notes[i].time) < 1e-4f) e++;
                if (e == i) { res.Add(notes[i]); i++; continue; }
                var group = new List<NoteData>();
                for (int k = i; k <= e; k++) group.Add(notes[k]);
                Vector3 d = Vector3.zero;
                foreach (var g in group) if (HasDirection(g)) { d = CutDir(g); break; }
                float Key(NoteData x) { var p = AutoReplayMath.GetNotePos(x); return Vector3.Dot(p, d); }
                group.Sort((a, b) =>
                {
                    int c = HasDirection(a).CompareTo(HasDirection(b));                 // 點塊在前
                    if (c != 0) return c;
                    c = Key(a).CompareTo(Key(b));                                         // 沿揮刀方向先後
                    if (c != 0) return c;
                    c = ((int)b.noteLineLayer).CompareTo((int)a.noteLineLayer);           // 其餘: 上面的先, 左邊的先
                    return c != 0 ? c : a.lineIndex.CompareTo(b.lineIndex);
                });
                res.AddRange(group);
                i = e + 1;
            }
            return res;
        }

        // 第一刀從上往下, 之後上下交替; 能一刀砍的(按普通模式的 ForceChainGap/DotChainGap/ChainGap/ChainAlign 判定)合成一刀
        public static List<NoteSwingData> PlanSwings(List<NoteData> notes, List<NoteData> bombs, Vector3 wristPos, bool isLeft, bool noArrows = false)
        {
            notes = SortStacks(notes);
            int n = notes.Count;
            var result = new List<NoteSwingData>(n);
            if (n == 0) return result;

            var v = new Vector3[n];          // 手腕 -> 方塊 的視線方向 (手腕位置不變)
            var dir = new Vector3[n];        // 每一刀的切割方向(2D)
            var axis = new Vector3[n];       // 揮動平面法線
            var hitQ = new Quaternion[n];
            var chained = new bool[n];

            // ---------- 1. 決定每一刀的方向, 以及是否和上一刀合併成一刀 ----------
            // 點塊(無箭頭)的方向不再只看上一刀, 而是對整串音符做全域性規劃 (見 PlanDotDirections)
            Vector3[] dotDir = noArrows ? null : PlanDotDirections(notes, isLeft, bombs, wristPos);
            for (int i = 0; i < n; i++)
            {
                var note = notes[i];
                // 手腕會跟著劍的指向移動, 所以命中姿態要用不動點求解, 保證劍尖仍然正好穿過方塊中心
                hitQ[i] = HumanModel.AimAt(isLeft, AutoReplayMath.GetNotePos(note));
                v[i] = hitQ[i] * Vector3.forward;

                float gap = (i > 0) ? note.time - notes[i - 1].time : float.MaxValue;

                if (noArrows) continue; // NA 模式在迴圈結束後統一規劃方向

                Vector3 d;
                bool sliderTail = false;   // 這個塊是不是接在上一個塊後面的滑塊(沿上一刀方向繼續走)
                if (HasDirection(note)) d = CutDir(note);
                else
                {
                    d = dotDir[i];
                    // 點塊滑塊(含斜對角): 規劃結果沿用了上一刀方向 -> 並進同一刀
                    sliderTail = i > 0 && Vector3.Dot(d, dir[i - 1]) >= ChainAlign && IsSliderTail(notes[i - 1], note, dir[i - 1], gap);
                }

                // 同一層(時間幾乎相同)的點塊排在方向塊前面: 它們會被強制併成一刀, 點塊直接用後面方向塊的方向,
                // 否則點塊會先按"和上一刀反向"隨便猜一個方向, 導致整刀的預揮平面/交替判定都錯
                if (!HasDirection(note) && i + 1 < n && notes[i + 1].time - note.time <= ForceChainGap && HasDirection(notes[i + 1]))
                    d = CutDir(notes[i + 1]);
                dir[i] = d;

                chained[i] = i > 0 &&
                             (gap <= ForceChainGap ||
                              sliderTail ||
                              (gap <= ChainGap && (Vector3.Dot(dir[i - 1], d) >= ChainAlign ||
                                                   IsSliderTail(notes[i - 1], note, dir[i - 1], gap, d))));
            }

            if (noArrows) PlanNoArrowStrokes(notes, dir, chained);

            for (int i = 0; i < n; i++)
            {
                Vector3 ax = Vector3.Cross(v[i], dir[i]);
                if (ax.sqrMagnitude < 1e-4f) ax = Vector3.Cross(v[i], Vector3.up);
                axis[i] = ax.normalized;
            }

            // ---------- 2. 分組成"筆畫"(一刀可以打多個塊) ----------
            var strokes = new List<int[]>();
            for (int i = 0; i < n;)
            {
                int e = i;
                while (e + 1 < n && chained[e + 1]) e++;
                strokes.Add(new[] { i, e });
                i = e + 1;
            }
            int m = strokes.Count;

            // ---------- 3. 每個筆畫和前後筆畫的關係 (間隔, 是否來回反向) ----------
            // 命中姿態統一用 HumanModel.Compose 的滾轉約定 (只有指向重要, 滾轉之後會換成真人模型)
            for (int i = 0; i < n; i++) hitQ[i] = HumanModel.Compose(v[i], 0f);

            var sStart = new float[m];
            var sEnd = new float[m];
            var gapPrev = new float[m];
            var gapNext = new float[m];
            var relPrev = new int[m];   // 和上一筆揮動方向的夾角分組 (SwingTemplates.RelBin)
            var relNext = new int[m];
            var preScale = new float[m];
            for (int k = 0; k < m; k++)
            {
                sStart[k] = notes[strokes[k][0]].time;
                sEnd[k] = notes[strokes[k][1]].time;
            }
            for (int k = 0; k < m; k++)
            {
                if (k == 0) { gapPrev[k] = IdleGap; relPrev[k] = SwingTemplates.RelBack; }
                else
                {
                    gapPrev[k] = sStart[k] - sStart[k - 1];
                    relPrev[k] = SwingTemplates.RelBin(dir[strokes[k - 1][1]], dir[strokes[k][0]]);
                }
                if (k == m - 1) { gapNext[k] = IdleGap; relNext[k] = SwingTemplates.RelBack; }
                else
                {
                    gapNext[k] = sStart[k + 1] - sStart[k];
                    relNext[k] = SwingTemplates.RelBin(dir[strokes[k][1]], dir[strokes[k + 1][0]]);
                }

                // 反應時間限制: 方塊出現後還要一點時間才看得到/反應過來, 預揮最遠點不能比這更早.
                // 太早的話把模板的預揮部分在時間上壓縮 (RT 由 NJS / JD 算出, 見 AutoReplayGenerator)
                int s0 = strokes[k][0];
                float lead = SwingTemplates.PreExtremeLead(relPrev[k], SwingTemplates.DirClass(dir[s0]), gapPrev[k]);
                float maxLead = Mathf.Max(MinPreLead, ReactionTime - PerceptionDelay);
                preScale[k] = lead > maxLead ? lead / maxLead : 1f;
            }

            // ---------- 4. 用真人揮刀模板生成統一的關鍵幀軌道 ----------
            //  命中幀之間: 上一筆的後揮模板 和 下一筆的預揮模板 混合 (在下一筆的揮動座標系裡對角度插值,
            //  這樣同向復位時劍會從前方/側邊繞回去, 來回揮時則是一條帶 8 字偏移的往返弧)
            var track = new List<PoseKey>(n * 16);
            var hitIdx = new int[n];
            var endQ = new Quaternion[n];
            var endT = new float[n];
            for (int i = 0; i < n; i++) { endQ[i] = hitQ[i]; endT[i] = notes[i].time; }

            float side = isLeft ? -1f : 1f;   // 模板的側向角以右手為準, 左手映象

            // 某一筆的預揮 / 後揮在 t 時刻的 (alpha, beta), 以及對應的座標系
            Vector3 FrameDir(int i, float alpha, float beta)
            {
                Vector3 fc = v[i];
                Vector3 d3 = dir[i] - Vector3.Dot(dir[i], fc) * fc;
                if (d3.sqrMagnitude < 1e-6f) d3 = Vector3.Cross(axis[i], fc);
                d3.Normalize();
                Vector3 nn = Vector3.Cross(fc, d3);
                float a = alpha * Mathf.Deg2Rad, b = beta * side * Mathf.Deg2Rad;
                return (Mathf.Cos(b) * (Mathf.Cos(a) * fc + Mathf.Sin(a) * d3) + Mathf.Sin(b) * nn).normalized;
            }
            void PreAB(int k, float t, out float a, out float b)
            {
                int i = strokes[k][0];
                SwingTemplates.Eval(relPrev[k], SwingTemplates.DirClass(dir[i]), gapPrev[k], (t - sStart[k]) * preScale[k], out a, out b);
            }
            void PostAB(int k, float t, out float a, out float b)
            {
                int i = strokes[k][1];
                SwingTemplates.Eval(relNext[k], SwingTemplates.DirClass(dir[i]), gapNext[k], t - sEnd[k], out a, out b);
            }
            // 預揮視窗: 從最遠點再往前留一點準備時間
            float PreWindow(int k) => Mathf.Min(0.75f, SwingTemplates.PreExtremeLead(relPrev[k], SwingTemplates.DirClass(dir[strokes[k][0]]), gapPrev[k]) / preScale[k] + 0.15f);
            Vector3 PreDir(int k, float t) { PreAB(k, t, out float a, out float b); return FrameDir(strokes[k][0], a, b); }
            Vector3 PostDir(int k, float t) { PostAB(k, t, out float a, out float b); return FrameDir(strokes[k][1], a, b); }
            // 長間隔/第一刀之前/最後一刀之後: 後揮結束後就停在原地 (模板在 +0.4s 之後保持不變, 只有待機微動在晃),
            // 到下一刀的預揮視窗才開始動. 不再額外切換到"休息姿勢", 否則空檔裡會多出一次上下襬動
            Vector3 ViaRest(int kPost, int kPre, float t)
            {
                Vector3 f = kPost >= 0 ? PostDir(kPost, t) : PreDir(kPre, t);
                if (kPre >= 0)
                {
                    float wPre = AutoReplayMath.SmoothStep((t - (sStart[kPre] - PreWindow(kPre) - RestFade)) / RestFade);
                    if (wPre > 0f) f = Vector3.Slerp(f, PreDir(kPre, t), wPre);
                }
                return f.normalized;
            }
            Vector3 DirAt(float t)
            {
                if (t <= sStart[0]) return ViaRest(-1, 0, t);
                if (t >= sEnd[m - 1]) return ViaRest(m - 1, -1, t);
                int k = 0;
                { int lo = 0, hi = m - 1; while (lo < hi) { int mid = (lo + hi + 1) >> 1; if (sEnd[mid] <= t) lo = mid; else hi = mid - 1; } k = lo; }
                if (sStart[k + 1] - sEnd[k] > RestGap) return ViaRest(k, k + 1, t);
                int nextS = strokes[k + 1][0];
                PostAB(k, t, out float pa, out float pb);
                Vector3 fPost = FrameDir(strokes[k][1], pa, pb);
                PreAB(k + 1, t, out float qa, out float qb);
                // 上一筆的後揮模板 和 下一筆的預揮模板 描述的是同一段真人動作, 直接在指向上球面插值.
                // (離線對比真人 replay: 原本在下一筆座標系裡對角度插值, tech 常見的 90° 轉向在中段會偏 89°;
                //  直接球面插值後中段誤差降到約 30°, 和真人自己兩次遊玩之間的差異相當)
                float u = (t - sEnd[k]) / Mathf.Max(1e-4f, sStart[k + 1] - sEnd[k]);
                float w = AutoReplayMath.SmoothStep((u - BlendDelay) / (1f - BlendDelay));
                return Vector3.Slerp(fPost, FrameDir(nextS, qa, qb), w).normalized;
            }

            // 關鍵幀時刻: 均勻網格 (筆畫內部除外) + 命中幀 + 每筆一個結束幀
            var keyTimes = new List<(float t, byte tag)>();
            for (int k = 0; k < m; k++)
            {
                for (int i = strokes[k][0]; i <= strokes[k][1]; i++) keyTimes.Add((notes[i].time, (byte)1));
                float nextT = k + 1 < m ? sStart[k + 1] : sEnd[k] + 1f;
                keyTimes.Add((sEnd[k] + Mathf.Min(0.1f, 0.4f * (nextT - sEnd[k])), (byte)2));
            }
            {
                float g0 = sStart[0] - LeadInTime - 0.7f, g1 = sEnd[m - 1] + 1.0f;
                int k = 0;
                for (float t = g0; t <= g1; t += TrackStep)
                {
                    while (k < m && sEnd[k] < t - 2e-3f) k++;
                    if (k < m && t > sStart[k] - 2e-3f && t < sEnd[k] + 2e-3f) continue;   // 筆畫內部 / 命中幀附近
                    keyTimes.Add((t, (byte)0));
                }
            }
            keyTimes.Sort((x, y) => x.t != y.t ? x.t.CompareTo(y.t) : y.tag.CompareTo(x.tag) * -1);

            {
                int hi = 0;
                for (int j = 0; j < keyTimes.Count; j++)
                {
                    var (t, tag) = keyTimes[j];
                    if (tag == 0 && j > 0 && Mathf.Abs(keyTimes[j - 1].t - t) < 2e-3f) continue;
                    Quaternion q = tag == 1 ? hitQ[hi++] : HumanModel.Compose(DirAt(t), 0f);
                    if (tag == 0) Add(track, t, q); else AddTagged(track, t, q, tag);
                }
            }

            // ---------- 4.5 躲炸彈: 只改非命中幀, 命中幀原樣保留 ----------
            if (bombs != null && bombs.Count > 0)
            {
                int found = 0;
                for (int pass = 0; pass < 2; pass++)
                {
                    int c = BombAvoider.Apply(track, notes, bombs, wristPos, isLeft);
                    if (pass == 0) found = c;
                    if (c == 0) break;
                }
                int hitLeft, nearLeft;
                BombAvoider.CountRemaining(track, bombs, wristPos, isLeft, out hitLeft, out nearLeft);
                Plugin.Log?.Info(string.Format("[BombAvoider v6] hand={0} notes={1} bombs={2} hitZ={3} speed={4} len={5} 需躲避={6} 躲避后: 仍会撞={7} 仍过近={8}",
                    isLeft ? "L" : "R", n, bombs.Count, BombAvoider.BombHitZ, BombAvoider.BombSpeed, BombAvoider.SaberLen, found, hitLeft, nearLeft));
            }
            else
            {
                Plugin.Log?.Warn("[BombAvoider v6] 传进来的 bombs 是空的, 所以没有执行躲避! 检查调用方有没有把炸弹解析/传入。");
            }

            // 加密/偏移會改變下標, 所以按 tag 重新定位命中幀和每筆結束幀
            {
                int hi = 0, si = 0;
                for (int idx = 0; idx < track.Count; idx++)
                {
                    if (track[idx].tag == 1) hitIdx[hi++] = idx;
                    else if (track[idx].tag == 2)
                    {
                        int e = strokes[si++][1];
                        endQ[e] = track[idx].q;
                        endT[e] = track[idx].t;
                    }
                }
            }

            // ---------- 5. 把軌道切回每個音符 ----------
            for (int i = 0; i < n; i++)
            {
                int from = (i == 0) ? 0 : hitIdx[i - 1] + 1;
                int to = hitIdx[i];
                var keys = track.GetRange(from, to - from + 1).ToArray();

                var sw = new NoteSwingData
                {
                    time = notes[i].time,
                    tStart = keys[0].t,
                    tEnd = endT[i],
                    startPose = keys[0].q,
                    hitPose = hitQ[i],
                    endPose = endQ[i],
                    isChainedWithPrev = chained[i],
                    target = AutoReplayMath.GetNotePos(notes[i]),
                    cutDirection = noArrows ? (int)NoteCutDirection.Any : (int)notes[i].cutDirection,
                    swingDir = (dir[i] - v[i] * Vector3.Dot(dir[i], v[i])).normalized,
                    keys = keys,
                    tail = null
                };
                if (i == n - 1)
                {
                    int tf = hitIdx[i] + 1;
                    sw.tail = (tf < track.Count) ? track.GetRange(tf, track.Count - tf).ToArray() : new PoseKey[0];
                }
                result.Add(sw);
            }

            return result;
        }

        // ================= 取樣 =================
        public static Quaternion SampleHandRotation(float t, List<NoteSwingData> swings, List<NoteData> bombs, bool isLeft)
        {
            if (swings == null || swings.Count == 0) return AutoReplayMath.PoseUp;

            // 二分: 第一個 命中時刻 >= t 的音符
            int lo = 0, hi = swings.Count - 1, idx = swings.Count;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (HitKey(swings[mid]).t >= t) { idx = mid; hi = mid - 1; }
                else lo = mid + 1;
            }

            if (idx == swings.Count)
            {
                var last = swings[swings.Count - 1];
                return SampleSeq(HitKey(last), last.tail, t);
            }

            var cur = swings[idx];
            if (idx == 0) return SampleSeq(cur.keys[0], cur.keys, t);
            return SampleSeq(HitKey(swings[idx - 1]), cur.keys, t);
        }

        // ================= 工具 =================
        static PoseKey HitKey(NoteSwingData s)
        {
            return s.keys[s.keys.Length - 1];
        }

        static Quaternion SampleSeq(PoseKey prev, PoseKey[] keys, float t)
        {
            if (keys == null || keys.Length == 0) return prev.q;
            PoseKey a = prev;
            for (int i = 0; i < keys.Length; i++)
            {
                PoseKey b = keys[i];
                if (t <= b.t)
                {
                    float d = b.t - a.t;
                    if (d <= 1e-6f) return b.q;
                    return Quaternion.Slerp(a.q, b.q, Mathf.Clamp01((t - a.t) / d));
                }
                a = b;
            }
            return a.q;
        }

        // 緩動: 預揮用 cos, 後揮用 sin -> 命中處速度最快, 兩端(揮到最遠處)速度為 0,
        // 來回揮的折返點不會再出現"速度瞬間反向"的頓挫感 (原本遠端還保留 0.75 倍速度)

        // 在切割平面內, 繞 axis 從命中姿態旋轉 theta 度. 不用 LookRotation, 避免豎直時 roll 翻轉
        static Quaternion PoseAt(Vector3 axis, Quaternion hit, float theta)
        {
            return Quaternion.AngleAxis(theta, axis) * hit;
        }

        static void AddTagged(List<PoseKey> tr, float t, Quaternion q, byte tag)
        {
            Add(tr, t, q);
            MarkLast(tr, tag);
        }

        static void MarkLast(List<PoseKey> tr, byte tag)
        {
            PoseKey k = tr[tr.Count - 1];
            k.tag = tag;
            tr[tr.Count - 1] = k;
        }

        static void Add(List<PoseKey> tr, float t, Quaternion q)
        {
            if (tr.Count > 0 && t <= tr[tr.Count - 1].t + 1e-4f) t = tr[tr.Count - 1].t + 1e-4f; // 時間嚴格遞增
            tr.Add(new PoseKey(t, q));
        }

        // 從當前最後一幀過渡到 target(時刻 tTarget): 先停靠, 臨近時再用 SmoothStep 平滑復位
        static void Connect(List<PoseKey> tr, float tTarget, Quaternion target)
        {
            PoseKey from = tr[tr.Count - 1];
            float dur = Mathf.Min(MaxConnectTime, tTarget - from.t);
            if (dur <= 0.004f) return;

            float t0 = tTarget - dur;
            if (t0 > from.t + 1e-3f) Add(tr, t0, from.q);
            // 復位用"劍尖走最短大圓弧"(AimSlerp), 不能用 Quaternion.Slerp:
            // 起點(上一刀後揮)和終點(下一刀預揮)的切割平面不同, 整體旋轉差可達 170°+,
            // 四元數 Slerp 會讓劍尖從正後方(z≈-0.9)繞過去, 看起來像往後轉了一圈
            const int steps = 8;
            for (int j = 1; j < steps; j++)
            {
                float u = j / (float)steps;
                Add(tr, t0 + dur * u, BombAvoider.AimSlerp(from.q, target, AutoReplayMath.SmoothStep(u)));
            }
        }

        // NA 模式的方向/分組規劃 —— 只用普通模式已有的常量, 沒有新增任何閾值:
        //  1) 這一刀的方向和普通點塊一樣: 第一刀向下, 之後和上一刀反向(上下交替)
        //  2) 下一個方塊並進同一刀的條件, 和普通模式"一刀連打"的判定一致:
        //       間隔 <= ForceChainGap 或 <= DotChainGap -> 直接併入 (疊塊)
        //       間隔 <= ChainGap 且 "方向一致" -> 併入
        //     這裡的"方向一致"= 前後兩塊的連線方向 和 這一刀的切割方向 的點積 >= ChainAlign
        //     (普通模式是比較兩塊的箭頭方向, NA 沒有箭頭, 就用"這一刀要走的方向"代替)
        static void PlanNoArrowStrokes(List<NoteData> notes, Vector3[] dir, bool[] chained)
        {
            int n = notes.Count;
            Vector3 prevDir = Vector3.up;   // 初始劍向上, 所以第一刀是"和上一刀反向"= 向下

            int s = 0;
            while (s < n)
            {
                Vector3 d = prevDir.y > 0f ? Vector3.down : Vector3.up;

                int e = s;
                while (e + 1 < n)
                {
                    float gap = notes[e + 1].time - notes[e].time;
                    bool join;
                    if (gap <= ForceChainGap || gap <= DotChainGap) join = true;
                    else if (gap <= ChainGap)
                    {
                        Vector3 delta = AutoReplayMath.GetNotePos(notes[e + 1]) - AutoReplayMath.GetNotePos(notes[e]);
                        delta.z = 0f;
                        float dist = delta.magnitude;
                        join = dist >= 0.05f && Vector3.Dot(d, delta / dist) >= ChainAlign;
                    }
                    else join = false;

                    if (!join) break;
                    e++;
                }

                for (int k = s; k <= e; k++)
                {
                    dir[k] = d;
                    chained[k] = k > s;
                }
                prevDir = d;
                s = e + 1;
            }
        }

        // 點塊方向的全域性規劃 (動態規劃 / Viterbi):
        //   每個點塊的候選方向 = 上/下 (按真人習慣略微外傾) + 左右/斜向 (有額外代價, 只在滑塊等場合才划算);
        //   箭頭塊只有自己的方向.
        //   相鄰兩刀: 反向(來回揮) 代價 0; 同向需要復位, 間隔越短代價越高 (ResetScale/ResetTau/ResetBase);
        //   疊塊/滑塊(間隔極短或沿上一刀方向排列) 同向代價 0.
        // 這樣一整串點塊會從後面的箭頭倒推起始方向, 必要時在長間隔處復位 —— 和真人一樣.
        // 點塊方向要看炸彈: 預揮從反方向過來、收勢往揮的方向走, 路上有炸彈就不選這個方向.
        // 例: Extra Credit on the Chromosome Test! (Hard 113s) 左手點塊在最下排兩側輪流, 中間夾著下排炸彈:
        // 真人兩次都是每一刀往下砍再從上面繞回來 (炸彈重置), 原本的規劃照節奏上下交替, 往上揮時會從炸彈那排穿過去
        public static float DotBombApproach = 0.45f;   // 預揮 / 收勢時劍指向 方塊 ∓ 方向 * 這個距離 (米)
        public static float DotBombCost = 6f;          // 路上會撞到炸彈的代價 (要大過兩次同向重置 ~3.6: 寧可重置也不穿過炸彈)
        public static bool DebugDots = false;          // debug: 印出點塊各方向的炸彈代價

        static Vector3[] PlanDotDirections(List<NoteData> notes, bool isLeft, List<NoteData> bombs = null, Vector3 wrist = default)
        {
            int n = notes.Count;
            var cand = new List<Vector3>[n];
            var prior = new List<float>[n];
            float upTilt = (isLeft ? -10f : 5f) * Mathf.Deg2Rad;    // 真人點塊上挑略往外
            float downTilt = (isLeft ? 6f : 2f) * Mathf.Deg2Rad;
            for (int i = 0; i < n; i++)
            {
                cand[i] = new List<Vector3>(); prior[i] = new List<float>();
                if (HasDirection(notes[i])) { cand[i].Add(CutDir(notes[i])); prior[i].Add(0f); continue; }
                cand[i].Add(new Vector3(Mathf.Sin(downTilt), -Mathf.Cos(downTilt), 0f)); prior[i].Add(0f);
                cand[i].Add(new Vector3(Mathf.Sin(upTilt), Mathf.Cos(upTilt), 0f)); prior[i].Add(i == 0 ? 0.2f : 0f);
                float dg = 0.7071f;
                foreach (var v in new[] { Vector3.left, Vector3.right })
                { cand[i].Add(v); prior[i].Add(0.35f); }
                foreach (var v in new[] { new Vector3(-dg, dg, 0f), new Vector3(dg, dg, 0f), new Vector3(-dg, -dg, 0f), new Vector3(dg, -dg, 0f) })
                { cand[i].Add(v); prior[i].Add(0.25f); }

                if (bombs != null && bombs.Count > 0)
                {
                    Vector3 p = AutoReplayMath.GetNotePos(notes[i]);
                    float tPrev = i > 0 ? notes[i - 1].time : notes[i].time - 1f;
                    float tNext = i + 1 < n ? notes[i + 1].time : notes[i].time + 1f;
                    float a0 = Mathf.Max(tPrev + 0.05f, notes[i].time - 0.25f), a1 = notes[i].time - 0.03f;
                    float b0 = notes[i].time + 0.03f, b1 = Mathf.Min(tNext - 0.05f, notes[i].time + 0.20f);
                    for (int k = 0; k < cand[i].Count; k++)
                    {
                        Vector3 d = cand[i][k];
                        float pen = 0f;
                        if (a1 > a0) pen += BombAvoider.PoseBombCost(bombs, wrist, isLeft, p - d * DotBombApproach, a0, a1);
                        if (b1 > b0) pen += 0.5f * BombAvoider.PoseBombCost(bombs, wrist, isLeft, p + d * DotBombApproach, b0, b1);
                        prior[i][k] += DotBombCost * pen * pen;   // 平方: 稍微靠近幾乎不扣, 真的會撞才重扣
                        if (DebugDots && notes[i].time > 113f && notes[i].time < 115.5f && k < 2) Plugin.Log?.Info($"dot t={notes[i].time:F2} x={notes[i].lineIndex} cand={(k == 0 ? "down" : "up")} pen={pen:F2}");
                    }
                }
            }

            var cost = new float[n][];
            var back = new int[n][];
            cost[0] = prior[0].ToArray();
            back[0] = new int[cand[0].Count];
            for (int i = 1; i < n; i++)
            {
                float g = notes[i].time - notes[i - 1].time;
                cost[i] = new float[cand[i].Count];
                back[i] = new int[cand[i].Count];
                for (int b = 0; b < cand[i].Count; b++)
                {
                    float best = float.MaxValue; int arg = 0;
                    for (int a = 0; a < cand[i - 1].Count; a++)
                    {
                        float c = cost[i - 1][a] + TransitionCost(notes[i - 1], notes[i], cand[i - 1][a], cand[i][b], g);
                        if (c < best) { best = c; arg = a; }
                    }
                    cost[i][b] = best + prior[i][b];
                    back[i][b] = arg;
                }
            }
            var res = new Vector3[n];
            int cur = 0;
            for (int b = 1; b < cand[n - 1].Count; b++) if (cost[n - 1][b] < cost[n - 1][cur]) cur = b;
            for (int i = n - 1; i >= 0; i--)
            {
                res[i] = cand[i][cur];
                cur = back[i][cur];
            }
            return res;
        }

        static float TransitionCost(NoteData prev, NoteData note, Vector3 a, Vector3 b, float g)
        {
            float dt = Vector3.Dot(a, b);
            if (g <= StackGap) return dt >= ChainAlign ? 0f : (g <= ForceChainGap ? 6f : 3f);   // 疊塊/幾乎同時: 當成同一刀
            if (dt <= AlternateDot) return 0f;                                  // 來回揮
            if (g <= ChainGap && dt >= SliderDirAlign && IsSliderTail(prev, note, a, g, b)) return 0f;   // 滑塊
            float reset = ResetScale * Mathf.Exp(-g / ResetTau) + ResetBase;
            // 垂直轉向比完全同向容易一點
            if (dt < ChainAlign) reset *= 0.4f + 0.6f * Mathf.InverseLerp(AlternateDot, ChainAlign, dt);
            return reset;
        }

        // 滑塊判定: note 是不是接在 prev 後面、沿 prev 這一刀的切割方向繼續走的塊.
        //  - 間隔 <= ChainGap
        //  - 兩塊不在同一格, 且 連線方向 和 prevDir 夾角夠小 (斜對角也算: 直箭頭接斜對角約45度, 斜箭頭接斜對角0度)
        //  - tailDir != null(滑塊自己有箭頭)時, 它的方向不能和 prevDir 差太多(反向就是交替, 不是滑塊)
        static bool IsSliderTail(NoteData prev, NoteData note, Vector3 prevDir, float gap, Vector3? tailDir = null)
        {
            if (gap > ChainGap) return false;

            Vector3 delta = AutoReplayMath.GetNotePos(note) - AutoReplayMath.GetNotePos(prev);
            delta.z = 0f;
            float dist = delta.magnitude;
            if (dist < 0.05f) return false;
            if (Vector3.Dot(prevDir, delta / dist) < SliderAlign) return false;

            if (tailDir.HasValue && Vector3.Dot(prevDir, tailDir.Value) < SliderDirAlign) return false;
            return true;
        }

        static bool HasDirection(NoteData note)
        {
            return note.cutDirection != NoteCutDirection.Any && note.cutDirection != NoteCutDirection.None;
        }

        // 角度: 0=上, 90=右, 180=下, 270=左 (和 AutoReplayMath.GetCutAngle 一致)
        static Vector3 CutDir(NoteData note)
        {
            float a = AutoReplayMath.GetCutAngle(note) * Mathf.Deg2Rad;
            return new Vector3(Mathf.Sin(a), Mathf.Cos(a), 0f).normalized;
        }
    }

    /// <summary>
    /// 躲炸彈: 在 NoteSwingPlanner 生成好關鍵幀軌道之後做後處理.
    /// 1) 危險時間窗由幾何推出: 炸彈飛進"劍夠得著的 z 範圍"到"飛過手"之間 (注意在 note.time 之後!)
    /// 2) 目標點 = 視窗內所有炸彈 "斜對角點(以當前瞄準點為中心點對稱)" 的平均 (同一層的炸彈先合併)
    /// 3) 在候選點裡挑 "按真實時間軌跡檢測確實躲得開" 且離目標最近的點, 把劍混合過去
    /// 命中幀(tag==1)永遠不動, 所以切方塊的精度不受影響.
    /// </summary>
    public static class BombAvoider
    {
        // ================= 可調引數 =================
        public const float BombRadius = 0.20f;   // 炸彈半徑 0.18 + 劍身半徑 0.02
        // 在判定半徑之外再留的餘量(米). 原本 0.07: 劍總是貼著炸彈過 (斯皮奇 Hard: 生成中位 0.54 m, 真人 0.90 m).
        // 真人 40 份回放 / 5719 顆炸彈: 距離中位 1.00 m, P10 0.66 m, < 0.40 m 只有 3%
        public static float Safety = 0.30f;
        public static bool PreferUp = true;      // 躲避時優先把劍舉起朝上 (真人 55~58%)
        public static float UpMargin = 0.30f;    // 朝上時額外要求的餘量 (最後的手臂動作會讓手移動, 規劃時的檢查不夠保守)
        public const float SaberStart = 0.30f;   // 劍身起點(離手腕)
        public const float SaberLen = 1.60f;     // 手腕(旋轉中心) -> 劍尖. 真人 replay 實測: 命中時方塊中心到控制器的距離 p99=1.57m, 再減去 pommel 0.20, 取 1.6 偏保守
        public const float BombHitZ = 0.72f;     // 炸彈在 note.time 時的真實 z (真人 replay 迴歸: z0=0.722). 注意不是 CutDistanceZ=1.5!
        public static float BombSpeed = 19.25f; // 炸彈飛向玩家的速度(米/歌曲秒). 真人 replay 迴歸=19.25; 換圖請改成該圖 NJS
        public const float Ramp = 0.04f;         // 偏移淡入淡出的時長
        public const float LockRamp = 0.03f;     // 離命中幀多近開始把偏移收回 0
        public const float DenseStep = 1f / 60f; // 視窗內加密關鍵幀的間隔
        public const float EvalDt = 1f / 120f;   // 檢測取樣間隔
        public const float BombLayerGap = 0.02f; // 炸彈時間間隔 <= 此值算同一"層"(風格同 DotChainGap/ForceChainGap)
        public const float GridCenterX = 0f;      // 12 格(4列x3行)的中心: 6和7之間
        public static float GridCenterY => 1.25f + AutoReplayMath.NoteYOffset;   // 中間一行的高度
        public static float ClusterGap = 0.5f;    // 相鄰炸彈間隔 <= 此值(且中間沒命中幀)算同一串, 整串期間劍保持躲避姿勢
        public static float MaxClusterSpan = 0.6f;// 一串炸彈最長跨度, 超過就拆開 (原本 1.5: 整串一個姿勢, 繞圈炸彈時劍僵住指著中間的洞; 真人會跟著炸彈順序換姿勢)
        public const float FadeOut = 0.12f;      // 炸彈過去後, 劍從躲避姿勢淡回原軌跡的時長
        public const float MaxDodgeSpeed = 1800f; // 躲避入場/出場的平均角速度上限(度/秒); 平滑曲線峰值約 1.5 倍, 和揮刀的 MaxAvgSpeed 同量級
        public const float PreHold = 0.10f;      // 下一刀命中前這麼久開始預揮, 淡出必須在這之前完成

        // 危險窗 = [bomb.t - Before, bomb.t + After]; Before 可以為負(視窗從 note.time 之後才開始). 由 Configure 算出
        static float Before = 0f;
        static float After = 0.08f;

        struct Bomb { public float t; public Vector3 p; }

        /// <summary>炸彈在 note.time 時 z=BombHitZ, 之後繼續往玩家飛.
        /// 中心 z &lt; 手腕z+劍長+半徑 時劍尖夠得著; z &lt; 手腕z-半徑 時已經飛過手.</summary>
        static bool s_isLeft;

        static void Configure(Vector3 wrist, bool isLeft)
        {
            s_isLeft = isLeft;
            float reachZ = wrist.z + HumanModel.MaxWristForwardZ + SaberLen + BombRadius;
            float behindZ = wrist.z - BombRadius;
            Before = (reachZ - BombHitZ) / BombSpeed;
            After = (BombHitZ - behindZ) / BombSpeed;
        }

        /// <returns>本輪發現的碰撞數(0 說明已經全部躲開)</returns>
        public static int Apply(List<PoseKey> track, List<NoteData> notes, List<NoteData> bombs, Vector3 wrist, bool isLeft)
        {
            if (bombs == null || bombs.Count == 0 || track.Count < 2) return 0;
            Configure(wrist, isLeft);

            var bl = new List<Bomb>(bombs.Count);
            foreach (var b in bombs) bl.Add(new Bomb { t = b.time, p = AutoReplayMath.GetNotePos(b) });
            bl.Sort((x, y) => x.t.CompareTo(y.t));
            Bomb[] B = bl.ToArray();
            List<Vector3> cands = BuildCandidates(notes, bombs);

            // 炸彈可能出現在這隻手第一個音符之前 / 最後一個音符之後: 軌道兩端各補一個停靠關鍵幀,
            // 這樣那些炸彈也有幀可以調整 (否則會被 "tb 超出軌道範圍" 直接跳過)
            float padFirst = B[0].t - 1.0f, padLast = B[B.Length - 1].t + 1.0f;
            if (padFirst < track[0].t) track.Insert(0, new PoseKey(padFirst, track[0].q));
            if (padLast > track[track.Count - 1].t) track.Add(new PoseKey(padLast, track[track.Count - 1].q));

            // 命中幀時刻(從不被修改): 用來把炸彈分段, 以及判斷下一刀什麼時候開始預揮
            var allLocks = new List<float>();
            for (int k = 0; k < track.Count; k++) if (track[k].tag == 1) allLocks.Add(track[k].t);

            int collisions = 0;
            int bi = 0;
            while (bi < B.Length)
            {
                // 0) 分組: 相鄰炸彈間隔 <= ClusterGap, 中間沒有命中幀, 總跨度 <= MaxClusterSpan 的算同一串.
                //    整串炸彈只做一次躲避, 劍在整串期間保持同一個姿勢(不會炸彈一顆顆來就上下抖).
                int bj = bi;
                while (bj + 1 < B.Length
                       && B[bj + 1].t - B[bj].t <= ClusterGap
                       && B[bj + 1].t - B[bi].t <= MaxClusterSpan
                       && !LockBetween(allLocks, B[bj].t, B[bj + 1].t))
                    bj++;

                float tb = B[bi].t, tF = tb - Before, tL = B[bj].t + After;
                int next = bj + 1;
                if (tb < track[0].t - Before || B[bj].t > track[track.Count - 1].t + After
                    || Clearance(track, B, wrist, tF, tL) >= Safety)
                {
                    bi = next; continue;
                }
                collisions++;

                // 1) 目標點: 整串炸彈的"斜對角點"平均; 再挑一個真正躲得開、離目標最近的點
                Vector3 aim = B[bi].p;
                Vector3 f = Sample(track, tb) * Vector3.forward;
                if (f.z > 0.05f) aim = wrist + f * ((B[bi].p.z - wrist.z) / f.z);
                Vector3 target = OppositeTarget(B, tF, tL);
                Vector3 safe = PickSafePoint(cands, B, wrist, tF, tL, aim, target);
                Vector3 safeDir = HumanModel.AimAt(s_isLeft, safe) * Vector3.forward;

                // 躲避姿勢: 只改"劍指向", 滾轉(繞劍身)沿用入場時原軌跡的姿勢 -> 不會憑空多轉半圈/一圈
                Quaternion qIn = Sample(track, tF - Ramp);
                Quaternion qSafe = AimRotation(qIn * Vector3.forward, safeDir) * qIn;

                // 2) 放手時機: 這串炸彈過去之後繼續保持, 直到下一刀預揮開始之前(PreHold)才淡出.
                //    (下一刀通常是向下切, 預揮本來就從上面開始, 所以手不用先放下來)
                //    淡出時長按角度限速: 角度越大越慢, 人手轉不了那麼快
                int li = LowerBoundFloat(allLocks, tL);
                float fadeOut = Mathf.Max(FadeOut, Quaternion.Angle(Sample(track, tL + 0.12f), qSafe) / MaxDodgeSpeed);
                float tOut = tL;
                if (li < allLocks.Count)
                {
                    float nl = allLocks[li];
                    tOut = Mathf.Max(tL, nl - PreHold - fadeOut);
                    fadeOut = Mathf.Clamp(nl - PreHold - tOut, Ramp, fadeOut);
                }

                // 3) 入場時長同樣按角度限速. 前一刀離得太近來不及時, 依次試 1.0 / 0.7 / 0.5 倍時長, 取第一個躲得開的
                float fadeIn = Mathf.Max(Ramp, Quaternion.Angle(qIn, qSafe) / MaxDodgeSpeed);
                List<PoseKey> bestSeg = null; int bestI0 = 0, bestI1 = 0; float bestC = -9f;
                float[] scales = { 1f, 0.7f, 0.5f };
                for (int sc = 0; sc < scales.Length; sc++)
                {
                    float fi = Mathf.Max(Ramp, fadeIn * scales[sc]);
                    float S0 = tF - fi - 0.01f, S1 = tOut + fadeOut + 0.01f;
                    int i0 = Mathf.Max(0, LowerBound(track, S0) - 1);
                    int i1 = Mathf.Min(track.Count - 1, LowerBound(track, S1));

                    // 視窗內加密關鍵幀(軌跡形狀不變), 這樣才有地方做偏移
                    List<PoseKey> sub = Densify(track, i0, i1, S0, S1);
                    var locks = new List<float>();
                    for (int k = 0; k < sub.Count; k++) if (sub[k].tag == 1) locks.Add(sub[k].t);

                    // 命中幀不動, 其餘幀按權重過渡到 qSafe. 過渡用 AimSlerp: 劍尖沿最短大圓弧走, 不會繞到身後
                    for (int k = 0; k < sub.Count; k++)
                    {
                        PoseKey key = sub[k];
                        if (key.tag == 1) continue;
                        float w = Weight(key.t, tF, fi, tOut, fadeOut, locks);
                        if (w > 0f) { key.q = AimSlerp(key.q, qSafe, w); sub[k] = key; }
                    }

                    var tt = new List<PoseKey>(track.Count + sub.Count);
                    for (int k = 0; k < i0; k++) tt.Add(track[k]);
                    tt.AddRange(sub);
                    for (int k = i1 + 1; k < track.Count; k++) tt.Add(track[k]);
                    float c = Clearance(tt, B, wrist, S0 + 0.01f, S1 - 0.01f);
                    if (bestSeg == null || c > bestC) { bestC = c; bestSeg = sub; bestI0 = i0; bestI1 = i1; }
                    if (c >= Safety) break;
                }
                track.RemoveRange(bestI0, bestI1 - bestI0 + 1);
                track.InsertRange(bestI0, bestSeg);
                bi = next;
            }
            return collisions;
        }

        // 劍尖沿最短大圓弧從 a 的指向轉到 b 的指向 (Quaternion.Slerp 在兩個姿勢差很大時, 指向會繞到身後/轉一圈);
        // 繞劍身的滾轉角單獨、線性地過渡
        public static Quaternion AimSlerp(Quaternion a, Quaternion b, float u)
        {
            if (u <= 0f) return a;
            if (u >= 1f) return b;
            Vector3 fa = a * Vector3.forward, fb = b * Vector3.forward;
            Quaternion rot = AimRotation(fa, fb);
            Quaternion qEnd = rot * a;                          // 指向 = fb, 滾轉 = 平行搬運
            Quaternion tw = b * Quaternion.Inverse(qEnd);       // 剩下的滾轉差 (軸 = fb)
            if (tw.w < 0f) tw = new Quaternion(-tw.x, -tw.y, -tw.z, -tw.w);
            float ang; Vector3 axis;
            tw.ToAngleAxis(out ang, out axis);
            if (ang > 180f) ang -= 360f;
            if (Vector3.Dot(axis, fb) < 0f) ang = -ang;
            Quaternion qd = Quaternion.Slerp(Quaternion.identity, rot, u) * a;
            Vector3 fu = qd * Vector3.forward;
            return Quaternion.AngleAxis(ang * u, fu) * qd;
        }

        // fa -> fb 的最短旋轉; 幾乎正反向時固定從"朝前"那一側繞過去
        static Quaternion AimRotation(Vector3 fa, Vector3 fb)
        {
            if (Vector3.Dot(fa, fb) < -0.995f)
            {
                Vector3 ax = Vector3.Cross(fa, Vector3.forward);
                if (ax.sqrMagnitude < 1e-6f) ax = Vector3.right;
                return Quaternion.AngleAxis(180f, ax.normalized);
            }
            return Quaternion.FromToRotation(fa, fb);
        }

        static bool LockBetween(List<float> locks, float a, float b)
        {
            int i = LowerBoundFloat(locks, a + 1e-5f);
            return i < locks.Count && locks[i] < b;
        }

        static int LowerBoundFloat(List<float> v, float t)
        {
            int lo = 0, hi = v.Count;
            while (lo < hi) { int mid = (lo + hi) >> 1; if (v[mid] < t) lo = mid + 1; else hi = mid; }
            return lo;
        }

        /// <summary>躲避完成後的自檢: hit=仍會撞到的炸彈數, near=離炸彈小於安全餘量的數. 每個仍會撞的炸彈會單獨打一行日誌</summary>
        public static void CountRemaining(List<PoseKey> track, List<NoteData> bombs, Vector3 wrist, bool isLeft, out int hit, out int near)
        {
            hit = 0; near = 0;
            Configure(wrist, isLeft);
            var bl = new List<Bomb>(bombs.Count);
            foreach (var b in bombs) bl.Add(new Bomb { t = b.time, p = AutoReplayMath.GetNotePos(b) });
            bl.Sort((x, y) => x.t.CompareTo(y.t));
            Bomb[] B = bl.ToArray();
            int logged = 0;
            for (int i = 0; i < B.Length; i++)
            {
                float c = Clearance(track, B, wrist, B[i].t - Before, B[i].t + After);
                if (c < 0f)
                {
                    hit++;
                    if (logged++ < 40) LogRemaining(track, B, i, wrist, c);
                }
                else if (c < Safety) near++;
            }
        }

        static void LogRemaining(List<PoseKey> track, Bomb[] B, int i, Vector3 wrist, float clr)
        {
            // 格子編號 1..12 (左上=1, 右下=12)
            int col = Mathf.Clamp(Mathf.RoundToInt(B[i].p.x / 0.6f + 1.5f), 0, 3);
            int row = Mathf.Clamp(Mathf.RoundToInt((B[i].p.y - 0.72f - AutoReplayMath.NoteYOffset) / 0.53f), 0, 2);
            int cell = (2 - row) * 4 + col + 1;

            // 這顆炸彈自己的視窗裡, 最近的時刻 / 距離最近命中幀(tag==1)多遠 / 是否在軌道範圍外
            float bestD = 9f, bestT = B[i].t;
            for (float t = B[i].t - Before; t <= B[i].t + After; t += EvalDt)
            {
                Vector3 c = B[i].p;
                c.z = BombHitZ + (B[i].t - t) * BombSpeed;
                float d = SaberDist(c, wrist, Sample(track, t)) - BombRadius;
                if (d < bestD) { bestD = d; bestT = t; }
            }
            float nearestHit = 9f;
            for (int k = 0; k < track.Count; k++)
                if (track[k].tag == 1) nearestHit = Mathf.Min(nearestHit, Mathf.Abs(track[k].t - bestT));
            bool outside = B[i].t < track[0].t || B[i].t > track[track.Count - 1].t;

            // 同一時刻附近的炸彈(格子編號), 方便看是不是整排炸彈
            var near = new System.Text.StringBuilder();
            for (int j = 0; j < B.Length; j++)
            {
                if (Mathf.Abs(B[j].t - B[i].t) > 0.1f) continue;
                int cc = Mathf.Clamp(Mathf.RoundToInt(B[j].p.x / 0.6f + 1.5f), 0, 3);
                int rr = Mathf.Clamp(Mathf.RoundToInt((B[j].p.y - 0.72f - AutoReplayMath.NoteYOffset) / 0.53f), 0, 2);
                near.Append((2 - rr) * 4 + cc + 1).Append(' ');
            }
            Plugin.Log?.Info(string.Format(
                "[BombAvoider v6] 仍会撞: 炸弹t={0:F3} 格={1} 间隙={2:F3} 最近时刻=t{3:+0.000;-0.000} 距命中帧={4:F3}s 轨道外={5} 同时炸弹格=[{6}]",
                B[i].t, cell, clr, bestT - B[i].t, nearestHit, outside, near.ToString().Trim()));
        }

        // ---------- 偏移權重: 視窗內=1, 兩側淡出; 靠近命中幀時收回到 0 ----------
        // 躲避強度: tIn 之前用 fadeIn 淡入, [tIn,tOut] 為 1, tOut 之後用 fadeOut 淡出; 靠近命中幀時收回到 0
        static float Weight(float t, float tIn, float fadeIn, float tOut, float fadeOut, List<float> locks)
        {
            float w = 1f;
            if (t < tIn) w = Smooth(1f - (tIn - t) / fadeIn);
            else if (t > tOut) w = Smooth(1f - (t - tOut) / fadeOut);
            float dl = 9f;
            for (int i = 0; i < locks.Count; i++) dl = Mathf.Min(dl, Mathf.Abs(t - locks[i]));
            return w * Smooth(dl / LockRamp);
        }

        static float Smooth(float u) { u = Mathf.Clamp01(u); return u * u * (3f - 2f * u); }

        // ---------- 斜對角平均 ----------
        // 格子編號:  1  2  3  4
        //            5  6  7  8
        //            9 10 11 12
        // 每顆炸彈的"斜對角" = 以格子中心(6和7之間)點對稱的格子: 12->1, 9->4, 10->3, 11->2 ...
        // 例: 只有12有炸彈 -> 劍指向1;  9/10/11/12 都有炸彈 -> 對角是 4/3/2/1, 平均 = 2和3之間(上排正中).
        // 時間間隔 <= BombLayerGap 的炸彈算同一層, 層內先平均, 再對各層平均.
        static Vector3 OppositeTarget(Bomb[] B, float t0, float t1)
        {
            int lo = LowerBoundBomb(B, t0 - After), hi = LowerBoundBomb(B, t1 + Before);
            float sx = 0f, sy = 0f; int layers = 0;
            int i = lo;
            while (i < hi)
            {
                float tl = B[i].t;
                float lx = 0f, ly = 0f; int cnt = 0;
                while (i < hi && B[i].t - tl <= BombLayerGap)
                {
                    lx += 2f * GridCenterX - B[i].p.x;
                    ly += 2f * GridCenterY - B[i].p.y;
                    cnt++; i++;
                }
                if (cnt > 0) { sx += lx / cnt; sy += ly / cnt; layers++; }
            }
            if (layers == 0) return new Vector3(GridCenterX, GridCenterY, AutoReplayMath.CutDistanceZ);
            return new Vector3(sx / layers, sy / layers, AutoReplayMath.CutDistanceZ);
        }

        // ---------- 選點: 候選點裡 "真的躲得開(按炸彈飛行軌跡檢測)" 的, 離目標最近 ----------
        static Vector3 PickSafePoint(List<Vector3> cands, Bomb[] B, Vector3 wrist, float t0, float t1, Vector3 aim, Vector3 target)
        {
            // 0) 真人的習慣: 炸彈飛過來、手上沒有方塊要切時, 劍多半直接舉起朝上 (55~58%, 朝下 15~17%, 往前約 20%;
            //    40 份回放 / 2700 次統計). 朝上躲得開就朝上 (劍尖稍微往前, 不會戳到頭)
            if (PreferUp)
            {
                Vector3 up = wrist + new Vector3(0f, 2.0f, 0.6f);
                if (ClearanceConst(HumanModel.AimAt(s_isLeft, up), B, wrist, t0, t1) >= Safety + UpMargin) return up;
            }

            // 1) 斜對角目標本身能躲開 -> 直接朝它
            Quaternion qt = HumanModel.AimAt(s_isLeft, target);
            if (ClearanceConst(qt, B, wrist, t0, t1) >= Safety + 0.02f) return target;

            // 2) 否則在候選點裡, 取"能躲開"且離目標最近的; 都躲不開就取最安全的
            Vector3 best = target, fallback = target;
            float bestCost = float.MaxValue, fallbackClear = -9f;
            for (int g = 0; g < cands.Count; g++)
            {
                Vector3 p = cands[g];
                Quaternion q = HumanModel.AimAt(s_isLeft, p);
                float c = ClearanceConst(q, B, wrist, t0, t1);
                if (c > fallbackClear) { fallbackClear = c; fallback = p; }
                if (c >= Safety + 0.02f)
                {
                    float cost = Dist2D(p, target) + 0.02f * Dist2D(p, aim);
                    if (cost < bestCost) { bestCost = cost; best = p; }
                }
            }
            return bestCost < float.MaxValue ? best : fallback;
        }

        // 劍保持固定朝向 q 時, 視窗內離炸彈的最小間隙
        static List<NoteData> _poseCacheSrc;
        static Bomb[] _poseCache;

        /// <summary>
        /// 劍固定指向 aimPoint 時, [t0, t1] 內離炸彈多近: 0 = 夠遠 (>= Safety), 1 = 碰到, 中間線性.
        /// 點塊方向規劃用 (預揮 / 收勢的路上有沒有炸彈)
        /// </summary>
        public static float PoseBombCost(List<NoteData> bombs, Vector3 wrist, bool isLeft, Vector3 aimPoint, float t0, float t1)
        {
            if (bombs == null || bombs.Count == 0) return 0f;
            if (!ReferenceEquals(_poseCacheSrc, bombs))
            {
                var bl = new List<Bomb>(bombs.Count);
                foreach (var b in bombs) bl.Add(new Bomb { t = b.time, p = AutoReplayMath.GetNotePos(b) });
                bl.Sort((x, y) => x.t.CompareTo(y.t));
                _poseCache = bl.ToArray(); _poseCacheSrc = bombs;
            }
            Configure(wrist, isLeft);
            float c = ClearanceConst(HumanModel.AimAt(isLeft, aimPoint), _poseCache, wrist, t0, t1);
            return c >= Safety ? 0f : Mathf.Clamp01(1f - c / Safety);
        }

        static float ClearanceConst(Quaternion q, Bomb[] B, Vector3 wrist, float t0, float t1)
        {
            int lo = LowerBoundBomb(B, t0 - After), hi = LowerBoundBomb(B, t1 + Before);
            float best = 9f;
            for (int i = lo; i < hi; i++)
            {
                float a = Mathf.Max(t0, B[i].t - Before), e = Mathf.Min(t1, B[i].t + After);
                for (float t = a; t <= e + 1e-5f; t += EvalDt)
                {
                    Vector3 c = B[i].p;
                    c.z = BombHitZ + (B[i].t - t) * BombSpeed;
                    float d = SaberDist(c, wrist, q) - BombRadius;
                    if (d < best) best = d;
                }
            }
            return best;
        }

        // 候選點 = 譜面裡出現過的位置及中點 + 一圈均勻點陣(允許劍躲到格子外面一點)
        static List<Vector3> BuildCandidates(List<NoteData> notes, List<NoteData> bombs)
        {
            List<Vector3> all = BuildGrid(notes, bombs);
            for (float x = -1.05f; x <= 1.051f; x += 0.15f)
                for (float y = 0.55f; y <= 1.951f; y += 0.15f)
                    AddUnique(all, new Vector3(x, y, AutoReplayMath.CutDistanceZ));
            return all;
        }


        // 網格點直接取自譜面裡實際出現過的方塊/炸彈位置(保證和 GetNotePos 同一座標系), 再補相鄰點中點
        static List<Vector3> BuildGrid(List<NoteData> notes, List<NoteData> bombs)
        {
            var basePts = new List<Vector3>();
            if (notes != null) foreach (var n in notes) AddUnique(basePts, AutoReplayMath.GetNotePos(n));
            foreach (var b in bombs) AddUnique(basePts, AutoReplayMath.GetNotePos(b));
            var all = new List<Vector3>(basePts);
            for (int i = 0; i < basePts.Count; i++)
                for (int j = i + 1; j < basePts.Count; j++)
                    if (Dist2D(basePts[i], basePts[j]) <= 0.7f)
                        AddUnique(all, (basePts[i] + basePts[j]) * 0.5f);
            return all;
        }

        static void AddUnique(List<Vector3> list, Vector3 p)
        {
            for (int i = 0; i < list.Count; i++) if (Dist2D(list[i], p) < 0.05f) return;
            list.Add(p);
        }

        static float Dist2D(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x, dy = a.y - b.y;
            return Mathf.Sqrt(dx * dx + dy * dy);
        }

        // ---------- 碰撞檢測: 返回 (最近距離 - 炸彈半徑), 越大越安全 ----------
        static float Clearance(List<PoseKey> keys, Bomb[] B, Vector3 wrist, float t0, float t1)
        {
            int lo = LowerBoundBomb(B, t0 - After), hi = LowerBoundBomb(B, t1 + Before);
            float best = 9f;
            for (int i = lo; i < hi; i++)
            {
                float a = Mathf.Max(t0, B[i].t - Before), e = Mathf.Min(t1, B[i].t + After);
                for (float t = a; t <= e + 1e-5f; t += EvalDt)
                {
                    Vector3 c = B[i].p;
                    c.z = BombHitZ + (B[i].t - t) * BombSpeed;   // note.time 時在 BombHitZ, 之前更遠, 之後更近
                    float d = SaberDist(c, wrist, Sample(keys, t)) - BombRadius;
                    if (d < best) best = d;
                }
            }
            return best;
        }

        // wrist 是手腕的基準位置; 實際手腕位置隨劍的指向移動 (和 PoseCalculator 用同一個手臂模型)
        static float SaberDist(Vector3 c, Vector3 wrist, Quaternion q)
        {
            // 和最終輸出用同一套人體模型: 先限位, 再由指向算手腕位置
            Vector3 f = HumanModel.LimitForward(s_isLeft, q * Vector3.forward);
            Vector3 a = HumanModel.WristForDir(s_isLeft, f) + f * SaberStart;
            Vector3 ab = f * (SaberLen - SaberStart);
            float s = Mathf.Clamp01(Vector3.Dot(c - a, ab) / ab.sqrMagnitude);
            return (c - (a + ab * s)).magnitude;
        }

        // ---------- 軌道工具 ----------
        static List<PoseKey> Densify(List<PoseKey> tr, int i0, int i1, float S0, float S1)
        {
            var res = new List<PoseKey>(i1 - i0 + 1 + 32);
            res.Add(tr[i0]);
            for (int i = i0 + 1; i <= i1; i++)
            {
                PoseKey a = res[res.Count - 1], k = tr[i];
                float gap = k.t - a.t;
                if (gap > DenseStep * 1.5f && k.t > S0 && a.t < S1)
                {
                    int nn = Mathf.CeilToInt(gap / DenseStep);
                    for (int j = 1; j < nn; j++)
                    {
                        float u = j / (float)nn;
                        res.Add(new PoseKey(a.t + gap * u, Quaternion.Slerp(a.q, k.q, u)));
                    }
                }
                res.Add(k);
            }
            return res;
        }

        static Quaternion Sample(List<PoseKey> keys, float t)
        {
            int lo = LowerBound(keys, t);
            if (lo == 0) return keys[0].q;
            if (lo >= keys.Count) return keys[keys.Count - 1].q;
            PoseKey a = keys[lo - 1], b = keys[lo];
            float d = b.t - a.t;
            return d > 1e-6f ? Quaternion.Slerp(a.q, b.q, Mathf.Clamp01((t - a.t) / d)) : b.q;
        }

        static int LowerBound(List<PoseKey> keys, float t)
        {
            int lo = 0, hi = keys.Count;
            while (lo < hi) { int mid = (lo + hi) >> 1; if (keys[mid].t < t) lo = mid + 1; else hi = mid; }
            return lo;
        }

        static int LowerBoundBomb(Bomb[] B, float t)
        {
            int lo = 0, hi = B.Length;
            while (lo < hi) { int mid = (lo + hi) >> 1; if (B[mid].t < t) lo = mid + 1; else hi = mid; }
            return lo;
        }
    }
}
