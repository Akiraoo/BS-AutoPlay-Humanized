using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace AutoReplay.Core
{
    public enum ModelTrust
    {
        Unpacked,        // 本機訓練的資料夾 (model.json)
        Unsigned,        // 打包了但沒簽名 (例如遊戲內打包的)
        Official,        // 作者簽名, 驗證透過
        BadSignature,    // 有簽名但驗證失敗 (被改過, 或不是作者籤的)
    }

    /// <summary>
    /// .apmodel 單檔格式 (小端):
    ///   "APMD" | u16 版本 | u16 flags(bit0=有簽名) | 16 位元組 salt | u32 payload 長度 | payload | u16 簽名長度 | 簽名
    ///   payload = gzip(model.json UTF-8) 再和 SHA256(Secret|salt|計數器) 生成的金鑰流做 XOR (只是混淆, 不是加密)
    ///   簽名 = RSA-SHA256 (PKCS#1 v1.5), 籤的是 "簽名長度" 欄位之前的所有位元組; 公鑰見 OfficialKey.PublicKeyXml
    /// </summary>
    public static class ApModelFile
    {
        public const string Extension = ".apmodel";
        static readonly byte[] Magic = { (byte)'A', (byte)'P', (byte)'M', (byte)'D' };
        const ushort Version = 1;
        const ushort FlagSigned = 1;
        static readonly byte[] Secret = Encoding.UTF8.GetBytes("AutoPlay-unofficial/apmodel/v1/obfuscation");

        /// <summary>打包. privateKeyXml 為 null 時不簽名</summary>
        public static byte[] Pack(string modelJson, string privateKeyXml = null)
        {
            byte[] raw = Encoding.UTF8.GetBytes(modelJson);
            byte[] gz = null;
            try
            {
                using (var ms = new MemoryStream())
                {
                    using (var z = new GZipStream(ms, CompressionMode.Compress, true)) z.Write(raw, 0, raw.Length);
                    gz = ms.ToArray();
                }
                // 遊戲內 Mono 的 GZipStream 可能產出壞資料: 用自己的解壓器驗證, 不對就改用不壓縮的 gzip
                byte[] check = MiniGzip.Gunzip(gz);
                if (check.Length != raw.Length) gz = null;
                else for (int i = 0; i < raw.Length; i++) if (check[i] != raw[i]) { gz = null; break; }
            }
            catch { gz = null; }
            if (gz == null) gz = MiniGzip.WrapStored(raw);
            byte[] salt = new byte[16];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(salt);
            Xor(gz, salt);

            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(Magic);
                w.Write(Version);
                w.Write(privateKeyXml != null ? FlagSigned : (ushort)0);
                w.Write(salt);
                w.Write((uint)gz.Length);
                w.Write(gz);
                w.Flush();
                byte[] signed = ms.ToArray();
                byte[] sig = privateKeyXml != null ? Sign(signed, privateKeyXml) : new byte[0];
                w.Write((ushort)sig.Length);
                w.Write(sig);
                w.Flush();
                return ms.ToArray();
            }
        }

        /// <summary>解包, 返回 model.json 文字和簽名狀態</summary>
        public static string Unpack(byte[] file, out ModelTrust trust)
        {
            using (var ms = new MemoryStream(file))
            using (var r = new BinaryReader(ms))
            {
                byte[] magic = r.ReadBytes(4);
                if (magic.Length != 4 || magic[0] != Magic[0] || magic[1] != Magic[1] || magic[2] != Magic[2] || magic[3] != Magic[3])
                    throw new InvalidDataException("不是 .apmodel 文件");
                ushort ver = r.ReadUInt16();
                if (ver > Version) throw new InvalidDataException($".apmodel 版本 {ver} 太新, 請更新 AutoPlay");
                ushort flags = r.ReadUInt16();
                byte[] salt = r.ReadBytes(16);
                uint len = r.ReadUInt32();
                if (len > 64 * 1024 * 1024) throw new InvalidDataException("模型文件損壞");
                byte[] payload = r.ReadBytes((int)len);
                if (payload.Length != len) throw new InvalidDataException("模型文件不完整");
                int signedLen = (int)ms.Position;
                ushort sigLen = r.ReadUInt16();
                byte[] sig = r.ReadBytes(sigLen);

                if ((flags & FlagSigned) == 0 || sigLen == 0) trust = ModelTrust.Unsigned;
                else
                {
                    byte[] signedPart = new byte[signedLen];
                    Array.Copy(file, signedPart, signedLen);
                    trust = Verify(signedPart, sig, OfficialKey.PublicKeyXml) ? ModelTrust.Official : ModelTrust.BadSignature;
                }

                Xor(payload, salt);
                string json = Encoding.UTF8.GetString(MiniGzip.Gunzip(payload));
                if (string.IsNullOrWhiteSpace(json)) throw new InvalidDataException("模型內容是空的");
                return json;
            }
        }

        static void Xor(byte[] data, byte[] salt)
        {
            using (var sha = SHA256.Create())
            {
                byte[] block = null;
                for (int i = 0; i < data.Length; i++)
                {
                    int bi = i % 32;
                    if (bi == 0)
                    {
                        int counter = i / 32;
                        byte[] input = new byte[Secret.Length + salt.Length + 4];
                        Buffer.BlockCopy(Secret, 0, input, 0, Secret.Length);
                        Buffer.BlockCopy(salt, 0, input, Secret.Length, salt.Length);
                        BitConverter.GetBytes(counter).CopyTo(input, Secret.Length + salt.Length);
                        block = sha.ComputeHash(input);
                    }
                    data[i] ^= block[bi];
                }
            }
        }

        static byte[] Sign(byte[] data, string privateKeyXml)
        {
            using (var rsa = RSA.Create())
            {
                rsa.FromXmlString(privateKeyXml);
                return rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            }
        }

        static bool Verify(byte[] data, byte[] sig, string publicKeyXml)
        {
            if (string.IsNullOrEmpty(publicKeyXml)) return false;
            try
            {
                using (var rsa = RSA.Create())
                {
                    rsa.FromXmlString(publicKeyXml);
                    return rsa.VerifyData(data, sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                }
            }
            catch
            {
                // 舊 Mono 的後備寫法
                try
                {
                    using (var csp = new RSACryptoServiceProvider())
                    {
                        csp.FromXmlString(publicKeyXml);
                        return csp.VerifyData(data, "SHA256", sig);
                    }
                }
                catch { return false; }
            }
        }

        /// <summary>模型內容的短指紋 (浮水印裡用來標識模型)</summary>
        public static string Fingerprint(string modelJson)
        {
            using (var sha = SHA256.Create())
            {
                byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(modelJson));
                var sb = new StringBuilder();
                for (int i = 0; i < 8; i++) sb.Append(h[i].ToString("x2"));
                return sb.ToString();
            }
        }
    }
}
