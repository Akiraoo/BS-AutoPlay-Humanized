using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AutoReplay.Core;
using BeatSaberMarkupLanguage.Attributes;
using BeatSaberMarkupLanguage.Components.Settings;
using BeatSaberMarkupLanguage.Settings;
using BeatSaberMarkupLanguage.Util;

namespace AutoReplay.UI
{
    /// <summary>设定 > 模组设定 > AutoPlay: 三个分页 (模型 / 训练 / 自动训练)</summary>
    public class SettingsHost : INotifyPropertyChanged
    {
        public static readonly SettingsHost Instance = new SettingsHost();
        public event PropertyChangedEventHandler PropertyChanged;

        static bool _registered;

        public static void Register()
        {
            if (_registered) return;
            _registered = true;
            MainMenuAwaiter.MainMenuInitializing += () =>
            {
                try { BSMLSettings.Instance.AddSettingsMenu("AutoPlay", "AutoReplay.UI.Settings.bsml", Instance); }
                catch (Exception ex) { Plugin.Log?.Error($"[AutoPlay] Settings menu failed: {ex}"); }
                try { AutoTrainer.Ensure(); } catch (Exception ex) { Plugin.Log?.Error($"[AutoPlay] AutoTrainer failed: {ex}"); }
                Instance.SchedulePreview();
            };
            ModelManager.Changed += () => Instance.RefreshAll();
            TrainingService.StateChanged += () => Instance.OnTrainingState();
            AutoTrainer.StatusChanged += () => Instance.Notify(nameof(AutoStatus));
        }

        static TrainSettings T => ModelManager.Train;

        [UIComponent("model-dropdown")] private DropDownListSetting _dropdown = null;

        private string _packStatus = "";
        private string _taskStatus = "";
        private string _preview = "";

        // =============================== 模型 ===============================
        [UIValue("model-choices")]
        public List<object> ModelChoices => ModelManager.Entries.Select(e => (object)e.displayName).DefaultIfEmpty("(沒有模型)").ToList();

        [UIValue("selected-model")]
        public object SelectedModel
        {
            get
            {
                var e = ModelManager.Entries.FirstOrDefault(x => x.id == ModelManager.CurrentId);
                return e != null ? e.displayName : ModelChoices.FirstOrDefault();
            }
            set { }
        }

        [UIAction("model-changed")]
        private void OnModelChanged(object value)
        {
            var e = ModelManager.Entries.FirstOrDefault(x => x.displayName == value as string);
            if (e != null && e.id != ModelManager.CurrentId)
            {
                if (!ModelManager.Select(e.id)) SetPack(ModelManager.LastError);
            }
            RefreshAll();
        }

        [UIValue("model-status")]
        public string ModelStatus
        {
            get
            {
                var m = ModelManager.Current;
                if (m == null) return ModelManager.LastError ?? "沒有載入模型";
                string s = ModelManager.TrustLabel(ModelManager.CurrentTrust) + " · 指紋 " + ModelManager.CurrentFingerprint;
                if (ModelManager.CurrentTrust == ModelTrust.Unsigned || ModelManager.CurrentTrust == ModelTrust.Unpacked)
                    s += " · 未簽名, 資訊僅供參考";
                return s;
            }
        }

        [UIValue("model-info")]
        public string ModelInfo
        {
            get
            {
                var m = ModelManager.Current;
                if (m == null) return "";
                try { return string.Join("\n", ModelInfoText.Lines(m)); }
                catch (Exception ex) { return "模型資訊讀取失敗: " + ex.Message; }
            }
        }

        [UIValue("pack-status")] public string PackStatus => _packStatus;

        [UIAction("info-click")]
        private void OnInfo() => Notify(nameof(ModelInfo));

        [UIValue("can-pack")]
        public bool CanPack => !TrainingService.Busy && ModelManager.Entries.Any(x => x.id == ModelManager.CurrentId && !x.packed);

        [UIAction("refresh-click")]
        private void OnRefresh()
        {
            ModelManager.Refresh();
            if (ModelManager.Current == null && ModelManager.Entries.Count > 0) ModelManager.Select(ModelManager.Entries[0].id);
            RefreshAll();
            SetPack($"找到 {ModelManager.Entries.Count} 個模型");
            SchedulePreview();
        }

        [UIAction("pack-click")]
        private void OnPack()
        {
            try
            {
                string id = ModelManager.Pack(ModelManager.CurrentId);
                RefreshAll();
                SetPack($"已打包: Models/{id}");
            }
            catch (Exception ex) { SetPack("打包失敗: " + ex.Message); }
        }

        // =============================== 訓練 ===============================
        [UIValue("from-days")]
        public float FromDays { get => T.fromDaysAgo; set { T.fromDaysAgo = Clamp(value, 1, TrainSettings.AllDays); Changed(); } }

        [UIValue("to-days")]
        public float ToDays { get => T.toDaysAgo; set { T.toDaysAgo = Clamp(value, 0, TrainSettings.AllDays - 1); Changed(); } }

        [UIValue("per-map")]
        public float PerMap { get => T.perMapCap; set { T.perMapCap = Clamp(value, 1, 10); Changed(); } }

        [UIValue("max-replays")]
        public float MaxReplays { get => T.maxReplays; set { T.maxReplays = Clamp(value, 50, 1000); Changed(); } }

        [UIValue("min-stars")]
        public float MinStars { get => T.minStars; set { T.minStars = Math.Max(0f, value); Changed(); } }

        [UIValue("lookup-stars")]
        public bool LookupStars { get => T.lookupStars; set { T.lookupStars = value; Changed(); } }

        [UIValue("ranked-only")]
        public bool RankedOnly { get => T.rankedOnly; set { T.rankedOnly = value; Changed(); } }

        [UIValue("prefer-acc")]
        public bool PreferAcc { get => T.preferAcc; set { T.preferAcc = value; Changed(); } }

        [UIAction("fmt-from")]
        private string FmtFrom(float v)
        {
            int d = (int)Math.Round(v);
            return d >= TrainSettings.AllDays ? "不限日期" : $"{DateTime.Now.Date.AddDays(-d):yyyy-MM-dd} ({d} 天前)";
        }

        [UIAction("fmt-to")]
        private string FmtTo(float v)
        {
            int d = (int)Math.Round(v);
            return d <= 0 ? "今天" : $"{DateTime.Now.Date.AddDays(-d):yyyy-MM-dd} ({d} 天前)";
        }

        [UIAction("fmt-count")] private string FmtCount(float v) => $"{(int)Math.Round(v)} 份";
        [UIAction("fmt-stars")] private string FmtStars(float v) => v <= 0f ? "不限" : $"{v:0.0} ★";

        [UIValue("preview")] public string Preview => _preview;
        [UIValue("task-status")] public string TaskStatus => _taskStatus;
        [UIValue("can-train")] public bool CanTrain => !TrainingService.Busy;

        [UIAction("train-click")]
        private async void OnTrain()
        {
            if (TrainingService.Busy) return;
            try { await TrainingService.TrainAsync(false, CancellationToken.None); }
            catch { }   // 讯息已经在 TrainingService.Progress
            SetTask(TrainingService.Progress + (ModelManager.CurrentTrust == ModelTrust.Unpacked ? "\n想分享可以到「模型」頁按「打包成 .apmodel」" : ""));
            RefreshAll();
        }

        // =============================== 自動 ===============================
        [UIValue("auto-follow")]
        public bool AutoFollow { get => T.autoFollow; set { T.autoFollow = value; ModelManager.SaveTrainSettings(); } }

        [UIValue("auto-every")]
        public float AutoEvery { get => T.autoEveryN; set { T.autoEveryN = Clamp(value, 1, 50); ModelManager.SaveTrainSettings(); } }

        [UIValue("auto-status")] public string AutoStatus => AutoTrainer.Status;

        // =============================== 預覽 ===============================
        int _previewVersion;

        void Changed()
        {
            ModelManager.SaveTrainSettings();
            SchedulePreview();
        }

        /// <summary>设定变了: 等 0.4 秒没再变, 在背景算每一步筛完剩几份 (星数只看快取, 不连网)</summary>
        async void SchedulePreview()
        {
            int v = ++_previewVersion;
            await Task.Delay(400);
            if (v != _previewVersion) return;
            try
            {
                string pid = await TrainingService.GetPlayerIdAsync();
                if (string.IsNullOrEmpty(pid)) { SetPreview("無法取得玩家 ID"); return; }
                var opts = TrainingService.Selection(T, pid, false);
                var stars = T.lookupStars ? ModelManager.Stars : null;
                SetPreview("計算中…");
                var r = await Task.Run(() => ReplaySelector.Select(opts, stars, false));
                if (v != _previewVersion) return;
                string s = r.Summary().Replace(" → 去重複後", "\n→ 去重複後");
                bool needStars = T.minStars > 0 || T.rankedOnly || T.preferAcc;
                if (needStars && !T.lookupStars) s += "\n(沒有開啟星數查詢, 星數 / ranked / acc 篩選不會生效)";
                else if (needStars) s += "\n(預覽只用快取, 實際數量以訓練時查詢為準)";
                SetPreview(s);
            }
            catch (Exception ex) { SetPreview("預覽失敗: " + ex.Message); }
        }

        // =============================== 刷新 ===============================
        static int Clamp(float v, int lo, int hi) => Math.Max(lo, Math.Min(hi, (int)Math.Round(v)));

        void OnTrainingState()
        {
            if (TrainingService.Busy) SetTask(Short(TrainingService.Progress));
            Notify(nameof(CanTrain));
            Notify(nameof(CanPack));
        }

        // 进度讯息可能带很长的文件名, 截短免得超出版面
        static string Short(string s) => s == null || s.Length <= 60 ? s : s.Substring(0, 57) + "…";

        void SetPack(string s) { _packStatus = s ?? ""; Notify(nameof(PackStatus)); }
        void SetTask(string s) { _taskStatus = s ?? ""; Notify(nameof(TaskStatus)); }
        void SetPreview(string s) { _preview = s ?? ""; Notify(nameof(Preview)); }

        void RefreshAll()
        {
            try
            {
                if (_dropdown != null)
                {
                    _dropdown.Values = ModelChoices;
                    _dropdown.UpdateChoices();
                    _dropdown.Value = SelectedModel;
                }
            }
            catch (Exception ex) { Plugin.Log?.Warn($"[AutoPlay] Dropdown refresh failed: {ex.Message}"); }
            Notify(nameof(ModelStatus));
            Notify(nameof(ModelInfo));
            Notify(nameof(CanTrain));
            Notify(nameof(CanPack));
        }

        void Notify([CallerMemberName] string name = null)
        {
            try { PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)); } catch { }
        }
    }
}
