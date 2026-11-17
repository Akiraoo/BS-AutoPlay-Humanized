using System;
using System.Collections.Generic;
using UnityEngine;

namespace AutoReplay
{
    public struct PoseKey
    {
        public float t;
        public Quaternion q;
        public byte tag; // 0=普通, 1=命中帧(躲炸弹时绝不能动), 2=一笔画结束帧
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
        public Vector3 target;         // 方块中心 (命中时剑要穿过这里)
        public int cutDirection;       // 谱面上的切割方向 (0..7 箭头, 8 点块), 用于反手扭转
        public Vector3 swingDir;       // 命中时剑的挥动方向 (3D, 垂直于命中指向; 点块是规划出来的方向), 手臂模板用

        // 关键帧: 时间落在 (上一个音符命中时刻, 本音符命中时刻] 内, 最后一个就是命中帧(时间≈note.time)
        public PoseKey[] keys;
        // 只有最后一个音符才有: 命中之后的后挥/停靠关键帧
        public PoseKey[] tail;
    }

    public static class NoteSwingPlanner
    {
        // ================= 可调参数 =================
        public const float PreAngle = 105f;          // 预挥角度(度): 115分要求 >=100, 留 +5 余量
        public const float PostAngle = 65f;          // 后挥角度(度): 115分要求 >=60, 留 +5 余量
        public const float MaxAvgSpeed = 2400f;      // 单段挥动平均角速度上限(度/秒), 间隔太短时用它压缩角度
        public const float NominalPreTime = 0.09f;   // 间隔充足时的预挥时长
        public const float NominalPostTime = 0.07f;  // 间隔充足时的后挥时长
        public const float ChainGap = 0.16f;         // 方向一致且间隔 <= 此值 -> 一刀连打(滑块/叠块)
        public const float DotChainGap = 0.02f;      // 点块(无箭头): 间隔 <= 此值才沿用上一刀方向(叠块), 否则上下交替
        public const float ForceChainGap = 0.02f;    // 间隔小于此值 -> 不管方向, 强制一刀打完
        public const float ChainAlign = 0.8f;        // 两块切割方向点积 >= 此值才算"同向"
        public const float SliderAlign = 0.65f;      // 滑块: 两块连线方向 和 上一刀切割方向 点积 >= 此值(约49度内, 直箭头接斜对角滑块=45度也算)
        public const float SliderDirAlign = 0.5f;    // 滑块自带箭头时: 它的方向和上一刀方向点积 >= 此值(60度内, 不能是反向)
        public const float AlternateDot = -0.3f;     // 点积 <= 此值 -> 反向交替挥(上下上下), 回程即下一刀预挥
        public const float MaxConnectTime = 0.2f;    // 两刀之间"复位"动作最长时长
        public const float AlternateMaxGap = 0.36f;  // 反向交替只用于间隔 <= 此值(来不及复位)的连续快刀; 间隔更长时 上一刀只做正常后挥(PostAngle),
                                                     // 手停在这个位置(剑朝上), 到下一刀前 MaxConnectTime 才转到预挥位 -> 不会一挥完就把剑翻到脑后并停在那里
        public const float LeadInTime = 0.3f;        // 第一刀之前从初始姿态过渡的时间
        const int ArcSteps = 10;                     // 每段弧线的关键帧数
        public const float TrackStep = 1f / 120f;    // 模板轨道的关键帧间隔
        public const float IdleGap = 2.0f;           // 第一刀之前/最后一刀之后 当作"间隔很长"
        public const float MinPreLead = 0.10f;       // 预挥最远点至少提前这么久
        public const float PerceptionDelay = 0.20f;  // 方块出现后到开始反应的时间
        public static float ReactionTime = 0.60f;
        // 点块方向规划: 和上一刀同向(要复位)的代价 = ResetScale * exp(-间隔/ResetTau) + ResetBase
        // 真人数据: 不管间隔多长, 点块 93% 和上一刀反向; 但一整串点块的起始方向由后面的箭头决定, 需要时在长间隔处复位
        public const float ResetScale = 3.0f;
        public const float ResetTau = 0.45f;
        public const float ResetBase = 0.4f;
        public const float StackGap = 0.06f;         // 间隔 <= 此值的两块, 真人当成同一刀 (统计模板时也是这样合并的)
        // 长间隔: 超过这么久, 后挥完就停在原地, 到预挥窗口才开始下一刀 (不再按来回挥的模板硬插值)
        public const float RestGap = 1.2f;
        public static float BlendDelay = 0f;     // 收势 -> 下一刀预挥 的交接从间隔的这个比例才开始 (试过 0.15/0.3: 收势多 2~4° 但误差变大, 所以维持 0)
        public const float RestFade = 0.35f;    // 方块从出现到被切的时间(RT), 由 AutoReplayGenerator 按 NJS/JD 设定

        // ================= 规划 =================
        // noArrows = true (NA 无箭头模式): 所有方块一律当点块处理, 完全不看谱面里的切割方向(tech 之类的方向设计都不管),
        /// <summary>
        /// 同一时间的方块 (叠块 / 滑块) 排成固定顺序: 点块在前 (后面的方向块会决定它们的方向), 方向块按挥刀方向先后
        /// (例如往下挥: 上面的先切). 游戏给的同时间方块顺序不固定, 不排的话"一刀的第一块/最后一块"会变, 方向差约 30°.
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
                    int c = HasDirection(a).CompareTo(HasDirection(b));                 // 点块在前
                    if (c != 0) return c;
                    c = Key(a).CompareTo(Key(b));                                         // 沿挥刀方向先后
                    if (c != 0) return c;
                    c = ((int)b.noteLineLayer).CompareTo((int)a.noteLineLayer);           // 其余: 上面的先, 左边的先
                    return c != 0 ? c : a.lineIndex.CompareTo(b.lineIndex);
                });
                res.AddRange(group);
                i = e + 1;
            }
            return res;
        }

        // 第一刀从上往下, 之后上下交替; 能一刀砍的(按普通模式的 ForceChainGap/DotChainGap/ChainGap/ChainAlign 判定)合成一刀
        public static List<NoteSwingData> PlanSwings(List<NoteData> notes, List<NoteData> bombs, Vector3 wristPos, bool isLeft, bool noArrows = false)
        {
            notes = SortStacks(notes);
            int n = notes.Count;
            var result = new List<NoteSwingData>(n);
            if (n == 0) return result;

            var v = new Vector3[n];          // 手腕 -> 方块 的视线方向 (手腕位置不变)
            var dir = new Vector3[n];        // 每一刀的切割方向(2D)
            var axis = new Vector3[n];       // 挥动平面法线
            var hitQ = new Quaternion[n];
            var chained = new bool[n];

            // ---------- 1. 决定每一刀的方向, 以及是否和上一刀合并成一刀 ----------
            // 点块(无箭头)的方向不再只看上一刀, 而是对整串音符做全局规划 (见 PlanDotDirections)
            Vector3[] dotDir = noArrows ? null : PlanDotDirections(notes, isLeft, bombs, wristPos);
            for (int i = 0; i < n; i++)
            {
                var note = notes[i];
                // 手腕会跟着剑的指向移动, 所以命中姿态要用不动点求解, 保证剑尖仍然正好穿过方块中心
                hitQ[i] = HumanModel.AimAt(isLeft, AutoReplayMath.GetNotePos(note));
                v[i] = hitQ[i] * Vector3.forward;

                float gap = (i > 0) ? note.time - notes[i - 1].time : float.MaxValue;

                if (noArrows) continue; // NA 模式在循环结束后统一规划方向

                Vector3 d;
                bool sliderTail = false;   // 这个块是不是接在上一个块后面的滑块(沿上一刀方向继续走)
                if (HasDirection(note)) d = CutDir(note);
                else
                {
                    d = dotDir[i];
                    // 点块滑块(含斜对角): 规划结果沿用了上一刀方向 -> 并进同一刀
                    sliderTail = i > 0 && Vector3.Dot(d, dir[i - 1]) >= ChainAlign && IsSliderTail(notes[i - 1], note, dir[i - 1], gap);
                }

                // 同一层(时间几乎相同)的点块排在方向块前面: 它们会被强制并成一刀, 点块直接用后面方向块的方向,
                // 否则点块会先按"和上一刀反向"随便猜一个方向, 导致整刀的预挥平面/交替判定都错
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

            // ---------- 2. 分组成"笔画"(一刀可以打多个块) ----------
            var strokes = new List<int[]>();
            for (int i = 0; i < n;)
            {
                int e = i;
                while (e + 1 < n && chained[e + 1]) e++;
                strokes.Add(new[] { i, e });
                i = e + 1;
            }
            int m = strokes.Count;

            // ---------- 3. 每个笔画和前后笔画的关系 (间隔, 是否来回反向) ----------
            // 命中姿态统一用 HumanModel.Compose 的滚转约定 (只有指向重要, 滚转之后会换成真人模型)
            for (int i = 0; i < n; i++) hitQ[i] = HumanModel.Compose(v[i], 0f);

            var sStart = new float[m];
            var sEnd = new float[m];
            var gapPrev = new float[m];
            var gapNext = new float[m];
            var relPrev = new int[m];   // 和上一笔挥动方向的夹角分组 (SwingTemplates.RelBin)
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

                // 反应时间限制: 方块出现后还要一点时间才看得到/反应过来, 预挥最远点不能比这更早.
                // 太早的话把模板的预挥部分在时间上压缩 (RT 由 NJS / JD 算出, 见 AutoReplayGenerator)
                int s0 = strokes[k][0];
                float lead = SwingTemplates.PreExtremeLead(relPrev[k], SwingTemplates.DirClass(dir[s0]), gapPrev[k]);
                float maxLead = Mathf.Max(MinPreLead, ReactionTime - PerceptionDelay);
                preScale[k] = lead > maxLead ? lead / maxLead : 1f;
            }

            // ---------- 4. 用真人挥刀模板生成统一的关键帧轨道 ----------
            //  命中帧之间: 上一笔的后挥模板 和 下一笔的预挥模板 混合 (在下一笔的挥动坐标系里对角度插值,
            //  这样同向复位时剑会从前方/侧边绕回去, 来回挥时则是一条带 8 字偏移的往返弧)
            var track = new List<PoseKey>(n * 16);
            var hitIdx = new int[n];
            var endQ = new Quaternion[n];
            var endT = new float[n];
            for (int i = 0; i < n; i++) { endQ[i] = hitQ[i]; endT[i] = notes[i].time; }

            float side = isLeft ? -1f : 1f;   // 模板的侧向角以右手为准, 左手镜像

            // 某一笔的预挥 / 后挥在 t 时刻的 (alpha, beta), 以及对应的坐标系
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
            // 预挥窗口: 从最远点再往前留一点准备时间
            float PreWindow(int k) => Mathf.Min(0.75f, SwingTemplates.PreExtremeLead(relPrev[k], SwingTemplates.DirClass(dir[strokes[k][0]]), gapPrev[k]) / preScale[k] + 0.15f);
            Vector3 PreDir(int k, float t) { PreAB(k, t, out float a, out float b); return FrameDir(strokes[k][0], a, b); }
            Vector3 PostDir(int k, float t) { PostAB(k, t, out float a, out float b); return FrameDir(strokes[k][1], a, b); }
            // 长间隔/第一刀之前/最后一刀之后: 后挥结束后就停在原地 (模板在 +0.4s 之后保持不变, 只有待机微动在晃),
            // 到下一刀的预挥窗口才开始动. 不再额外切换到"休息姿势", 否则空档里会多出一次上下摆动
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
                // 上一笔的后挥模板 和 下一笔的预挥模板 描述的是同一段真人动作, 直接在指向上球面插值.
                // (离线对比真人 replay: 原本在下一笔坐标系里对角度插值, tech 常见的 90° 转向在中段会偏 89°;
                //  直接球面插值后中段误差降到约 30°, 和真人自己两次游玩之间的差异相当)
                float u = (t - sEnd[k]) / Mathf.Max(1e-4f, sStart[k + 1] - sEnd[k]);
                float w = AutoReplayMath.SmoothStep((u - BlendDelay) / (1f - BlendDelay));
                return Vector3.Slerp(fPost, FrameDir(nextS, qa, qb), w).normalized;
            }

            // 关键帧时刻: 均匀网格 (笔画内部除外) + 命中帧 + 每笔一个结束帧
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
                    if (k < m && t > sStart[k] - 2e-3f && t < sEnd[k] + 2e-3f) continue;   // 笔画内部 / 命中帧附近
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

            // ---------- 4.5 躲炸弹: 只改非命中帧, 命中帧原样保留 ----------
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

            // 加密/偏移会改变下标, 所以按 tag 重新定位命中帧和每笔结束帧
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

            // ---------- 5. 把轨道切回每个音符 ----------
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

        // ================= 采样 =================
        public static Quaternion SampleHandRotation(float t, List<NoteSwingData> swings, List<NoteData> bombs, bool isLeft)
        {
            if (swings == null || swings.Count == 0) return AutoReplayMath.PoseUp;

            // 二分: 第一个 命中时刻 >= t 的音符
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

        // 缓动: 预挥用 cos, 后挥用 sin -> 命中处速度最快, 两端(挥到最远处)速度为 0,
        // 来回挥的折返点不会再出现"速度瞬间反向"的顿挫感 (原本远端还保留 0.75 倍速度)

        // 在切割平面内, 绕 axis 从命中姿态旋转 theta 度. 不用 LookRotation, 避免竖直时 roll 翻转
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
            if (tr.Count > 0 && t <= tr[tr.Count - 1].t + 1e-4f) t = tr[tr.Count - 1].t + 1e-4f; // 时间严格递增
            tr.Add(new PoseKey(t, q));
        }

        // 从当前最后一帧过渡到 target(时刻 tTarget): 先停靠, 临近时再用 SmoothStep 平滑复位
        static void Connect(List<PoseKey> tr, float tTarget, Quaternion target)
        {
            PoseKey from = tr[tr.Count - 1];
            float dur = Mathf.Min(MaxConnectTime, tTarget - from.t);
            if (dur <= 0.004f) return;

            float t0 = tTarget - dur;
            if (t0 > from.t + 1e-3f) Add(tr, t0, from.q);
            // 复位用"剑尖走最短大圆弧"(AimSlerp), 不能用 Quaternion.Slerp:
            // 起点(上一刀后挥)和终点(下一刀预挥)的切割平面不同, 整体旋转差可达 170°+,
            // 四元数 Slerp 会让剑尖从正后方(z≈-0.9)绕过去, 看起来像往后转了一圈
            const int steps = 8;
            for (int j = 1; j < steps; j++)
            {
                float u = j / (float)steps;
                Add(tr, t0 + dur * u, BombAvoider.AimSlerp(from.q, target, AutoReplayMath.SmoothStep(u)));
            }
        }

        // NA 模式的方向/分组规划 —— 只用普通模式已有的常量, 没有新增任何阈值:
        //  1) 这一刀的方向和普通点块一样: 第一刀向下, 之后和上一刀反向(上下交替)
        //  2) 下一个方块并进同一刀的条件, 和普通模式"一刀连打"的判定一致:
        //       间隔 <= ForceChainGap 或 <= DotChainGap -> 直接并入 (叠块)
        //       间隔 <= ChainGap 且 "方向一致" -> 并入
        //     这里的"方向一致"= 前后两块的连线方向 和 这一刀的切割方向 的点积 >= ChainAlign
        //     (普通模式是比较两块的箭头方向, NA 没有箭头, 就用"这一刀要走的方向"代替)
        static void PlanNoArrowStrokes(List<NoteData> notes, Vector3[] dir, bool[] chained)
        {
            int n = notes.Count;
            Vector3 prevDir = Vector3.up;   // 初始剑向上, 所以第一刀是"和上一刀反向"= 向下

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

        // 点块方向的全局规划 (动态规划 / Viterbi):
        //   每个点块的候选方向 = 上/下 (按真人习惯略微外倾) + 左右/斜向 (有额外代价, 只在滑块等场合才划算);
        //   箭头块只有自己的方向.
        //   相邻两刀: 反向(来回挥) 代价 0; 同向需要复位, 间隔越短代价越高 (ResetScale/ResetTau/ResetBase);
        //   叠块/滑块(间隔极短或沿上一刀方向排列) 同向代价 0.
        // 这样一整串点块会从后面的箭头倒推起始方向, 必要时在长间隔处复位 —— 和真人一样.
        // 点块方向要看炸弹: 预挥从反方向过来、收势往挥的方向走, 路上有炸弹就不选这个方向.
        // 例: Extra Credit on the Chromosome Test! (Hard 113s) 左手点块在最下排两侧轮流, 中间夹着下排炸弹:
        // 真人两次都是每一刀往下砍再从上面绕回来 (炸弹重置), 原本的规划照节奏上下交替, 往上挥时会从炸弹那排穿过去
        public static float DotBombApproach = 0.45f;   // 预挥 / 收势时剑指向 方块 ∓ 方向 * 这个距离 (米)
        public static float DotBombCost = 6f;          // 路上会撞到炸弹的代价 (要大过两次同向重置 ~3.6: 宁可重置也不穿过炸弹)
        public static bool DebugDots = false;          // debug: 印出点块各方向的炸弹代价

        static Vector3[] PlanDotDirections(List<NoteData> notes, bool isLeft, List<NoteData> bombs = null, Vector3 wrist = default)
        {
            int n = notes.Count;
            var cand = new List<Vector3>[n];
            var prior = new List<float>[n];
            float upTilt = (isLeft ? -10f : 5f) * Mathf.Deg2Rad;    // 真人点块上挑略往外
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
                        prior[i][k] += DotBombCost * pen * pen;   // 平方: 稍微靠近几乎不扣, 真的会撞才重扣
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
            if (g <= StackGap) return dt >= ChainAlign ? 0f : (g <= ForceChainGap ? 6f : 3f);   // 叠块/几乎同时: 当成同一刀
            if (dt <= AlternateDot) return 0f;                                  // 来回挥
            if (g <= ChainGap && dt >= SliderDirAlign && IsSliderTail(prev, note, a, g, b)) return 0f;   // 滑块
            float reset = ResetScale * Mathf.Exp(-g / ResetTau) + ResetBase;
            // 垂直转向比完全同向容易一点
            if (dt < ChainAlign) reset *= 0.4f + 0.6f * Mathf.InverseLerp(AlternateDot, ChainAlign, dt);
            return reset;
        }

        // 滑块判定: note 是不是接在 prev 后面、沿 prev 这一刀的切割方向继续走的块.
        //  - 间隔 <= ChainGap
        //  - 两块不在同一格, 且 连线方向 和 prevDir 夹角够小 (斜对角也算: 直箭头接斜对角约45度, 斜箭头接斜对角0度)
        //  - tailDir != null(滑块自己有箭头)时, 它的方向不能和 prevDir 差太多(反向就是交替, 不是滑块)
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
    /// 躲炸弹: 在 NoteSwingPlanner 生成好关键帧轨道之后做后处理.
    /// 1) 危险时间窗由几何推出: 炸弹飞进"剑够得着的 z 范围"到"飞过手"之间 (注意在 note.time 之后!)
    /// 2) 目标点 = 窗口内所有炸弹 "斜对角点(以当前瞄准点为中心点对称)" 的平均 (同一层的炸弹先合并)
    /// 3) 在候选点里挑 "按真实时间轨迹检测确实躲得开" 且离目标最近的点, 把剑混合过去
    /// 命中帧(tag==1)永远不动, 所以切方块的精度不受影响.
    /// </summary>
    public static class BombAvoider
    {
        // ================= 可调参数 =================
        public const float BombRadius = 0.20f;   // 炸弹半径 0.18 + 剑身半径 0.02
        // 在判定半径之外再留的余量(米). 原本 0.07: 剑总是贴着炸弹过 (斯皮奇 Hard: 生成中位 0.54 m, 真人 0.90 m).
        // 真人 40 份回放 / 5719 颗炸彈: 距离中位 1.00 m, P10 0.66 m, < 0.40 m 只有 3%
        public static float Safety = 0.30f;
        public static bool PreferUp = true;      // 躲避时优先把剑举起朝上 (真人 55~58%)
        public static float UpMargin = 0.30f;    // 朝上时额外要求的余量 (最后的手臂动作会让手移动, 规划时的检查不够保守)
        public const float SaberStart = 0.30f;   // 剑身起点(离手腕)
        public const float SaberLen = 1.60f;     // 手腕(旋转中心) -> 剑尖. 真人 replay 实测: 命中时方块中心到控制器的距离 p99=1.57m, 再减去 pommel 0.20, 取 1.6 偏保守
        public const float BombHitZ = 0.72f;     // 炸弹在 note.time 时的真实 z (真人 replay 回归: z0=0.722). 注意不是 CutDistanceZ=1.5!
        public static float BombSpeed = 19.25f; // 炸弹飞向玩家的速度(米/歌曲秒). 真人 replay 回归=19.25; 换图请改成该图 NJS
        public const float Ramp = 0.04f;         // 偏移淡入淡出的时长
        public const float LockRamp = 0.03f;     // 离命中帧多近开始把偏移收回 0
        public const float DenseStep = 1f / 60f; // 窗口内加密关键帧的间隔
        public const float EvalDt = 1f / 120f;   // 检测采样间隔
        public const float BombLayerGap = 0.02f; // 炸弹时间间隔 <= 此值算同一"层"(风格同 DotChainGap/ForceChainGap)
        public const float GridCenterX = 0f;      // 12 格(4列x3行)的中心: 6和7之间
        public static float GridCenterY => 1.25f + AutoReplayMath.NoteYOffset;   // 中间一行的高度
        public static float ClusterGap = 0.5f;    // 相邻炸弹间隔 <= 此值(且中间没命中帧)算同一串, 整串期间剑保持躲避姿势
        public static float MaxClusterSpan = 0.6f;// 一串炸弹最长跨度, 超过就拆开 (原本 1.5: 整串一个姿势, 绕圈炸弹时剑僵住指着中间的洞; 真人会跟着炸弹顺序换姿势)
        public const float FadeOut = 0.12f;      // 炸弹过去后, 剑从躲避姿势淡回原轨迹的时长
        public const float MaxDodgeSpeed = 1800f; // 躲避入场/出场的平均角速度上限(度/秒); 平滑曲线峰值约 1.5 倍, 和挥刀的 MaxAvgSpeed 同量级
        public const float PreHold = 0.10f;      // 下一刀命中前这么久开始预挥, 淡出必须在这之前完成

        // 危险窗 = [bomb.t - Before, bomb.t + After]; Before 可以为负(窗口从 note.time 之后才开始). 由 Configure 算出
        static float Before = 0f;
        static float After = 0.08f;

        struct Bomb { public float t; public Vector3 p; }

        /// <summary>炸弹在 note.time 时 z=BombHitZ, 之后继续往玩家飞.
        /// 中心 z &lt; 手腕z+剑长+半径 时剑尖够得着; z &lt; 手腕z-半径 时已经飞过手.</summary>
        static bool s_isLeft;

        static void Configure(Vector3 wrist, bool isLeft)
        {
            s_isLeft = isLeft;
            float reachZ = wrist.z + HumanModel.MaxWristForwardZ + SaberLen + BombRadius;
            float behindZ = wrist.z - BombRadius;
            Before = (reachZ - BombHitZ) / BombSpeed;
            After = (BombHitZ - behindZ) / BombSpeed;
        }

        /// <returns>本轮发现的碰撞数(0 说明已经全部躲开)</returns>
        public static int Apply(List<PoseKey> track, List<NoteData> notes, List<NoteData> bombs, Vector3 wrist, bool isLeft)
        {
            if (bombs == null || bombs.Count == 0 || track.Count < 2) return 0;
            Configure(wrist, isLeft);

            var bl = new List<Bomb>(bombs.Count);
            foreach (var b in bombs) bl.Add(new Bomb { t = b.time, p = AutoReplayMath.GetNotePos(b) });
            bl.Sort((x, y) => x.t.CompareTo(y.t));
            Bomb[] B = bl.ToArray();
            List<Vector3> cands = BuildCandidates(notes, bombs);

            // 炸弹可能出现在这只手第一个音符之前 / 最后一个音符之后: 轨道两端各补一个停靠关键帧,
            // 这样那些炸弹也有帧可以调整 (否则会被 "tb 超出轨道范围" 直接跳过)
            float padFirst = B[0].t - 1.0f, padLast = B[B.Length - 1].t + 1.0f;
            if (padFirst < track[0].t) track.Insert(0, new PoseKey(padFirst, track[0].q));
            if (padLast > track[track.Count - 1].t) track.Add(new PoseKey(padLast, track[track.Count - 1].q));

            // 命中帧时刻(从不被修改): 用来把炸弹分段, 以及判断下一刀什么时候开始预挥
            var allLocks = new List<float>();
            for (int k = 0; k < track.Count; k++) if (track[k].tag == 1) allLocks.Add(track[k].t);

            int collisions = 0;
            int bi = 0;
            while (bi < B.Length)
            {
                // 0) 分组: 相邻炸弹间隔 <= ClusterGap, 中间没有命中帧, 总跨度 <= MaxClusterSpan 的算同一串.
                //    整串炸弹只做一次躲避, 剑在整串期间保持同一个姿势(不会炸弹一颗颗来就上下抖).
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

                // 1) 目标点: 整串炸弹的"斜对角点"平均; 再挑一个真正躲得开、离目标最近的点
                Vector3 aim = B[bi].p;
                Vector3 f = Sample(track, tb) * Vector3.forward;
                if (f.z > 0.05f) aim = wrist + f * ((B[bi].p.z - wrist.z) / f.z);
                Vector3 target = OppositeTarget(B, tF, tL);
                Vector3 safe = PickSafePoint(cands, B, wrist, tF, tL, aim, target);
                Vector3 safeDir = HumanModel.AimAt(s_isLeft, safe) * Vector3.forward;

                // 躲避姿势: 只改"剑指向", 滚转(绕剑身)沿用入场时原轨迹的姿势 -> 不会凭空多转半圈/一圈
                Quaternion qIn = Sample(track, tF - Ramp);
                Quaternion qSafe = AimRotation(qIn * Vector3.forward, safeDir) * qIn;

                // 2) 放手时机: 这串炸弹过去之后继续保持, 直到下一刀预挥开始之前(PreHold)才淡出.
                //    (下一刀通常是向下切, 预挥本来就从上面开始, 所以手不用先放下来)
                //    淡出时长按角度限速: 角度越大越慢, 人手转不了那么快
                int li = LowerBoundFloat(allLocks, tL);
                float fadeOut = Mathf.Max(FadeOut, Quaternion.Angle(Sample(track, tL + 0.12f), qSafe) / MaxDodgeSpeed);
                float tOut = tL;
                if (li < allLocks.Count)
                {
                    float nl = allLocks[li];
                    tOut = Mathf.Max(tL, nl - PreHold - fadeOut);
                    fadeOut = Mathf.Clamp(nl - PreHold - tOut, Ramp, fadeOut);
                }

                // 3) 入场时长同样按角度限速. 前一刀离得太近来不及时, 依次试 1.0 / 0.7 / 0.5 倍时长, 取第一个躲得开的
                float fadeIn = Mathf.Max(Ramp, Quaternion.Angle(qIn, qSafe) / MaxDodgeSpeed);
                List<PoseKey> bestSeg = null; int bestI0 = 0, bestI1 = 0; float bestC = -9f;
                float[] scales = { 1f, 0.7f, 0.5f };
                for (int sc = 0; sc < scales.Length; sc++)
                {
                    float fi = Mathf.Max(Ramp, fadeIn * scales[sc]);
                    float S0 = tF - fi - 0.01f, S1 = tOut + fadeOut + 0.01f;
                    int i0 = Mathf.Max(0, LowerBound(track, S0) - 1);
                    int i1 = Mathf.Min(track.Count - 1, LowerBound(track, S1));

                    // 窗口内加密关键帧(轨迹形状不变), 这样才有地方做偏移
                    List<PoseKey> sub = Densify(track, i0, i1, S0, S1);
                    var locks = new List<float>();
                    for (int k = 0; k < sub.Count; k++) if (sub[k].tag == 1) locks.Add(sub[k].t);

                    // 命中帧不动, 其余帧按权重过渡到 qSafe. 过渡用 AimSlerp: 剑尖沿最短大圆弧走, 不会绕到身后
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

        // 剑尖沿最短大圆弧从 a 的指向转到 b 的指向 (Quaternion.Slerp 在两个姿势差很大时, 指向会绕到身后/转一圈);
        // 绕剑身的滚转角单独、线性地过渡
        public static Quaternion AimSlerp(Quaternion a, Quaternion b, float u)
        {
            if (u <= 0f) return a;
            if (u >= 1f) return b;
            Vector3 fa = a * Vector3.forward, fb = b * Vector3.forward;
            Quaternion rot = AimRotation(fa, fb);
            Quaternion qEnd = rot * a;                          // 指向 = fb, 滚转 = 平行搬运
            Quaternion tw = b * Quaternion.Inverse(qEnd);       // 剩下的滚转差 (轴 = fb)
            if (tw.w < 0f) tw = new Quaternion(-tw.x, -tw.y, -tw.z, -tw.w);
            float ang; Vector3 axis;
            tw.ToAngleAxis(out ang, out axis);
            if (ang > 180f) ang -= 360f;
            if (Vector3.Dot(axis, fb) < 0f) ang = -ang;
            Quaternion qd = Quaternion.Slerp(Quaternion.identity, rot, u) * a;
            Vector3 fu = qd * Vector3.forward;
            return Quaternion.AngleAxis(ang * u, fu) * qd;
        }

        // fa -> fb 的最短旋转; 几乎正反向时固定从"朝前"那一侧绕过去
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

        /// <summary>躲避完成后的自检: hit=仍会撞到的炸弹数, near=离炸弹小于安全余量的数. 每个仍会撞的炸弹会单独打一行日志</summary>
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
            // 格子编号 1..12 (左上=1, 右下=12)
            int col = Mathf.Clamp(Mathf.RoundToInt(B[i].p.x / 0.6f + 1.5f), 0, 3);
            int row = Mathf.Clamp(Mathf.RoundToInt((B[i].p.y - 0.72f - AutoReplayMath.NoteYOffset) / 0.53f), 0, 2);
            int cell = (2 - row) * 4 + col + 1;

            // 这颗炸弹自己的窗口里, 最近的时刻 / 距离最近命中帧(tag==1)多远 / 是否在轨道范围外
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

            // 同一时刻附近的炸弹(格子编号), 方便看是不是整排炸弹
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

        // ---------- 偏移权重: 窗口内=1, 两侧淡出; 靠近命中帧时收回到 0 ----------
        // 躲避强度: tIn 之前用 fadeIn 淡入, [tIn,tOut] 为 1, tOut 之后用 fadeOut 淡出; 靠近命中帧时收回到 0
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

        // ---------- 斜对角平均 ----------
        // 格子编号:  1  2  3  4
        //            5  6  7  8
        //            9 10 11 12
        // 每颗炸弹的"斜对角" = 以格子中心(6和7之间)点对称的格子: 12->1, 9->4, 10->3, 11->2 ...
        // 例: 只有12有炸弹 -> 剑指向1;  9/10/11/12 都有炸弹 -> 对角是 4/3/2/1, 平均 = 2和3之间(上排正中).
        // 时间间隔 <= BombLayerGap 的炸弹算同一层, 层内先平均, 再对各层平均.
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

        // ---------- 选点: 候选点里 "真的躲得开(按炸弹飞行轨迹检测)" 的, 离目标最近 ----------
        static Vector3 PickSafePoint(List<Vector3> cands, Bomb[] B, Vector3 wrist, float t0, float t1, Vector3 aim, Vector3 target)
        {
            // 0) 真人的习惯: 炸弹飞过来、手上没有方块要切时, 剑多半直接举起朝上 (55~58%, 朝下 15~17%, 往前约 20%;
            //    40 份回放 / 2700 次统计). 朝上躲得开就朝上 (剑尖稍微往前, 不会戳到头)
            if (PreferUp)
            {
                Vector3 up = wrist + new Vector3(0f, 2.0f, 0.6f);
                if (ClearanceConst(HumanModel.AimAt(s_isLeft, up), B, wrist, t0, t1) >= Safety + UpMargin) return up;
            }

            // 1) 斜对角目标本身能躲开 -> 直接朝它
            Quaternion qt = HumanModel.AimAt(s_isLeft, target);
            if (ClearanceConst(qt, B, wrist, t0, t1) >= Safety + 0.02f) return target;

            // 2) 否则在候选点里, 取"能躲开"且离目标最近的; 都躲不开就取最安全的
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

        // 剑保持固定朝向 q 时, 窗口内离炸弹的最小间隙
        static List<NoteData> _poseCacheSrc;
        static Bomb[] _poseCache;

        /// <summary>
        /// 剑固定指向 aimPoint 时, [t0, t1] 内离炸弹多近: 0 = 够远 (>= Safety), 1 = 碰到, 中间线性.
        /// 点块方向规划用 (预挥 / 收势的路上有没有炸弹)
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

        // 候选点 = 谱面里出现过的位置及中点 + 一圈均匀点阵(允许剑躲到格子外面一点)
        static List<Vector3> BuildCandidates(List<NoteData> notes, List<NoteData> bombs)
        {
            List<Vector3> all = BuildGrid(notes, bombs);
            for (float x = -1.05f; x <= 1.051f; x += 0.15f)
                for (float y = 0.55f; y <= 1.951f; y += 0.15f)
                    AddUnique(all, new Vector3(x, y, AutoReplayMath.CutDistanceZ));
            return all;
        }


        // 网格点直接取自谱面里实际出现过的方块/炸弹位置(保证和 GetNotePos 同一坐标系), 再补相邻点中点
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

        // ---------- 碰撞检测: 返回 (最近距离 - 炸弹半径), 越大越安全 ----------
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
                    c.z = BombHitZ + (B[i].t - t) * BombSpeed;   // note.time 时在 BombHitZ, 之前更远, 之后更近
                    float d = SaberDist(c, wrist, Sample(keys, t)) - BombRadius;
                    if (d < best) best = d;
                }
            }
            return best;
        }

        // wrist 是手腕的基准位置; 实际手腕位置随剑的指向移动 (和 PoseCalculator 用同一个手臂模型)
        static float SaberDist(Vector3 c, Vector3 wrist, Quaternion q)
        {
            // 和最终输出用同一套人体模型: 先限位, 再由指向算手腕位置
            Vector3 f = HumanModel.LimitForward(s_isLeft, q * Vector3.forward);
            Vector3 a = HumanModel.WristForDir(s_isLeft, f) + f * SaberStart;
            Vector3 ab = f * (SaberLen - SaberStart);
            float s = Mathf.Clamp01(Vector3.Dot(c - a, ab) / ab.sqrMagnitude);
            return (c - (a + ab * s)).magnitude;
        }

        // ---------- 轨道工具 ----------
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
