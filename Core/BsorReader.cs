using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AutoReplay.Core
{
    /// <summary>精簡版 .bsor 讀取器: 只讀訓練要用的 info / frames / notes. 不依賴 BeatLeader 和 Unity.</summary>
    public sealed class BsorReplay
    {
        public string playerId, playerName, hash, songName, difficulty, mode, modifiers, hmd, platform, modVersion, gameVersion;
        public long timestamp;     // unix 秒 (回放錄製時間)
        public int score;
        public bool leftHanded;
        public float height;
        public float[] time;      // 幀時間
        public float[][] head;    // 每幀 7: px py pz qx qy qz qw
        public float[][] left;
        public float[][] right;
        public List<BsorNote> notes = new List<BsorNote>();

        public bool HasSpeedModifier(params string[] codes)
        {
            if (string.IsNullOrEmpty(modifiers)) return false;
            foreach (var c in codes) if (modifiers.Contains(c)) return true;
            return false;
        }
    }

    public struct BsorNote
    {
        public int id;        // BeatLeader noteID: ...,lineIndex*1000 + layer*100 + color*10 + cutDirection
        public float time;
        public int type;      // 0 good, 1 bad, 2 miss, 3 bomb
        public int saberType; // 只有 good/bad 有, 其餘 -1
        public int Color => (id / 10) % 10;
        public int CutDirection => id % 10;
    }

    public static class BsorReader
    {
        const int MagicNumber = 0x442d3d69;

        /// <summary>只讀 info (很快, 用來篩選)</summary>
        public static BsorReplay ReadInfo(string path)
        {
            using (var fs = File.OpenRead(path))
            using (var r = new BinaryReader(fs))
                return ReadHeader(r);
        }

        public static BsorReplay Read(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
            using (var r = new BinaryReader(fs))
            {
                var rep = ReadHeader(r);
                if (r.ReadByte() != 1) throw new InvalidDataException("frames section missing");
                int n = r.ReadInt32();
                if (n < 0 || n > 5_000_000) throw new InvalidDataException("bad frame count");
                rep.time = new float[n]; rep.head = new float[n][]; rep.left = new float[n][]; rep.right = new float[n][];
                for (int i = 0; i < n; i++)
                {
                    rep.time[i] = r.ReadSingle(); r.ReadInt32();
                    rep.head[i] = Read7(r); rep.left[i] = Read7(r); rep.right[i] = Read7(r);
                }
                if (r.ReadByte() != 2) throw new InvalidDataException("notes section missing");
                int nn = r.ReadInt32();
                for (int i = 0; i < nn; i++)
                {
                    var note = new BsorNote { id = r.ReadInt32(), time = r.ReadSingle(), saberType = -1 };
                    r.ReadSingle();                // spawn time
                    note.type = r.ReadInt32();
                    if (note.type == 0 || note.type == 1)
                    {
                        r.ReadBytes(4);            // speedOK, directionOK, saberTypeOK, wasCutTooSoon
                        r.ReadSingle();            // saberSpeed
                        r.ReadBytes(12);           // saberDir
                        note.saberType = r.ReadInt32();
                        r.ReadBytes(4 * 2);        // timeDeviation, cutDirDeviation
                        r.ReadBytes(12 * 2);       // cutPoint, cutNormal
                        r.ReadBytes(4 * 4);        // cutDistanceToCenter, cutAngle, beforeCutRating, afterCutRating
                    }
                    rep.notes.Add(note);
                }
                return rep;
            }
        }

        static BsorReplay ReadHeader(BinaryReader r)
        {
            if (r.ReadInt32() != MagicNumber) throw new InvalidDataException("not a bsor file");
            r.ReadByte();                          // file version
            if (r.ReadByte() != 0) throw new InvalidDataException("info section missing");
            var rep = new BsorReplay();
            rep.modVersion = Str(r); rep.gameVersion = Str(r);
            long.TryParse(Str(r), out rep.timestamp);
            rep.playerId = Str(r); rep.playerName = Str(r);
            rep.platform = Str(r); Str(r); rep.hmd = Str(r); Str(r);   // platform, tracking, hmd, controller
            rep.hash = Str(r); rep.songName = Str(r); Str(r); rep.difficulty = Str(r);
            rep.score = r.ReadInt32();
            rep.mode = Str(r); Str(r); rep.modifiers = Str(r);
            r.ReadSingle();                        // jump distance
            rep.leftHanded = r.ReadBoolean();
            rep.height = r.ReadSingle();
            r.ReadSingle(); r.ReadSingle(); r.ReadSingle();   // start, fail, speed
            return rep;
        }

        static float[] Read7(BinaryReader r)
        {
            var v = new float[7];
            for (int i = 0; i < 7; i++) v[i] = r.ReadSingle();
            return v;
        }

        static string Str(BinaryReader r)
        {
            int len = r.ReadInt32();
            if (len < 0 || len > 1 << 20) throw new InvalidDataException("bad string");
            return Encoding.UTF8.GetString(r.ReadBytes(len));
        }
    }
}
