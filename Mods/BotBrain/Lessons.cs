using System.IO;
using BoneLib;
using Il2CppSLZ.Marrow;
using Il2CppSLZ.Marrow.Pool;
using MelonLoader;
using UnityEngine;

namespace ThirdEyeBonelabMod.Bots
{
    /// <summary>
    /// How the player uses each thing they pick up, kept exactly: while "Learn from me" is on,
    /// every time they take hold of a tool or a weapon, all they do with it is recorded as a
    /// clone recording is (their body's way, head and hands, every finger, trigger and button,
    /// what they spawned, took and let go of, each shot), from the moment they take it to the
    /// moment they put it down. A bot then does the same with one of its own (see BotLesson),
    /// wherever it stands. Kept by the thing's barcode in UserData/Bot Player/lessons, the
    /// newest few of each.
    /// </summary>
    public static class Lessons
    {
        public class Lesson
        {
            public string file;
            public string item = "", name = "", level = "";
            public float length;
            public float savedAt;
            public Clone.Take take;

            /// <summary>
            /// What the player got done with the thing in it, worked out from what happened:
            /// "climb" (they ended up well above where they began), "portal" (they were carried
            /// off somewhere), "fight" (something was hurt), "shoot", "make" (things appeared),
            /// "move" (things about were moved), or just "use".
            /// </summary>
            public string purpose = "use";
            /// <summary>How far up it took them, in all.</summary>
            public float rise;
            /// <summary>A climb: when in it they began to go up; and the stretch of it that can be
            /// done over and over to go higher than they did (from, to, and how far up that takes it each time).</summary>
            public float riseAt, loopFrom, loopTo, loopRise;
            /// <summary>A climb: when they were nearly up (the getting over the top begins), how far up that still took them, and how far the end of the repeatable stretch still is from the top.</summary>
            public float topAt, overRise, afterLoop;
            /// <summary>Things about that were moved, and hurt, while they used it (seen as it was recorded).</summary>
            public int moved, harm;
        }

        /// <summary>A lesson for a purpose (the newest, mostly); null if it has none.</summary>
        public static Lesson For(string purpose)
        {
            List<Lesson> of = All.FindAll(l => l.purpose == purpose);
            if (of.Count == 0)
                return null;
            return of[UnityEngine.Random.value < 0.7f ? of.Count - 1 : UnityEngine.Random.Range(0, of.Count)];
        }

        /// <summary>The newest lesson in which the player had that kind of thing in their hands (not one for driving or climbing); null if none.</summary>
        public static Lesson With(string barcode)
        {
            if (string.IsNullOrEmpty(barcode))
                return null;
            for (int i = All.Count - 1; i >= 0; i--)
            {
                Lesson l = All[i];
                if (l.purpose == "drive" || l.purpose == "climb" || l.item == "wall" || l.take == null)
                    continue;
                foreach (Clone.Event e in l.take.events)
                {
                    if (e.barcode == barcode)
                        return l;
                }
            }
            return null;
        }

        /// <summary>Every lesson it can drive by, the newest first (one for each kind of thing: the newest that will do).</summary>
        public static List<Lesson> Drives()
        {
            List<Lesson> found = new List<Lesson>();
            HashSet<string> kinds = new HashSet<string>();
            for (int i = All.Count - 1; i >= 0; i--)
            {
                if (All[i].purpose != "drive")
                    continue;
                Driving.Model m = Driving.Of(All[i]);
                if (m != null && m.ok && kinds.Add(m.barcode))
                    found.Add(All[i]);
            }
            return found;
        }

        /// <summary>The newest lesson it can drive by (one in which the player drove the thing about enough to tell how); null if none.</summary>
        public static Lesson Drive()
        {
            for (int i = All.Count - 1; i >= 0; i--)
            {
                if (All[i].purpose != "drive")
                    continue;
                Driving.Model m = Driving.Of(All[i]);
                if (m != null && m.ok)
                    return All[i];
            }
            return null;
        }

        /// <summary>A way the player drove on this level (the newest, mostly), to drive along as they did; null if none was kept.</summary>
        public static Driving.Model Road()
        {
            List<Driving.Model> roads = new List<Driving.Model>();
            foreach (Lesson l in All)
            {
                if (l.purpose != "drive" || l.level != Level)
                    continue;
                Driving.Model m = Driving.Of(l);
                if (m != null && m.road.Count > 8)
                    roads.Add(m);
            }
            if (roads.Count == 0)
                return null;
            return roads[UnityEngine.Random.value < 0.6f ? roads.Count - 1 : UnityEngine.Random.Range(0, roads.Count)];
        }

        /// <summary>For the lab: how each thing it was shown to drive is driven.</summary>
        public static string DriveSaid()
        {
            int n = 0;
            foreach (Lesson l in All)
            {
                if (l.purpose == "drive" && Driving.Of(l) != null)
                    n++;
            }
            return n + " lesson(s) with a seat";
        }

        /// <summary>What each of its lessons is for, in a line.</summary>
        public static string Purposes()
        {
            List<string> said = new List<string>();
            foreach (Lesson l in All)
                said.Add(l.name + ": " + l.purpose + (l.purpose == "climb" ? " (" + l.rise.ToString("0.0") + " m up" + (l.loopRise > 0f ? ", " + l.loopRise.ToString("0.0") + " m a go, again and again" : "") + ")" : ""));
            return said.Count == 0 ? "no lessons" : string.Join("; ", said);
        }

        // What a lesson is for, from what is in it (and what was seen about the player as it was recorded).
        private static void Analyse(Lesson l)
        {
            List<Clone.Frame> f = l.take.frames;
            if (f.Count < 10)
                return;
            ReadInfo(l);
            float y0 = f[0].pelvis.y;
            float peak = y0;
            int jumps = 0;
            for (int i = 1; i < f.Count; i++)
            {
                peak = Mathf.Max(peak, f[i].pelvis.y);
                if ((f[i].pelvis - f[i - 1].pelvis).magnitude > 1.5f)
                    jumps++;
            }
            l.rise = peak - y0;
            float ended = f[f.Count - 1].pelvis.y - y0;
            int shots = l.take.events.FindAll(e => e.kind == Clone.Kind.Fire).Count;
            int spawns = l.take.events.FindAll(e => e.kind == Clone.Kind.Spawn).Count;
            bool sat = l.take.events.Exists(e => e.kind == Clone.Kind.Seat && e.hand == 1);
            l.purpose = sat ? "drive" : jumps > 0 ? "portal" : l.rise > 1.5f && ended > 1.2f ? "climb" : l.harm > 0 ? "fight" : shots > 0 ? "shoot" : spawns > 0 ? "make" : l.moved > 0 ? "move" : "use";
            l.loopRise = 0f;
            if (l.purpose != "climb")
                return;
            // Where the going up begins (the last moment still at the bottom), and a stretch of it
            // that begins and ends in much the same pose, some way up: done over and over, that
            // goes up as far as it is asked to.
            int start = 0;
            for (int i = 0; i < f.Count && f[i].pelvis.y < y0 + l.rise * 0.9f; i++)
            {
                if (f[i].pelvis.y <= y0 + 0.15f)
                    start = i;
            }
            l.riseAt = f[start].t;
            l.topAt = l.take.Length;
            l.overRise = 0f;
            for (int i = start; i < f.Count; i++)
            {
                if (f[i].pelvis.y >= y0 + l.rise * 0.9f)
                {
                    l.topAt = f[i].t;
                    l.overRise = peak - f[i].pelvis.y;
                    break;
                }
            }
            int a = start;
            while (a < f.Count - 1 && f[a].pelvis.y < y0 + l.rise * 0.2f)
                a++;
            int best = -1;
            float least = float.MaxValue;
            for (int b = a + 1; b < f.Count && f[b].pelvis.y < y0 + l.rise * 0.88f; b++)
            {
                if (f[b].t - f[a].t < 1f || f[b].pelvis.y - f[a].pelvis.y < 0.4f)
                    continue;
                float d = Apart(f[a], f[b]);
                if (d < least)
                {
                    least = d;
                    best = b;
                }
            }
            if (best > a)
            {
                l.loopFrom = f[a].t;
                l.loopTo = f[best].t;
                l.loopRise = f[best].pelvis.y - f[a].pelvis.y;
                l.afterLoop = peak - f[best].pelvis.y;
            }
        }

        // How unlike two moments of a recording are (the hands from the head, and what they hold shut).
        private static float Apart(Clone.Frame a, Clone.Frame b)
        {
            Vector3 la = a.left.position - a.head.position, lb = b.left.position - b.head.position;
            Vector3 ra = a.right.position - a.head.position, rb = b.right.position - b.head.position;
            return (la - lb).magnitude + (ra - rb).magnitude + 0.25f * Mathf.Abs(a.leftIn.grip - b.leftIn.grip) + 0.25f * Mathf.Abs(a.rightIn.grip - b.rightIn.grip)
                   + 0.15f * (a.leftIn.primary != b.leftIn.primary ? 1f : 0f) + 0.15f * (a.rightIn.primary != b.rightIn.primary ? 1f : 0f);
        }

        private static void ReadInfo(Lesson l)
        {
            try
            {
                string info = l.file + ".info";
                if (!File.Exists(info))
                    return;
                foreach (string line in File.ReadAllLines(info))
                {
                    string[] p = line.Split('=');
                    if (p.Length != 2 || !int.TryParse(p[1], out int n))
                        continue;
                    if (p[0] == "moved")
                        l.moved = n;
                    else if (p[0] == "harm")
                        l.harm = n;
                }
            }
            catch (Exception)
            {
            }
        }

        // What is about the player as a lesson begins (to see, at its end, what they moved), and what was hurt meanwhile.
        private static readonly List<(Rigidbody rb, Vector3 at)> about = new List<(Rigidbody, Vector3)>();
        private static int harmed;

        /// <summary>Something was hurt (a bot, by anything) while a lesson is being recorded.</summary>
        public static void NoteHarm()
        {
            if (Clone.Lesson.On)
                harmed++;
        }

        private static void LookAbout()
        {
            about.Clear();
            harmed = 0;
            Transform head = Player.Head;
            if (head == null)
                return;
            HashSet<IntPtr> seen = new HashSet<IntPtr>();
            foreach (Collider c in Physics.OverlapSphere(head.position, 12f, ~0, QueryTriggerInteraction.Ignore))
            {
                Rigidbody rb = c != null ? c.attachedRigidbody : null;
                if (rb == null || rb.isKinematic || !seen.Add(rb.Pointer) || rb.GetComponentInParent<RigManager>() != null || rb.velocity.magnitude > 0.3f)
                    continue;
                if (watchedThing != null && rb.transform.IsChildOf(watchedThing))
                    continue;
                about.Add((rb, rb.worldCenterOfMass));
                if (about.Count >= 40)
                    break;
            }
        }

        private const int KeptOfEach = 6;
        private const float Longest = 90f;

        private static List<Lesson> all;
        private static IntPtr watched;
        private static Transform watchedThing;
        private static float lastHeldAt;

        private static string Folder => AssetFiles.SaveFolder("lessons");

        /// <summary>The level the player is on (its barcode), or "".</summary>
        public static string Level
        {
            get
            {
                try
                {
                    return Il2CppSLZ.Marrow.SceneStreaming.SceneStreamer.Session.Level.Barcode.ID;
                }
                catch (Exception)
                {
                    return "";
                }
            }
        }

        private static List<Lesson> All
        {
            get
            {
                if (all != null)
                    return all;
                all = new List<Lesson>();
                try
                {
                    if (Directory.Exists(Folder))
                    {
                        string[] files = Directory.GetFiles(Folder, "*.bin");
                        Array.Sort(files, StringComparer.Ordinal);
                        foreach (string file in files)
                        {
                            try
                            {
                                Clone.Take take = Clone.Load(file);
                                if (take.frames.Count > 10 && !string.IsNullOrEmpty(take.item))
                                {
                                    Lesson read = new Lesson { file = file, item = take.item, name = take.itemName, level = take.level, length = take.Length, take = take, savedAt = -1000f };
                                    Analyse(read);
                                    all.Add(read);
                                }
                            }
                            catch (Exception e)
                            {
                                MelonLogger.Warning("Bot Player: a lesson could not be read (" + Path.GetFileName(file) + "): " + e.Message);
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    MelonLogger.Warning("Bot Player: the lessons could not be read: " + e.Message);
                }
                return all;
            }
        }

        /// <summary>Everything forgotten, in memory (its files have been put away).</summary>
        public static void Reset()
        {
            Cancel();
            all = null;
        }

        public static int Count => All.Count;

        /// <summary>The things it has lessons for are things there are to try, too.</summary>
        public static void TellSeen()
        {
            foreach (Lesson l in All)
                SelfTaught.Seen(l.item, l.name);
        }

        public static string Summary()
        {
            HashSet<string> things = new HashSet<string>();
            foreach (Lesson l in All)
                things.Add(l.item);
            return All.Count + " lessons with " + things.Count + " tools";
        }

        /// <summary>The things it has lessons for, by name.</summary>
        public static string Names()
        {
            List<string> names = new List<string>();
            foreach (Lesson l in All)
            {
                if (!names.Contains(l.name))
                    names.Add(l.name);
            }
            return names.Count == 0 ? "nothing yet" : string.Join(", ", names);
        }

        /// <summary>
        /// A lesson to do: a thing by chance (not the one just done, if there is another; one
        /// named, if asked for), then one of its lessons, the newer the likelier.
        /// </summary>
        public static Lesson Pick(string notItem, string named = null)
        {
            List<string> things = new List<string>();
            foreach (Lesson l in All)
            {
                if (named != null && l.name.IndexOf(named, StringComparison.OrdinalIgnoreCase) < 0 && l.item.IndexOf(named, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                // (A climb is for a wall, and is done at one: never just anywhere, up into the air.)
                if (l.purpose == "climb" || l.item == "wall")
                    continue;
                // (Nor is driving done by going just where the player went: it is driven somewhere, see BotDrive.)
                if (l.purpose == "drive" && named == null)
                    continue;
                // (Nor an older lesson with a thing that is driven, kept before sitting down was: only the hands of it, in the air.)
                if (named == null && All.Exists(d => d.purpose == "drive" && d.item == l.item))
                    continue;
                if (!things.Contains(l.item))
                    things.Add(l.item);
            }
            if (things.Count == 0)
                return null;
            if (things.Count > 1 && notItem != null)
                things.Remove(notItem);
            string thing = things[UnityEngine.Random.Range(0, things.Count)];
            List<Lesson> of = All.FindAll(l => l.item == thing && l.purpose != "climb" && (l.purpose != "drive" || named != null));
            // (In order of age: the newest twice as likely as the oldest.)
            float total = 0f;
            for (int i = 0; i < of.Count; i++)
                total += 1f + (of.Count > 1 ? i / (float)(of.Count - 1) : 0f);
            float roll = UnityEngine.Random.value * total;
            for (int i = 0; i < of.Count; i++)
            {
                roll -= 1f + (of.Count > 1 ? i / (float)(of.Count - 1) : 0f);
                if (roll <= 0f)
                    return of[i];
            }
            return of[of.Count - 1];
        }

        // ---------------------------------------------------------------- watching the player

        /// <summary>The player taking hold of something: a lesson begins (before the grab is noted, so it is its first moment).</summary>
        public static void OnGrip(Grip grip, Hand hand, bool on)
        {
            if (!on || grip == null || hand == null)
                return;
            if (hand != Player.LeftHand && hand != Player.RightHand)
                return;
            NoteSeen(grip);
            if (!Recorder.Recording || Clone.Lesson.On)
                return;
            try
            {
                if (grip.GetComponentInParent<RigManager>() != null)
                    return;
                Poolee poolee = grip.GetComponentInParent<Poolee>();
                if (poolee == null || poolee.SpawnableCrate == null)
                    return;
                string barcode = poolee.SpawnableCrate.Barcode.ID;
                string name = poolee.gameObject.name;
                string upper = name.ToUpperInvariant();
                // Not a body, not the player's own tools for making things, not a magazine by itself.
                if (Clone.IsBody(barcode, name) || upper.Contains("SPAWN") || upper.Contains("NIMBUS") || poolee.GetComponent<Magazine>() != null)
                    return;
                // (Nor a gun, nor ammunition: how they shoot and reload is learned in its own way
                // (see GunMoves and the Fire and Reload slots). Kept as a lesson, a minute of
                // their shooting and fetching ammunition was done over by every bot, all at once.)
                Gun firearm = poolee.GetComponentInChildren<Gun>();
                if ((firearm != null && firearm.defaultMagazine != null) || upper.Contains("AMMO") || barcode.ToUpperInvariant().Contains("AMMO"))
                    return;
                if (!Clone.Begin(Clone.Lesson))
                    return;
                Clone.Take take = Clone.Lesson.taking;
                take.item = barcode;
                string title = null;
                try
                {
                    title = poolee.SpawnableCrate.Title;
                }
                catch (Exception)
                {
                }
                take.itemName = string.IsNullOrEmpty(title) ? Clean(name) : title;
                take.level = Level;
                watched = poolee.Pointer;
                watchedThing = poolee.transform;
                lastHeldAt = Time.time;
                LookAbout();
                Lab.Event("lesson: watching you with the " + take.itemName);
            }
            catch (Exception e)
            {
                MelonLogger.Warning("Bot Player: a lesson could not begin: " + e.Message);
            }
        }

        /// <summary>A spawned thing's name, as the game's menus have it.</summary>
        public static string NameOf(Poolee poolee)
        {
            string title = null;
            try
            {
                title = poolee.SpawnableCrate.Title;
            }
            catch (Exception)
            {
            }
            return string.IsNullOrEmpty(title) ? Clean(poolee.gameObject.name) : title;
        }

        // Whatever the player takes in hand (learning from them or not) is something there is in
        // the world for a bot to try for itself (see SelfTaught).
        private static void NoteSeen(Grip grip)
        {
            try
            {
                if (grip.GetComponentInParent<RigManager>() != null)
                    return;
                Poolee poolee = grip.GetComponentInParent<Poolee>();
                if (poolee == null || poolee.SpawnableCrate == null)
                    return;
                string barcode = poolee.SpawnableCrate.Barcode.ID;
                string upper = poolee.gameObject.name.ToUpperInvariant();
                if (Clone.IsBody(barcode, poolee.gameObject.name) || upper.Contains("SPAWN") || upper.Contains("NIMBUS"))
                    return;
                if (poolee.GetComponentInChildren<Gun>() != null || poolee.GetComponentInChildren<Magazine>() != null)
                    return;
                SelfTaught.Seen(barcode, NameOf(poolee));
            }
            catch (Exception)
            {
            }
        }

        private static string Clean(string name)
        {
            int cut = name.IndexOf(" [", StringComparison.Ordinal);
            return (cut > 0 ? name.Substring(0, cut) : name).Replace("(Clone)", "").Trim();
        }

        /// <summary>Once a frame: the lesson going on ends when the thing has been out of their hands a moment.</summary>
        public static void Update()
        {
            Tidy();
            if (!Clone.Lesson.On)
            {
                Driving.Live = null;
                liveSaid = false;
                return;
            }
            // Driving something: what there is of it so far is learned from as they go, every few
            // seconds, and bots driving the same kind of thing take it up at once.
            if (Time.time >= liveAt)
            {
                liveAt = Time.time + Tuning.F("drive.live", 4f);
                Clone.Take so = Clone.Lesson.taking;
                if (so != null && so.frames.Count > 300 && so.events.Exists(e => e.kind == Clone.Kind.Seat && e.hand == 1))
                {
                    Driving.Model m = Driving.LearnNow(so);
                    if (m.ok)
                    {
                        Driving.Live = m;
                        if (!liveSaid)
                        {
                            liveSaid = true;
                            Lab.Event("driving, live: from how you are driving now, it " + m.Said());
                        }
                    }
                }
            }
            // (Climbing a wall by their bare hands: for as long as a hand has hold of it, and a little after, for getting over the top.)
            if (wallLesson)
            {
                if (WallGrab.Holding(Player.LeftHand) || WallGrab.Holding(Player.RightHand))
                    lastHeldAt = Time.time;
                if (!Recorder.Recording || Time.time - lastHeldAt > 2.5f || Clone.Lesson.Now > Longest)
                    Finish(lastHeldAt - Clone.Lesson.startedAt + 1.5f);
                return;
            }
            bool gone = watchedThing == null;
            if (!gone && (Holds(Player.LeftHand) || Holds(Player.RightHand)))
                lastHeldAt = Time.time;
            if (gone || !Recorder.Recording || Time.time - lastHeldAt > 1.5f || Clone.Lesson.Now > Longest)
                Finish(!Recorder.Recording && Time.time - lastHeldAt < 0.2f ? Clone.Lesson.Now : lastHeldAt - Clone.Lesson.startedAt + 0.35f);
        }

        private static bool wallLesson;

        /// <summary>
        /// The player's bare hand taking hold of a wall (see WallGrab), or letting go of it.
        /// With the bots learning from them and no lesson going, one begins: how they climb by
        /// their hands. Each hold and letting go is kept in it.
        /// </summary>
        public static void OnWall(int hand, bool on)
        {
            try
            {
                if (on && Recorder.Recording && !Clone.Lesson.On && Clone.Begin(Clone.Lesson))
                {
                    Clone.Take take = Clone.Lesson.taking;
                    take.item = "wall";
                    take.itemName = "Bare hands";
                    take.level = Level;
                    wallLesson = true;
                    watchedThing = null;
                    lastHeldAt = Time.time;
                    Lab.Event("lesson: watching you climb by your hands");
                }
                if (Clone.Lesson.On && wallLesson)
                    Clone.Lesson.taking.events.Add(new Clone.Event { t = Clone.Lesson.Now, kind = on ? Clone.Kind.Grab : Clone.Kind.Release, hand = hand, name = "@wall" });
            }
            catch (Exception)
            {
            }
        }

        private static float liveAt;
        private static bool liveSaid;

        private static bool Holds(Hand hand)
        {
            GameObject held = hand != null ? hand.m_CurrentAttachedGO : null;
            return held != null && watchedThing != null && held.transform.IsChildOf(watchedThing);
        }

        /// <summary>Drops the lesson being recorded (the level ending, the bots stopping).</summary>
        public static void Cancel()
        {
            Clone.End(Clone.Lesson);
            watchedThing = null;
        }

        // The lesson over: cut where they let go, kept if they did something with the thing.
        private static void Finish(float until)
        {
            Clone.Take take = Clone.End(Clone.Lesson);
            watchedThing = null;
            bool byHands = wallLesson;
            wallLesson = false;
            if (take == null)
                return;
            take.frames.RemoveAll(f => f.t > until);
            take.events.RemoveAll(e => e.t > until);
            int presses = 0;
            float fastest = 0f;
            for (int i = 1; i < take.frames.Count; i++)
            {
                Clone.Frame a = take.frames[i - 1], b = take.frames[i];
                presses += Presses(a.leftIn, b.leftIn) + Presses(a.rightIn, b.rightIn);
                float dt = b.t - a.t;
                if (dt > 0f)
                    fastest = Mathf.Max(fastest, (b.left.position - a.left.position).magnitude / dt, (b.right.position - a.right.position).magnitude / dt);
            }
            int shots = take.events.FindAll(e => e.kind == Clone.Kind.Fire).Count;
            // (Only carried about: nothing to learn in that.)
            // (Sat in something and drove it is something done, with never a button pressed.)
            bool sat = take.events.Exists(e => e.kind == Clone.Kind.Seat);
            // (A climb by their hands: kept if it took them up anywhere.)
            if (byHands)
            {
                float low = float.MaxValue, high = float.MinValue;
                foreach (Clone.Frame fr in take.frames)
                {
                    low = Mathf.Min(low, fr.pelvis.y);
                    high = Mathf.Max(high, fr.pelvis.y);
                }
                sat = take.frames.Count > 30 && high - take.frames[0].pelvis.y > 1.2f;
                if (!sat)
                {
                    Lab.Event("lesson: your hands on a wall, but not up it (" + (take.frames.Count > 0 ? (high - take.frames[0].pelvis.y).ToString("0.0") : "0") + " m), not kept");
                    return;
                }
            }
            if (take.frames.Count < 2 || take.Length < 2f || (presses == 0 && shots == 0 && fastest < 2.5f && !sat))
            {
                Lab.Event("lesson: nothing done with the " + take.itemName + " (" + take.Length.ToString("0.0") + " s), not kept");
                return;
            }
            try
            {
                Directory.CreateDirectory(Folder);
                string file = Path.Combine(Folder, Safe(take.item) + "__" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".bin");
                Clone.Save(take, file);
                List<Lesson> list = All;
                Lesson made = new Lesson { file = file, item = take.item, name = take.itemName, level = take.level, length = take.Length, take = take, savedAt = Time.time };
                foreach ((Rigidbody rb, Vector3 at) in about)
                    made.moved += rb != null && (rb.worldCenterOfMass - at).magnitude > 0.5f ? 1 : 0;
                made.harm = harmed;
                try
                {
                    File.WriteAllLines(file + ".info", new[] { "moved=" + made.moved, "harm=" + made.harm });
                }
                catch (Exception)
                {
                }
                Analyse(made);
                list.Add(made);
                Lab.Event("lesson: the " + made.name + " is for: " + made.purpose + (made.purpose == "climb" ? " (" + made.rise.ToString("0.0") + " m up; the part to do again and again: "
                    + (made.loopRise > 0f ? made.loopFrom.ToString("0.0") + " to " + made.loopTo.ToString("0.0") + " s, " + made.loopRise.ToString("0.0") + " m each" : "none found") + ")" : "")
                    + (made.moved > 0 ? ", moved " + made.moved + " things" : "") + (made.harm > 0 ? ", hurt something " + made.harm + " times" : ""));
                // The newest few of each thing are kept.
                List<Lesson> of = list.FindAll(l => l.item == take.item);
                for (int i = 0; i < of.Count - KeptOfEach; i++)
                    Remove(of[i]);
                foreach (Bot bot in BotManager.All)
                    bot.LessonSoon();
                string what = made.purpose == "climb" ? " to climb" : made.purpose == "portal" ? " to get somewhere" : made.purpose == "fight" ? " to fight" : made.purpose == "move" ? " to move things" : "";
                Spawnables.Notify("Learned how you use the " + take.itemName + what + " (" + take.Length.ToString("0") + " s" + (presses > 0 ? ", " + presses + " presses" : "") + ")");
                Lab.Event("lesson: kept " + take.itemName + ", " + take.Length.ToString("0.0") + " s, " + presses + " presses, " + shots + " shots, " + take.events.Count + " events");
            }
            catch (Exception e)
            {
                MelonLogger.Warning("Bot Player: the lesson could not be saved: " + e.Message);
            }
        }

        // Buttons that went down from one frame to the next (not the grip: that is down for as long as the thing is held).
        private static int Presses(Clone.HandInput a, Clone.HandInput b)
        {
            int n = 0;
            n += b.primary && !a.primary || b.trigger > 0.5f && a.trigger <= 0.5f && !b.primary ? 1 : 0;
            n += b.a && !a.a ? 1 : 0;
            n += b.b && !a.b ? 1 : 0;
            n += b.menu && !a.menu ? 1 : 0;
            n += b.click && !a.click ? 1 : 0;
            n += b.pad && !a.pad ? 1 : 0;
            return n;
        }

        private static void Remove(Lesson lesson)
        {
            All.Remove(lesson);
            try
            {
                File.Delete(lesson.file);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>Takes back the lessons learned since then (and the one going on); how many.</summary>
        public static int ForgetSince(float since)
        {
            int n = 0;
            if (Clone.Lesson.On)
            {
                Cancel();
                n++;
            }
            foreach (Lesson l in All.FindAll(l => l.savedAt >= since))
            {
                Remove(l);
                n++;
            }
            return n;
        }

        private static string Safe(string barcode)
        {
            char[] c = barcode.ToCharArray();
            for (int i = 0; i < c.Length; i++)
            {
                if (!char.IsLetterOrDigit(c[i]) && c[i] != '.' && c[i] != '-')
                    c[i] = '_';
            }
            return new string(c);
        }

        // ---------------------------------------------------------------- clearing up after bots

        private static readonly List<(Poolee thing, float at)> litter = new List<(Poolee, float)>();

        /// <summary>A thing a bot spawned for a lesson and has put down: cleared away after a while, unless someone has it.</summary>
        public static void Litter(Poolee thing)
        {
            // (Bots left to what is on the map: nothing there is theirs to clear away.)
            if (!BotManager.OwnSpawns)
                return;
            if (thing != null)
                litter.Add((thing, Time.time));
        }

        /// <summary>The same, cleared away after so many seconds.</summary>
        public static void Litter(Poolee thing, float after)
        {
            if (!BotManager.OwnSpawns)
                return;
            if (thing != null)
                litter.Add((thing, Time.time - Tuning.F("lesson.litter", 30f) + after));
        }

        public static void ForgetLevel()
        {
            litter.Clear();
            Cancel();
        }

        private static void Tidy()
        {
            for (int i = litter.Count - 1; i >= 0; i--)
            {
                (Poolee thing, float at) = litter[i];
                if (thing == null)
                {
                    litter.RemoveAt(i);
                    continue;
                }
                if (Time.time - at < Tuning.F("lesson.litter", 30f))
                    continue;
                litter.RemoveAt(i);
                try
                {
                    bool held = false;
                    foreach (Grip g in thing.GetComponentsInChildren<Grip>())
                        held |= g != null && g.attachedHands != null && g.attachedHands.Count > 0;
                    foreach (Seat seat in thing.GetComponentsInChildren<Seat>(true))
                        held |= seat != null && seat.rigManager != null;
                    if (!held)
                        thing.Despawn();
                }
                catch (Exception)
                {
                }
            }
        }
    }
}
