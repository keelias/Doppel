using System.Linq;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using BoneLib;
using MelonLoader;
using UnityEngine;

namespace ThirdEyeBonelabMod.Bots
{
    /// <summary>
    /// The lab: a way to test and train the bots from outside the game while it runs, by files
    /// in UserData/Bot Player/lab:
    /// <list type="bullet">
    /// <item>commands.txt: commands, one a line, carried out and the file emptied (see Run);</item>
    /// <item>events.log: what was done and what happened (grabs, shots, reloads, goals...);</item>
    /// <item>telemetry.jsonl: each bot's state, five times a second, while the lab is on;</item>
    /// <item>snap_*.png: pictures of a bot, taken by a camera of the lab's own, on command;</item>
    /// <item>episodes.csv: how each round of training went;</item>
    /// <item>tuning.txt: numbers to tune the bots' handling by, read as they change (see Tuning).</item>
    /// </list>
    /// </summary>
    public static class Lab
    {
        public static string Folder => AssetFiles.SaveFolder("lab");
        private static string Commands => Path.Combine(Folder, "commands.txt");

        /// <summary>Whether bots' state is written out (and training may run).</summary>
        public static bool On;

        private static float switchAt;

        /// <summary>
        /// The lab is not in the menu (it is no use to a player). It is on where a file named
        /// enable.txt lies in its folder (UserData/Bot Player/lab), looked for every few seconds.
        /// </summary>
        public static void Switch()
        {
            if (Time.time < switchAt)
                return;
            switchAt = Time.time + 5f;
            try
            {
                On = File.Exists(Path.Combine(Folder, "enable.txt"));
            }
            catch (Exception)
            {
            }
        }

        private static float pollAt;
        private static readonly Queue<string> queue = new Queue<string>();
        private static float waitUntil;
        private static float telemetryAt;
        private static StreamWriter telemetry;
        private static StreamWriter events;

        private static Camera camera;
        private static RenderTexture picture;
        private static int snaps;

        // Training: rounds of enemies about the bot, one after another.
        private static int roundsLeft;
        private static int round;
        private static float roundStart;
        private static float roundEndAt = -1f;
        private static int killsAtStart;
        private static int closeCallsAtStart;
        private static int shotsAtStart;
        private static int reloadsAtStart;

        private static MethodInfo labSpawn;
        private static MethodInfo labRemoveAll;

        public static void Update()
        {
            Tuning.Update();
            if (Time.time >= pollAt)
            {
                pollAt = Time.time + 0.4f;
                Poll();
            }
            // The commands, in turn ("wait s" holds the rest back a while).
            while (queue.Count > 0 && Time.time >= waitUntil)
            {
                string next = queue.Dequeue();
                if (next.StartsWith("wait ", StringComparison.OrdinalIgnoreCase)
                    && float.TryParse(next.Substring(5).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float seconds))
                {
                    waitUntil = Time.time + seconds;
                    Event("> " + next);
                }
                else
                {
                    Run(next);
                }
            }
            if (On && Time.time >= telemetryAt)
            {
                telemetryAt = Time.time + 0.2f;
                WriteTelemetry();
            }
            Train();
        }

        /// <summary>Stopped for a newer build: its files closed (the next opens them again), its camera gone.</summary>
        public static void Shutdown()
        {
            try
            {
                telemetry?.Dispose();
                events?.Dispose();
            }
            catch (Exception)
            {
            }
            telemetry = null;
            events = null;
            if (camera != null)
                UnityEngine.Object.Destroy(camera.gameObject);
            if (picture != null)
                UnityEngine.Object.Destroy(picture);
            camera = null;
            picture = null;
            queue.Clear();
            roundsLeft = 0;
            roundEndAt = -1f;
        }

        public static void ForgetLevel()
        {
            queue.Clear();
            waitUntil = 0f;
            roundsLeft = 0;
            roundEndAt = -1f;
            camera = null;
        }

        // ---------------------------------------------------------------- commands

        private static void Poll()
        {
            try
            {
                if (!File.Exists(Commands))
                    return;
                string[] lines = File.ReadAllLines(Commands);
                File.WriteAllText(Commands, "");
                foreach (string line in lines)
                {
                    string cmd = line.Trim();
                    if (cmd.Length > 0 && !cmd.StartsWith("#", StringComparison.Ordinal))
                        queue.Enqueue(cmd);
                }
            }
            catch (IOException)
            {
                // (Being written just now: read it next time.)
            }
            catch (Exception e)
            {
                Event("commands failed: " + e.Message);
            }
        }

        /// <summary>
        /// Carries out a command:
        /// lab on|off · spawn · remove · bring · mode stand|follow|wander|gunplay|brain|copy|replay ·
        /// gun [barcode or name] · drop · zombies n [distance] [zombie|fast|bendy] · clear ·
        /// snap [front|side|back|gun|top] · train n · stop · say text · learned · forget
        /// </summary>
        public static void Run(string command)
        {
            string[] w = command.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string verb = w[0].ToLowerInvariant();
            string rest = command.Length > w[0].Length ? command.Substring(w[0].Length).Trim() : "";
            Event("> " + command);
            try
            {
                switch (verb)
                {
                    case "lab":
                        On = rest != "off";
                        Event("lab " + (On ? "on" : "off"));
                        break;
                    case "spawn":
                        BotManager.SpawnBot();
                        break;
                    case "remove":
                        BotManager.RemoveAllBots();
                        break;
                    case "bring":
                        BotManager.BringAllBots();
                        break;
                    case "mode":
                        if (Enum.TryParse(rest, true, out Bot.Mode mode))
                            BotManager.SetModeAll(mode);
                        else
                            Event("no such mode: " + rest);
                        break;
                    case "gun":
                        foreach (Bot bot in BotManager.All)
                            bot.LabGive(rest);
                        break;
                    case "wave":
                        int times = w.Length > 1 ? int.Parse(w[1], CultureInfo.InvariantCulture) : 3;
                        foreach (Bot bot in BotManager.All)
                            bot.LabWave(times);
                        break;
                    case "lesson":
                        Event("lessons: " + Lessons.Summary() + " (" + Lessons.Names() + ")");
                        Event("lessons are for: " + Lessons.Purposes());
                        if (rest != "list")
                            BotManager.UseLessons(rest.Length > 0 ? rest : null);
                        break;
                    case "hurt":
                        foreach (Bot bot in BotManager.All)
                            bot.LabHurt(w.Length > 1 ? float.Parse(w[1], CultureInfo.InvariantCulture) : 0.1f);
                        break;
                    case "portal":
                        foreach (Bot bot in BotManager.All)
                            bot.LabPortal();
                        break;
                    case "trip":
                        foreach (Bot bot in BotManager.All)
                            bot.LabTrip(w.Length > 1 ? float.Parse(w[1], CultureInfo.InvariantCulture) : 0.4f);
                        break;
                    case "curious":
                        Event("self-taught: " + SelfTaught.Summary());
                        foreach (Bot bot in BotManager.All)
                            bot.LabCurious(rest.Length > 0 ? rest : null);
                        break;
                    case "fresh":
                        Event("fresh start: " + BotManager.FreshStart());
                        break;
                    case "moves":
                        MoveSlots.LabArm(w.Length > 1 ? w[1] : "", w.Length > 2 ? int.Parse(w[2], CultureInfo.InvariantCulture) : 1);
                        break;
                    case "addslot":
                        MoveSlots.LabAddRemove(rest, true);
                        break;
                    case "removeslot":
                        MoveSlots.LabAddRemove(rest, false);
                        break;
                    case "signal":
                        MoveSlots.LabSignal();
                        break;
                    case "climb":
                        foreach (Bot bot in BotManager.All)
                            bot.LabClimb();
                        break;
                    case "sprint":
                        foreach (Bot bot in BotManager.All)
                            bot.LabSprint();
                        break;
                    case "legs":
                        Event("legs, you: " + Bot.LegsOf(BoneLib.Player.RigManager) + ", last jump " + (Time.time - Recorder.PlayerJumpedAt).ToString("0.0") + " s ago, time x" + Time.timeScale.ToString("0.00")
                            + ", gravity " + Physics.gravity.y.ToString("0.0") + ", avatar " + (BoneLib.Player.RigManager != null && BoneLib.Player.RigManager.avatar != null ? BoneLib.Player.RigManager.avatar.name : "?")
                            + ", speed " + (BoneLib.Player.PhysicsRig != null && BoneLib.Player.PhysicsRig.m_pelvis != null ? BoneLib.Player.PhysicsRig.m_pelvis.GetComponent<Rigidbody>().velocity.ToString("0.0") : "?")
                            + ", moves " + MoveSlots.State());
                        foreach (Bot bot in BotManager.All)
                            Event("legs, bot " + bot.Name + ": game last asked for its hands " + (Time.frameCount - bot.LastDriveFrame) + " frame(s) ago, " + Bot.LegsOf(bot.rig) + ", same legs as yours: " + (BoneLib.Player.RigManager != null && bot.rig.remapHeptaRig.Pointer == BoneLib.Player.RigManager.remapHeptaRig.Pointer));
                        break;
                    case "voice":
                        foreach (Bot bot in BotManager.All)
                            bot.LabVoice(rest);
                        break;
                    case "shoot":
                        if (rest == "reset")
                            ShootSkill.Reset();
                        Event("shoot skill: " + ShootSkill.Said());
                        break;
                    case "drive":
                        if (rest == "learn")
                        {
                            Driving.Forget();
                            Event("driving: " + Lessons.DriveSaid());
                        }
                        else
                        {
                            foreach (Bot bot in BotManager.All)
                                bot.LabDrive(rest);
                        }
                        break;
                    case "purpose":
                        foreach (Bot bot in BotManager.All)
                            bot.LabPurpose();
                        break;
                    case "fidget":
                        foreach (Bot bot in BotManager.All)
                            bot.LabFidget();
                        break;
                    case "startle":
                        foreach (Bot bot in BotManager.All)
                            bot.LabStartle();
                        break;
                    case "wavehello":
                        foreach (Bot bot in BotManager.All)
                            bot.LabWaveBack();
                        break;
                    case "toss":
                        string toss = w.Length > 1 ? GunBarcode(w[1]) : CommonBarcodes.Guns.M1911;
                        foreach (Bot bot in BotManager.All)
                            bot.LabToss(toss);
                        break;
                    case "safe":
                        // (For trying fights out with the player away from their headset: nothing can kill them meanwhile. A level begun again by their death takes every bot with it.)
                        Safe = rest != "off";
                        ApplySafe();
                        Event("safe: the player " + (Safe ? "cannot be killed (for a test)" : "can be hurt again"));
                        break;
                    case "count":
                        Event("count: " + Targets.Hostiles().Count + " hostile(s) alive, " + BotManager.All.Count() + " bot(s), ammo failures let pass " + BotManager.AmmoFailures);
                        foreach (Bot bot in BotManager.All)
                            Event("status: " + bot.LabStatus());
                        break;
                    case "holster":
                        foreach (Bot bot in BotManager.All)
                            bot.LabHolster();
                        break;
                    case "arm":
                        foreach (Bot bot in BotManager.All)
                            bot.LabArm();
                        break;
                    case "lay":
                        foreach (Bot bot in BotManager.All)
                            bot.LabLay(rest);
                        break;
                    case "drop":
                        foreach (Bot bot in BotManager.All)
                            bot.StopPlaying();
                        break;
                    case "zombies":
                        int n = w.Length > 1 ? int.Parse(w[1], CultureInfo.InvariantCulture) : 1;
                        float far = w.Length > 2 ? float.Parse(w[2], CultureInfo.InvariantCulture) : 10f;
                        SpawnEnemies(n, far, w.Length > 3 ? w[3].ToLowerInvariant() : "zombie");
                        break;
                    case "clear":
                        ClearEnemies();
                        break;
                    case "snap":
                        foreach (Bot bot in BotManager.All)
                            Snap(bot, rest.Length > 0 ? rest.ToLowerInvariant() : "front");
                        break;
                    case "train":
                        roundsLeft = w.Length > 1 ? int.Parse(w[1], CultureInfo.InvariantCulture) : 10;
                        On = true;
                        roundEndAt = Time.time;
                        Event("training " + roundsLeft + " rounds");
                        break;
                    case "stop":
                        roundsLeft = 0;
                        roundEndAt = -1f;
                        Event("training stopped");
                        break;
                    case "say":
                        Spawnables.Notify(rest);
                        break;
                    case "avatar":
                        foreach (Bot bot in BotManager.All)
                            BotRig.CopyPlayerAvatar(bot.rig, null);
                        break;
                    case "clone":
                        if (rest == "record")
                            Clone.Start();
                        else if (rest == "stop")
                            Clone.Stop();
                        else if (rest == "loop")
                        {
                            BotManager.SetCloneLoop(true);
                            BotManager.PlayClone();
                        }
                        else
                        {
                            BotManager.SetCloneLoop(false);
                            BotManager.PlayClone();
                        }
                        break;
                    case "teach":
                        GunMoves.Teaching = rest != "off";
                        Event("teaching " + (GunMoves.Teaching ? "on" : "off") + ": " + GunMoves.Summary());
                        break;
                    case "gunmoves":
                        Event("gun moves: " + GunMoves.Summary());
                        break;
                    case "learned":
                        Event("learned: " + Knowledge.Summary());
                        Event("moves: " + MotionLibrary.Summary() + "; to teach next: " + MotionLibrary.TeachNext());
                        break;
                    case "forget":
                        Knowledge.Forget();
                        break;
                    default:
                        Event("unknown command: " + verb);
                        break;
                }
            }
            catch (Exception e)
            {
                Event("command failed: " + e.Message);
            }
        }

        /// <summary>A gun's barcode, from its name in the game's list (AKM, M1911...), or the barcode itself.</summary>
        public static string GunBarcode(string which)
        {
            if (which.Contains("."))
                return which;
            foreach (FieldInfo field in typeof(CommonBarcodes.Guns).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (field.FieldType == typeof(string) && string.Equals(field.Name, which, StringComparison.OrdinalIgnoreCase))
                    return field.GetValue(null) as string;
            }
            return CommonBarcodes.Guns.M1911;
        }

        // ---------------------------------------------------------------- enemies (the Zombies mod's, asked for by name)

        private static bool FindZombies()
        {
            if (labSpawn != null)
                return true;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type manager = assembly.GetType("ThirdEyeBonelabMod.Zombies.ZombieManager", false);
                if (manager == null)
                    continue;
                labSpawn = manager.GetMethod("LabSpawn", BindingFlags.Public | BindingFlags.Static);
                labRemoveAll = manager.GetMethod("LabRemoveAll", BindingFlags.Public | BindingFlags.Static);
                return labSpawn != null;
            }
            return false;
        }

        private static void SpawnEnemies(int count, float far, string kind)
        {
            if (!FindZombies())
            {
                Event("the Zombies mod is not there");
                return;
            }
            Bot bot = null;
            foreach (Bot b in BotManager.All)
            {
                bot = b;
                break;
            }
            Transform around = bot != null && bot.Head != null ? bot.Head : Player.Head;
            if (around == null)
                return;
            for (int i = 0; i < count; i++)
            {
                if (!Navigator.RandomPlace(around.position - Vector3.up * 1.4f, far * 0.75f, far * 1.25f, out Vector3 at))
                    at = around.position + Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f) * Vector3.forward * far;
                // Not right on top of the player.
                if (Player.Head != null && (at - Player.Head.position).magnitude < 3f)
                    at += (at - Player.Head.position).normalized * 3f;
                labSpawn.Invoke(null, new object[] { kind, at });
            }
            Event("spawned " + count + " " + kind + " about " + far.ToString("0") + " m out");
        }

        private static void ClearEnemies()
        {
            if (FindZombies() && labRemoveAll != null)
                labRemoveAll.Invoke(null, null);
        }

        // ---------------------------------------------------------------- training

        private static void Train()
        {
            if (roundsLeft <= 0 && roundEndAt < 0f)
                return;
            Bot bot = null;
            foreach (Bot b in BotManager.All)
            {
                bot = b;
                break;
            }
            if (bot == null)
                return;
            int living = Targets.Hostiles().Count;
            bool running = roundEndAt < 0f;
            if (running)
            {
                // Over when they are all down, or after a minute.
                if (living == 0 || Time.time - roundStart > 60f)
                {
                    float time = Time.time - roundStart;
                    int kills = Knowledge.Data.kills - killsAtStart;
                    int calls = Knowledge.Data.closeCalls - closeCallsAtStart;
                    int shots = bot.LabShots - shotsAtStart;
                    int reloads = bot.LabReloads - reloadsAtStart;
                    string line = round + "," + time.ToString("0.0", CultureInfo.InvariantCulture) + "," + kills + "," + living + "," + calls + "," + shots + "," + reloads;
                    string file = Path.Combine(Folder, "episodes.csv");
                    if (!File.Exists(file))
                        File.WriteAllText(file, "round,seconds,kills,left,closecalls,shots,reloads\n");
                    File.AppendAllText(file, line + "\n");
                    Event("round " + round + " done: " + time.ToString("0") + " s, " + kills + " kills, " + living + " left, " + calls + " close calls, " + shots + " shots, " + reloads + " reloads");
                    ClearEnemies();
                    roundEndAt = Time.time;
                }
                return;
            }
            if (Time.time - roundEndAt < 3f)
                return;
            if (roundsLeft <= 0)
            {
                roundEndAt = -1f;
                Event("training done: " + Knowledge.Summary());
                Knowledge.Save();
                return;
            }
            // A new round: one to four enemies, near or far.
            roundsLeft--;
            round++;
            if (bot.mode != Bot.Mode.Brain)
                BotManager.SetModeAll(Bot.Mode.Brain);
            int n = UnityEngine.Random.Range(1, 5);
            SpawnEnemies(n, UnityEngine.Random.Range(6f, 18f), UnityEngine.Random.value < 0.25f ? "fast" : "zombie");
            roundStart = Time.time;
            roundEndAt = -1f;
            killsAtStart = Knowledge.Data.kills;
            closeCallsAtStart = Knowledge.Data.closeCalls;
            shotsAtStart = bot.LabShots;
            reloadsAtStart = bot.LabReloads;
        }

        // ---------------------------------------------------------------- what happened, and how things are

        /// <summary>Which bot is at work now (set as each has its turn): its lines in the log begin with its name.</summary>
        public static string Who;

        /// <summary>Whether the player is kept from being killed (while fights are tried out with nobody playing).</summary>
        public static bool Safe;
        private static float safeAt;

        public static void ApplySafe()
        {
            try
            {
                Il2CppSLZ.Marrow.Health health = BoneLib.Player.RigManager != null ? BoneLib.Player.RigManager.health : null;
                if (health == null)
                    return;
                if (Safe && health.healthMode != Il2CppSLZ.Marrow.Health.HealthMode.Invincible)
                    health.SetHealthMode((int)Il2CppSLZ.Marrow.Health.HealthMode.Invincible);
                else if (!Safe && health.healthMode == Il2CppSLZ.Marrow.Health.HealthMode.Invincible)
                    health.SetHealthMode((int)Il2CppSLZ.Marrow.Health.HealthMode.Mortal);
                if (Safe)
                    health.SetFullHealth();
            }
            catch (Exception)
            {
            }
        }

        /// <summary>Now and then while it is on (a level begun again puts the player back as they were).</summary>
        private static string youWas = "";
        private static bool youClickWas;
        private static float youSpeedAt;

        // For the lab: the player's own legs and stick clicks, written down when they change (why can they not sprint?).
        private static void WatchYou()
        {
            try
            {
                Il2CppSLZ.Marrow.RigManager you = BoneLib.Player.RigManager;
                if (you == null || you.remapHeptaRig == null)
                    return;
                Il2CppSLZ.Marrow.OpenControllerRig c = you.ControllerRig.TryCast<Il2CppSLZ.Marrow.OpenControllerRig>();
                bool click = c != null && (c._primaryStick || c._secondaryStick);
                bool raw = false;
                string rawSaid = "";
                if (c != null)
                {
                    foreach (Il2CppSLZ.Marrow.BaseController hand in new[] { c.leftController, c.rightController })
                    {
                        if (hand == null)
                            continue;
                        bool on = hand.GetThumbStick();
                        raw |= on;
                        rawSaid += (hand == c.leftController ? " left " : " right ") + (on ? "DOWN" : "up");
                    }
                }
                string now = you.remapHeptaRig.travState + ", rig click " + (c != null ? c._primaryStick + "/" + c._secondaryStick : "?") + ", sticks" + rawSaid + ", body " + you.bodyState;
                UnityEngine.Rigidbody hips = BoneLib.Player.PhysicsRig != null && BoneLib.Player.PhysicsRig.m_pelvis != null ? BoneLib.Player.PhysicsRig.m_pelvis.GetComponent<UnityEngine.Rigidbody>() : null;
                float going = hips != null ? new Vector2(hips.velocity.x, hips.velocity.z).magnitude : 0f;
                if (c != null && c._axisPrimary.magnitude > 0.5f && Time.time - youSpeedAt > 0.5f)
                {
                    youSpeedAt = Time.time;
                    Event("you: going " + going.ToString("0.0") + " m/s, " + you.remapHeptaRig.travState + ", legs' own " + you.remapHeptaRig._currentVelocity.magnitude.ToString("0.0") + ", most now " + you.remapHeptaRig.currentMaxVelocity.ToString("0.0") + " of " + you.remapHeptaRig.maxVelocity.ToString("0.00") + ", crouch " + you.remapHeptaRig._crouchTarget.ToString("0.00"));
                }
                if (now != youWas || (click || raw) != youClickWas)
                {
                    youWas = now;
                    youClickWas = click || raw;
                    Event("you: " + now + ", top speed " + you.remapHeptaRig.maxVelocity.ToString("0.00") + ", stick " + (c != null ? c._axisPrimary.ToString("0.0") : "?"));
                }
            }
            catch (Exception)
            {
            }
        }

        public static void KeepSafe()
        {
            WatchYou();
            if (!Safe || Time.time - safeAt < 2f)
                return;
            safeAt = Time.time;
            ApplySafe();
        }

        // Lines for finding things out, many a second: left out of the log unless asked for (lab.chatty 1 in tuning.txt).
        private static readonly string[] chatter = { "shot: ", "run: going", "head limit: head was", "fists: that blow", "climb lift", "flinch ", "you: going" };

        public static void Event(string what)
        {
            if (what != null && Tuning.F("lab.chatty", 0f) < 0.5f)
            {
                foreach (string c in chatter)
                {
                    if (what.StartsWith(c, StringComparison.Ordinal))
                        return;
                }
            }
            if (Who != null)
                what = "[" + Who + "] " + what;
            try
            {
                if (events == null)
                {
                    Directory.CreateDirectory(Folder);
                    events = new StreamWriter(Path.Combine(Folder, "events.log"), true, new UTF8Encoding(false)) { AutoFlush = true };
                }
                events.WriteLine(DateTime.Now.ToString("HH:mm:ss.f", CultureInfo.InvariantCulture) + " " + what);
            }
            catch (Exception)
            {
            }
        }

        private static void WriteTelemetry()
        {
            try
            {
                if (telemetry == null)
                {
                    Directory.CreateDirectory(Folder);
                    string file = Path.Combine(Folder, "telemetry.jsonl");
                    // (Started afresh when it has grown large.)
                    bool fresh = File.Exists(file) && new FileInfo(file).Length > 20_000_000;
                    telemetry = new StreamWriter(file, !fresh, new UTF8Encoding(false)) { AutoFlush = true };
                }
                foreach (Bot bot in BotManager.All)
                    telemetry.WriteLine(bot.LabState());
            }
            catch (Exception e)
            {
                MelonLogger.Warning("Bot Player lab: " + e.Message);
                telemetry = null;
            }
        }

        // ---------------------------------------------------------------- pictures

        /// <summary>A picture of the bot from a camera of the lab's own: front, side, back, top, or of its gun hand.</summary>
        public static void Snap(Bot bot, string view)
        {
            Transform head = bot.Head;
            if (head == null)
                return;
            try
            {
                if (camera == null)
                {
                    GameObject go = new GameObject("ThirdEyeLabCamera");
                    camera = go.AddComponent<Camera>();
                    camera.enabled = false;
                    camera.fieldOfView = 55f;
                    camera.nearClipPlane = 0.03f;
                    camera.stereoTargetEye = StereoTargetEyeMask.None;
                    picture = new RenderTexture(800, 600, 24);
                    camera.targetTexture = picture;
                }
                Vector3 forward = bot.Facing;
                Vector3 right = Vector3.Cross(Vector3.up, forward);
                Vector3 chest = head.position - Vector3.up * 0.35f;
                Vector3 at;
                Vector3 look = chest;
                switch (view)
                {
                    case "side":
                        at = chest + right * 2.2f + Vector3.up * 0.2f;
                        break;
                    case "back":
                        at = chest - forward * 2.2f + Vector3.up * 0.3f;
                        break;
                    case "top":
                        at = chest + Vector3.up * 2.5f + forward * 0.5f;
                        break;
                    case "map":
                        at = chest + Vector3.up * 40f - forward * 0.5f;
                        break;
                    case "far":
                        at = chest - forward * 9f + Vector3.up * 5f;
                        break;
                    case "gun":
                        Transform hand = bot.GunHandTransform;
                        look = hand != null ? hand.position : chest;
                        at = look + forward * 0.7f + right * 0.6f + Vector3.up * 0.25f;
                        break;
                    default:
                        at = chest + forward * 2.4f + right * 0.6f + Vector3.up * 0.15f;
                        break;
                }
                camera.transform.SetPositionAndRotation(at, Quaternion.LookRotation(look - at, Vector3.up));
                camera.Render();
                RenderTexture before = RenderTexture.active;
                RenderTexture.active = picture;
                Texture2D image = new Texture2D(picture.width, picture.height, TextureFormat.RGB24, false);
                image.ReadPixels(new Rect(0, 0, picture.width, picture.height), 0, 0);
                image.Apply();
                RenderTexture.active = before;
                byte[] png = ImageConversion.EncodeToPNG(image);
                UnityEngine.Object.Destroy(image);
                snaps++;
                string file = Path.Combine(Folder, "snap_" + snaps.ToString("000") + "_" + view + ".png");
                File.WriteAllBytes(file, png);
                Event("snap " + Path.GetFileName(file) + " | " + bot.LabSummary());
            }
            catch (Exception e)
            {
                Event("snap failed: " + e.Message);
            }
        }
    }
}
