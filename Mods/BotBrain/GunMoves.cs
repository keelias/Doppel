using System.IO;
using System.Text.Json;
using Pose = ThirdEyeBonelabMod.Bots.Bot.Pose;
using BoneLib;
using Il2CppSLZ.Marrow;
using Il2CppSLZ.Marrow.Interaction;
using Il2CppSLZ.Marrow.Pool;
using MelonLoader;
using UnityEngine;

namespace ThirdEyeBonelabMod.Bots
{
    /// <summary>
    /// How the player handles guns, learned by watching them (while "Teach me guns" is on), for
    /// the bot to handle them the same way, kept in UserData/Bot Player/gunmoves.json:
    /// <list type="bullet">
    /// <item>how they hold each gun aimed: where each controller is, from their head, and which
    /// way the barrel points from where they look (so the bot aims by looking, as they do);</item>
    /// <item>how they hold it low, not aiming;</item>
    /// <item>their reloads, moment by moment, with when the magazine came out, when the new one
    /// was taken, when it went in and when the slide was worked.</item>
    /// </list>
    /// Kept for each gun (by its barcode), and for long guns and pistols as a whole.
    /// </summary>
    public static class GunMoves
    {
        // ---------------------------------------------------------------- what is kept

        public class Hold
        {
            public int n { get; set; }
            public float[] right { get; set; }
            public float[] left { get; set; }
            public float[] muzzle { get; set; }
        }

        public class ReloadFrame
        {
            public float t { get; set; }
            public float[] head { get; set; }
            public float[] right { get; set; }
            public float[] left { get; set; }
        }

        public class Reload
        {
            public float eject { get; set; } = -1f;
            public float grab { get; set; } = -1f;
            public float insert { get; set; } = -1f;
            public float rack { get; set; } = -1f;
            public float end { get; set; }
            public List<ReloadFrame> frames { get; set; } = new List<ReloadFrame>();
        }

        public class GunRecord
        {
            public Hold aim { get; set; }
            public Hold low { get; set; }
            public List<Reload> reloads { get; set; } = new List<Reload>();
        }

        /// <summary>A punch of the player's: both hands from their head, from just before it to just after.</summary>
        public class Punch
        {
            public bool right { get; set; }
            public List<ReloadFrame> frames { get; set; } = new List<ReloadFrame>();
        }

        public class Store
        {
            public Dictionary<string, GunRecord> guns { get; set; } = new Dictionary<string, GunRecord>();
            public List<Punch> punches { get; set; } = new List<Punch>();
            /// <summary>What kind of gun each one is (by its barcode): "pistol", "smg" or "long".</summary>
            public Dictionary<string, string> kinds { get; set; } = new Dictionary<string, string>();
        }

        private static Store store;
        private static bool dirty;
        private static float savedAt;

        /// <summary>Whether it watches the player's gun handling (the menu's "Teach me guns").</summary>
        public static bool Teaching;

        private static string File => Path.Combine(AssetFiles.SaveFolder(""), "gunmoves.json");

        private static Store Data
        {
            get
            {
                if (store != null)
                    return store;
                store = new Store();
                try
                {
                    if (System.IO.File.Exists(File))
                        store = JsonSerializer.Deserialize<Store>(System.IO.File.ReadAllText(File)) ?? new Store();
                }
                catch (Exception e)
                {
                    MelonLogger.Warning("Bot Player: the gun moves could not be read: " + e.Message);
                }
                return store;
            }
        }

        // The lessons lately learned, and how to take each back (for "forget the last 30 seconds").
        private static readonly List<(float at, Action undo)> learned = new List<(float, Action)>();

        private static void Learned(Action undo)
        {
            learned.Add((Time.time, undo));
            if (learned.Count > 200)
                learned.RemoveAt(0);
        }

        /// <summary>Takes back the reloads and punches learned since then; how many.</summary>
        public static int ForgetSince(float since)
        {
            int n = 0;
            for (int i = learned.Count - 1; i >= 0 && learned[i].at >= since; i--)
            {
                try
                {
                    learned[i].undo();
                }
                catch (Exception)
                {
                }
                learned.RemoveAt(i);
                n++;
            }
            if (n > 0)
            {
                dirty = true;
                Save();
            }
            return n;
        }

        public static void Save()
        {
            if (!dirty || store == null)
                return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(File));
                System.IO.File.WriteAllText(File, JsonSerializer.Serialize(store));
                dirty = false;
                savedAt = Time.time;
            }
            catch (Exception e)
            {
                MelonLogger.Warning("Bot Player: the gun moves could not be saved: " + e.Message);
            }
        }

        public static string KeyOf(Gun gun, bool twoHanded)
        {
            try
            {
                Poolee poolee = gun.GetComponentInParent<Poolee>();
                if (poolee != null && poolee.SpawnableCrate != null)
                {
                    string barcode = poolee.SpawnableCrate.Barcode.ID;
                    NoteKind(barcode, gun);
                    return barcode;
                }
            }
            catch (Exception)
            {
            }
            return twoHanded ? "long" : "pistol";
        }

        /// <summary>
        /// What kind of gun it is, by its length from its handle to its muzzle: a pistol, a
        /// submachine gun, or a long gun. A gun the player has not shown the bot is held and
        /// reloaded as they showed it for one of its own kind (not, as before, as whichever
        /// gun they last held in as many hands).
        /// </summary>
        public static string KindOf(Gun gun)
        {
            float length = 0.2f;
            try
            {
                if (gun.triggerGrip != null && gun.firePointTransform != null)
                    length = (gun.firePointTransform.position - gun.triggerGrip.transform.position).magnitude;
            }
            catch (Exception)
            {
            }
            return length < Tuning.F("gun.kind.smg", 0.3f) ? "pistol" : length < Tuning.F("gun.kind.long", 0.46f) ? "smg" : "long";
        }

        private static void NoteKind(string barcode, Gun gun)
        {
            if (Data.kinds == null)
                Data.kinds = new Dictionary<string, string>();
            if (Data.kinds.ContainsKey(barcode))
                return;
            string kind = KindOf(gun);
            Data.kinds[barcode] = kind;
            dirty = true;
            float length = gun.triggerGrip != null && gun.firePointTransform != null ? (gun.firePointTransform.position - gun.triggerGrip.transform.position).magnitude : 0f;
            Lab.Event("gun kind: " + gun.name + " is a " + kind + " (" + length.ToString("0.00") + " m from handle to muzzle)");
        }

        private static float scannedAt = -100f;

        // The guns about are looked at now and then, so each one's kind is known.
        private static void ScanGuns()
        {
            if (Time.time - scannedAt < 15f)
                return;
            scannedAt = Time.time;
            try
            {
                foreach (Gun gun in UnityEngine.Object.FindObjectsOfType<Gun>())
                {
                    if (gun != null && gun.triggerGrip != null)
                        KeyOf(gun, false);
                }
            }
            catch (Exception)
            {
            }
        }

        // The record of the gun of the same kind the player has shown it most of (by what is asked for), not this gun's own.
        private static GunRecord OfItsKind(string key, Func<GunRecord, int> worth)
        {
            ScanGuns();
            if (Data.kinds == null || !Data.kinds.TryGetValue(key, out string kind))
                return null;
            GunRecord best = null;
            int most = 0;
            foreach (KeyValuePair<string, GunRecord> pair in Data.guns)
            {
                if (pair.Key == key || !Data.kinds.TryGetValue(pair.Key, out string other) || other != kind)
                    continue;
                int n = worth(pair.Value);
                if (n > most)
                {
                    most = n;
                    best = pair.Value;
                }
            }
            return best;
        }

        private static GunRecord Record(string key)
        {
            if (!Data.guns.TryGetValue(key, out GunRecord r))
                Data.guns[key] = r = new GunRecord();
            return r;
        }

        /// <summary>The player's aimed (or low) hold for this gun, else for its kind; null if never seen.</summary>
        /// <summary>The player's hold of this very gun (not of guns like it), if they have shown it enough; else null.</summary>
        public static Hold Own(string key, bool aimed, int least = 15)
        {
            if (!Data.guns.TryGetValue(key, out GunRecord r))
                return null;
            Hold hold = aimed ? r.aim : r.low;
            return hold != null && hold.n >= least ? hold : null;
        }

        public static Hold Held(string key, bool twoHanded, bool aimed, int least = 15)
        {
            Hold hold = null;
            if (Data.guns.TryGetValue(key, out GunRecord r))
                hold = aimed ? r.aim : r.low;
            if (hold == null || hold.n < least)
            {
                // (As the player holds a gun of its kind, then.)
                GunRecord kind = OfItsKind(key, g => (aimed ? g.aim : g.low) != null ? (aimed ? g.aim : g.low).n : 0);
                Hold general = kind != null ? (aimed ? kind.aim : kind.low) : null;
                if (general != null && general.n >= least)
                    hold = general;
            }
            // A two-handed hold needs the other hand in it.
            if (hold != null && (hold.n < least || (twoHanded && hold.left == null)))
                return null;
            return hold;
        }

        /// <summary>One of the player's reloads for this gun (or its kind), at random; null if none.</summary>
        public static Reload ReloadFor(string key, bool twoHanded)
        {
            List<Reload> list = null;
            if (Data.guns.TryGetValue(key, out GunRecord r) && r.reloads.Count > 0)
                list = r.reloads;
            else
            {
                // (As the player reloads a gun of its kind, then.)
                GunRecord kind = OfItsKind(key, g => g.reloads.Count);
                if (kind != null && kind.reloads.Count > 0)
                    list = kind.reloads;
            }
            return list != null ? list[UnityEngine.Random.Range(0, list.Count)] : null;
        }

        /// <summary>One of the player's punches with that hand, at random; null if never seen.</summary>
        public static Punch PunchFor(bool right)
        {
            List<Punch> mine = new List<Punch>();
            foreach (Punch p in Data.punches ?? new List<Punch>())
            {
                if (p.right == right && p.frames.Count > 3)
                    mine.Add(p);
            }
            return mine.Count > 0 ? mine[UnityEngine.Random.Range(0, mine.Count)] : null;
        }

        /// <summary>When the player last threw a punch (seen while teaching).</summary>
        public static float PlayerPunchedAt = -10f;

        // The last second of the player's hands, for cutting a punch out of.
        private struct Moment
        {
            public float t;
            public Pose head, right, left;
        }

        private static readonly List<Moment> moments = new List<Moment>();
        private static float punchPeak = -1f;
        private static bool punchRight;
        private static float momentAt;

        // A punch: an empty hand going out fast ahead of them; kept from 0.3 s before its fastest to 0.3 s after.
        private static void WatchFists(OpenControllerRig controls)
        {
            float now = Time.time;
            if (now - momentAt < 1f / 60f)
                return;
            momentAt = now;
            Transform root = controls.vrRoot;
            Moment m = new Moment
            {
                t = now,
                head = Local(root, controls.headset),
                right = Local(root, controls.rightController.transform),
                left = Local(root, controls.leftController.transform),
            };
            moments.Add(m);
            while (moments.Count > 0 && now - moments[0].t > 1f)
                moments.RemoveAt(0);
            if (moments.Count < 3)
                return;
            Moment before = moments[moments.Count - 2];
            float dt = Mathf.Max(m.t - before.t, 0.001f);
            Vector3 forward = m.head.rotation * Vector3.forward;
            if (punchPeak < 0f && now - PlayerPunchedAt > 0.5f)
            {
                foreach (bool right in new[] { true, false })
                {
                    Hand hand = right ? Player.RightHand : Player.LeftHand;
                    if (hand == null || hand.m_CurrentAttachedGO != null)
                        continue;
                    Vector3 v = ((right ? m.right.position : m.left.position) - (right ? before.right.position : before.left.position)) / dt;
                    if (v.magnitude > Tuning.F("teach.punch.speed", 3f) && Vector3.Dot(v, forward) > Tuning.F("teach.punch.forward", 1.8f))
                    {
                        punchPeak = now;
                        punchRight = right;
                        break;
                    }
                }
            }
            if (punchPeak > 0f && now - punchPeak > 0.3f)
            {
                Punch punch = new Punch { right = punchRight };
                float start = punchPeak - 0.3f;
                foreach (Moment k in moments)
                {
                    if (k.t < start)
                        continue;
                    Pose head = k.head.Inverse();
                    punch.frames.Add(new ReloadFrame { t = k.t - start, right = P(head * k.right), left = P(head * k.left) });
                }
                // (No longer kept: its punches are the ones the player records in the Punch slots.)
                if (Data.punches != null && Data.punches.Count > 0)
                {
                    Data.punches.Clear();
                    Lab.Event("teach: the punches learned by watching are forgotten (its punches are the Punch slots now)");
                }
                if (punch.frames.Count > 5 && false)
                {
                    Data.punches ??= new List<Punch>();
                    Data.punches.Add(punch);
                    Punch added = punch;
                    Learned(() => Data.punches.Remove(added));
                    if (Data.punches.Count > 30)
                        Data.punches.RemoveAt(0);
                    dirty = true;
                    Lab.Event("teach: punch learned (" + (punchRight ? "right" : "left") + ")");
                }
                PlayerPunchedAt = punchPeak;
                punchPeak = -1f;
            }
        }

        /// <summary>How many moments of the player aiming it has seen, over all guns.</summary>
        public static int AimMoments()
        {
            int n = 0;
            foreach (GunRecord record in Data.guns.Values)
                n += record.aim != null ? record.aim.n : 0;
            return n;
        }

        /// <summary>What it learned of one gun, taken back: its reloads, or how it is held and aimed.</summary>
        public static void ForgetGun(string barcode, bool reloads, bool holds)
        {
            if (!Data.guns.TryGetValue(barcode, out GunRecord record))
                return;
            if (reloads)
                record.reloads.Clear();
            if (holds)
                record.aim = record.low = null;
            dirty = true;
            Save();
        }

        /// <summary>Everything forgotten, in memory (its file has been put away).</summary>
        public static void Reset()
        {
            store = new Store();
            learned.Clear();
            dirty = false;
            Teaching = false;
        }

        /// <summary>How much of each it has: guns it has seen aimed enough to copy, reloads, punches.</summary>
        public static void Lessons(out int guns, out int reloads, out int punches)
        {
            guns = reloads = 0;
            foreach (KeyValuePair<string, GunRecord> pair in Data.guns)
            {
                if (pair.Key == "long" || pair.Key == "pistol")
                    continue;
                if (pair.Value.aim != null && pair.Value.aim.n >= 20)
                    guns++;
                reloads += pair.Value.reloads.Count;
            }
            punches = Data.punches != null ? Data.punches.Count : 0;
        }

        public static string Summary()
        {
            int aims = 0, lows = 0, reloads = 0;
            foreach (KeyValuePair<string, GunRecord> pair in Data.guns)
            {
                if (pair.Key == "long" || pair.Key == "pistol")
                    continue;
                aims += pair.Value.aim != null ? pair.Value.aim.n : 0;
                lows += pair.Value.low != null ? pair.Value.low.n : 0;
                reloads += pair.Value.reloads.Count;
            }
            return "watched " + aims + " moments aiming, " + lows + " holding low, " + reloads + " reloads, "
                + (Data.punches != null ? Data.punches.Count : 0) + " punches, " + PlayerWatch.Imitated + " choices of yours";
        }

        // ---------------------------------------------------------------- watching

        private static float sampleAt;
        private static Gun watched;
        private static bool hadMagazine;
        private static Reload taking;
        private static float takingSince;
        private static string takingKey;
        private static bool takingTwoHanded;
        private static float takeFrameAt;

        /// <summary>Once a frame: watches how the player holds and reloads the gun in their hand.</summary>
        public static void Update()
        {
            if (store != null && dirty && Time.time - savedAt > 10f)
                Save();
            if (!Teaching)
            {
                taking = null;
                return;
            }
            OpenControllerRig controls = Player.ControllerRig;
            if (controls != null && controls.vrRoot != null && controls.headset != null && controls.rightController != null && controls.leftController != null)
                WatchFists(controls);
            // The gun in their hands, and which hand has its trigger: a left-handed hold is learned
            // as its mirror image (the bot holds its trigger in its right hand, always).
            Gun rightGun = Player.GetComponentInHand<Gun>(Player.RightHand);
            Gun leftGun = Player.GetComponentInHand<Gun>(Player.LeftHand);
            Gun gun = rightGun ?? leftGun;
            // (Not the spawn gun, nor any such tool: it is a "gun" to the game, not to the player.)
            if (gun != null && (gun.name.ToUpperInvariant().Contains("SPAWN") || gun.name.ToUpperInvariant().Contains("NIMBUS")))
                gun = null;
            bool lefty = rightGun == null && leftGun != null;
            if (gun != null && gun.triggerGrip != null && gun.triggerGrip.attachedHands != null)
            {
                bool onLeft = gun.triggerGrip.attachedHands.Contains(Player.LeftHand);
                bool onRight = gun.triggerGrip.attachedHands.Contains(Player.RightHand);
                if (onLeft != onRight)
                    lefty = onLeft;
            }
            Hand otherHand = lefty ? Player.RightHand : Player.LeftHand;
            if (controls == null || controls.vrRoot == null || controls.headset == null || gun == null || gun.firePointTransform == null)
            {
                watched = null;
                taking = null;
                return;
            }
            bool twoHanded = OtherHandOn(gun, otherHand);
            string key = KeyOf(gun, twoHanded);
            Transform root = controls.vrRoot;
            Pose head = Local(root, controls.headset);
            // ("Right" is the trigger hand from here on, "left" the other.)
            Pose right = Local(root, (lefty ? controls.leftController : controls.rightController).transform);
            Pose left = Local(root, (lefty ? controls.rightController : controls.leftController).transform);
            Pose muzzle = Local(root, gun.firePointTransform);
            bool has = Safe(() => gun.HasMagazine(), true);

            // A reload: from the magazine coming out to a round in the chamber.
            if (gun != watched)
            {
                watched = gun;
                hadMagazine = has;
                taking = null;
            }
            if (hadMagazine && !has && taking == null)
            {
                taking = new Reload { eject = 0f };
                takingSince = Time.time;
                takingKey = key;
                takingTwoHanded = twoHanded || Safe(() => gun.otherGrips != null && gun.otherGrips.Length > 1, false);
                takeFrameAt = 0f;
                Lab.Event("teach: reload started (" + key + ")");
            }
            hadMagazine = has;
            if (taking != null)
            {
                Take(gun, head, right, left, has, otherHand, lefty);
                return;
            }

            // Holding it: aimed (the barrel along where they look, the gun still), or low.
            if (Time.time < sampleAt)
                return;
            sampleAt = Time.time + 0.1f;
            // Firing (the trigger down, or a shot just gone): that is aiming, however they hold
            // the gun to do it (tilted, from the hip, not along where they look), kick and all.
            BaseController trigger = lefty ? controls.leftController : controls.rightController;
            bool firing = trigger._primaryAxis > 0.5f || Time.time - PlayerWatch.FiredAt < 0.6f;
            Rigidbody body = gun.GetComponentInParent<Rigidbody>();
            if (!firing && body != null && body.angularVelocity.magnitude > 1.2f)
                return;
            float off = Vector3.Angle(head.rotation * Vector3.forward, muzzle.rotation * Vector3.forward);
            Pose rightInHead = head.Inverse() * right;
            Pose leftInHead = head.Inverse() * left;
            Quaternion muzzleInHead = Quaternion.Inverse(head.rotation) * muzzle.rotation;
            if (lefty)
            {
                rightInHead = Mirrored(rightInHead);
                leftInHead = Mirrored(leftInHead);
                muzzleInHead = Mirrored(new Pose(Vector3.zero, muzzleInHead)).rotation;
            }
            if (off < 12f || (firing && off < 50f))
            {
                // (Recorded into a slot: this is how the gun is held from now on, not one more
                // moment added to how it was held before.)
                if (slotFresh && freshened.Add(key))
                {
                    Record(key).aim = null;
                    Lab.Event("teach: the aimed hold of " + key + " begun afresh" + (lefty ? " (left-handed, learned mirrored)" : "") + (twoHanded ? ", two hands" : ", one hand"));
                }
                Add(Record(key), true, rightInHead, twoHanded ? leftInHead : (Pose?)null, muzzleInHead);
            }
            else if (off > 30f && !firing)
            {
                Add(Record(key), false, rightInHead, twoHanded ? leftInHead : (Pose?)null, muzzleInHead);
            }
        }

        private static bool slotFresh;
        private static readonly HashSet<string> freshened = new HashSet<string>();

        /// <summary>While a Fire slot is recorded: the hold shown is the gun's hold from now on (begun afresh, not averaged into the old).</summary>
        public static void SlotFresh(bool on)
        {
            slotFresh = on;
            freshened.Clear();
        }

        // A pose as its mirror image, left for right (in the head's frame).
        private static Pose Mirrored(Pose p)
        {
            return new Pose(new Vector3(-p.position.x, p.position.y, p.position.z), new Quaternion(p.rotation.x, -p.rotation.y, -p.rotation.z, p.rotation.w));
        }

        // A moment of a reload; and its marks (the new magazine taken, in, the slide worked), and its end.
        private static void Take(Gun gun, Pose head, Pose right, Pose left, bool has, Hand otherHand, bool lefty)
        {
            float t = Time.time - takingSince;
            if (t >= takeFrameAt)
            {
                takeFrameAt = t + 1f / 30f;
                // In the frame of where they face (turned about up only), from their head.
                Quaternion yaw = Quaternion.Euler(0f, head.rotation.eulerAngles.y, 0f);
                Pose frame = new Pose(head.position, yaw);
                Pose headIn = new Pose(Vector3.zero, Quaternion.Inverse(yaw) * head.rotation);
                Pose rightIn = frame.Inverse() * right, leftIn = frame.Inverse() * left;
                if (lefty)
                {
                    headIn = Mirrored(headIn);
                    rightIn = Mirrored(rightIn);
                    leftIn = Mirrored(leftIn);
                }
                taking.frames.Add(new ReloadFrame
                {
                    t = t,
                    head = P(headIn),
                    right = P(rightIn),
                    left = P(leftIn),
                });
            }
            GameObject inLeft = otherHand != null ? otherHand.m_CurrentAttachedGO : null;
            if (taking.grab < 0f && inLeft != null && inLeft.GetComponentInParent<Magazine>() != null)
                taking.grab = t;
            if (taking.insert < 0f && has)
                taking.insert = t;
            bool chambered = Safe(() => gun.cartridgeState == Gun.CartridgeStates.UNSPENT, false);
            if (taking.insert >= 0f && taking.rack < 0f && chambered && t - taking.insert > 0.15f)
                taking.rack = t;
            bool done = taking.insert >= 0f && ((taking.rack >= 0f && t - taking.rack > 0.4f) || (chambered && t - taking.insert > 1.2f));
            if (done || t > 10f)
            {
                taking.end = t;
                if (taking.insert >= 0f && taking.frames.Count > 10)
                {
                    foreach (string k in new[] { takingKey })
                    {
                        List<Reload> list = Record(k).reloads;
                        list.Add(taking);
                        Reload added = taking;
                        Learned(() => list.Remove(added));
                        if (list.Count > 8)
                            list.RemoveAt(0);
                    }
                    dirty = true;
                    Lab.Event("teach: reload learned (" + t.ToString("0.0") + " s, grab " + taking.grab.ToString("0.0") + ", in " + taking.insert.ToString("0.0") + ", rack " + taking.rack.ToString("0.0") + ")");
                    Spawnables.Notify("Learned your reload");
                }
                taking = null;
            }
        }

        // A held pose added to the average kept.
        private static void Add(GunRecord record, bool aimed, Pose right, Pose? left, Quaternion muzzle)
        {
            Hold hold = aimed ? record.aim : record.low;
            if (hold == null)
            {
                hold = new Hold();
                if (aimed)
                    record.aim = hold;
                else
                    record.low = hold;
            }
            // A running average (the most recent counting a little more, after a while).
            float k = 1f / Mathf.Min(hold.n + 1, 200);
            hold.right = P(Pose.Lerp(U(hold.right, right), right, k));
            hold.muzzle = Q(Quaternion.Slerp(UQ(hold.muzzle, muzzle), muzzle, k));
            if (left.HasValue)
                hold.left = P(Pose.Lerp(U(hold.left, left.Value), left.Value, k));
            hold.n++;
            dirty = true;
            if (hold.n == 15 || hold.n == 100)
                Lab.Event("teach: " + (aimed ? "aimed" : "low") + " hold learned (" + hold.n + " moments)" + (left.HasValue ? ", two hands" : ""));
        }

        private static bool OtherHandOn(Gun gun, Hand other)
        {
            GameObject inLeft = other != null ? other.m_CurrentAttachedGO : null;
            if (inLeft == null || inLeft.GetComponentInParent<Magazine>() != null)
                return false;
            MarrowEntity mine = gun.GetComponentInParent<MarrowEntity>();
            return mine != null && inLeft.GetComponentInParent<MarrowEntity>() == mine;
        }

        // ---------------------------------------------------------------- poses as numbers

        private static Pose Local(Transform root, Transform t)
        {
            return new Pose(root.InverseTransformPoint(t.position), Quaternion.Inverse(root.rotation) * t.rotation);
        }

        public static float[] P(Pose p) => new[] { p.position.x, p.position.y, p.position.z, p.rotation.x, p.rotation.y, p.rotation.z, p.rotation.w };

        public static Pose U(float[] a, Pose otherwise)
        {
            if (a == null || a.Length < 7)
                return otherwise;
            return new Pose(new Vector3(a[0], a[1], a[2]), new Quaternion(a[3], a[4], a[5], a[6]));
        }

        public static float[] Q(Quaternion q) => new[] { q.x, q.y, q.z, q.w };

        public static Quaternion UQ(float[] a, Quaternion otherwise)
        {
            return a == null || a.Length < 4 ? otherwise : new Quaternion(a[0], a[1], a[2], a[3]);
        }

        private static T Safe<T>(Func<T> f, T otherwise)
        {
            try
            {
                return f();
            }
            catch (Exception)
            {
                return otherwise;
            }
        }
    }
}
