using System;
using System.Collections;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoReplay.Core;
using UnityEngine;
using UnityEngine.SceneManagement;
using Zenject;

namespace AutoReplay
{
    /// <summary>訓練設定 (存在 UserData/AutoPlay/settings.json)</summary>
    public sealed class TrainSettings
    {
        public const int AllDays = 730;      // 起始滑桿拉到底 = 不限日期

        public int fromDaysAgo = 30;         // 從幾天前開始
        public int toDaysAgo = 0;            // 到幾天前 (0 = 今天)
        public int perMapCap = 3;
        public int maxReplays = 300;
        public float minStars = 0f;
        public bool lookupStars = true;
        public bool rankedOnly = false;      // 只用 BL 或 SS ranked 的譜
        public bool preferAcc = false;       // 同譜取分數最高的, 超過上限時取 acc 最高的
        public bool autoFollow = false;      // 持續追蹤最新回放, 在背景自動重訓
        public int autoEveryN = 10;          // 每多幾份新回放重訓一次
        public long lastAutoTrainUnix;
        public string[] excludeMaps = new string[0];   // 另外不拿來訓練的譜面 hash (惡搞譜 Mope Mope / DADADADA 已內建排除)

        public DateTime? FromUtc => fromDaysAgo >= AllDays ? (DateTime?)null : DateTime.Now.Date.AddDays(-fromDaysAgo).ToUniversalTime();
        public DateTime? ToUtc => toDaysAgo <= 0 ? (DateTime?)null : DateTime.Now.Date.AddDays(1 - toDaysAgo).ToUniversalTime().AddSeconds(-1);
    }

    /// <summary>
    /// 訓練的共用入口 (設定頁按鈕和自動訓練). 同一時間只跑一個訓練.
    /// 只用本人的回放: 先向平臺取得目前登入的玩家 ID, 每份回放都要比對.
    /// </summary>
    public static class TrainingService
    {
        public static bool Busy { get; private set; }
        public static string Progress { get; private set; } = "";
        public static event Action StateChanged;

        static string _playerId;

        public static async Task<string> GetPlayerIdAsync()
        {
            if (!string.IsNullOrEmpty(_playerId)) return _playerId;
            try
            {
                var platform = ProjectContext.Instance.Container.TryResolve<IPlatformUserModel>();
                if (platform == null) return null;
                var info = await platform.GetUserInfo(CancellationToken.None);
                _playerId = info?.platformUserId;
                return _playerId;
            }
            catch (Exception ex)
            {
                Plugin.Log?.Warn($"[AutoPlay] GetUserInfo failed: {ex.Message}");
                return null;
            }
        }

        public static SelectionOptions Selection(TrainSettings t, string playerId, bool auto) => new SelectionOptions
        {
            replaysDir = ModelManager.ReplaysDir,
            playerId = playerId,
            fromUtc = t.FromUtc,
            toUtc = auto ? null : t.ToUtc,     // 自動模式永遠到最新
            perMapCap = t.perMapCap,
            maxReplays = t.maxReplays,
            minStars = t.minStars,
            rankedOnly = t.rankedOnly,
            preferAcc = t.preferAcc,
            excludeMaps = t.excludeMaps,
        };

        /// <summary>訓練並儲存. auto=true 寫到 my-auto/, 否則寫新的 my-日期/ 資料夾. 返回模型 id.</summary>
        public static async Task<string> TrainAsync(bool auto, CancellationToken cancel)
        {
            if (Busy) throw new InvalidOperationException("已經有訓練在進行中");
            Busy = true; Progress = "正在確認玩家身份…"; StateChanged?.Invoke();
            try
            {
                string pid = await GetPlayerIdAsync();
                if (string.IsNullOrEmpty(pid)) throw new InvalidOperationException("無法取得玩家 ID, 已取消 (只能用自己的回放訓練)");

                var t = ModelManager.Train;
                var opts = new TrainOptions
                {
                    selection = Selection(t, pid, auto),
                    modelName = auto ? ModelManager.AutoModelName : "my-" + DateTime.Now.ToString("yyyyMMdd-HHmm"),
                    stars = t.lookupStars ? ModelManager.Stars : null,
                    network = t.lookupStars,
                    modVersion = Plugin.Version,
                    mode = auto ? "auto" : "manual",
                    rollingDays = auto ? Math.Min(t.fromDaysAgo, TrainSettings.AllDays) : 0,
                    progress = s => Progress = s,
                    cancel = cancel,
                };
                string report = null;
                var task = Task.Run(() =>
                {
                    var th = Thread.CurrentThread; var prio = th.Priority;
                    th.Priority = System.Threading.ThreadPriority.BelowNormal;   // 不要搶遊戲的 CPU
                    try { return ModelTrainer.Train(opts, out report); }
                    finally { th.Priority = prio; }
                });
                while (!task.IsCompleted) { StateChanged?.Invoke(); await Task.Delay(200); }
                var model = await task;

                string id = auto ? ModelManager.SaveAuto(model, report) : ModelManager.SaveUnpacked(model, report);
                if (auto)
                {
                    t.lastAutoTrainUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    ModelManager.SaveTrainSettings();
                    // 只有正在用自動模型 (或沒有模型) 時才切換, 不搶玩家自己選的模型
                    if (ModelManager.Current == null || ModelManager.CurrentId == id) ModelManager.Select(id);
                    else ModelManager.Refresh();
                }
                else ModelManager.Select(id);
                Progress = $"訓練完成: Models/{id} ({model.meta.replayCount} 份回放, {model.meta.swingCount} 次揮刀)";
                return id;
            }
            catch (Exception ex)
            {
                var e = ex is AggregateException ae && ae.InnerException != null ? ae.InnerException : ex;
                Progress = e is OperationCanceledException ? "訓練已取消" : "訓練失敗: " + e.Message;
                if (!(e is OperationCanceledException)) Plugin.Log?.Error($"[AutoPlay] Training failed: {e}");
                throw e;
            }
            finally
            {
                Busy = false;
                StateChanged?.Invoke();
            }
        }

        public static bool InGameplay => SceneManager.GetSceneByName("GameCore").isLoaded;
    }

    /// <summary>
    /// 持續追蹤: 每 30 秒檢查一次, 在選單閒置時, 本人的新回放累積到 N 份就在背景重訓 my-auto/.
    /// 開始打歌就取消訓練, 之後再試.
    /// </summary>
    public sealed class AutoTrainer : MonoBehaviour
    {
        public static event Action StatusChanged;
        static string _status = "";
        public static string Status { get => _status; private set { _status = value; StatusChanged?.Invoke(); } }
        static AutoTrainer _inst;

        public static void Ensure()
        {
            if (_inst != null) return;
            var go = new GameObject("AutoPlay.AutoTrainer");
            DontDestroyOnLoad(go);
            _inst = go.AddComponent<AutoTrainer>();
        }

        CancellationTokenSource _cts;
        bool _checking;
        float _retryAfter;    // 失敗後 10 分鐘內不再自動重試

        void Start() => StartCoroutine(Loop());

        IEnumerator Loop()
        {
            var wait = new WaitForSecondsRealtime(2f);
            float next = Time.realtimeSinceStartup + 20f;   // 進遊戲先等一下
            while (true)
            {
                yield return wait;
                if (_cts != null && TrainingService.InGameplay) { _cts.Cancel(); Status = "開始打歌, 自動訓練已暫停"; }
                if (Time.realtimeSinceStartup < next) continue;
                next = Time.realtimeSinceStartup + 30f;
                if (!_checking) _ = Check();
            }
        }

        async Task Check()
        {
            var t = ModelManager.Train;
            if (!t.autoFollow) { Status = "自動訓練: 關閉"; return; }
            if (TrainingService.Busy || TrainingService.InGameplay || Time.realtimeSinceStartup < _retryAfter) return;
            _checking = true;
            try
            {
                string pid = await TrainingService.GetPlayerIdAsync();
                if (string.IsNullOrEmpty(pid)) { Status = "自動訓練: 無法取得玩家 ID"; return; }
                long since = t.lastAutoTrainUnix;
                int fresh = await Task.Run(() => ReplaySelector.ScanHeaders(ModelManager.ReplaysDir)
                                                               .Count(e => e.info.playerId == pid && e.info.timestamp > since));
                bool need = !ModelManager.AutoModelExists || fresh >= Math.Max(1, t.autoEveryN);
                Status = ModelManager.AutoModelExists
                    ? $"自動訓練: 上次之後有 {fresh} 份新回放 (每 {t.autoEveryN} 份重訓)"
                    : "自動訓練: 還沒有 my-auto 模型, 準備訓練";
                if (!need || TrainingService.InGameplay) return;

                _cts = new CancellationTokenSource();
                Status = "自動訓練中…";
                try
                {
                    await TrainingService.TrainAsync(true, _cts.Token);
                    Status = $"自動訓練完成 ({DateTime.Now:HH:mm})";
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Status = "自動訓練失敗: " + ex.Message; _retryAfter = Time.realtimeSinceStartup + 600f; }
                finally { _cts.Dispose(); _cts = null; }
            }
            catch (Exception ex) { Status = "自動訓練失敗: " + ex.Message; }
            finally { _checking = false; }
        }
    }
}
