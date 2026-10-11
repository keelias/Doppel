using BoneLib.BoneMenu;
using MelonLoader;

namespace ThirdEyeBonelabMod.Bots
{
    /// <summary>
    /// Where the Bot Player mod (the loader) starts and stops the bot's brain. It loads each new
    /// build of this assembly as it is made, stops the one before and starts the new one, which
    /// takes over the bots already in the level: their bodies stay, and so does what they were
    /// doing and what they learned.
    /// </summary>
    public static class Entry
    {
        /// <summary>
        /// The recordings the mod comes with (its maker's own moves, lessons and settings, in
        /// UserData/Bot Player/starter): put in place the first time it runs, and never over
        /// anything already there. So a bot knows how to move from the start, and what a player
        /// then records of their own is theirs, untouched by a later version of the mod.
        /// </summary>
        private static void Starter()
        {
            try
            {
                string home = AssetFiles.SaveFolder("");
                string starter = System.IO.Path.Combine(home, "starter");
                string done = System.IO.Path.Combine(home, "starter_done.txt");
                if (!System.IO.Directory.Exists(starter) || System.IO.File.Exists(done))
                    return;
                int put = 0;
                foreach (string from in System.IO.Directory.GetFiles(starter, "*", System.IO.SearchOption.AllDirectories))
                {
                    string to = System.IO.Path.Combine(home, from.Substring(starter.Length).TrimStart('\\', '/'));
                    if (System.IO.File.Exists(to))
                        continue;
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(to));
                    System.IO.File.Copy(from, to);
                    put++;
                }
                System.IO.File.WriteAllText(done, "The recordings the mod came with were put in place (" + put + " files). Delete this file to have any that are missing put back.");
                MelonLogger.Msg("Bot Player: the recordings it came with were put in place (" + put + " files)");
            }
            catch (Exception e)
            {
                MelonLogger.Warning("Bot Player: could not put its starter recordings in place: " + e.Message);
            }
        }

        /// <summary>Starts it: its menu on the page given, its hooks into the game, and the bots already about.</summary>
        public static void Start(Page page)
        {
            AssetFiles.Root = "Bot Player";
            Starter();
            // A Harmony of its own each time (an earlier build's hooks are switched off, not taken out).
            HarmonyLib.Harmony harmony = new HarmonyLib.Harmony("RudyHill.BotBrain." + Guid.NewGuid().ToString("N"));
            // A build before this one that never got started properly (it broke on the way) was
            // never stopped either: it still holds the lab's files, its hooks, and the bots. Stopped now.
            int seenBuilds = 0;
            try
            {
                System.Reflection.Assembly mine = typeof(Entry).Assembly;
                List<System.Reflection.Assembly> all = new List<System.Reflection.Assembly>(AppDomain.CurrentDomain.GetAssemblies());
                // (Each build is loaded into a place of its own: looked for there too.)
                foreach (System.Runtime.Loader.AssemblyLoadContext context in System.Runtime.Loader.AssemblyLoadContext.All)
                {
                    foreach (System.Reflection.Assembly a in context.Assemblies)
                    {
                        if (!all.Contains(a))
                            all.Add(a);
                    }
                }
                foreach (System.Reflection.Assembly other in all)
                {
                    string called;
                    try
                    {
                        called = other.GetName().Name;
                    }
                    catch (Exception)
                    {
                        continue;
                    }
                    // (Each build has a name of its own: known by what is in it.)
                    if (other == mine || !called.StartsWith("BotBrain", StringComparison.Ordinal))
                        continue;
                    Type manager = other.GetType(typeof(BotManager).FullName);
                    if (manager == null)
                        continue;
                    seenBuilds++;
                    System.Reflection.FieldInfo stopped = manager != null ? manager.GetField("shutDown", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static) : null;
                    if (stopped == null || (bool)stopped.GetValue(null))
                        continue;
                    try
                    {
                        manager.GetMethod("Shutdown").Invoke(null, null);
                        MelonLogger.Msg("Bot brain: an earlier build that had not been stopped was stopped");
                    }
                    catch (Exception e)
                    {
                        MelonLogger.Msg("Bot brain: an earlier build could not be stopped: " + (e.InnerException ?? e).Message);
                        // (Its files at least: the lab's log, held open, kept every later build from writing to it.)
                        try
                        {
                            Type lab = other.GetType(typeof(Lab).FullName);
                            lab.GetMethod("Shutdown").Invoke(null, null);
                        }
                        catch (Exception)
                        {
                        }
                    }
                }
            }
            catch (Exception e)
            {
                MelonLogger.Msg("Bot brain: looking for earlier builds failed: " + e.Message);
            }
            MelonLogger.Msg("Bot brain: " + seenBuilds + " earlier builds in memory");
            try
            {
                BotManager.Initialize(page, harmony);
            }
            catch (Exception)
            {
                // (Broken on the way: everything it had taken hold of is let go, for the next build to take.)
                try
                {
                    BotManager.Shutdown();
                }
                catch (Exception)
                {
                }
                throw;
            }
            MelonLogger.Msg("Bot brain started");
        }

        public static void Update()
        {
            BotManager.Update();
        }

        public static void FixedUpdate()
        {
            BotManager.FixedUpdate();
        }

        /// <summary>Stops it for a new build: its hooks do nothing from now on, its files are closed, what it learned is saved.</summary>
        public static void Stop()
        {
            BotManager.Shutdown();
        }
    }
}
