using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using HarmonyLib;
using Zenject;
using BeatLeader.Models;
using BeatLeader.Models.Replay;
using BeatLeader.Replayer;

namespace AutoReplay
{
    public class AutoReplayManager
    {
        private static AutoReplayManager _instance;
        public static AutoReplayManager Instance => _instance ??= new AutoReplayManager();

        private bool _isProcessing = false;

        public async void StartAutoPlay(StandardLevelDetailView view = null)
        {
            if (_isProcessing)
            {
                Plugin.Log?.Warn("AutoPlay is already generating a replay. Please wait...");
                return;
            }

            if (ModelManager.Current == null)
            {
                Plugin.Log?.Warn("[AutoPlay] 沒有載入模型, 無法生成回放. " + (ModelManager.LastError ?? "") +
                                 " 请到 设定 > Mod Settings > AutoPlay 用自己的回放训练, 或把 .apmodel 放进 " + ModelManager.ModelsDir);
                return;
            }

            try
            {
                _isProcessing = true;

                BeatmapLevel beatmapLevel = null;
                BeatmapKey beatmapKey = default;

                // 1. First priority: from StandardLevelDetailView directly
                if (view == null)
                {
                    view = Resources.FindObjectsOfTypeAll<StandardLevelDetailView>().FirstOrDefault();
                }

                if (view != null)
                {
                    var levelField = typeof(StandardLevelDetailView).GetField("_beatmapLevel", BindingFlags.NonPublic | BindingFlags.Instance);
                    beatmapLevel = levelField?.GetValue(view) as BeatmapLevel;
                    beatmapKey = view.beatmapKey;
                }

                // 2. Second priority: from BeatLeader.LeaderboardState
                if (beatmapLevel == null)
                {
                    var stateType = AccessTools.TypeByName("BeatLeader.LeaderboardState");
                    if (stateType != null)
                    {
                        var levelProp = stateType.GetProperty("SelectedBeatmapLevel", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                        var keyProp = stateType.GetProperty("SelectedBeatmapKey", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                        beatmapLevel = (BeatmapLevel)levelProp?.GetValue(null);
                        beatmapKey = (BeatmapKey)keyProp?.GetValue(null);
                    }
                }

                if (beatmapLevel == null)
                {
                    Plugin.Log?.Warn("No beatmap currently selected!");
                    return;
                }

                Plugin.Log?.Info($"Generating AutoPlay for: {beatmapLevel.songName} [{beatmapKey.difficulty}]");

                BeatmapLevelsModel levelsModel = null;
                try
                {
                    levelsModel = ProjectContext.Instance.Container.Resolve<BeatmapLevelsModel>();
                }
                catch { }

                if (levelsModel == null && ReplayerMenuLoader.Instance != null)
                {
                    var field = typeof(ReplayerMenuLoader).GetField("_levelsModel", BindingFlags.NonPublic | BindingFlags.Instance);
                    levelsModel = field?.GetValue(ReplayerMenuLoader.Instance) as BeatmapLevelsModel;
                }

                if (levelsModel == null)
                {
                    Plugin.Log?.Error("Could not find BeatmapLevelsModel!");
                    return;
                }

                var result = await levelsModel.LoadBeatmapLevelDataAsync(beatmapLevel.levelID, BeatmapLevelDataVersion.Original, CancellationToken.None);
                if (result.isError || result.beatmapLevelData == null)
                {
                    Plugin.Log?.Error("Failed to load beatmap level data!");
                    return;
                }

                PlayerDataModel playerDataModel = null;
                try { playerDataModel = ProjectContext.Instance.Container.Resolve<PlayerDataModel>(); } catch { }
                if (playerDataModel == null) playerDataModel = Resources.FindObjectsOfTypeAll<PlayerDataModel>().FirstOrDefault();
                GameplayModifiers modifiers = playerDataModel?.playerData?.gameplayModifiers ?? GameplayModifiers.noModifiers;

                // 玩家是不是左手模式: 是的話回放也按左手模式生成 (譜面映象 + 回放標記為左手),
                // 回放器啟動時會按回放裡的 leftHanded 重新設定遊戲, 所以這兩處必須一致.
                bool leftHanded = playerDataModel?.playerData?.playerSpecificSettings?.leftHanded ?? false;
                Plugin.Log?.Info($"Player left-handed mode: {leftHanded}");

                var dataLoader = new BeatmapDataLoader();
                var beatmapData = await dataLoader.LoadBeatmapDataAsync(
                    result.beatmapLevelData,
                    beatmapKey,
                    beatmapLevel.beatsPerMinute,
                    false,
                    null,
                    null,
                    BeatmapLevelDataVersion.Original,
                    modifiers,
                    new PlayerSpecificSettings(),
                    false
                );

                if (beatmapData == null)
                {
                    Plugin.Log?.Error("Failed to parse beatmap data!");
                    return;
                }

                string hash = beatmapLevel.levelID.StartsWith("custom_level_")
                    ? beatmapLevel.levelID.Substring("custom_level_".Length)
                    : beatmapLevel.levelID;

                var replay = AutoReplayGenerator.GenerateReplay(beatmapData, beatmapLevel, beatmapKey, hash, modifiers, leftHanded);
                ReplayFileSaver.SaveReplay(replay, hash, beatmapKey.difficulty.ToString());

                var bot = ModelManager.Bot;
                var dummyPlayer = new Player
                {
                    id = "Auto Play",
                    name = string.IsNullOrWhiteSpace(bot.name) ? "Auto Play" : bot.name,
                    avatar = bot.avatar,
                    country = string.IsNullOrWhiteSpace(bot.country) ? null : bot.country.Trim().ToUpperInvariant()
                };

                Plugin.Log?.Info($"Starting BeatLeader Replayer with modifiers: {AutoReplayMath.EncodeModifiers(modifiers)}");
                var replayerSettings = ReplayerSettings.UserSettings ?? new ReplayerSettings();
                replayerSettings.IgnoreModifiers = false;
                // BeatLeader 0.10.x: 簽名變為 (replay, player, optionalData, settings, finishCallback, token)
                var started = await ReplayerMenuLoader.Instance.StartReplayAsync(replay, dummyPlayer, settings: replayerSettings);
                if (!started)
                {
                    Plugin.Log?.Error("BeatLeader replayer failed to start (beatmap could not be loaded).");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.Error($"Error during AutoPlay generation: {ex}");
            }
            finally
            {
                _isProcessing = false;
            }
        }
    }
}


