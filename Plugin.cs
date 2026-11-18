using System.IO;
using System.Reflection;
using HarmonyLib;
using IPA;
using IPALogger = IPA.Logging.Logger;

namespace BotPlay
{
    [Plugin(RuntimeOptions.SingleStartInit)]
    public class Plugin
    {
        public static Plugin Instance { get; private set; }
        public static IPALogger Log { get; private set; }
        public const string HarmonyId = "com.botplay.beatsaber";
        public const string Version = "1.3.0";
        private Harmony _harmony;

        [Init]
        public void Init(IPALogger logger)
        {
            Instance = this;
            Log = logger;
            Log.Info("BotPlay initialized.");
            MigrateOldData();
        }

        /// <summary>舊版 (AutoPlay / AutoReplay) 的 UserData 資料夾搬到 UserData/BotPlay</summary>
        static void MigrateOldData()
        {
            try
            {
                string ud = Path.Combine(System.Environment.CurrentDirectory, "UserData");
                string dst = Path.Combine(ud, "BotPlay");
                string old = Path.Combine(ud, "AutoPlay");
                if (Directory.Exists(old) && !Directory.Exists(dst))
                {
                    Directory.Move(old, dst);
                    Log.Info("Moved UserData/AutoPlay to UserData/BotPlay");
                }
                string gif = Path.Combine(ud, "AutoReplay", "bs-pepe.gif");
                if (File.Exists(gif))
                {
                    Directory.CreateDirectory(dst);
                    string to = Path.Combine(dst, "bs-pepe.gif");
                    if (!File.Exists(to)) File.Move(gif, to);
                }
            }
            catch (System.Exception ex) { Log.Warn($"Migrating old UserData failed: {ex.Message}"); }
        }

        [OnStart]
        public void OnApplicationStart()
        {
            try
            {
                _harmony = new Harmony(HarmonyId);
                _harmony.PatchAll(Assembly.GetExecutingAssembly());
                Log.Info("BotPlay Harmony patches applied successfully.");
            }
            catch (System.Exception ex)
            {
                Log.Error($"BotPlay Harmony patching failed: {ex}");
            }

            ModelManager.Initialize();
            UI.SettingsHost.Register();
        }

        [OnExit]
        public void OnApplicationQuit()
        {
            try
            {
                _harmony?.UnpatchSelf();
            }
            catch (System.Exception ex)
            {
                Log.Error($"BotPlay Harmony unpatching failed: {ex}");
            }
        }
    }
}
