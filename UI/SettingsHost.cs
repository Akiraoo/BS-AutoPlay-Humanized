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
using Zenject;

namespace AutoReplay.UI
{
    /// <summary>设定 > Mod Settings > AutoPlay: 选模型 / 用自己的回放训练 / 打包成 .apmodel</summary>
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
            };
            ModelManager.Changed += () => Instance.RefreshAll();
        }

        [UIComponent("model-dropdown")] private DropDownListSetting _dropdown = null;

        private bool _busy;
        private string _taskStatus = "";

        // ---------- 下拉选单 ----------
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
                if (!ModelManager.Select(e.id)) SetTask(ModelManager.LastError);
            }
            RefreshAll();
        }

        // ---------- 状态 ----------
        [UIValue("model-status")]
        public string ModelStatus
        {
            get
            {
                var m = ModelManager.Current;
                if (m == null) return ModelManager.LastError ?? "沒有載入模型";
                return $"{ModelManager.TrustLabel(ModelManager.CurrentTrust)} · {m.meta.name}" +
                       (string.IsNullOrEmpty(m.meta.author) ? "" : $" by {m.meta.author}") +
                       $"\n{m.meta.replayCount} 份回放 · {m.meta.swingCount} 次揮刀 · {m.templates.groups.Count} 組模板 · 指紋 {ModelManager.CurrentFingerprint}";
            }
        }

        [UIValue("task-status")] public string TaskStatus => _taskStatus;
        [UIValue("can-train")] public bool CanTrain => !_busy;
        [UIValue("can-pack")]
        public bool CanPack => !_busy && ModelManager.Entries.Any(x => x.id == ModelManager.CurrentId && !x.packed);

        // ---------- 按钮 ----------
        [UIAction("refresh-click")]
        private void OnRefresh()
        {
            ModelManager.Refresh();
            if (ModelManager.Current == null && ModelManager.Entries.Count > 0) ModelManager.Select(ModelManager.Entries[0].id);
            RefreshAll();
            SetTask($"找到 {ModelManager.Entries.Count} 個模型");
        }

        [UIAction("pack-click")]
        private void OnPack()
        {
            try
            {
                string id = ModelManager.Pack(ModelManager.CurrentId);
                RefreshAll();
                SetTask($"已打包: Models/{id}");
            }
            catch (Exception ex) { SetTask("打包失敗: " + ex.Message); }
        }

        [UIAction("train-click")]
        private async void OnTrain()
        {
            if (_busy) return;
            _busy = true;
            RefreshAll();
            try
            {
                SetTask("正在確認玩家身份…");
                string playerId = await GetPlayerIdAsync();
                if (string.IsNullOrEmpty(playerId)) { SetTask("無法取得玩家 ID, 已取消\n(只能用自己的回放訓練)"); return; }

                string progress = "開始訓練…";
                var opts = new TrainOptions
                {
                    replaysDir = ModelManager.ReplaysDir,
                    playerId = playerId,
                    maxReplays = 80,
                    modelName = "my-" + DateTime.Now.ToString("yyyyMMdd-HHmm"),
                    author = "",
                    progress = s => progress = s,
                };
                string report = null;
                var task = Task.Run(() => ModelTrainer.Train(opts, out report));
                while (!task.IsCompleted)
                {
                    SetTask(progress);
                    await Task.Delay(200);
                }
                AutoPlayModel model = await task;   // 失败会在这里抛出
                string id = ModelManager.SaveUnpacked(model, report);
                ModelManager.Select(id);
                SetTask($"訓練完成: Models/{id} ({model.meta.replayCount} 份回放, {model.meta.swingCount} 次揮刀)\n想分享可以按「打包成 .apmodel」");
            }
            catch (Exception ex)
            {
                var e = ex is AggregateException ae && ae.InnerException != null ? ae.InnerException : ex;
                Plugin.Log?.Error($"[AutoPlay] Training failed: {e}");
                SetTask("訓練失敗: " + e.Message);
            }
            finally
            {
                _busy = false;
                RefreshAll();
            }
        }

        // 本人验证: 只用和目前登入平台帐号同一个玩家 ID 的回放
        static async Task<string> GetPlayerIdAsync()
        {
            try
            {
                var platform = ProjectContext.Instance.Container.TryResolve<IPlatformUserModel>();
                if (platform == null) return null;
                var info = await platform.GetUserInfo(CancellationToken.None);
                return info?.platformUserId;
            }
            catch (Exception ex)
            {
                Plugin.Log?.Warn($"[AutoPlay] GetUserInfo failed: {ex.Message}");
                return null;
            }
        }

        // ---------- 刷新 ----------
        void SetTask(string s)
        {
            _taskStatus = s ?? "";
            Notify(nameof(TaskStatus));
        }

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
            Notify(nameof(CanTrain));
            Notify(nameof(CanPack));
        }

        void Notify([CallerMemberName] string name = null)
        {
            try { PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)); } catch { }
        }
    }
}
