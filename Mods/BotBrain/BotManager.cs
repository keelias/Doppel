using BoneLib;
using BoneLib.BoneMenu;
using HarmonyLib;
using Il2CppSLZ.Marrow;
using Il2CppSLZ.Marrow.Pool;
using MelonLoader;
using UnityEngine;

namespace ThirdEyeBonelabMod.Bots
{
    /// <summary>
    /// The bots, their menu, and the few places the game is hooked to drive their bodies.
    /// </summary>
    public static class BotManager
    {
        public static float FollowDistance = 2f;

        /// <summary>Whether a bot with no gun near it spawns one for itself.</summary>
        public static bool SpawnGuns = true;

        /// <summary>
        /// Whether bots bring things into being for themselves (a gun, a kart, a thing to try).
        /// Off: they make do with what is on the map, put there by the player.
        /// </summary>
        public static bool OwnSpawns;

        /// <summary>Whether a bot playing on its own keeps near the player (off: it goes where it likes).</summary>
        public static bool StayNear;

        /// <summary>Whether bots' fists (and shots) can hurt the player.</summary>

        private static Action<Grip, Hand> onGripOn;
        private static Action<Grip, Hand> onGripOff;
        private static bool loopClone;
        private static BoolElement cloneToggle;

        /// <summary>Whether the bot shows above its head what it is thinking.</summary>
        public static bool ShowThoughts;

        private static readonly Dictionary<IntPtr, Bot> bots = new Dictionary<IntPtr, Bot>();
        private static bool spawning;
        private static BoolElement recordToggle;

        // Set while a bot's rig is being destroyed: BoneLib takes any rig going for the level
        // ending, and tells every mod so. Only good for the frame it was set in.
        private static bool botDestroyed;
        private static int botDestroyedFrame;

        // Stopped for a newer build: its hooks into the game (which stay in) do nothing.
        private static bool shutDown;
        private static Action<Gun> onGunFired;
        private static Action onLevelUnloaded;

        public static void Initialize(Page page, HarmonyLib.Harmony harmony)
        {
            Patch(harmony, typeof(OpenControllerRig), "OnRealHeptaEarlyUpdate", nameof(DrivePrefix), null);
            Patch(harmony, typeof(OpenControllerRig), "OnEarlyUpdate", nameof(EarlyPrefix), nameof(EarlyPostfix));
            Patch(harmony, typeof(OpenControllerRig), "OnBeginCameraRendering", nameof(CameraPrefix), null);
            Patch(harmony, typeof(RemapRig), "ApplyMovement", nameof(MovePrefix), null);
            Patch(harmony, typeof(RigManager), "OnDestroy", nameof(DestroyPrefix), null);
            Patch(harmony, typeof(Hooking), "OnRigManagerDestroyed", nameof(BoneLibDestroyedPrefix), null);
            // Everything spawned (for a clone recording).
            Patch(harmony, typeof(Poolee), "OnSpawn", null, nameof(SpawnPostfix));
            // What hits a bot's body: counted by the bot (an enemy's health), not the game's player health.
            Patch(harmony, typeof(Player_Health), "OnReceivedDamage", nameof(BotHitPrefix), null);
            Patch(harmony, typeof(Player_Health), "TAKEDAMAGE", nameof(BotDamagePrefix), null);
            // What runs into a bot's body (to knock it off its feet).
            Patch(harmony, typeof(PlayerDamageReceiver), "OnCollisionEnter", nameof(BotBumpPrefix), null);
            // A magazine taken by a bot's hand gives its rounds to no ammo pouch (a bot has none: the game fails there).
            Patch(harmony, typeof(Magazine), "OnGrab", nameof(BotMagazinePrefix), null);
            // (And the pouch itself, called on with none there: thousands of errors in the game's log, one for every magazine a bot touched.)
            PatchExact(harmony, typeof(AmmoInventory), "AddCartridge", new[] { typeof(Il2CppSLZ.Marrow.Data.CartridgeData), typeof(int) }, nameof(BotAmmoAddPrefix));
            PatchExact(harmony, typeof(AmmoInventory), "GetCartridgeCount", new[] { typeof(Il2CppSLZ.Marrow.Data.CartridgeData) }, nameof(BotAmmoCountPrefix));
            // (A round taken from the holder's pouch as a gun goes off: with no pouch (a bot's gun) the game
            // fails there, in the middle of firing, and the shot does nothing. The failure is let pass, so the shot goes on.)
            try
            {
                harmony.Patch(AccessTools.Method(typeof(AmmoInventory), "RemoveCartridge", new[] { typeof(Il2CppSLZ.Marrow.Data.CartridgeData), typeof(int) }),
                    prefix: new HarmonyMethod(typeof(BotManager), nameof(BotAmmoAddPrefix)), finalizer: new HarmonyMethod(typeof(BotManager), nameof(AmmoFinalizer)), ilmanipulator: null);
            }
            catch (Exception e)
            {
                MelonLogger.Warning("Bot Player: could not hook AmmoInventory.RemoveCartridge: " + e.Message);
            }

            onGunFired = gun =>
            {
                PlayerWatch.OnGunFired(gun);
                Clone.OnFired(gun);
                foreach (Bot bot in bots.Values)
                    bot.OnCloneGunFired(gun);
                foreach (Bot bot in bots.Values)
                    bot.OnAnyGunFired(gun);
                if (gun != null && gun.firePointTransform != null)
                {
                    foreach (Bot bot in bots.Values)
                    {
                        if (!bot.Holds(gun))
                            bot.Heard(gun.firePointTransform.position);
                    }
                }
            };
            Hooking.OnPostFireGun += onGunFired;
            onGripOn = (grip, hand) =>
            {
                Lessons.OnGrip(grip, hand, true);
                Clone.OnGrip(grip, hand, true);
                ClimbMoves.OnGrip(grip, hand, true);
            };
            onGripOff = (grip, hand) =>
            {
                Clone.OnGrip(grip, hand, false);
                ClimbMoves.OnGrip(grip, hand, false);
            };
            Hooking.OnGripAttached += onGripOn;
            Hooking.OnGripDetached += onGripOff;
            onLevelUnloaded = () =>
            {
                Lab.ForgetLevel();
                SelfTaught.Save();
                Lessons.ForgetLevel();
                HeadsetGuard.ForgetLevel();
                Knowledge.Save();
                GunMoves.Save();
                Bot.ForgetDropped();
                Targets.ForgetLevel();
                Tactics.ForgetLevel();
                bots.Clear();
                spawning = false;
                Recorder.Stop();
            };
            Hooking.OnLevelUnloaded += onLevelUnloaded;

            // What the last build left: its settings, and its bots (their bodies are still about).
            Carried();
            page.CreateFunction("Spawn bot", Color.green, () => Spawn(false));
            page.CreateFunction("Spawn enemy bot (fights you)", Color.red, () => Spawn(true));
            page.CreateFunction("Remove bots", Color.red, RemoveAll);
            page.CreateFunction("Bring bots to me", Color.white, BringAll);
            page.CreateFunction("Partner mode (stays near you, fights on its own)", Color.cyan, () => SetPartner(true));
            page.CreateFunction("Play on its own (brain)", Color.magenta, () => SetPartner(false));
            page.CreateBool("Practise driving (learns by itself)", Color.magenta, DriveSkill.Practice, v => DriveSkill.Practice = v);
            page.CreateBool("Show what it is thinking", Color.magenta, ShowThoughts, v => ShowThoughts = v);
            recordToggle = page.CreateBool("Learn from me (watches everything you do)", Color.green, Recorder.Recording, SetLearning);
            page.CreateFunction("Forget the last 30 seconds", Color.yellow, ForgetLast);
            page.CreateFunction("Forget what it learned", Color.red, () =>
            {
                Knowledge.Forget();
                SelfTaught.Forget();
                Spawnables.Notify("It has forgotten everything it learned");
            });
            MoveSlots.BuildMenu(page);
            page.CreateFunction("Stand still", Color.white, () => SetMode(Bot.Mode.Stand));
            page.CreateFunction("Follow me", Color.white, () => SetMode(Bot.Mode.Follow));
            cloneToggle = page.CreateBool("Record clone (exact copy of me)", Color.red, Clone.Recording, Clone.Set);
            page.CreateFunction("Play clone", Color.green, PlayClone);
            page.CreateBool("Loop clone", Color.white, loopClone, v => loopClone = v);
            page.CreateFunction("Copy my avatar", Color.white, () =>
            {
                foreach (Bot bot in bots.Values)
                    BotRig.CopyPlayerAvatar(bot.rig, null);
            });
        }

        // One switch for all its learning: your moves (recorded) and how you handle what you hold.
        private static void SetLearning(bool on)
        {
            Recorder.Set(on);
            GunMoves.Teaching = on;
            if (on)
            {
                Spawnables.Notify("Learning from you: play as you like, it sorts it out (pick up any tool and use it: it learns that too)");
            }
            else
            {
                MotionLibrary.Reload();
                Spawnables.Notify("Learned: " + MotionLibrary.Summary() + "; " + Lessons.Summary() + ". Next: " + MotionLibrary.TeachNext());
            }
        }

        private static void ForgetLast()
        {
            int lessons = GunMoves.ForgetSince(Time.time - 30f) + ClimbMoves.ForgetSince(Time.time - 30f) + Lessons.ForgetSince(Time.time - 30f);
            bool cut = Recorder.ForgetLast(30f);
            MotionLibrary.Reload();
            Spawnables.Notify(cut || lessons > 0 ? "Forgot the last 30 seconds" + (lessons > 0 ? " (" + lessons + " lessons)" : "") : "Nothing to forget");
        }

        // ---------------------------------------------------------------- one build to the next

        private const string Keep = "ThirdEyeBotPlayer.";

        /// <summary>Stops this build for a newer one: saves, closes its files, quiets its hooks, lets go of what its bots hold.</summary>
        public static void Shutdown()
        {
            WallGrab.DropAll();
            AppDomain.CurrentDomain.SetData(Keep + "lab", Lab.On);
            AppDomain.CurrentDomain.SetData(Keep + "safe", Lab.Safe);
            AppDomain.CurrentDomain.SetData(Keep + "thoughts", ShowThoughts);
            AppDomain.CurrentDomain.SetData(Keep + "ownSpawns", OwnSpawns);
            AppDomain.CurrentDomain.SetData(Keep + "stayNear", StayNear);
            AppDomain.CurrentDomain.SetData(Keep + "drivePractice", DriveSkill.Practice);
            AppDomain.CurrentDomain.SetData(Keep + "practice", Targets.Practice);
            AppDomain.CurrentDomain.SetData(Keep + "follow", FollowDistance);
            AppDomain.CurrentDomain.SetData(Keep + "teach", GunMoves.Teaching);
            foreach (Bot bot in bots.Values)
            {
                AppDomain.CurrentDomain.SetData(Keep + "mode", bot.KeptMode.ToString());
                AppDomain.CurrentDomain.SetData(Keep + "gun." + bot.rig.Pointer, bot.OwnGun);
                AppDomain.CurrentDomain.SetData(Keep + "stow." + bot.rig.Pointer, bot.KeepStowed());
                try
                {
                    bot.Detach();
                }
                catch (Exception)
                {
                }
            }
            bots.Clear();
            shutDown = true;
            Hooking.OnPostFireGun -= onGunFired;
            Hooking.OnLevelUnloaded -= onLevelUnloaded;
            Hooking.OnGripAttached -= onGripOn;
            Hooking.OnGripDetached -= onGripOff;
            Clone.Stop();
            MoveSlots.Shutdown();
            SelfTaught.Save();
            Lessons.Cancel();
            Recorder.Stop();
            Knowledge.Save();
            GunMoves.Save();
            Lab.Shutdown();
        }

        // The settings the last build left, and its bots taken over (in the mode they were in).
        private static void Carried()
        {
            AppDomain domain = AppDomain.CurrentDomain;
            if (domain.GetData(Keep + "lab") is bool lab)
                Lab.On = lab;
            if (domain.GetData(Keep + "safe") is bool safe)
                Lab.Safe = safe;
            if (domain.GetData(Keep + "thoughts") is bool thoughts)
                ShowThoughts = thoughts;
            OwnSpawns = false;
            if (domain.GetData(Keep + "stayNear") is bool stayNear)
                StayNear = stayNear;
            if (domain.GetData(Keep + "drivePractice") is bool drivePractice)
                DriveSkill.Practice = drivePractice;
            if (domain.GetData(Keep + "practice") is bool practice)
                Targets.Practice = practice;
            if (domain.GetData(Keep + "follow") is float follow)
                FollowDistance = follow;
            if (domain.GetData(Keep + "teach") is bool teach)
                GunMoves.Teaching = teach;
            Bot.Mode mode = Bot.Mode.Stand;
            if (domain.GetData(Keep + "mode") is string named)
                Enum.TryParse(named, out mode);
            try
            {
                foreach (RigManager rig in UnityEngine.Object.FindObjectsOfType<RigManager>())
                {
                    if (!BotRig.IsBot(rig) || bots.ContainsKey(rig.Pointer))
                        continue;
                    OpenControllerRig controls = rig.ControllerRig.TryCast<OpenControllerRig>();
                    float yaw = controls != null && controls.vrRoot != null ? controls.vrRoot.eulerAngles.y : rig.transform.eulerAngles.y;
                    Bot bot = new Bot(rig, yaw);
                    bots[rig.Pointer] = bot;
                    bot.TakeStowed(domain.GetData(Keep + "stow." + rig.Pointer) as object[]);
                    domain.SetData(Keep + "stow." + rig.Pointer, null);
                    if (mode != Bot.Mode.Stand)
                        bot.SetMode(mode);
                    // Its gun: still in its hand, or (dropped) still its own.
                    Gun held = rig.physicsRig != null ? Player.GetComponentInHand<Gun>(rig.physicsRig.rightHand) : null;
                    Gun own = held ?? domain.GetData(Keep + "gun." + rig.Pointer) as Gun;
                    if (own != null)
                        bot.AdoptGun(own, held != null);
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning("Bot Player: the bots about could not be taken over: " + e.Message);
            }
            if (bots.Count > 0)
                Lab.Event("new build took over " + bots.Count + " bot(s), mode " + mode);
        }

        private static void PatchExact(HarmonyLib.Harmony harmony, Type type, string method, Type[] takes, string prefix)
        {
            try
            {
                harmony.Patch(AccessTools.Method(type, method, takes), new HarmonyMethod(typeof(BotManager), prefix));
            }
            catch (Exception e)
            {
                MelonLogger.Warning("Bot Player: could not hook " + type.Name + "." + method + ": " + e.Message);
            }
        }

        private static void Patch(HarmonyLib.Harmony harmony, Type type, string method, string prefix, string postfix)
        {
            try
            {
                harmony.Patch(AccessTools.Method(type, method),
                    prefix != null ? new HarmonyMethod(typeof(BotManager), prefix) : null,
                    postfix != null ? new HarmonyMethod(typeof(BotManager), postfix) : null);
            }
            catch (Exception e)
            {
                MelonLogger.Warning("Bot Player: could not hook " + type.Name + "." + method + ": " + e.Message);
            }
        }

        public static void Update()
        {
            if (shutDown)
                return;
            HeadsetGuard.Update();
            Lab.Update();
            Recorder.Watch();
            Recorder.Update();
            Clone.Update();
            MoveSlots.Update();
            Lessons.Update();
            Lab.Switch();
            Lab.KeepSafe();
            WallGrab.Update();
            ClimbMoves.Update();
            Knowledge.Tick(Time.deltaTime, bots.Count > 0);
            SelfTaught.Tick();
            GunMoves.Update();
            if (bots.Count > 0 || GunMoves.Teaching)
                PlayerWatch.Update();
            if (bots.Count > 0)
            {
                Gestures.Update();
                PortalWays.Update();
            }
            Bot.ClearDropped();
            if (bots.Count == 0)
                return;
            float dt = Time.deltaTime;
            List<IntPtr> gone = null;
            foreach (KeyValuePair<IntPtr, Bot> pair in bots)
            {
                if (!pair.Value.Alive)
                {
                    (gone ??= new List<IntPtr>()).Add(pair.Key);
                    continue;
                }
                try
                {
                    Lab.Who = pair.Value.Name;
                    pair.Value.Update(dt);
                    // (Sat in some things, the game no longer asks for its head and hands each
                    // frame, and nothing it meant to do with them was done: put on it here.)
                    if (Time.frameCount - pair.Value.LastDriveFrame > 2)
                    {
                        pair.Value.Drive();
                        Missed++;
                    }
                }
                finally
                {
                    Lab.Who = null;
                }
                try
                {
                }
                catch (Exception e)
                {
                    MelonLogger.Error("Bot Player: " + e);
                    (gone ??= new List<IntPtr>()).Add(pair.Key);
                }
            }
            if (gone != null)
            {
                foreach (IntPtr key in gone)
                    bots.Remove(key);
            }
        }

        private static Bot Find(RigManager rig)
        {
            return rig != null && bots.TryGetValue(rig.Pointer, out Bot bot) ? bot : null;
        }

        /// <summary>Each physics step: a bot playing a clone is pulled along the player's path.</summary>
        public static void FixedUpdate()
        {
            if (shutDown)
                return;
            foreach (Bot bot in bots.Values)
            {
                try
                {
                    Lab.Who = bot.Name;
                    bot.CloneFixed(Time.fixedDeltaTime);
                    bot.ClimbFixed(Time.fixedDeltaTime);
                    bot.StowFollow();
                    bot.SpeedGuard();
                    Lab.Who = null;
                    bot.GunFixed(Time.fixedDeltaTime);
                }
                catch (Exception e)
                {
                    MelonLogger.Error("Bot Player clone: " + e.Message);
                }
            }
        }

        /// <summary>The first bot plays the last clone recorded (a recording still going is finished first).</summary>
        public static void PlayClone()
        {
            if (Clone.Recording)
            {
                Clone.Stop();
                if (cloneToggle != null)
                    cloneToggle.Value = false;
            }
            if (bots.Count == 0)
            {
                Spawnables.Notify("Spawn a bot first");
                return;
            }
            foreach (Bot bot in bots.Values)
            {
                bot.PlayClone(Clone.Last, loopClone);
                break;
            }
        }

        /// <summary>Every bot does a lesson now, where it stands (one for the thing named, or any).</summary>
        public static void UseLessons(string named)
        {
            if (bots.Count == 0)
            {
                Spawnables.Notify("Spawn a bot first");
                return;
            }
            if (Lessons.Count == 0)
            {
                Spawnables.Notify("Nothing learned yet: turn on Learn from me, pick a tool up and use it");
                return;
            }
            foreach (Bot bot in bots.Values)
            {
                Lessons.Lesson lesson = Lessons.Pick(null, named);
                if (lesson == null)
                    Spawnables.Notify("No lesson for that");
                else if (bot.UseLesson(lesson, false))
                    Spawnables.Notify("Using the " + lesson.name + " as you did");
            }
        }

        /// <summary>The nearest bot does a move once, for the player to see it.</summary>
        public static void Preview(Frame[] clip)
        {
            Transform head = Player.Head;
            Bot nearest = null;
            float best = float.MaxValue;
            foreach (Bot bot in bots.Values)
            {
                if (bot.Head == null || bot.dead || bot.Down || head == null)
                    continue;
                float d = (bot.Head.position - head.position).magnitude;
                if (d < best)
                {
                    best = d;
                    nearest = bot;
                }
            }
            if (nearest != null)
                nearest.Preview(clip);
        }

        /// <summary>
        /// A fresh start: every move learned so far (the long recordings, the tool lessons, the
        /// gun handling, the climbs) is put away in a backup folder, and forgotten. What it
        /// learned of choosing what to do, and what it found out for itself, are kept.
        /// </summary>
        public static string FreshStart()
        {
            Recorder.Stop();
            if (recordToggle != null)
                recordToggle.Value = false;
            GunMoves.Save();
            string root = AssetFiles.SaveFolder("");
            string backup = System.IO.Path.Combine(root, "backup_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            System.IO.Directory.CreateDirectory(backup);
            int moved = 0;
            foreach (string folder in new[] { "recordings", "lessons", "moves" })
            {
                string from = System.IO.Path.Combine(root, folder);
                if (System.IO.Directory.Exists(from) && System.IO.Directory.GetFileSystemEntries(from).Length > 0)
                {
                    System.IO.Directory.Move(from, System.IO.Path.Combine(backup, folder));
                    moved++;
                }
            }
            foreach (string file in new[] { "gunmoves.json", "climbs.json" })
            {
                string from = System.IO.Path.Combine(root, file);
                if (System.IO.File.Exists(from))
                {
                    System.IO.File.Move(from, System.IO.Path.Combine(backup, file));
                    moved++;
                }
            }
            GunMoves.Reset();
            ClimbMoves.Reset();
            Lessons.Reset();
            MoveSlots.Reload();
            MotionLibrary.Reload();
            Lab.Event("fresh start: " + moved + " things put away in " + backup);
            return backup;
        }

        public static void SetCloneLoop(bool on)
        {
            loopClone = on;
        }

        // A spawned thing (the player's, while recording a clone).
        // The bot whose body this health is, if it is one.
        private static Bot BotOf(Player_Health health)
        {
            RigManager rig = health != null ? health.GetComponentInParent<RigManager>() : null;
            return rig != null && BotRig.IsBot(rig) && bots.TryGetValue(rig.Pointer, out Bot bot) ? bot : null;
        }

        private static bool BotMagazinePrefix(Hand hand)
        {
            try
            {
                return shutDown || hand == null || !BotRig.IsBot(hand.GetComponentInParent<RigManager>());
            }
            catch (Exception)
            {
                return true;
            }
        }

        private static bool ammoSaid;

        // Whether this ammo pouch is one the game can use (not a bot's, not a missing one).
        private static bool AmmoWorks(AmmoInventory pouch)
        {
            if (shutDown)
                return true;
            try
            {
                bool bots = pouch != null && BotRig.IsBot(pouch.GetComponentInParent<RigManager>());
                if ((pouch == null || bots) && !ammoSaid)
                {
                    ammoSaid = true;
                    Lab.Event("ammo: a call to " + (pouch == null ? "no pouch at all" : "a bot's pouch") + " left out");
                }
                return pouch != null && !bots;
            }
            catch (Exception)
            {
                return true;
            }
        }

        private static bool BotAmmoAddPrefix(AmmoInventory __instance) => AmmoWorks(__instance);

        public static int AmmoFailures;

        private static Exception AmmoFinalizer(Exception __exception)
        {
            if (__exception != null)
                AmmoFailures++;
            return shutDown ? __exception : null;
        }

        private static bool BotAmmoCountPrefix(AmmoInventory __instance, ref int __result)
        {
            if (AmmoWorks(__instance))
                return true;
            // (Plenty: a gun asked how many rounds its holder carries, and told none, would not be loaded or fire a live round.)
            __result = 999;
            return false;
        }

        private static void BotBumpPrefix(PlayerDamageReceiver __instance, Collision collision)
        {
            if (shutDown || bots.Count == 0 || __instance == null || collision == null)
                return;
            try
            {
                if (collision.impulse.sqrMagnitude < 25f)
                    return;
                Bot bot = Find(__instance.GetComponentInParent<RigManager>());
                if (bot != null)
                    bot.Bumped(__instance.bodyPart, collision);
            }
            catch (Exception)
            {
            }
        }

        private static bool BotHitPrefix(Player_Health __instance, Il2CppSLZ.Marrow.Combat.Attack attack, PlayerDamageReceiver.BodyPart part)
        {
            if (shutDown)
                return true;
            Bot bot = BotOf(__instance);
            if (bot == null)
                return true;
            try
            {
                bot.TakeHit(attack != null ? attack.damage : 0f, attack, part);
            }
            catch (Exception)
            {
            }
            return false;
        }

        private static bool BotDamagePrefix(Player_Health __instance, float damage)
        {
            if (shutDown)
                return true;
            Bot bot = BotOf(__instance);
            if (bot == null)
                return true;
            bot.TakeKnock(damage);
            return false;
        }

        private static void SpawnPostfix(Poolee __instance)
        {
            if (shutDown)
                return;
            HeadsetGuard.OnSpawn(__instance);
            Bot.NoteSpawn(__instance);
            Clone.OnSpawn(__instance);
        }

        // ---------------------------------------------------------------- for the lab

        public static IEnumerable<Bot> All => bots.Values;
        public static void SpawnBot() => Spawn(false);

        /// <summary>Whether this rig is an enemy bot's (the player is its target).</summary>
        public static bool IsEnemy(RigManager rig)
        {
            return rig != null && bots.TryGetValue(rig.Pointer, out Bot bot) && bot.enemy;
        }
        public static void RemoveAllBots() => RemoveAll();
        public static void BringAllBots() => BringAll();
        public static void SetModeAll(Bot.Mode mode) => SetMode(mode);

        private static void SetMode(Bot.Mode mode)
        {
            if (bots.Count == 0)
            {
                Spawnables.Notify("Spawn a bot first");
                return;
            }
            // A recording still going is finished first: it is the one to play.
            if (mode == Bot.Mode.Replay && Recorder.Recording)
            {
                Recorder.Stop();
                if (recordToggle != null)
                    recordToggle.Value = false;
            }
            foreach (Bot bot in bots.Values)
                bot.SetMode(mode);
        }

        // Partner mode: its own brain, kept within reach of the player (see BotBrain). Turned on or off for
        // every bot, and for the ones spawned after it, until the player chooses otherwise.
        private static void SetPartner(bool on)
        {
            StayNear = on;
            SetMode(Bot.Mode.Brain);
        }

        private static void BringAll()
        {
            Transform head = Player.Head;
            if (head == null)
                return;
            Vector3 forward = head.forward;
            forward.y = 0f;
            forward = forward.sqrMagnitude < 0.001f ? Vector3.forward : forward.normalized;
            int n = 0;
            foreach (Bot bot in bots.Values)
            {
                Vector3 side = Vector3.Cross(Vector3.up, forward) * ((n % 2 == 0 ? 1f : -1f) * (n + 1) / 2 * 1.2f);
                Vector3 feet = head.position + forward * 1.8f + side;
                if (Physics.Raycast(feet, Vector3.down, out RaycastHit ground, 4f, ~0, QueryTriggerInteraction.Ignore))
                    feet = ground.point;
                bot.BringTo(feet, -forward);
                n++;
            }
        }

        private static void Spawn(bool enemy)
        {
            Transform head = Player.Head;
            if (head == null || spawning)
                return;
            Vector3 forward = head.forward;
            forward.y = 0f;
            forward = forward.sqrMagnitude < 0.001f ? Vector3.forward : forward.normalized;
            Vector3 feet = head.position + forward * 2f;
            if (Physics.Raycast(feet, Vector3.down, out RaycastHit ground, 4f, ~0, QueryTriggerInteraction.Ignore))
                feet = ground.point;
            else
                feet.y -= 1.6f;
            float yaw = Recorder.Yaw(-forward);

            spawning = true;
            BotRig.Create(feet, -forward, rig =>
            {
                spawning = false;
                if (shutDown)
                    return;
                if (enemy)
                    new GameObject(Bot.EnemyMark).transform.SetParent(rig.transform, false);
                Bot bot = new Bot(rig, yaw);
                bots[rig.Pointer] = bot;
                BotRig.CopyPlayerAvatar(rig, null);
                if (enemy)
                {
                    // On its own, with its brain: it arms itself and comes for the player.
                    bot.SetMode(Bot.Mode.Brain);
                    Spawnables.Notify("Enemy bot spawned (" + bot.personality.Describe() + "): it is coming for you");
                }
                else
                {
                    // (Partner mode chosen already: it comes ready, not standing about to be told.)
                    if (StayNear)
                        bot.SetMode(Bot.Mode.Brain);
                    Spawnables.Notify("Bot spawned: " + bot.personality.Describe());
                }
            });
        }

        private static void RemoveAll()
        {
            foreach (Bot bot in bots.Values)
                bot.Remove();
            bots.Clear();
        }

        // ---------------------------------------------------------------- hooks

        // Just before the game works out a body from its head and hands: a bot's come from its brain.
        public static int PrefixCalls, PrefixNoBot, Missed;

        private static void DrivePrefix(OpenControllerRig __instance)
        {
            if (shutDown)
                return;
            PrefixCalls++;
            Bot bot = Find(__instance.manager);
            if (bot == null)
            {
                PrefixNoBot++;
                return;
            }
            try
            {
                Lab.Who = bot.Name;
                bot.Drive();
            }
            finally
            {
                Lab.Who = null;
            }
            try
            {
            }
            catch (Exception e)
            {
                MelonLogger.Error("Bot Player: " + e.Message);
            }
        }

        // A bot's rig pausing (the headset taken off, the menu opened) is not the game pausing.
        private static void EarlyPrefix(OpenControllerRig __instance, ref Il2CppSystem.Action<bool> __state)
        {
            __state = OpenControllerRig.OnPauseStateChange;
            if (!shutDown && BotRig.IsBot(__instance.manager))
                OpenControllerRig.OnPauseStateChange = null;
        }

        private static void EarlyPostfix(ref Il2CppSystem.Action<bool> __state)
        {
            OpenControllerRig.OnPauseStateChange = __state;
        }

        // A bot has no camera: what the controller rig does for its player's camera, it skips.
        private static bool CameraPrefix(OpenControllerRig __instance)
        {
            return shutDown || !BotRig.IsBot(__instance.manager);
        }

        // A bot walks where its brain pushes its stick.
        private static void MovePrefix(RemapRig __instance, ref Vector2 axis, ref bool inputPressed)
        {
            if (shutDown)
                return;
            Bot bot = Find(__instance.manager);
            if (bot == null)
                return;
            axis = bot.Walk;
            inputPressed = axis.sqrMagnitude > 0.0004f;
        }

        private static void DestroyPrefix(RigManager __instance)
        {
            if (!shutDown && BotRig.IsBot(__instance))
            {
                botDestroyed = true;
                botDestroyedFrame = Time.frameCount;
            }
        }

        // A bot going is not the level ending.
        private static bool BoneLibDestroyedPrefix()
        {
            if (shutDown || !botDestroyed)
                return true;
            botDestroyed = false;
            return botDestroyedFrame != Time.frameCount;
        }
    }
}
