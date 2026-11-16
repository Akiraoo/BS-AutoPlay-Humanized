using System;
using System.IO;

namespace AutoReplay.Core
{
    /// <summary>
    /// 纯托管的 gzip 解压 (RFC 1952 + RFC 1951 inflate) 和 "stored" gzip 打包.
    /// 游戏内的 Mono GZipStream 不可靠 (解出空数据), 所以 .apmodel 不依赖它.
    /// </summary>
    public static class MiniGzip
    {
        // ---------------- 打包: 不压缩的 deflate stored 块 (任何 gzip 解压器都能读) ----------------
        public static byte[] WrapStored(byte[] data)
        {
            using (var ms = new MemoryStream())
            {
                ms.Write(new byte[] { 0x1f, 0x8b, 8, 0, 0, 0, 0, 0, 0, 0xff }, 0, 10);
                int pos = 0;
                do
                {
                    int n = Math.Min(65535, data.Length - pos);
                    bool last = pos + n >= data.Length;
                    ms.WriteByte((byte)(last ? 1 : 0));
                    ms.WriteByte((byte)(n & 0xff)); ms.WriteByte((byte)(n >> 8));
                    ms.WriteByte((byte)(~n & 0xff)); ms.WriteByte((byte)((~n >> 8) & 0xff));
                    ms.Write(data, pos, n);
                    pos += n;
                } while (pos < data.Length);
                WriteU32(ms, Crc32(data));
                WriteU32(ms, (uint)data.Length);
                return ms.ToArray();
            }
        }

        static void WriteU32(Stream s, uint v) { for (int i = 0; i < 4; i++) s.WriteByte((byte)(v >> (8 * i))); }

        // ---------------- 解压 ----------------
        public static byte[] Gunzip(byte[] gz)
        {
            if (gz.Length < 18 || gz[0] != 0x1f || gz[1] != 0x8b || gz[2] != 8) throw new InvalidDataException("模型資料不是 gzip");
            int flg = gz[3], p = 10;
            if ((flg & 4) != 0) p += 2 + (gz[p] | (gz[p + 1] << 8));
            if ((flg & 8) != 0) { while (gz[p] != 0) p++; p++; }
            if ((flg & 16) != 0) { while (gz[p] != 0) p++; p++; }
            if ((flg & 2) != 0) p += 2;
            var inf = new Inflater(gz, p);
            byte[] outp = inf.Run();
            int t = inf.BytePos;
            uint crc = (uint)(gz[t] | (gz[t + 1] << 8) | (gz[t + 2] << 16) | (gz[t + 3] << 24));
            if (crc != Crc32(outp)) throw new InvalidDataException("模型資料 CRC 不符 (檔案損壞)");
            return outp;
        }

        sealed class Inflater
        {
            readonly byte[] src; int pos; int bitBuf, bitCnt;
            byte[] win = new byte[1 << 16]; int outLen;

            public Inflater(byte[] s, int start) { src = s; pos = start; }
            public int BytePos => pos;

            int Bits(int n)
            {
                while (bitCnt < n)
                {
                    if (pos >= src.Length) throw new InvalidDataException("模型資料不完整");
                    bitBuf |= src[pos++] << bitCnt; bitCnt += 8;
                }
                int v = bitBuf & ((1 << n) - 1);
                bitBuf >>= n; bitCnt -= n;
                return v;
            }

            void Put(byte b)
            {
                if (outLen == win.Length) Array.Resize(ref win, win.Length * 2);
                win[outLen++] = b;
            }

            public byte[] Run()
            {
                int final;
                do
                {
                    final = Bits(1);
                    int type = Bits(2);
                    if (type == 0) Stored();
                    else if (type == 1) Codes(FixedLit, FixedDist);
                    else if (type == 2) Dynamic();
                    else throw new InvalidDataException("模型資料損壞 (deflate 區塊類型)");
                } while (final == 0);
                bitBuf = 0; bitCnt = 0;   // 剩余位丢弃, 对齐到字节
                var r = new byte[outLen];
                Array.Copy(win, r, outLen);
                return r;
            }

            void Stored()
            {
                bitBuf = 0; bitCnt = 0;
                int len = src[pos] | (src[pos + 1] << 8);
                int nlen = src[pos + 2] | (src[pos + 3] << 8);
                if ((len ^ 0xffff) != nlen) throw new InvalidDataException("模型資料損壞 (stored 長度)");
                pos += 4;
                for (int i = 0; i < len; i++) Put(src[pos++]);
            }

            // Huffman 表: count[len], symbol[] (canonical)
            sealed class Huff { public short[] count = new short[16]; public short[] sym; }

            static Huff Build(int[] lengths, int n)
            {
                var h = new Huff { sym = new short[n] };
                for (int i = 0; i < n; i++) h.count[lengths[i]]++;
                h.count[0] = 0;
                var offs = new short[16];
                for (int i = 1; i < 16; i++) offs[i] = (short)(offs[i - 1] + h.count[i - 1]);
                for (int i = 0; i < n; i++) if (lengths[i] != 0) h.sym[offs[lengths[i]]++] = (short)i;
                return h;
            }

            int Decode(Huff h)
            {
                int code = 0, first = 0, index = 0;
                for (int len = 1; len < 16; len++)
                {
                    code |= Bits(1);
                    int count = h.count[len];
                    if (code - count < first) return h.sym[index + (code - first)];
                    index += count; first += count; first <<= 1; code <<= 1;
                }
                throw new InvalidDataException("模型資料損壞 (Huffman)");
            }

            static readonly short[] LBase = { 3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258 };
            static readonly short[] LExt = { 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0 };
            static readonly short[] DBase = { 1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577 };
            static readonly short[] DExt = { 0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13 };

            static readonly Huff FixedLit, FixedDist;
            static Inflater()
            {
                var l = new int[288];
                for (int i = 0; i < 144; i++) l[i] = 8;
                for (int i = 144; i < 256; i++) l[i] = 9;
                for (int i = 256; i < 280; i++) l[i] = 7;
                for (int i = 280; i < 288; i++) l[i] = 8;
                FixedLit = Build(l, 288);
                var d = new int[30];
                for (int i = 0; i < 30; i++) d[i] = 5;
                FixedDist = Build(d, 30);
            }

            void Codes(Huff lit, Huff dist)
            {
                while (true)
                {
                    int s = Decode(lit);
                    if (s < 256) { Put((byte)s); continue; }
                    if (s == 256) return;
                    s -= 257;
                    if (s >= 29) throw new InvalidDataException("模型資料損壞 (長度碼)");
                    int len = LBase[s] + Bits(LExt[s]);
                    int ds = Decode(dist);
                    if (ds >= 30) throw new InvalidDataException("模型資料損壞 (距離碼)");
                    int d = DBase[ds] + Bits(DExt[ds]);
                    if (d > outLen) throw new InvalidDataException("模型資料損壞 (距離太遠)");
                    for (int i = 0; i < len; i++) Put(win[outLen - d]);
                }
            }

            static readonly byte[] Order = { 16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15 };

            void Dynamic()
            {
                int nlen = Bits(5) + 257, ndist = Bits(5) + 1, ncode = Bits(4) + 4;
                var lengths = new int[320];
                for (int i = 0; i < ncode; i++) lengths[Order[i]] = Bits(3);
                var lencode = Build(lengths, 19);
                lengths = new int[320];
                int idx = 0;
                while (idx < nlen + ndist)
                {
                    int sym = Decode(lencode);
                    if (sym < 16) { lengths[idx++] = sym; continue; }
                    int len = 0, rep;
                    if (sym == 16) { if (idx == 0) throw new InvalidDataException("模型資料損壞 (重複長度)"); len = lengths[idx - 1]; rep = 3 + Bits(2); }
                    else if (sym == 17) rep = 3 + Bits(3);
                    else rep = 11 + Bits(7);
                    if (idx + rep > nlen + ndist) throw new InvalidDataException("模型資料損壞 (長度表)");
                    while (rep-- > 0) lengths[idx++] = len;
                }
                var lit = Build(lengths, nlen);
                var dl = new int[Math.Max(30, ndist)];
                Array.Copy(lengths, nlen, dl, 0, ndist);
                var dist = Build(dl, dl.Length);
                Codes(lit, dist);
            }
        }

        static uint[] _crcTable;
        public static uint Crc32(byte[] data)
        {
            if (_crcTable == null)
            {
                var t = new uint[256];
                for (uint i = 0; i < 256; i++)
                {
                    uint c = i;
                    for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                    t[i] = c;
                }
                _crcTable = t;
            }
            uint crc = 0xffffffffu;
            foreach (byte b in data) crc = _crcTable[(crc ^ b) & 0xff] ^ (crc >> 8);
            return crc ^ 0xffffffffu;
        }
    }
}
