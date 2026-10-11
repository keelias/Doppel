using System.IO;
using BoneLib;
using Il2CppSLZ.Marrow;
using Il2CppSLZ.Marrow.Interaction;
using Il2CppSLZ.Marrow.Pool;
using MelonLoader;
using UnityEngine;
using Pose = ThirdEyeBonelabMod.Bots.Bot.Pose;

namespace ThirdEyeBonelabMod.Bots
{
    /// <summary>
    /// A clone recording: everything the player does, exactly, for a bot to do again just so,
    /// from where they started: where their body went (in the world), their head and hands,
    /// fingers, trigger, buttons and crouch, frame by frame; and what they did with things:
    /// each thing they spawned (what, where, when), each thing they took hold of and let go of
    /// (and by which of its grips), each shot. Kept in UserData/Bot Player/clones.
    /// </summary>
    public static class Clone
    {
        public struct HandInput
        {
            public float index, middle, ring, pinky, thumb, grip, trigger;
            public bool primary, secondary;
            public Vector2 stick;
            /// <summary>Every other button a tool may read: A, B, the menu tap, the stick clicked, the touch pad.</summary>
            public bool a, b, menu, click, pad;
            public Vector2 padAxis;
        }

        public struct Frame
        {
            public float t;
            public Vector3 pelvis, pelvisVelocity, pelvisSpin;
            public Quaternion pelvisTurn, playspace;
            public Pose head, left, right;
            public HandInput leftIn, rightIn;
            public bool aPrimary, aSecondary;
            public float crouch, feet;
            /// <summary>Where the play space was; and, sat in something, where its seat was (to tell how the thing is driven, see Driving).</summary>
            public Vector3 root;
            public bool seated;
            public Pose seat;
        }

        public enum Kind
        {
            Spawn,
            Grab,
            Release,
            Fire,
            /// <summary>The player put on another avatar (barcode; its scale kept in "at").</summary>
            Avatar,
            /// <summary>The player sat down in a seat of a thing (hand 1), or got out of it (hand 0); "grip" is which of the thing's seats.</summary>
            Seat,
        }

        public class Event
        {
            public float t;
            public Kind kind;
            /// <summary>0 left hand, 1 right.</summary>
            public int hand;
            /// <summary>Which spawn (of this recording) it is, or was taken from; -1 for a thing already about.</summary>
            public int spawn = -1;
            public string barcode = "";
            public string name = "";
            public Vector3 at;
            public Quaternion turn = Quaternion.identity;
            /// <summary>Which of the thing's grips (in the order they are found).</summary>
            public int grip = -1;
        }

        public class Take
        {
            public Vector3 startFeet;
            /// <summary>The avatar the player wore (its barcode and scale): a clone is played in it.</summary>
            public string avatar = "";
            public Vector3 avatarScale = Vector3.one;
            public float startYaw;
            /// <summary>A lesson's (see Lessons): the thing it is about, the level it was on, how high the player's eyes were.</summary>
            public string item = "", itemName = "", level = "";
            public float eyes;
            public List<Frame> frames = new List<Frame>();
            public List<Event> events = new List<Event>();
            public float Length => frames.Count > 0 ? frames[frames.Count - 1].t : 0f;
        }

        private const float Rate = 1f / 60f;

        /// <summary>
        /// One recording being made. There are two at most at a time: the one the player asks
        /// for ("Record clone"), and a lesson (how they use the thing in their hand, see Lessons).
        /// </summary>
        public class Session
        {
            public Take taking;
            public float startedAt;
            public float nextFrameAt;
            public readonly Dictionary<IntPtr, int> spawned = new Dictionary<IntPtr, int>();
            /// <summary>The things taken hold of, numbered as they are first taken (two alike are two things).</summary>
            public readonly Dictionary<IntPtr, int> things = new Dictionary<IntPtr, int>();
            public int spawns;
            public string wearing = "";
            /// <summary>The seat the player was in when last looked at.</summary>
            public IntPtr seat;
            // Buttons pressed since the last frame kept (a tap shorter than a frame is not lost).
            public HandInput leftLatch, rightLatch;
            public bool On => taking != null;
            public float Now => Time.time - startedAt;
        }

        private static readonly Session manual = new Session();

        /// <summary>The lesson being recorded (Lessons starts and ends it).</summary>
        public static readonly Session Lesson = new Session();

        private static readonly Session[] sessions = { manual, Lesson };

        public static bool Recording => manual.taking != null;
        private static Take last;

        /// <summary>Bots' own spawns (not the player's) are not recorded: set just before one.</summary>
        public static float SuppressUntil;

        public static void Suppress()
        {
            SuppressUntil = Time.time + 1.5f;
        }

        private static string Folder => AssetFiles.SaveFolder("clones");

        public static void Set(bool on)
        {
            if (on)
                Start();
            else
                Stop();
        }

        public static void Start()
        {
            if (!Begin(manual))
                return;
            Spawnables.Notify("Clone recording: everything you do");
            Lab.Event("clone: recording");
        }

        /// <summary>Starts a recording; false if it is going already (or there is no player to record).</summary>
        public static bool Begin(Session s)
        {
            if (s.taking != null || Player.PhysicsRig == null || Player.Head == null || Player.RigManager == null)
                return false;
            Take taking = new Take();
            s.startedAt = Time.time;
            s.nextFrameAt = 0f;
            s.spawned.Clear();
            s.things.Clear();
            s.spawns = 0;
            s.leftLatch = s.rightLatch = default;
            s.seat = IntPtr.Zero;
            Rig physics = Player.PhysicsRig;
            taking.startFeet = physics.centerOfPressure != null ? physics.centerOfPressure.position : Player.Head.position - Vector3.up * 1.6f;
            taking.startYaw = Recorder.Yaw(Player.Head.forward);
            taking.avatar = BotRig.AvatarOf(Player.RigManager);
            Il2CppSLZ.VRMK.Avatar avatar = Player.RigManager.avatar;
            taking.avatarScale = avatar != null ? avatar.transform.localScale : Vector3.one;
            taking.eyes = avatar != null ? avatar.eyeHeight * avatar.transform.lossyScale.y : 0f;
            s.wearing = taking.avatar;
            s.taking = taking;
            return true;
        }

        /// <summary>Ends a recording and hands it over (null if none was going).</summary>
        public static Take End(Session s)
        {
            Take take = s.taking;
            s.taking = null;
            return take;
        }

        public static void Stop()
        {
            if (manual.taking == null)
                return;
            last = manual.taking;
            manual.taking = null;
            Spawnables.Notify("Clone recorded: " + last.Length.ToString("0") + " s, " + last.events.Count + " things done");
            Lab.Event("clone: recorded " + last.Length.ToString("0.0") + " s, " + last.frames.Count + " frames, " + last.events.Count + " events");
            try
            {
                Save(last, Path.Combine(Folder, "clone_last.bin"));
                Save(last, Path.Combine(Folder, "clone_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".bin"));
            }
            catch (Exception e)
            {
                MelonLogger.Warning("Bot Player: the clone could not be saved: " + e.Message);
            }
        }

        /// <summary>The last clone recorded (or, after a new build, read back from its file).</summary>
        public static Take Last
        {
            get
            {
                if (last == null)
                {
                    try
                    {
                        string file = Path.Combine(Folder, "clone_last.bin");
                        if (File.Exists(file))
                            last = Load(file);
                    }
                    catch (Exception e)
                    {
                        MelonLogger.Warning("Bot Player: the clone could not be read: " + e.Message);
                    }
                }
                return last;
            }
        }

        // ---------------------------------------------------------------- watching

        /// <summary>Once a frame: the player's body and hands, sixty times a second.</summary>
        public static void Update()
        {
            foreach (Session s in sessions)
                Sample(s);
        }

        private static void Sample(Session s)
        {
            Take taking = s.taking;
            if (taking == null)
                return;
            WatchAvatar(s);
            WatchSeat(s);
            OpenControllerRig controls = Player.ControllerRig;
            if (controls != null && controls.leftController != null && controls.rightController != null)
            {
                Latch(ref s.leftLatch, controls.leftController);
                Latch(ref s.rightLatch, controls.rightController);
            }
            float t = Time.time - s.startedAt;
            if (t < s.nextFrameAt)
                return;
            s.nextFrameAt = t + Rate;
            PhysicsRig physics = Player.PhysicsRig;
            RigManager rig = Player.RigManager;
            if (controls == null || physics == null || rig == null || controls.vrRoot == null)
                return;
            Rigidbody pelvis = physics.m_pelvis != null ? physics.m_pelvis.GetComponent<Rigidbody>() : null;
            if (pelvis == null)
                return;
            Frame f = new Frame
            {
                t = t,
                pelvis = pelvis.position,
                pelvisTurn = pelvis.rotation,
                pelvisVelocity = pelvis.velocity,
                pelvisSpin = pelvis.angularVelocity,
                playspace = controls.vrRoot.rotation,
                head = LocalOf(controls.headset),
                left = LocalOf(controls.leftController.transform),
                right = LocalOf(controls.rightController.transform),
                leftIn = InputOf(controls.leftController, ref s.leftLatch),
                rightIn = InputOf(controls.rightController, ref s.rightLatch),
                aPrimary = controls._primaryAButton,
                aSecondary = controls._secondaryAButton,
                crouch = rig.remapHeptaRig._crouchTarget,
                feet = rig.remapHeptaRig._feetOffset,
                root = controls.vrRoot.position,
            };
            Seat sat = rig.activeSeat;
            if (sat != null)
            {
                f.seated = true;
                f.seat = Pose.Of(sat.transform);
            }
            taking.frames.Add(f);
        }

        private static Pose LocalOf(Transform t) => new Pose(t.localPosition, t.localRotation);

        // A button down at any moment since the last frame kept counts as down in the next.
        private static void Latch(ref HandInput latch, BaseController c)
        {
            latch.primary |= c._primaryInteractionButton;
            latch.secondary |= c._secondaryInteractionButton;
            latch.a |= c._aButton;
            latch.b |= c._bButton;
            latch.menu |= c._menuTap;
            latch.click |= c._thumbstick;
            latch.pad |= c._touchPad;
        }

        private static HandInput InputOf(BaseController c, ref HandInput latch)
        {
            HandInput held = latch;
            latch = default;
            return new HandInput
            {
                a = c._aButton || held.a,
                b = c._bButton || held.b,
                menu = c._menuTap || held.menu,
                click = c._thumbstick || held.click,
                pad = c._touchPad || held.pad,
                padAxis = c._touchPadAxis,
                index = c._processedIndex,
                middle = c._processedMiddle,
                ring = c._processedRing,
                pinky = c._processedPinky,
                thumb = c._processedThumb,
                grip = c._solvedGrip,
                trigger = c._primaryAxis,
                primary = c._primaryInteractionButton || held.primary,
                secondary = c._secondaryInteractionButton || held.secondary,
                stick = c._thumbstickAxis,
            };
        }

        // The player sitting down in something (a kart, a car), or getting out: kept, so the clone does at the same moment.
        private static void WatchSeat(Session s)
        {
            try
            {
                RigManager rig = Player.RigManager;
                Seat seat = rig != null ? rig.activeSeat : null;
                IntPtr now = seat != null ? seat.Pointer : IntPtr.Zero;
                if (now == s.seat)
                    return;
                s.seat = now;
                Event e = new Event { t = Time.time - s.startedAt, kind = Kind.Seat, hand = seat != null ? 1 : 0 };
                if (seat != null)
                {
                    Poolee poolee = seat.GetComponentInParent<Poolee>();
                    Transform thing = poolee != null ? poolee.transform : seat.transform.root;
                    e.barcode = poolee != null && poolee.SpawnableCrate != null ? poolee.SpawnableCrate.Barcode.ID : "";
                    IntPtr which = poolee != null ? poolee.Pointer : thing.Pointer;
                    if (!s.things.TryGetValue(which, out int number))
                    {
                        number = s.things.Count;
                        s.things[which] = number;
                    }
                    e.name = thing.name + "||" + number;
                    e.at = thing.position;
                    e.turn = thing.rotation;
                    Seat[] seats = thing.GetComponentsInChildren<Seat>(true);
                    for (int i = 0; i < seats.Length; i++)
                    {
                        if (seats[i] == seat)
                            e.grip = i;
                    }
                }
                s.taking.events.Add(e);
                Lab.Event((s == Lesson ? "lesson" : "clone") + ": you " + (seat != null ? "sat down in the " + ThingName(e) : "got out"));
            }
            catch (Exception)
            {
            }
        }

        // The player putting on another avatar: kept, so the clone changes at the same moment.
        private static void WatchAvatar(Session s)
        {
            RigManager rig = Player.RigManager;
            if (rig == null || rig.avatar == null)
                return;
            string now = BotRig.AvatarOf(rig);
            if (string.IsNullOrEmpty(now) || now == s.wearing)
                return;
            s.wearing = now;
            s.taking.events.Add(new Event { t = Time.time - s.startedAt, kind = Kind.Avatar, barcode = now, at = rig.avatar.transform.localScale });
            Lab.Event("clone: avatar " + now);
        }

        /// <summary>
        /// Whether a spawned thing is a whole player body. The game makes a blank one of its own
        /// while the player changes avatar; made again by a clone, it is a second player, with a
        /// headset camera of its own that takes the player's view.
        /// </summary>
        public static bool IsBody(string barcode, string name)
        {
            return (barcode ?? "").IndexOf("RigManager", StringComparison.OrdinalIgnoreCase) >= 0
                || (name ?? "").IndexOf("RigManager", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Anything spawned (the player's, while recording): what, where, when.</summary>
        public static void OnSpawn(Poolee poolee)
        {
            foreach (Session s in sessions)
                OnSpawn(s, poolee);
        }

        private static void OnSpawn(Session s, Poolee poolee)
        {
            Take taking = s.taking;
            if (taking == null || poolee == null || Time.time < SuppressUntil)
                return;
            try
            {
                if (poolee.SpawnableCrate == null)
                    return;
                GameObject go = poolee.gameObject;
                // Things to hold and throw (not effects, spent cases, or bodies: a body only just
                // made is not switched on yet, so its parts are looked for switched off too).
                if (IsBody(poolee.SpawnableCrate.Barcode.ID, go.name) || go.GetComponentInChildren<RigManager>(true) != null)
                    return;
                if (go.GetComponentInChildren<Rigidbody>() == null)
                    return;
                string name = go.name.ToLowerInvariant();
                if (name.Contains("cartridge") || name.Contains("casing") || name.Contains("shell") || name.Contains("bullet"))
                    return;
                // (Nor what a shot or a blow throws up: flares, sparks, blood.)
                if (name.StartsWith("vfx", StringComparison.Ordinal) || name.Contains("muzzle") || name.Contains("flare") || name.Contains("decal") || name.Contains("blood") || name.Contains("impact"))
                    return;
                // Not the player's tools coming out (the spawn gun and the like), nor magazines
                // their ammo pouch makes ready (several at a time): a magazine they take in hand is
                // kept as that grab, and the clone's comes into its hand then.
                if (name.Contains("spawn_gun") || name.Contains("spawngun") || name.Contains("nimbus") || go.GetComponentInChildren<Magazine>() != null)
                    return;
                int id = s.spawns++;
                s.spawned[poolee.Pointer] = id;
                taking.events.Add(new Event
                {
                    t = Time.time - s.startedAt,
                    kind = Kind.Spawn,
                    spawn = id,
                    barcode = poolee.SpawnableCrate.Barcode.ID,
                    name = go.name,
                    at = go.transform.position,
                    turn = go.transform.rotation,
                });
                if (s == manual)
                    Lab.Event("clone: spawn " + go.name);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>One of the player's hands taking hold of (or letting go of) a grip.</summary>
        public static void OnGrip(Grip grip, Hand hand, bool on)
        {
            foreach (Session s in sessions)
                OnGrip(s, grip, hand, on);
        }

        private static void OnGrip(Session s, Grip grip, Hand hand, bool on)
        {
            Take taking = s.taking;
            float startedAt = s.startedAt;
            if (taking == null || grip == null || hand == null)
                return;
            int which = hand == Player.LeftHand ? 0 : hand == Player.RightHand ? 1 : -1;
            if (which < 0)
                return;
            if (!on)
            {
                taking.events.Add(new Event { t = Time.time - startedAt, kind = Kind.Release, hand = which });
                return;
            }
            try
            {
                // Not a grip on a body (the player's own: the body log taken to change avatar, a
                // holster): the clone would make a whole player body to take hold of, and that
                // body's headset camera takes the player's view.
                if (grip.GetComponentInParent<RigManager>() != null)
                    return;
                Poolee poolee = grip.GetComponentInParent<Poolee>();
                if (poolee != null && poolee.SpawnableCrate != null && IsBody(poolee.SpawnableCrate.Barcode.ID, poolee.gameObject.name))
                    return;
                Event e = new Event { t = Time.time - startedAt, kind = Kind.Grab, hand = which, grip = GripIndex(grip) };
                if (poolee != null && s.spawned.TryGetValue(poolee.Pointer, out int id))
                {
                    e.spawn = id;
                    e.name = "|" + grip.name;
                }
                else
                {
                    Transform thing = poolee != null ? poolee.transform : grip.transform.root;
                    e.barcode = poolee != null && poolee.SpawnableCrate != null ? poolee.SpawnableCrate.Barcode.ID : "";
                    // (And the grip's own name: its place among the thing's grips changes with
                    // what is fitted to the thing, a gun with its magazine in or out.)
                    // (And which of the things of its kind it is: one in each hand are two.)
                    IntPtr which2 = poolee != null ? poolee.Pointer : thing.Pointer;
                    if (!s.things.TryGetValue(which2, out int number))
                    {
                        number = s.things.Count;
                        s.things[which2] = number;
                    }
                    e.name = thing.name + "|" + grip.name + "|" + number;
                    e.at = thing.position;
                    // (And which way it faced: a thing too big for a hand is put where it was from the player, so.)
                    e.turn = thing.rotation;
                    e.spawn = -1;
                }
                // (The spawn gun is the player's tool, not the clone's: its spawns come from the recording.)
                string what = (poolee != null ? poolee.gameObject.name : grip.name).ToUpperInvariant();
                if (what.Contains("SPAWN") || what.Contains("NIMBUS"))
                    return;
                taking.events.Add(e);
            }
            catch (Exception)
            {
            }
        }

        public static void OnFired(Gun gun)
        {
            if (gun == null || gun.triggerGrip == null || gun.triggerGrip.attachedHands == null)
                return;
            int which = gun.triggerGrip.attachedHands.Contains(Player.LeftHand) ? 0 : gun.triggerGrip.attachedHands.Contains(Player.RightHand) ? 1 : -1;
            if (which < 0)
                return;
            foreach (Session s in sessions)
            {
                if (s.taking != null)
                    s.taking.events.Add(new Event { t = Time.time - s.startedAt, kind = Kind.Fire, hand = which });
            }
        }

        /// <summary>The thing's name in a grab (without the grip's, kept after it).</summary>
        public static string ThingName(Event e)
        {
            string name = e.name ?? "";
            int cut = name.IndexOf('|');
            return cut >= 0 ? name.Substring(0, cut) : name;
        }

        /// <summary>The name of the grip taken, if the recording has it (null in older ones).</summary>
        public static string GripName(Event e)
        {
            string[] parts = (e.name ?? "").Split('|');
            return parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null;
        }

        /// <summary>Which of the things of its kind was taken (numbered as first taken), if the recording says; else -1.</summary>
        public static int ThingNumber(Event e)
        {
            string[] parts = (e.name ?? "").Split('|');
            return parts.Length > 2 && int.TryParse(parts[2], out int n) ? n : -1;
        }

        /// <summary>A grip's place among its thing's grips (found the same way on a copy of it).</summary>
        public static int GripIndex(Grip grip)
        {
            Grip[] grips = GripsOf(grip.transform);
            for (int i = 0; i < grips.Length; i++)
            {
                if (grips[i] == grip)
                    return i;
            }
            return -1;
        }

        public static Grip[] GripsOf(Transform part)
        {
            MarrowEntity entity = part.GetComponentInParent<MarrowEntity>();
            Component root = entity != null ? entity : (Component)part.root;
            return root.GetComponentsInChildren<Grip>(true);
        }

        // ---------------------------------------------------------------- on disk

        public static void Save(Take take, string file)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            using BinaryWriter w = new BinaryWriter(File.Create(file));
            w.Write(4);
            V(w, take.startFeet);
            w.Write(take.startYaw);
            w.Write(take.avatar ?? "");
            V(w, take.avatarScale);
            w.Write(take.item ?? ""); w.Write(take.itemName ?? ""); w.Write(take.level ?? "");
            w.Write(take.eyes);
            w.Write(take.frames.Count);
            foreach (Frame f in take.frames)
            {
                w.Write(f.t);
                V(w, f.pelvis); V(w, f.pelvisVelocity); V(w, f.pelvisSpin);
                Q(w, f.pelvisTurn); Q(w, f.playspace);
                P(w, f.head); P(w, f.left); P(w, f.right);
                H(w, f.leftIn); H(w, f.rightIn);
                w.Write(f.aPrimary); w.Write(f.aSecondary);
                w.Write(f.crouch); w.Write(f.feet);
                V(w, f.root); w.Write(f.seated);
                if (f.seated)
                    P(w, f.seat);
            }
            w.Write(take.events.Count);
            foreach (Event e in take.events)
            {
                w.Write(e.t); w.Write((int)e.kind); w.Write(e.hand); w.Write(e.spawn);
                w.Write(e.barcode ?? ""); w.Write(e.name ?? "");
                V(w, e.at); Q(w, e.turn); w.Write(e.grip);
            }
        }

        public static Take Load(string file)
        {
            using BinaryReader r = new BinaryReader(File.OpenRead(file));
            int version = r.ReadInt32();
            Take take = new Take { startFeet = V(r), startYaw = r.ReadSingle() };
            if (version >= 2)
            {
                take.avatar = r.ReadString();
                take.avatarScale = V(r);
            }
            if (version >= 3)
            {
                take.item = r.ReadString(); take.itemName = r.ReadString(); take.level = r.ReadString();
                take.eyes = r.ReadSingle();
            }
            int frames = r.ReadInt32();
            for (int i = 0; i < frames; i++)
            {
                Frame f = new Frame { t = r.ReadSingle() };
                f.pelvis = V(r); f.pelvisVelocity = V(r); f.pelvisSpin = V(r);
                f.pelvisTurn = Q(r); f.playspace = Q(r);
                f.head = P(r); f.left = P(r); f.right = P(r);
                f.leftIn = H(r, version); f.rightIn = H(r, version);
                f.aPrimary = r.ReadBoolean(); f.aSecondary = r.ReadBoolean();
                f.crouch = r.ReadSingle(); f.feet = r.ReadSingle();
                if (version >= 4)
                {
                    f.root = V(r);
                    f.seated = r.ReadBoolean();
                    if (f.seated)
                        f.seat = P(r);
                }
                take.frames.Add(f);
            }
            int events = r.ReadInt32();
            for (int i = 0; i < events; i++)
            {
                Event e = new Event { t = r.ReadSingle(), kind = (Kind)r.ReadInt32(), hand = r.ReadInt32(), spawn = r.ReadInt32() };
                e.barcode = r.ReadString(); e.name = r.ReadString();
                e.at = V(r); e.turn = Q(r); e.grip = r.ReadInt32();
                take.events.Add(e);
            }
            return take;
        }

        private static void V(BinaryWriter w, Vector3 v) { w.Write(v.x); w.Write(v.y); w.Write(v.z); }
        private static void Q(BinaryWriter w, Quaternion q) { w.Write(q.x); w.Write(q.y); w.Write(q.z); w.Write(q.w); }
        private static void P(BinaryWriter w, Pose p) { V(w, p.position); Q(w, p.rotation); }
        private static void H(BinaryWriter w, HandInput h)
        {
            w.Write(h.index); w.Write(h.middle); w.Write(h.ring); w.Write(h.pinky); w.Write(h.thumb);
            w.Write(h.grip); w.Write(h.trigger); w.Write(h.primary); w.Write(h.secondary);
            w.Write(h.stick.x); w.Write(h.stick.y);
            w.Write(h.a); w.Write(h.b); w.Write(h.menu); w.Write(h.click); w.Write(h.pad);
            w.Write(h.padAxis.x); w.Write(h.padAxis.y);
        }

        private static Vector3 V(BinaryReader r) => new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        private static Quaternion Q(BinaryReader r) => new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        private static Pose P(BinaryReader r) => new Pose(V(r), Q(r));
        private static HandInput H(BinaryReader r, int version)
        {
            HandInput h = new HandInput
            {
                index = r.ReadSingle(), middle = r.ReadSingle(), ring = r.ReadSingle(), pinky = r.ReadSingle(), thumb = r.ReadSingle(),
                grip = r.ReadSingle(), trigger = r.ReadSingle(), primary = r.ReadBoolean(), secondary = r.ReadBoolean(),
            };
            h.stick = new Vector2(r.ReadSingle(), r.ReadSingle());
            if (version >= 3)
            {
                h.a = r.ReadBoolean(); h.b = r.ReadBoolean(); h.menu = r.ReadBoolean(); h.click = r.ReadBoolean(); h.pad = r.ReadBoolean();
                h.padAxis = new Vector2(r.ReadSingle(), r.ReadSingle());
            }
            return h;
        }
    }
}
