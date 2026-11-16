using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AutoReplay.Core;
using Newtonsoft.Json;

namespace AutoReplay
{
    /// <summary>
    /// 模型管理: UserData/AutoPlay/Models 下
    ///   *.apmodel           打包好的单档 (混淆 + 可选作者签名), 可以分享
    ///   &lt;名称&gt;/model.json  本机训练输出的未打包模型 (可读)
    /// 选中的模型记在 UserData/AutoPlay/settings.json.
    /// </summary>
    public static class ModelManager
    {
        public sealed class Entry
        {
            public string id;          // 文件名或资料夹名 (唯一)
            public string path;
            public bool packed;
            public string displayName; // 下拉选单显示
        }

        sealed class Settings
        {
            public string selectedModel;
            public TrainSettings train = new TrainSettings();
        }

        static Settings _settings;
        static Settings Cfg => _settings ?? (_settings = LoadSettings());

        /// <summary>训练设定 (设定页和自动训练共用), 改完要呼叫 SaveTrainSettings</summary>
        public static TrainSettings Train => Cfg.train ?? (Cfg.train = new TrainSettings());
        public static void SaveTrainSettings() => SaveSettings(Cfg);

        public const string AutoModelName = "my-auto";
        public static string StarCachePath => Path.Combine(RootDir, "star-cache.json");
        static StarService _stars;
        public static StarService Stars => _stars ?? (_stars = new StarService(StarCachePath));

        public static string RootDir => Path.Combine(Environment.CurrentDirectory, "UserData", "AutoPlay");
        public static string ModelsDir => Path.Combine(RootDir, "Models");
        static string SettingsPath => Path.Combine(RootDir, "settings.json");
        public static string ReplaysDir => Path.Combine(Environment.CurrentDirectory, "UserData", "BeatLeader", "Replays");

        public static AutoPlayModel Current { get; private set; }
        public static ModelTrust CurrentTrust { get; private set; }
        public static string CurrentId { get; private set; }
        public static string CurrentFingerprint { get; private set; }
        public static string LastError { get; private set; }
        public static event Action Changed;

        public static List<Entry> Entries { get; private set; } = new List<Entry>();

        /// <summary>生成回放时用: 没有模型就抛异常 (调用前应先检查 Current)</summary>
        public static AutoPlayModel Require()
        {
            var m = Current;
            if (m == null) throw new InvalidOperationException("尚未載入 AutoPlay 模型");
            return m;
        }

        public static void Initialize()
        {
            try
            {
                Directory.CreateDirectory(ModelsDir);
                Refresh();
                string want = Cfg.selectedModel;
                var pick = Entries.FirstOrDefault(e => e.id == want) ?? Entries.FirstOrDefault();
                if (pick != null) Select(pick.id);
                else
                {
                    LastError = "Models 資料夾裡沒有模型: 請在設定裡用自己的回放訓練, 或下載作者的 .apmodel 放進 " + ModelsDir;
                    Plugin.Log?.Warn("[AutoPlay] " + LastError);
                }
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Plugin.Log?.Error($"[AutoPlay] ModelManager init failed: {ex}");
            }
        }

        public static void Refresh()
        {
            var list = new List<Entry>();
            if (Directory.Exists(ModelsDir))
            {
                foreach (var f in Directory.GetFiles(ModelsDir, "*" + ApModelFile.Extension).OrderBy(x => x))
                    list.Add(new Entry { id = Path.GetFileName(f), path = f, packed = true });
                foreach (var d in Directory.GetDirectories(ModelsDir).OrderBy(x => x))
                {
                    if (d.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)) continue;   // 自动训练的备份
                    string mj = Path.Combine(d, "model.json");
                    if (File.Exists(mj)) list.Add(new Entry { id = Path.GetFileName(d) + "/", path = mj, packed = false });
                }
            }
            foreach (var e in list) e.displayName = e.packed ? Path.GetFileNameWithoutExtension(e.id) : e.id.TrimEnd('/') + " (未打包)";
            Entries = list;
        }

        public static bool Select(string id)
        {
            var e = Entries.FirstOrDefault(x => x.id == id);
            if (e == null) { LastError = "找不到模型: " + id; return false; }
            try
            {
                string json;
                ModelTrust trust;
                if (e.packed) json = ApModelFile.Unpack(File.ReadAllBytes(e.path), out trust);
                else { json = File.ReadAllText(e.path); trust = ModelTrust.Unpacked; }
                var model = JsonConvert.DeserializeObject<AutoPlayModel>(json);
                string err = model == null ? "模型文件是空的" : model.Validate();
                if (err != null) throw new InvalidDataException(err);

                Current = model;
                CurrentTrust = trust;
                CurrentId = e.id;
                CurrentFingerprint = ApModelFile.Fingerprint(json);
                LastError = null;
                Cfg.selectedModel = e.id;
                SaveSettings(Cfg);
                Plugin.Log?.Info($"[AutoPlay] Model loaded: {e.id} ({trust}, {model.meta.replayCount} replays, {model.templates.groups.Count} templates, fp {CurrentFingerprint})");
                if (trust == ModelTrust.BadSignature) Plugin.Log?.Warn("[AutoPlay] 這個模型的簽名驗證失敗 (被修改過, 或不是作者簽的)");
                Changed?.Invoke();
                return true;
            }
            catch (Exception ex)
            {
                LastError = $"載入模型失敗 ({e.id}): {ex.Message}";
                Plugin.Log?.Error("[AutoPlay] " + LastError);
                return false;
            }
        }

        /// <summary>保存本机训练结果 (未打包), 返回 id</summary>
        public static string SaveUnpacked(AutoPlayModel model, string report)
        {
            string name = SafeName(model.meta.name);
            string dir = Path.Combine(ModelsDir, name);
            for (int i = 2; Directory.Exists(dir); i++) dir = Path.Combine(ModelsDir, $"{name}-{i}");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "model.json"), JsonConvert.SerializeObject(model, Formatting.Indented));
            if (!string.IsNullOrEmpty(report)) File.WriteAllText(Path.Combine(dir, "train-report.txt"), report);
            Refresh();
            return Path.GetFileName(dir) + "/";
        }

        /// <summary>自动训练: 覆盖固定的 my-auto/ 资料夹, 旧的留一份 my-auto.bak/</summary>
        public static string SaveAuto(AutoPlayModel model, string report)
        {
            string dir = Path.Combine(ModelsDir, AutoModelName);
            string bak = Path.Combine(ModelsDir, AutoModelName + ".bak");
            if (Directory.Exists(dir))
            {
                if (Directory.Exists(bak)) Directory.Delete(bak, true);
                Directory.Move(dir, bak);
            }
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "model.json"), JsonConvert.SerializeObject(model, Formatting.Indented));
            if (!string.IsNullOrEmpty(report)) File.WriteAllText(Path.Combine(dir, "train-report.txt"), report);
            Refresh();
            return AutoModelName + "/";
        }

        public static bool AutoModelExists => File.Exists(Path.Combine(ModelsDir, AutoModelName, "model.json"));

        /// <summary>把未打包的模型打包成 .apmodel (不签名), 返回新文件 id</summary>
        public static string Pack(string id)
        {
            var e = Entries.FirstOrDefault(x => x.id == id);
            if (e == null) throw new InvalidOperationException("找不到模型: " + id);
            if (e.packed) throw new InvalidOperationException("這個模型已經是 .apmodel 了");
            string json = File.ReadAllText(e.path);
            var model = JsonConvert.DeserializeObject<AutoPlayModel>(json);
            string err = model == null ? "模型文件是空的" : model.Validate();
            if (err != null) throw new InvalidDataException(err);
            string name = SafeName(id.TrimEnd('/'));
            string path = Path.Combine(ModelsDir, name + ApModelFile.Extension);
            for (int i = 2; File.Exists(path); i++) path = Path.Combine(ModelsDir, $"{name}-{i}{ApModelFile.Extension}");
            File.WriteAllBytes(path, ApModelFile.Pack(json));
            Refresh();
            return Path.GetFileName(path);
        }

        public static string TrustLabel(ModelTrust t)
        {
            switch (t)
            {
                case ModelTrust.Official: return "官方 (作者簽名 ✓)";
                case ModelTrust.Unsigned: return "使用者打包 (未簽名)";
                case ModelTrust.Unpacked: return "本機訓練 (未打包)";
                case ModelTrust.BadSignature: return "簽名驗證失敗!";
                default: return t.ToString();
            }
        }

        static string SafeName(string s)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            s = s.Trim().Trim('.');
            return string.IsNullOrEmpty(s) ? "model" : s;
        }

        static Settings LoadSettings()
        {
            try { if (File.Exists(SettingsPath)) return JsonConvert.DeserializeObject<Settings>(File.ReadAllText(SettingsPath)) ?? new Settings(); }
            catch { }
            return new Settings();
        }

        static void SaveSettings(Settings s)
        {
            try { Directory.CreateDirectory(RootDir); File.WriteAllText(SettingsPath, JsonConvert.SerializeObject(s, Formatting.Indented)); }
            catch (Exception ex) { Plugin.Log?.Warn($"[AutoPlay] Failed to save settings: {ex.Message}"); }
        }
    }
}
