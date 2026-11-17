using System;
using System.IO;
using System.Reflection;
using HarmonyLib;
using BeatLeader.Models.Replay;

namespace AutoReplay
{
    public static class ReplayFileSaver
    {
        public static void SaveReplay(Replay replay, string hash, string diff)
        {
            try
            {
                string folder = Path.Combine(Environment.CurrentDirectory, "UserData", "BeatLeader", "ReplayerCache");
                if (!Directory.Exists(folder))
                {
                    Directory.CreateDirectory(folder);
                }
                string safeHash = Safe(hash);
                // 只用目前选中的模型生成一份; 文件名带上模型名称和生成时间, 每次生成都是新文件, 不会互相覆盖
                string model = Safe(Path.GetFileNameWithoutExtension((ModelManager.CurrentId ?? "no-model").TrimEnd('/')));
                string file = Path.Combine(folder, $"AutoPlay_{safeHash}_{diff}_{model}_{DateTime.Now:yyyyMMdd-HHmmss}.bsor");

                var encoderType = AccessTools.TypeByName("BeatLeader.Models.Replay.ReplayEncoder");
                var encodeMethod = encoderType?.GetMethod("Encode", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

                if (encodeMethod != null)
                {
                    using (var fs = File.Open(file, FileMode.Create, FileAccess.Write))
                    using (var bw = new BinaryWriter(fs))
                    {
                        encodeMethod.Invoke(null, new object[] { replay, bw });
                    }
                    Plugin.Log?.Info($"Saved AutoPlay replay to: {file}");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.Warn($"Failed to save replay to disk: {ex.Message}");
            }
        }

        static string Safe(string s)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s;
        }
    }
}
