using System.IO;
using System.Reflection;
using BoneLib.BoneMenu;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;

namespace ThirdEyeBonelabMod.Bots
{
    /// <summary>
    /// The Bot Player mod is only this: a loader for the bot's brain (BotBrain.dll, in
    /// UserData/Bot Player/live). It reads the brain into memory (so the file is never locked)
    /// and, whenever a new build of it is put there, stops the one running and starts the new
    /// one, while the game runs: no restart for a change to the bot.
    /// </summary>
    public static class BotLoader
    {
        private static string Folder => Path.Combine(MelonEnvironment.UserDataDirectory, "Bot Player", "live");
        private static string Brain => Path.Combine(Folder, "BotBrain.dll");

        private static Page page;
        private static DateTime loadedStamp;
        private static float checkAt;
        private static int builds;
        private static bool missingSaid;
        private static float errorSaidAt = -10f;

        private static Action update;
        private static Action fixedUpdate;
        private static Action stop;

        public static void Initialize(Page root)
        {
            page = root.CreatePage("Doppel", Color.cyan);
            Load();
        }

        public static void Update()
        {
            if (Time.realtimeSinceStartup >= checkAt)
            {
                checkAt = Time.realtimeSinceStartup + 1f;
                Load();
            }
            Run(update);
        }

        public static void FixedUpdate()
        {
            Run(fixedUpdate);
        }

        // A new build there (written a moment ago and not still being written): swapped in.
        private static void Load()
        {
            try
            {
                if (!File.Exists(Brain))
                {
                    if (!missingSaid)
                        MelonLogger.Warning("Bot Player: no brain yet (" + Brain + ")");
                    missingSaid = true;
                    return;
                }
                DateTime stamp = File.GetLastWriteTimeUtc(Brain);
                if (stamp == loadedStamp || (DateTime.UtcNow - stamp).TotalSeconds < 0.8)
                    return;
                loadedStamp = stamp;

                byte[] code = File.ReadAllBytes(Brain);
                string symbols = Path.ChangeExtension(Brain, ".pdb");
                Assembly brain = File.Exists(symbols) ? Assembly.Load(code, File.ReadAllBytes(symbols)) : Assembly.Load(code);
                Type entry = brain.GetType("ThirdEyeBonelabMod.Bots.Entry", true);

                // The one running stopped (its hooks go quiet, its files close), then the new one started.
                if (stop != null)
                {
                    try
                    {
                        stop();
                    }
                    catch (Exception e)
                    {
                        MelonLogger.Warning("Bot Player: the old brain did not stop cleanly: " + e.Message);
                    }
                }
                update = fixedUpdate = stop = null;
                page.RemoveAll();
                entry.GetMethod("Start").Invoke(null, new object[] { page });
                update = (Action)Delegate.CreateDelegate(typeof(Action), entry.GetMethod("Update"));
                fixedUpdate = (Action)Delegate.CreateDelegate(typeof(Action), entry.GetMethod("FixedUpdate"));
                stop = (Action)Delegate.CreateDelegate(typeof(Action), entry.GetMethod("Stop"));
                builds++;
                MelonLogger.Msg("Bot Player: brain build " + builds + " running (" + stamp.ToLocalTime().ToString("HH:mm:ss") + ")");
                if (builds > 1)
                    Spawnables.Notify("Bot brain updated (" + builds + ")");
            }
            catch (Exception e)
            {
                MelonLogger.Error("Bot Player: the brain could not be loaded: " + (e.InnerException ?? e));
            }
        }

        private static void Run(Action step)
        {
            if (step == null)
                return;
            try
            {
                step();
            }
            catch (Exception e)
            {
                // (Said now and then, not every frame.)
                if (Time.realtimeSinceStartup - errorSaidAt > 5f)
                {
                    errorSaidAt = Time.realtimeSinceStartup;
                    MelonLogger.Error("Bot Player brain: " + e);
                }
            }
        }
    }
}
