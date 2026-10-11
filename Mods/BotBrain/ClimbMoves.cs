using System.IO;
using System.Text.Json;
using BoneLib;
using Il2CppSLZ.Marrow;
using MelonLoader;
using UnityEngine;
using Pose = ThirdEyeBonelabMod.Bots.Bot.Pose;

namespace ThirdEyeBonelabMod.Bots
{
    /// <summary>
    /// How the player climbs, learned while "Learn from me" is on: from the moment a hand takes
    /// hold of the world (a ledge, a wall top: the game's world grip) until they have let go and
    /// are up, their head and hands moment by moment (from the edge first taken, turned the way
    /// they faced it) and which hand holds when. Kept if they did get up; a bot climbs the same
    /// way (BotClimb), its hands taking the world where the player's did.
    /// </summary>
    public static class ClimbMoves
    {
        public class Frame
        {
            public float t { get; set; }
            public float[] head { get; set; }
            public float[] left { get; set; }
            public float[] right { get; set; }
            public bool lh { get; set; }
            public bool rh { get; set; }
        }

        public class Climb
        {
            /// <summary>How high the edge was above their feet.</summary>
            public float height { get; set; }
            /// <summary>The slot it was recorded for (see MoveSlots), or -1: learned by watching.</summary>
            public int slot { get; set; } = -1;
            public List<Frame> frames { get; set; } = new List<Frame>();
            public float Length => frames.Count > 0 ? frames[frames.Count - 1].t : 0f;
        }

        public class Store
        {
            public List<Climb> climbs { get; set; } = new List<Climb>();
        }

        private static Store store;
        private static string File => Path.Combine(AssetFiles.SaveFolder(""), "climbs.json");

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
                    MelonLogger.Warning("Bot Player: the climbs could not be read: " + e.Message);
                }
                return store;
            }
        }

        private static void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(File));
                System.IO.File.WriteAllText(File, JsonSerializer.Serialize(Data));
            }
            catch (Exception e)
            {
                MelonLogger.Warning("Bot Player: the climbs could not be saved: " + e.Message);
            }
        }

        public static int Count => Data.climbs.Count;

        /// <summary>A climb of the player's for an edge about so high (one of those nearest in height, by chance); null if none.</summary>
        public static Climb Pick(float height)
        {
            List<Climb> all = Data.climbs;
            if (all.Count == 0)
                return null;
            List<Climb> sorted = new List<Climb>(all);
            sorted.Sort((a, b) => Mathf.Abs(a.height - height).CompareTo(Mathf.Abs(b.height - height)));
            return sorted[UnityEngine.Random.Range(0, Mathf.Min(3, sorted.Count))];
        }

        // ---------------------------------------------------------------- watching

        private static Climb taking;
        private static float startedAt;
        private static float sampleAt;
        private static float startHead;
        private static Vector3 edge;
        private static Quaternion facing;
        private static readonly bool[] holding = new bool[2];
        private static float lastHeldAt;
        private static readonly List<(float at, Climb climb)> learned = new List<(float, Climb)>();

        /// <summary>A hand of the player's taking or letting go of a grip (the world's is the one that matters).</summary>
        public static void OnGrip(Grip grip, Hand hand, bool on)
        {
            if (!Learning || grip == null || hand == null)
                return;
            RigManager rig = Player.RigManager;
            if (rig == null || rig.worldGrip == null || grip.Pointer != rig.worldGrip.Pointer)
                return;
            int which = hand == Player.LeftHand ? 0 : hand == Player.RightHand ? 1 : -1;
            if (which < 0)
                return;
            holding[which] = on;
            if (on)
            {
                lastHeldAt = Time.time;
                if (taking == null)
                    Begin(hand);
            }
        }

        private static void Begin(Hand hand)
        {
            Transform head = Player.Head;
            if (head == null)
                return;
            edge = hand.transform.position;
            facing = Quaternion.Euler(0f, Recorder.Yaw(head.forward), 0f);
            PhysicsRig body = Player.PhysicsRig;
            float feet = body != null && body.centerOfPressure != null ? body.centerOfPressure.position.y : head.position.y - 1.6f;
            taking = new Climb { height = edge.y - feet };
            startedAt = Time.time;
            sampleAt = 0f;
            startHead = head.position.y;
            Lab.Event("climb: watching (edge " + taking.height.ToString("0.00") + " m up)");
        }

        /// <summary>Once a frame: their climb, moment by moment, till they are up (or it comes to nothing).</summary>
        public static void Update()
        {
            if (taking == null)
                return;
            float t = Time.time - startedAt;
            if (t > 12f || !Learning)
            {
                taking = null;
                return;
            }
            OpenControllerRig controls = Player.ControllerRig;
            if (controls == null || controls.headset == null)
                return;
            if (t >= sampleAt)
            {
                sampleAt = t + 1f / 30f;
                Pose from = new Pose(edge, facing).Inverse();
                taking.frames.Add(new Frame
                {
                    t = t,
                    head = P(from * Pose.Of(controls.headset)),
                    left = P(from * Pose.Of(controls.leftController.transform)),
                    right = P(from * Pose.Of(controls.rightController.transform)),
                    lh = holding[0],
                    rh = holding[1],
                });
            }
            // Let go, and a moment gone by: up, or not.
            if (!holding[0] && !holding[1] && Time.time - lastHeldAt > 0.6f)
            {
                float rose = controls.headset.position.y - startHead;
                if (rose > 0.35f && taking.frames.Count > 10)
                {
                    taking.slot = MoveSlots.RecordingSlot(MoveSlots.Kind.Climb) >= 0 ? MoveSlots.Tag(MoveSlots.Kind.Climb, MoveSlots.RecordingSlot(MoveSlots.Kind.Climb)) : -1;
                    Data.climbs.Add(taking);
                    if (Data.climbs.Count > 20)
                        Data.climbs.RemoveAt(0);
                    learned.Add((Time.time, taking));
                    Save();
                    Lab.Event("climb: learned (" + taking.Length.ToString("0.0") + " s, rose " + rose.ToString("0.00") + " m)");
                    Spawnables.Notify("Learned how you climb");
                }
                taking = null;
            }
        }

        // Climbs are learned while the player is being learned from, or records a climb into a slot.
        private static bool Learning => Recorder.Recording || MoveSlots.Recording(MoveSlots.Kind.Climb);

        /// <summary>The climbs recorded for a slot, taken back.</summary>
        public static void ClearSlot(int slot)
        {
            if (Data.climbs.RemoveAll(c => c.slot == slot) > 0)
                Save();
        }

        /// <summary>The climbs of one slot are another's from now on (the slots have moved up).</summary>
        public static void MoveSlot(int from, int to)
        {
            bool any = false;
            foreach (Climb c in Data.climbs)
            {
                if (c.slot == from)
                {
                    c.slot = to;
                    any = true;
                }
            }
            if (any)
                Save();
        }

        /// <summary>Everything forgotten, in memory (its file has been put away).</summary>
        public static void Reset()
        {
            store = new Store();
            learned.Clear();
            taking = null;
        }

        /// <summary>Takes back the climbs learned since then; how many.</summary>
        public static int ForgetSince(float since)
        {
            int n = 0;
            for (int i = learned.Count - 1; i >= 0 && learned[i].at >= since; i--)
            {
                Data.climbs.Remove(learned[i].climb);
                learned.RemoveAt(i);
                n++;
            }
            if (n > 0)
                Save();
            return n;
        }

        public static string Summary() => Count + " climbs";

        private static float[] P(Pose p) => new[] { p.position.x, p.position.y, p.position.z, p.rotation.x, p.rotation.y, p.rotation.z, p.rotation.w };

        public static Pose Read(float[] v) => new Pose(new Vector3(v[0], v[1], v[2]), new Quaternion(v[3], v[4], v[5], v[6]));
    }
}
