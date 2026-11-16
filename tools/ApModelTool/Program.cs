using System;
using System.IO;
using AutoReplay.Core;
using Newtonsoft.Json;

namespace AutoReplay.Tools
{
    /// <summary>
    /// AutoPlay 模型命令行工具 (和游戏内共用 Core):
    ///   train   &lt;Replays资料夹&gt; &lt;输出 model.json&gt; [--player ID] [--max 80] [--name 名称] [--author 作者]
    ///   pack    &lt;model.json&gt; &lt;输出.apmodel&gt; [--sign 私钥.xml]
    ///   unpack  &lt;in.apmodel&gt; &lt;输出 model.json&gt;
    ///   inspect &lt;in.apmodel 或 model.json&gt;
    ///   keygen  &lt;私钥输出.xml&gt; &lt;公钥输出.xml&gt;
    /// </summary>
    static class Program
    {
        static int Main(string[] args)
        {
            try
            {
                if (args.Length == 0) { Usage(); return 1; }
                switch (args[0])
                {
                    case "train": return Train(args);
                    case "pack": return Pack(args);
                    case "unpack": return Unpack(args);
                    case "inspect": return Inspect(args);
                    case "keygen": return KeyGen(args);
                    default: Usage(); return 1;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("错误: " + ex.Message);
                return 2;
            }
        }

        static void Usage()
        {
            Console.WriteLine("ApModelTool train <Replays> <out model.json> [--player ID] [--max 80] [--name N] [--author A]");
            Console.WriteLine("ApModelTool pack <model.json> <out.apmodel> [--sign private.xml]");
            Console.WriteLine("ApModelTool unpack <in.apmodel> <out model.json>");
            Console.WriteLine("ApModelTool inspect <file>");
            Console.WriteLine("ApModelTool keygen <private.xml> <public.xml>");
        }

        static string Opt(string[] a, string name, string def = null)
        {
            int i = Array.IndexOf(a, name);
            return i >= 0 && i + 1 < a.Length ? a[i + 1] : def;
        }

        static int Train(string[] a)
        {
            var o = new TrainOptions
            {
                replaysDir = a[1],
                playerId = Opt(a, "--player"),
                maxReplays = int.Parse(Opt(a, "--max", "80")),
                modelName = Opt(a, "--name", "my-model"),
                author = Opt(a, "--author", ""),
                progress = s => { if (!s.StartsWith("[")) Console.WriteLine(s); },
            };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var model = ModelTrainer.Train(o, out _);
            File.WriteAllText(a[2], JsonConvert.SerializeObject(model, Formatting.Indented));
            Console.WriteLine($"已写入 {a[2]} ({sw.Elapsed.TotalSeconds:F1}s)");
            return 0;
        }

        static int Pack(string[] a)
        {
            string json = File.ReadAllText(a[1]);
            var model = JsonConvert.DeserializeObject<AutoPlayModel>(json);
            string err = model.Validate();
            if (err != null) throw new InvalidDataException(err);
            string keyPath = Opt(a, "--sign");
            byte[] bytes = ApModelFile.Pack(json, keyPath != null ? File.ReadAllText(keyPath) : null);
            File.WriteAllBytes(a[2], bytes);
            Console.WriteLine($"已打包 {a[2]} ({bytes.Length} bytes, {(keyPath != null ? "已签名" : "未签名")})");
            return 0;
        }

        static int Unpack(string[] a)
        {
            string json = ApModelFile.Unpack(File.ReadAllBytes(a[1]), out var trust);
            File.WriteAllText(a[2], json);
            Console.WriteLine($"已解包 {a[2]} (签名: {trust})");
            return 0;
        }

        static int Inspect(string[] a)
        {
            string json; ModelTrust trust;
            if (a[1].EndsWith(ApModelFile.Extension, StringComparison.OrdinalIgnoreCase)) json = ApModelFile.Unpack(File.ReadAllBytes(a[1]), out trust);
            else { json = File.ReadAllText(a[1]); trust = ModelTrust.Unpacked; }
            var m = JsonConvert.DeserializeObject<AutoPlayModel>(json);
            Console.WriteLine($"名称: {m.meta.name}  作者: {m.meta.author}  建立: {m.meta.createdUtc}  训练器: {m.meta.trainer}");
            Console.WriteLine($"replay {m.meta.replayCount} 份, 挥刀 {m.meta.swingCount} 次, 模板 {m.templates.groups.Count} 组");
            Console.WriteLine($"签名状态: {trust}   指纹: {ApModelFile.Fingerprint(json)}");
            Console.WriteLine($"检查: {m.Validate() ?? "OK"}");
            return 0;
        }

        static int KeyGen(string[] a)
        {
            if (File.Exists(a[1])) throw new IOException("私钥文件已存在, 不覆盖: " + a[1]);
            using (var rsa = System.Security.Cryptography.RSA.Create(2048))
            {
                File.WriteAllText(a[1], rsa.ToXmlString(true));
                File.WriteAllText(a[2], rsa.ToXmlString(false));
            }
            Console.WriteLine($"私钥: {a[1]} (不要外流, 不要进 repo)\n公钥: {a[2]} (贴进 Core/OfficialKey.cs)");
            return 0;
        }
    }
}
