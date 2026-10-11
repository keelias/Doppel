using System.Globalization;
using System.IO;
using BoneLib;
using BoneLib.BoneMenu;
using Il2CppSLZ.Marrow;
using Il2CppSLZ.Marrow.Pool;
using Il2CppTMPro;
using MelonLoader;
using UnityEngine;

namespace ThirdEyeBonelabMod.Bots
{
    /// <summary>
    /// The player's moves, kept one by one, by kind, in slots: five each of standing, walking,
    /// fast walking, running, jumping, crouching, climbing, waving, reloading and firing. A
    /// slot is picked in the menu and recorded on a signal: both thumbsticks pressed in and
    /// held count three and begin; pressed and held again, it ends, and what was recorded is
    /// that slot's (trimmed at its ends, checked to be what it says it is, shown once on a bot).
    /// The bots move by these: short, clean, each chosen by the player, where before they had
    /// to find their moves in long recordings of everything. A kind with nothing in its slots
    /// falls back on what was learned the old way, or on the bot's own plain moves.
    /// Kept in UserData/Bot Player/moves.
    /// </summary>
    public static class MoveSlots
    {
        public enum Kind { Idle, Walk, FastWalk, Run, Jump, Crouch, Climb, Wave, Reload, Fire, Punch, Melee, PickUp }

        /// <summary>How many slots a kind starts with, and the most it can be given.</summary>
        public const int Each = 5;
        public const int Most = 20;

        /// <summary>A slot's number for what is learned elsewhere on its account (a climb): its kind's hundred, and its place.</summary>
        public static int Tag(Kind kind, int slot) => Editing.id * 1000 + (int)kind * 100 + slot;

        private static readonly string[] Names = { "Idle", "Walk", "Fast walk", "Run", "Jump", "Crouch", "Climb", "Wave", "Reload", "Fire", "Punch", "Melee", "Pick up" };

        private static readonly string[] Hints =
        {
            "stand as you do, hands empty", "walk slowly", "walk at your usual pace", "sprint (click the stick)", "jump once",
            "crouch down, stay, and stand up", "climb up onto a ledge", "wave", "reload the gun in your hand", "aim and fire the gun in your hand",
            "punch at something in front of you, hands empty", "hit at something in front of you with the weapon in your hand", "bend down, pick something up off the ground, and stand up with it",
        };

        public class Slot
        {
            public bool filled;
            public Frame[] clip;
            public float length;
            /// <summary>How fast the player went over the ground in it (metres a second).</summary>
            public float speed;
            /// <summary>When in it they left the ground (a jump).</summary>
            public float jumpAt;
            /// <summary>The gun it was done with (a reload, firing): its barcode and name.</summary>
            public string gun = "", gunName = "";
            /// <summary>How many things were learned in it (climbs, reloads, moments of aim).</summary>
            public int count;
            /// <summary>How high the player's eyes were in the avatar it was recorded in (to size it to another body).</summary>
            public float eyes;
        }

        /// <summary>One avatar's moves: its slots, kind by kind (in a folder of its own).</summary>
        public class Set
        {
            public string avatar = "", name = "", folder = "";
            /// <summary>A number of its own (for what is learned elsewhere on a slot's account to be marked with).</summary>
            public int id;
            public List<Slot>[] slots;

            public int Filled
            {
                get
                {
                    int n = 0;
                    foreach (List<Slot> list in slots)
                    {
                        foreach (Slot slot in list)
                            n += slot.filled ? 1 : 0;
                    }
                    return n;
                }
            }

            public bool Has(Kind kind)
            {
                foreach (Slot slot in slots[(int)kind])
                {
                    if (slot.clip != null)
                        return true;
                }
                return false;
            }
        }

        private static Dictionary<string, Set> sets;
        private static readonly Dictionary<Frame[], Slot> byClip = new Dictionary<Frame[], Slot>();
        private static Set editing;
        private static float lookedAt = -10f;

        private static string Root => AssetFiles.SaveFolder("moves");

        private static string Safe(string code)
        {
            char[] c = code.ToCharArray();
            for (int i = 0; i < c.Length; i++)
            {
                if (!char.IsLetterOrDigit(c[i]) && c[i] != '.' && c[i] != '-')
                    c[i] = '_';
            }
            return new string(c);
        }

        private static int IdOf(string code)
        {
            uint h = 2166136261;
            foreach (char c in code)
                h = (h ^ c) * 16777619;
            return (int)(h % 100000u) + 1;
        }

        // Every avatar's moves, read from their folders.
        private static Dictionary<string, Set> Sets
        {
            get
            {
                if (sets != null)
                    return sets;
                sets = new Dictionary<string, Set>();
                byClip.Clear();
                try
                {
                    if (Directory.Exists(Root))
                    {
                        foreach (string folder in Directory.GetDirectories(Root))
                        {
                            string about = Path.Combine(folder, "set.txt");
                            if (!File.Exists(about))
                                continue;
                            string[] lines = File.ReadAllLines(about);
                            if (lines.Length == 0 || lines[0].Length == 0)
                                continue;
                            Set set = Make(lines[0], lines.Length > 1 ? lines[1] : lines[0], folder);
                            sets[set.avatar] = set;
                        }
                    }
                }
                catch (Exception e)
                {
                    MelonLogger.Warning("Bot Player: the moves could not be read: " + e.Message);
                }
                return sets;
            }
        }

        // A set, with what its folder holds (nothing, for one newly made).
        private static Set Make(string avatar, string name, string folder)
        {
            Set set = new Set { avatar = avatar, name = name, folder = folder, id = IdOf(avatar), slots = new List<Slot>[Names.Length] };
            int[] counts = ReadCounts(set);
            for (int k = 0; k < Names.Length; k++)
            {
                set.slots[k] = new List<Slot>();
                for (int i = 0; i < counts[k]; i++)
                {
                    set.slots[k].Add(new Slot());
                    try
                    {
                        Read(set, (Kind)k, i);
                    }
                    catch (Exception e)
                    {
                        MelonLogger.Warning("Bot Player: a move could not be read (" + name + ", " + (Kind)k + " " + (i + 1) + "): " + e.Message);
                    }
                }
            }
            return set;
        }

        // The avatar the player has on: its barcode, its name, and how high its eyes are.
        private static void PlayerAvatar(out string code, out string name, out float eyes)
        {
            code = "default";
            name = "your avatar";
            eyes = 0f;
            try
            {
                RigManager rig = Player.RigManager;
                if (rig == null)
                    return;
                string barcode = BotRig.AvatarOf(rig);
                if (!string.IsNullOrEmpty(barcode))
                    code = barcode;
                Il2CppSLZ.VRMK.Avatar avatar = rig.avatar;
                if (avatar != null)
                {
                    name = avatar.name.Replace("(Clone)", "").Trim();
                    eyes = avatar.eyeHeight * avatar.transform.lossyScale.y;
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>The set being recorded into and shown in the menu: that of the avatar the player has on.</summary>
        private static Set Editing
        {
            get
            {
                if (editing != null && Time.time - lookedAt < 1f)
                    return editing;
                lookedAt = Time.time;
                PlayerAvatar(out string code, out string name, out _);
                if (editing != null && editing.avatar == code)
                    return editing;
                if (!Sets.TryGetValue(code, out Set set))
                {
                    set = Make(code, name, Path.Combine(Root, Safe(code)));
                    Sets[code] = set;
                }
                bool changed = editing != null && editing.avatar != "default";
                bool first = editing != null && !changed;
                editing = set;
                if (first)
                    Menu();
                if (changed)
                {
                    // Another avatar on: its own moves are the ones in the menu now.
                    if (recording || counting)
                    {
                        recording = counting = false;
                        GunMoves.Teaching = taughtBefore;
                        Spawnables.Notify("Recording called off: you changed avatar");
                    }
                    armedKind = -1;
                    Spawnables.Notify("Moves: now those of " + set.name + " (" + set.Filled + " recorded)");
                    Menu();
                }
                return editing;
            }
        }

        private static List<Slot>[] Slots => Editing.slots;
        private static string Folder => Editing.folder;
        private static string FileOf(Kind kind, int i) => FileOf(Editing, kind, i);
        private static string FileOf(Set set, Kind kind, int i) => Path.Combine(set.folder, kind + "_" + (i + 1) + ".csv");

        // How many slots each kind has (kept beside the moves; five each to begin with).
        private static int[] ReadCounts(Set set)
        {
            int[] counts = new int[Names.Length];
            for (int k = 0; k < counts.Length; k++)
                counts[k] = Each;
            try
            {
                string file = Path.Combine(set.folder, "slots.txt");
                if (File.Exists(file))
                {
                    foreach (string line in File.ReadAllLines(file))
                    {
                        string[] p = line.Split('=');
                        if (p.Length == 2 && Enum.TryParse(p[0], out Kind kind) && int.TryParse(p[1], out int n))
                            counts[(int)kind] = Mathf.Clamp(n, 1, Most);
                    }
                }
            }
            catch (Exception)
            {
            }
            return counts;
        }

        // The set's folder, with whose it is written in it.
        private static void EnsureFolder(Set set)
        {
            Directory.CreateDirectory(set.folder);
            string about = Path.Combine(set.folder, "set.txt");
            if (!File.Exists(about))
                File.WriteAllLines(about, new[] { set.avatar, set.name });
        }

        private static void WriteCounts()
        {
            try
            {
                Set set = Editing;
                EnsureFolder(set);
                List<string> lines = new List<string>();
                for (int k = 0; k < Names.Length; k++)
                    lines.Add((Kind)k + "=" + set.slots[k].Count);
                File.WriteAllLines(Path.Combine(set.folder, "slots.txt"), lines);
            }
            catch (Exception e)
            {
                MelonLogger.Warning("Bot Player: the number of slots could not be saved: " + e.Message);
            }
        }

        /// <summary>Read again from its files (after a fresh start).</summary>
        public static void Reload()
        {
            sets = null;
            editing = null;
            armedKind = -1;
            Menu();
        }

        private static void Read(Set set, Kind kind, int i)
        {
            string file = FileOf(set, kind, i);
            if (!File.Exists(file))
                return;
            Slot slot = set.slots[(int)kind][i];
            foreach (string line in File.ReadLines(file))
            {
                if (!line.StartsWith("#,", StringComparison.Ordinal))
                    break;
                foreach (string part in line.Substring(2).Split(';'))
                {
                    int eq = part.IndexOf('=');
                    if (eq <= 0)
                        continue;
                    string key = part.Substring(0, eq), value = part.Substring(eq + 1);
                    switch (key)
                    {
                        case "length": slot.length = float.Parse(value, CultureInfo.InvariantCulture); break;
                        case "speed": slot.speed = float.Parse(value, CultureInfo.InvariantCulture); break;
                        case "jumpAt": slot.jumpAt = float.Parse(value, CultureInfo.InvariantCulture); break;
                        case "eyes": slot.eyes = float.Parse(value, CultureInfo.InvariantCulture); break;
                        case "gun": slot.gun = value; break;
                        case "gunName": slot.gunName = value; break;
                        case "count": slot.count = int.Parse(value, CultureInfo.InvariantCulture); break;
                    }
                }
            }
            List<Frame> frames = Recorder.Load(file);
            if (frames.Count > 1)
            {
                slot.clip = frames.ToArray();
                byClip[slot.clip] = slot;
            }
            slot.filled = slot.clip != null || slot.count > 0;
        }

        private static void Write(Kind kind, int i, Slot slot)
        {
            EnsureFolder(Editing);
            using StreamWriter w = new StreamWriter(FileOf(kind, i), false, new System.Text.UTF8Encoding(false));
            w.WriteLine("#,length=" + slot.length.ToString("0.###", CultureInfo.InvariantCulture) + ";speed=" + slot.speed.ToString("0.###", CultureInfo.InvariantCulture)
                + ";jumpAt=" + slot.jumpAt.ToString("0.###", CultureInfo.InvariantCulture) + ";eyes=" + slot.eyes.ToString("0.###", CultureInfo.InvariantCulture)
                + ";gun=" + slot.gun + ";gunName=" + slot.gunName.Replace(';', ' ').Replace('=', ' ')
                + ";count=" + slot.count.ToString(CultureInfo.InvariantCulture));
            if (slot.clip != null)
            {
                foreach (Frame f in slot.clip)
                    w.WriteLine(Recorder.Write(f));
            }
        }

        // ---------------------------------------------------------------- for the bots

        /// <summary>Whether the avatar the player has on has anything of the kind.</summary>
        public static bool Any(Kind kind)
        {
            foreach (Slot slot in Slots[(int)kind])
            {
                if (slot.filled)
                    return true;
            }
            return false;
        }

        // One of a set's moves of a kind (not one played lately, if there is another); null if it has none.
        private static Frame[] PickIn(Set set, Kind kind, List<Frame[]> lately)
        {
            if (set == null)
                return null;
            List<Frame[]> have = new List<Frame[]>();
            List<Frame[]> fresh = new List<Frame[]>();
            foreach (Slot slot in set.slots[(int)kind])
            {
                if (slot.clip == null)
                    continue;
                have.Add(slot.clip);
                if (lately == null || !lately.Contains(slot.clip))
                    fresh.Add(slot.clip);
            }
            if (have.Count == 0)
                return null;
            List<Frame[]> from = fresh.Count > 0 ? fresh : have;
            return from[UnityEngine.Random.Range(0, from.Count)];
        }

        /// <summary>
        /// One of the kind's moves for a bot in that avatar: its own avatar's, if it has any of
        /// the kind; else borrowed from the avatar with the most recorded that has (played
        /// sized to the bot: see Size). Null if no avatar has any.
        /// </summary>
        public static Frame[] Pick(Kind kind, List<Frame[]> lately, string avatar)
        {
            Set own = null;
            if (!string.IsNullOrEmpty(avatar))
                Sets.TryGetValue(avatar, out own);
            Frame[] clip = PickIn(own, kind, lately);
            if (clip != null)
                return clip;
            Set lender = null;
            foreach (Set set in Sets.Values)
            {
                if (set != own && set.Has(kind) && (lender == null || set.Filled > lender.Filled))
                    lender = set;
            }
            return PickIn(lender, kind, lately);
        }

        /// <summary>The move for a gait: standing; walking (the slow walks for a stroll, the fast for its usual pace, either if only one is there); running.</summary>
        public static Frame[] For(MotionLibrary.Gait gait, bool slow, List<Frame[]> lately, string avatar)
        {
            switch (gait)
            {
                case MotionLibrary.Gait.Idle:
                    return Pick(Kind.Idle, lately, avatar);
                case MotionLibrary.Gait.Walk:
                    return Pick(slow ? Kind.Walk : Kind.FastWalk, lately, avatar) ?? Pick(slow ? Kind.FastWalk : Kind.Walk, lately, avatar) ?? Pick(Kind.Run, lately, avatar);
                default:
                    return Pick(Kind.Run, lately, avatar) ?? Pick(Kind.FastWalk, lately, avatar) ?? Pick(Kind.Walk, lately, avatar);
            }
        }

        public static bool IsSlot(Frame[] clip) => clip != null && byClip.ContainsKey(clip);

        public static float SpeedOf(Frame[] clip) => clip != null && byClip.TryGetValue(clip, out Slot s) ? s.speed : 0f;

        public static float JumpAt(Frame[] clip) => clip != null && byClip.TryGetValue(clip, out Slot s) ? s.jumpAt : 0f;

        /// <summary>How much bigger (or smaller) a body with its eyes so high is than the one the move was recorded in.</summary>
        public static float Size(Frame[] clip, float eyes)
        {
            if (clip == null || eyes < 0.3f || !byClip.TryGetValue(clip, out Slot s))
                return 1f;
            float theirs = s.eyes > 0.3f ? s.eyes : clip[0].height;
            return theirs > 0.3f ? Mathf.Clamp(eyes / theirs, 0.3f, 3.5f) : 1f;
        }

        /// <summary>The kinds with nothing in any slot yet (for the avatar the player has on), by name.</summary>
        public static string EmptyKinds()
        {
            List<string> empty = new List<string>();
            for (int k = 0; k < Names.Length; k++)
            {
                if (!Any((Kind)k))
                    empty.Add(Names[k]);
            }
            return string.Join(", ", empty);
        }

        public static string Summary()
        {
            int filled = 0;
            List<string> kinds = new List<string>();
            for (int k = 0; k < Names.Length; k++)
            {
                int n = 0;
                for (int i = 0; i < Slots[k].Count; i++)
                    n += Slots[k][i].filled ? 1 : 0;
                filled += n;
                if (n > 0)
                    kinds.Add(Names[k] + " " + n);
            }
            int others = 0;
            foreach (Set set in Sets.Values)
                others += set != Editing && set.Filled > 0 ? 1 : 0;
            return (filled == 0 ? "no moves recorded for " + Editing.name + " yet" : "moves of " + Editing.name + ": " + string.Join(", ", kinds))
                + (others > 0 ? " (" + others + " other avatars have moves)" : "");
        }

        // ---------------------------------------------------------------- recording

        private static int armedKind = -1, armedSlot;
        private static bool recording, counting;
        private static float countUntil, startedAt, nextFrameAt, lastJumpSeen;
        private static int counted;
        private static readonly List<Frame> taking = new List<Frame>();
        private static float bothSince = -1f;
        private static bool bothUsed;
        private static bool taughtBefore;
        private static int climbsBefore, reloadsBefore, aimsBefore;
        private static float shotsBefore;

        /// <summary>The kind being recorded into a slot now (or about to be); -1 if none.</summary>
        public static bool Recording(Kind kind) => (recording || counting) && armedKind == (int)kind;

        /// <summary>Which slot of the kind is being recorded (for what is learned meanwhile to be marked as its own).</summary>
        public static int RecordingSlot(Kind kind) => recording && armedKind == (int)kind ? armedSlot : -1;

        /// <summary>Once a frame: the signal (both sticks pressed in and held), the count, and the recording itself.</summary>
        public static void Update()
        {
            OpenControllerRig controls = Player.ControllerRig;
            if (controls == null || controls.leftController == null || controls.rightController == null)
                return;
            // (The avatar the player has on is looked at as it goes: its moves are the ones in the menu.)
            _ = Editing;
            bool both = controls.leftController._thumbstick && controls.rightController._thumbstick;
            if (!both)
            {
                bothSince = -1f;
                bothUsed = false;
            }
            else if (bothSince < 0f)
            {
                bothSince = Time.time;
            }
            if (both && !bothUsed && Time.time - bothSince >= Tuning.F("moves.hold", 0.7f))
            {
                bothUsed = true;
                Signal();
            }
            if (counting && Time.time >= countUntil)
                Begin();
            if (recording)
                Sample();
            Hud();
        }

        private static void Signal()
        {
            if (recording)
            {
                End();
                return;
            }
            if (counting)
            {
                counting = false;
                Spawnables.Notify("Recording called off");
                return;
            }
            if (armedKind < 0)
            {
                Spawnables.Notify("Pick a slot first: Bot Player, Moves");
                return;
            }
            // (Not straight after one has ended: the hold that stopped it is not a new start.)
            if (Time.time - endedAt < 1.5f)
                return;
            counting = true;
            counted = 0;
            countUntil = Time.time + 3f;
        }

        private static void Begin()
        {
            counting = false;
            recording = true;
            startedAt = Time.time;
            nextFrameAt = 0f;
            lastJumpSeen = Recorder.PlayerJumpedAt;
            taking.Clear();
            Kind kind = (Kind)armedKind;
            // (A reload and firing are learned as the bots' gun handling always was, for as long as this goes on.)
            taughtBefore = GunMoves.Teaching;
            if (kind == Kind.Reload || kind == Kind.Fire)
                GunMoves.Teaching = true;
            GunMoves.SlotFresh(kind == Kind.Fire);
            // (A climb recorded into a slot again takes the place of the one that was there.)
            if (kind == Kind.Climb)
                ClimbMoves.ClearSlot(Tag(kind, armedSlot));
            climbsBefore = ClimbMoves.Count;
            GunMoves.Lessons(out _, out reloadsBefore, out _);
            aimsBefore = GunMoves.AimMoments();
            shotsBefore = PlayerWatch.ShotsSeen;
            Lab.Event("moves: recording " + kind + " " + (armedSlot + 1));
        }

        private static void Sample()
        {
            float t = Time.time - startedAt;
            if (t < nextFrameAt)
                return;
            nextFrameAt = t + 1f / 60f;
            if (!Recorder.Capture(out Frame f))
                return;
            f.time = t;
            f.enemyDistance = -1f;
            f.leftHeld = f.rightHeld = "";
            f.jumped = Recorder.PlayerJumpedAt > lastJumpSeen;
            if (f.jumped)
                lastJumpSeen = Recorder.PlayerJumpedAt;
            taking.Add(f);
            if (t > Tuning.F("moves.longest", 30f))
                End();
        }

        private static void End()
        {
            recording = false;
            endedAt = Time.time;
            Kind kind = (Kind)armedKind;
            GunMoves.Teaching = taughtBefore;
            GunMoves.SlotFresh(false);
            float end = Time.time - startedAt;
            // (Its ends cut off: settling in at the start, and the hands going to the sticks to stop it.)
            float from = 0.25f, to = end - Tuning.F("moves.hold", 0.7f) - 0.35f;
            List<Frame> kept = taking.FindAll(f => f.time >= from && f.time <= to);
            Tighten(kind, kept);
            Slot slot = new Slot();
            PlayerAvatar(out _, out _, out slot.eyes);
            string wrong = null;
            if (kind == Kind.Climb)
            {
                slot.count = ClimbMoves.Count - climbsBefore;
                slot.length = to;
                if (slot.count <= 0)
                    wrong = "no climb was seen (take hold of a ledge and pull yourself up onto it)";
            }
            else if (kind == Kind.Reload || kind == Kind.Fire)
            {
                GunMoves.Lessons(out _, out int reloads, out _);
                slot.count = kind == Kind.Reload ? reloads - reloadsBefore : (GunMoves.AimMoments() - aimsBefore) + Mathf.RoundToInt(PlayerWatch.ShotsSeen - shotsBefore);
                slot.length = to;
                HeldGun(out slot.gun, out slot.gunName);
                if (slot.count <= 0)
                    wrong = kind == Kind.Reload ? "no reload was seen (magazine out, a new one in, the slide worked)" : "no aiming or firing was seen (hold the gun up and fire it)";
            }
            else
            {
                if (kept.Count < 2 || kept[kept.Count - 1].time - kept[0].time < 0.8f)
                {
                    wrong = "too short";
                }
                else
                {
                    float t0 = kept[0].time;
                    float way = 0f;
                    for (int j = 0; j < kept.Count; j++)
                    {
                        Frame f = kept[j];
                        f.time -= t0;
                        if (f.jumped && slot.jumpAt <= 0f)
                            slot.jumpAt = f.time;
                        if (j > 0)
                            way += new Vector2(f.headWorld.x - kept[j - 1].headWorld.x, f.headWorld.z - kept[j - 1].headWorld.z).magnitude;
                        kept[j] = f;
                    }
                    slot.clip = kept.ToArray();
                    slot.length = kept[kept.Count - 1].time;
                    slot.speed = way / Mathf.Max(0.1f, slot.length);
                    bool moving = kind == Kind.Walk || kind == Kind.FastWalk || kind == Kind.Run;
                    if (moving && slot.speed < 0.4f)
                        wrong = "you were hardly moving in it (" + slot.speed.ToString("0.0") + " m/s)";
                    else if (kind == Kind.Idle && slot.speed > 0.5f)
                        wrong = "you were moving in it (" + slot.speed.ToString("0.0") + " m/s)";
                    else if (kind == Kind.Jump && slot.jumpAt <= 0f)
                        wrong = "there was no jump in it";
                    else if (Glitched(slot.clip))
                        wrong = "your tracking jumped in it";
                }
            }
            hudNote = wrong != null ? "<color=#ff6060>Not saved</color>" : "<color=#70ff90>Saved</color>";
            hudNoteUntil = Time.time + 1.6f;
            if (wrong != null)
            {
                // (What was learned meanwhile for a slot not kept is not kept either.)
                if (kind == Kind.Climb)
                    ClimbMoves.ClearSlot(Tag(kind, armedSlot));
                Spawnables.Notify(Names[armedKind] + " " + (armedSlot + 1) + " not saved: " + wrong + ". Hold both sticks to try again.");
                Lab.Event("moves: " + kind + " " + (armedSlot + 1) + " not saved: " + wrong);
                return;
            }
            slot.filled = true;
            Slot old = Slots[armedKind][armedSlot];
            if (old.clip != null)
                byClip.Remove(old.clip);
            Slots[armedKind][armedSlot] = slot;
            if (slot.clip != null)
                byClip[slot.clip] = slot;
            try
            {
                Write(kind, armedSlot, slot);
            }
            catch (Exception e)
            {
                MelonLogger.Warning("Bot Player: the move could not be saved: " + e.Message);
            }
            string saved = Names[armedKind] + " " + (armedSlot + 1) + " saved: " + Describe(kind, slot);
            Lab.Event("moves: " + saved);
            int was = armedSlot;
            // Shown once on the nearest bot; and on to the kind's next empty slot.
            if (slot.clip != null)
                BotManager.Preview(slot.clip);
            int have = Slots[armedKind].Count;
            for (int step = 1; step <= have; step++)
            {
                int next = (was + step) % have;
                if (!Slots[armedKind][next].filled)
                {
                    armedSlot = next;
                    saved += ". Next: slot " + (next + 1);
                    break;
                }
            }
            Spawnables.Notify(saved);
            Menu();
        }

        private static float endedAt = -10f;

        // The waiting at either end taken off: a move begins when the player begins it (not
        // when the count ends) and ends when they are done (not when they reach for the sticks).
        private static void Tighten(Kind kind, List<Frame> frames)
        {
            if (frames.Count < 20 || kind == Kind.Idle || kind == Kind.Climb || kind == Kind.Reload || kind == Kind.Fire)
                return;
            int first = 0, last = frames.Count - 1;
            if (kind == Kind.Jump)
            {
                int jump = frames.FindIndex(f => f.jumped);
                if (jump < 0)
                    return;
                float at = frames[jump].time;
                first = frames.FindIndex(f => f.time >= at - 0.7f);
                last = frames.FindLastIndex(f => f.time <= at + 1.3f);
            }
            else
            {
                // How much is going on at each moment: going over the ground (a walk, a run), or
                // the hands and head moving (a wave, a crouch).
                bool moving = kind == Kind.Walk || kind == Kind.FastWalk || kind == Kind.Run;
                float need = moving ? 0.45f : 0.35f;
                bool Busy(int j)
                {
                    int a = Mathf.Max(0, j - 6), b = Mathf.Min(frames.Count - 1, j + 6);
                    float dt = frames[b].time - frames[a].time;
                    if (dt <= 0f)
                        return false;
                    if (moving)
                        return new Vector2(frames[b].headWorld.x - frames[a].headWorld.x, frames[b].headWorld.z - frames[a].headWorld.z).magnitude / dt > need;
                    float most = Mathf.Max((frames[b].leftPos - frames[a].leftPos).magnitude, (frames[b].rightPos - frames[a].rightPos).magnitude, Mathf.Abs(frames[b].height - frames[a].height));
                    return most / dt > need;
                }
                while (first < last && !Busy(first))
                    first++;
                while (last > first && !Busy(last))
                    last--;
                // (A little of the stillness kept at each end, for a wave or a crouch to come out of and go back to.)
                float lead = moving ? 0f : 0.25f;
                float start = frames[first].time - lead, stop = frames[last].time + lead;
                first = frames.FindIndex(f => f.time >= start);
                last = frames.FindLastIndex(f => f.time <= stop);
            }
            if (first < 0 || last <= first || frames[last].time - frames[first].time < 0.8f)
                return;
            frames.RemoveRange(last + 1, frames.Count - last - 1);
            frames.RemoveRange(0, first);
        }

        // ---------------------------------------------------------------- what the player sees while recording

        private static TextMeshPro hud;
        private static string hudNote = "";
        private static float hudNoteUntil;

        // The count, the recording light, and "saved", in front of the player's eyes as they
        // happen (the game's notices come one after another, seconds late: no use for a count).
        private static void Hud()
        {
            string text = null;
            float down = 0.1f;
            if (counting)
            {
                text = "<size=260%>" + Mathf.Max(1, Mathf.CeilToInt(countUntil - Time.time)) + "</size>\n<size=70%>" + Names[armedKind] + " " + (armedSlot + 1) + ": " + Hints[armedKind] + "</size>";
                down = 0.02f;
            }
            else if (recording)
            {
                text = "<color=#ff4040>\u25CF REC</color> " + (Time.time - startedAt).ToString("0.0") + " s\n<size=60%>" + Names[armedKind] + " " + (armedSlot + 1) + " - hold both sticks to stop</size>";
                down = 0.2f;
            }
            else if (Time.time < hudNoteUntil)
            {
                text = hudNote;
            }
            Transform head = Player.Head;
            if (text == null || head == null)
            {
                if (hud != null)
                    hud.gameObject.SetActive(false);
                return;
            }
            try
            {
                if (hud == null)
                {
                    GameObject old = GameObject.Find("ThirdEyeMovesHud");
                    if (old != null)
                        UnityEngine.Object.Destroy(old);
                    GameObject label = new GameObject("ThirdEyeMovesHud");
                    hud = label.AddComponent<TextMeshPro>();
                    foreach (TMP_FontAsset candidate in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
                    {
                        if (candidate != null)
                        {
                            hud.font = candidate;
                            break;
                        }
                    }
                    hud.fontSize = 0.45f;
                    hud.rectTransform.sizeDelta = new Vector2(2f, 0.8f);
                    hud.alignment = TextAlignmentOptions.Center;
                    hud.enableWordWrapping = false;
                    hud.richText = true;
                    hud.color = Color.white;
                }
                hud.gameObject.SetActive(true);
                hud.text = text;
                Transform t = hud.transform;
                t.position = head.position + head.forward * 0.75f - head.up * down;
                t.rotation = Quaternion.LookRotation(head.forward, head.up);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>Its text taken away (a new build takes over).</summary>
        public static void Shutdown()
        {
            try
            {
                if (hud != null)
                    UnityEngine.Object.Destroy(hud.gameObject);
            }
            catch (Exception)
            {
            }
            hud = null;
            recording = counting = false;
        }

        // A hand or the head going from one place to another faster than a body can (tracking lost).
        private static bool Glitched(Frame[] clip)
        {
            for (int j = 1; j < clip.Length; j++)
            {
                float dt = clip[j].time - clip[j - 1].time;
                if (dt <= 0f)
                    continue;
                if ((clip[j].leftPos - clip[j - 1].leftPos).magnitude / dt > 9f || (clip[j].rightPos - clip[j - 1].rightPos).magnitude / dt > 9f
                    || Mathf.Abs(clip[j].height - clip[j - 1].height) / dt > 5f)
                    return true;
            }
            return false;
        }

        private static void HeldGun(out string barcode, out string name)
        {
            barcode = name = "";
            try
            {
                Gun gun = Player.GetComponentInHand<Gun>(Player.RightHand) ?? Player.GetComponentInHand<Gun>(Player.LeftHand);
                Poolee poolee = gun != null ? gun.GetComponentInParent<Poolee>() : null;
                if (poolee == null || poolee.SpawnableCrate == null)
                    return;
                barcode = poolee.SpawnableCrate.Barcode.ID;
                name = Lessons.NameOf(poolee);
            }
            catch (Exception)
            {
            }
        }

        private static string Describe(Kind kind, Slot slot)
        {
            if (!slot.filled)
                return "empty";
            switch (kind)
            {
                case Kind.Climb:
                    return slot.count + (slot.count == 1 ? " climb" : " climbs");
                case Kind.Reload:
                    return slot.count + (slot.count == 1 ? " reload" : " reloads") + (slot.gunName.Length > 0 ? ", " + slot.gunName : "");
                case Kind.Fire:
                    return "aim and fire" + (slot.gunName.Length > 0 ? ", " + slot.gunName : "");
                case Kind.Walk:
                case Kind.FastWalk:
                case Kind.Run:
                    return slot.length.ToString("0.0") + " s at " + slot.speed.ToString("0.0") + " m/s";
                default:
                    return slot.length.ToString("0.0") + " s";
            }
        }

        private static void Clear(Kind kind, int i)
        {
            Slot slot = Slots[(int)kind][i];
            if (!slot.filled)
                return;
            if (slot.clip != null)
                byClip.Remove(slot.clip);
            if (kind == Kind.Climb)
                ClimbMoves.ClearSlot(Tag(kind, i));
            if ((kind == Kind.Reload || kind == Kind.Fire) && slot.gun.Length > 0)
                GunMoves.ForgetGun(slot.gun, kind == Kind.Reload, kind == Kind.Fire);
            Slots[(int)kind][i] = new Slot();
            try
            {
                File.Delete(FileOf(kind, i));
            }
            catch (Exception)
            {
            }
            Spawnables.Notify(Names[(int)kind] + " " + (i + 1) + " cleared");
            Menu();
        }

        // One more slot for the kind (empty, and picked, ready to record into).
        private static void Add(Kind kind)
        {
            List<Slot> list = Slots[(int)kind];
            if (recording || counting)
                return;
            if (list.Count >= Most)
            {
                Spawnables.Notify(Names[(int)kind] + " has " + Most + " slots: no more");
                return;
            }
            list.Add(new Slot());
            armedKind = (int)kind;
            armedSlot = list.Count - 1;
            WriteCounts();
            Spawnables.Notify(Names[(int)kind] + " now has " + list.Count + " slots. Slot " + list.Count + " picked: hold both thumbsticks to record");
            Menu();
        }

        // The slot gone, with what was in it; those after it move up one.
        private static void Remove(Kind kind, int i)
        {
            List<Slot> list = Slots[(int)kind];
            if (recording || counting || i < 0 || i >= list.Count)
                return;
            if (list.Count <= 1)
            {
                Spawnables.Notify(Names[(int)kind] + " keeps one slot at least (clear it instead)");
                return;
            }
            Slot gone = list[i];
            if (gone.clip != null)
                byClip.Remove(gone.clip);
            if (kind == Kind.Climb)
                ClimbMoves.ClearSlot(Tag(kind, i));
            if ((kind == Kind.Reload || kind == Kind.Fire) && gone.gun.Length > 0)
            {
                // (Unless another slot of the kind is for the same gun.)
                bool shared = false;
                for (int j = 0; j < list.Count; j++)
                    shared |= j != i && list[j].filled && list[j].gun == gone.gun;
                if (!shared)
                    GunMoves.ForgetGun(gone.gun, kind == Kind.Reload, kind == Kind.Fire);
            }
            list.RemoveAt(i);
            try
            {
                // Their files, and what is marked as theirs, move up with them.
                File.Delete(FileOf(kind, i));
                for (int j = i; j < list.Count; j++)
                {
                    if (File.Exists(FileOf(kind, j + 1)))
                        File.Move(FileOf(kind, j + 1), FileOf(kind, j));
                    if (kind == Kind.Climb)
                        ClimbMoves.MoveSlot(Tag(kind, j + 1), Tag(kind, j));
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning("Bot Player: the slots' files could not be moved up: " + e.Message);
            }
            armedSlot = Mathf.Clamp(armedSlot, 0, list.Count - 1);
            WriteCounts();
            Spawnables.Notify(Names[(int)kind] + " slot " + (i + 1) + " removed: " + list.Count + " left");
            Menu();
        }

        /// <summary>For the lab: a slot added to a kind, or its picked one removed.</summary>
        public static void LabAddRemove(string kindName, bool add)
        {
            if (!Enum.TryParse(kindName, true, out Kind kind))
                return;
            if (add)
                Add(kind);
            else if (armedKind == (int)kind)
                Remove(kind, armedSlot);
            Lab.Event("moves: " + kind + " has " + Slots[(int)kind].Count + " slots");
        }

        // ---------------------------------------------------------------- the menu

        private static Page movesPage;
        private static readonly Page[] kindPages = new Page[32];

        /// <summary>Its pages in the menu: a page of the kinds, and for each kind its five slots.</summary>
        public static void BuildMenu(Page page)
        {
            movesPage = page.CreatePage("Moves (record in slots)", Color.green);
            movesPage.CreateFunction("Whose moves are these?", Color.cyan, () =>
                Spawnables.Notify("Each avatar has its own moves. " + Summary() + ". An avatar with none of a kind borrows another's, sized to it."));
            for (int k = 0; k < Names.Length; k++)
                kindPages[k] = movesPage.CreatePage(Names[k], Color.white);
            Menu();
        }

        private static void Menu()
        {
            if (movesPage == null)
                return;
            try
            {
                for (int k = 0; k < Names.Length; k++)
                {
                    Page p = kindPages[k];
                    if (p == null)
                        continue;
                    p.RemoveAll();
                    int kind = k;
                    for (int i = 0; i < Slots[kind].Count; i++)
                    {
                        int index = i;
                        bool armed = armedKind == kind && armedSlot == index;
                        p.CreateFunction((armed ? "> " : "") + "Slot " + (i + 1) + ": " + Describe((Kind)kind, Slots[kind][i]), armed ? Color.green : Slots[kind][i].filled ? Color.white : Color.gray, () =>
                        {
                            armedKind = kind;
                            armedSlot = index;
                            Spawnables.Notify(Names[kind] + " " + (index + 1) + " picked. Hold both thumbsticks: it counts 3 and records (" + Hints[kind] + "). Hold both again to stop.");
                            Menu();
                        });
                    }
                    p.CreateFunction("Show the picked slot on a bot", Color.yellow, () =>
                    {
                        Slot slot = armedKind == kind ? Slots[kind][armedSlot] : null;
                        if (slot == null || slot.clip == null)
                            Spawnables.Notify("Pick a slot of this kind with a move in it first");
                        else
                            BotManager.Preview(slot.clip);
                    });
                    p.CreateFunction("Clear the picked slot", Color.red, () =>
                    {
                        if (armedKind == kind)
                            Clear((Kind)kind, armedSlot);
                        else
                            Spawnables.Notify("Pick a slot of this kind first");
                    });
                    p.CreateFunction("Add a slot", Color.cyan, () => Add((Kind)kind));
                    p.CreateFunction("Remove the picked slot", Color.red, () =>
                    {
                        if (armedKind == kind)
                            Remove((Kind)kind, armedSlot);
                        else
                            Spawnables.Notify("Pick a slot of this kind first");
                    });
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning("Bot Player: the moves menu could not be made: " + e.Message);
            }
        }

        /// <summary>For the lab: a slot picked, and the signal given, as the player would.</summary>
        public static void LabArm(string kindName, int slot)
        {
            if (Enum.TryParse(kindName, true, out Kind kind))
            {
                armedKind = (int)kind;
                armedSlot = Mathf.Clamp(slot - 1, 0, Slots[armedKind].Count - 1);
                Menu();
                Lab.Event("moves: picked " + kind + " " + (armedSlot + 1) + "; " + Summary());
            }
            else
            {
                Lab.Event("moves: " + Summary());
            }
        }

        public static void LabSignal() => Signal();

        /// <summary>For the lab: what it is at (counting, recording, or nothing).</summary>
        public static string State() => recording ? "recording " + (Time.time - startedAt).ToString("0.0") + " s" : counting ? "counting" : "idle";
    }
}
