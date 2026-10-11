using BoneLib;
using Il2CppSLZ.Marrow;
using UnityEngine;

namespace ThirdEyeBonelabMod.Bots
{
    /// <summary>
    /// The player's wave, seen: a hand up by their head going side to side. (Watched once for
    /// all the bots; each decides for itself whether the wave was at it.)
    /// </summary>
    public static class Gestures
    {
        /// <summary>When the player last waved (Time.time), and which way they were facing.</summary>
        public static float WavedAt = -100f;
        public static Vector3 WaveFacing = Vector3.forward;

        private class Side
        {
            public Vector3 last;
            public bool has;
            public float way;
            public readonly List<float> turns = new List<float>();
        }

        private static readonly Side left = new Side(), right = new Side();

        public static void Update()
        {
            Transform head = Player.Head;
            if (head == null)
                return;
            Watch(left, Player.LeftHand, head);
            Watch(right, Player.RightHand, head);
        }

        private static void Watch(Side side, Hand hand, Transform head)
        {
            if (hand == null || Time.deltaTime <= 0f)
            {
                side.has = false;
                return;
            }
            // (Where the hand is from the head, so walking and turning are not taken for waving.)
            Vector3 at = head.InverseTransformPoint(hand.transform.position);
            Vector3 was = side.last;
            side.last = at;
            if (!side.has)
            {
                side.has = true;
                return;
            }
            // Up by the head, empty, and out from the body a little.
            bool raised = at.y > Tuning.F("wave.see.height", -0.15f) && at.z > -0.1f && hand.m_CurrentAttachedGO == null;
            if (!raised)
            {
                side.turns.Clear();
                side.way = 0f;
                return;
            }
            float speed = (at.x - was.x) / Time.deltaTime;
            if (Mathf.Abs(speed) < Tuning.F("wave.see.speed", 0.5f))
                return;
            float way = Mathf.Sign(speed);
            if (way == side.way)
                return;
            side.way = way;
            side.turns.Add(Time.time);
            while (side.turns.Count > 0 && Time.time - side.turns[0] > 1.6f)
                side.turns.RemoveAt(0);
            // Side to side and back again (three turns in a second and a half): a wave.
            if (side.turns.Count >= 3 && Time.time - WavedAt > 2.5f)
            {
                WavedAt = Time.time;
                WaveFacing = head.forward;
                side.turns.Clear();
                Lab.Event("you waved");
            }
        }
    }

    /// <summary>
    /// What it does without thinking: ducks at a shot close by (less each time, as the firing
    /// goes on), takes a landing in its knees with its arms out after a drop, waves back when
    /// the player waves at it, and says hello when they turn up again after a while.
    /// </summary>
    public partial class Bot
    {
        // A flinch: from when, how hard, and which way the bang was (in its body's frame).
        private float flinchAt = -10f;
        private float flinchSize;
        private float flinchSide;
        private float nerves;
        private float nervesAt;

        // Falling, and landing.
        private Vector3 fallLast;
        private bool fallHas;
        private float fallSpeed;
        private bool falling;
        private float fallTop;
        private float fallSince;
        private float landAt = -10f;
        private float landSize;
        private float noLandUntil;

        // Waving.
        private float waveSeen;
        private float waveDue = -1f;
        private int waveTimes;
        private bool waveLeft;
        private float lastNearYou;
        private bool nearYou;

        private bool HandsFree => !handOverride && !Playing && !climbing && !fists;

        /// <summary>A bang close by: it starts (the closer the harder; less when it has heard a lot of them lately).</summary>
        private void Startle(Vector3 from, float far)
        {
            float range = Tuning.F("flinch.range", 14f);
            if (far > range || !HandsFree)
                return;
            // (Its nerves: each bang steadies it a little; they come back over some seconds of quiet.)
            nerves = Mathf.Max(0f, nerves - (Time.time - nervesAt) / Tuning.F("flinch.calm", 6f));
            nervesAt = Time.time;
            float size = Mathf.Clamp01(1.15f - far / range) / (1f + nerves) * Mathf.Lerp(1.2f, 0.7f, Mathf.InverseLerp(0.5f, 1.8f, personality.calm));
            nerves += 0.8f;
            if (size < 0.12f || Time.time - flinchAt < 0.25f)
                return;
            Transform head = Head;
            Vector3 local = Quaternion.Euler(0f, -bodyYaw, 0f) * Flat(from - head.position).normalized;
            flinchSide = Mathf.Clamp(local.x, -1f, 1f);
            flinchSize = size;
            // (A start is quick: a tenth of a second or so after the bang.)
            flinchAt = Time.time + Random(0.06f, 0.13f) * personality.reaction;
            Lab.Event("flinch " + size.ToString("0.00") + " at a bang " + far.ToString("0") + " m off");
        }

        /// <summary>The wave it was asked for: so many times, a moment from now.</summary>
        private void StartWave(int times, float after)
        {
            waveTimes = times;
            waveDue = Time.time + after;
            waveLeft = personality.mirror > 0.5f;
        }

        /// <summary>For the lab: its next fidget now, a bang three metres to its right, a wave as if waved at.</summary>
        public void LabFidget() => fidNext = Time.time;

        public void LabStartle()
        {
            if (Head != null)
                Startle(Head.position + Quaternion.Euler(0f, bodyYaw + 90f, 0f) * Vector3.forward * 3f, 3f);
        }

        public void LabWaveBack() => StartWave(3, 0.3f);

        // Each frame, before its pose is made: what it has noticed, and what it does about it.
        private void Notice(Transform you, Transform head)
        {
            if (head == null)
                return;
            Fall(head);
            if (you == null || enemy)
                return;
            Vector3 to = you.position - head.position;
            float far = to.magnitude;
            bool calm = HandsFree && nearestSeen.transform == null && brainTarget.transform == null && Time.time >= waveUntil && waveDue < 0f;
            // The player waving, this way (facing it, near enough, nothing between them).
            if (Gestures.WavedAt > waveSeen)
            {
                waveSeen = Gestures.WavedAt;
                bool atMe = far < Tuning.F("wave.far", 25f) && Vector3.Angle(Flat(Gestures.WaveFacing), Flat(-to)) < Tuning.F("wave.cone", 35f);
                if (calm && atMe && Targets.InSight(head.position, you.position, Player.RigManager != null ? Player.RigManager.transform : null, rig))
                {
                    StartWave(UnityEngine.Random.Range(2, 4), Random(0.35f, 0.8f) * personality.reaction);
                    Lab.Event("waves back");
                }
            }
            // The player back after a while away: hello (not every time).
            bool near = far < 8f;
            if (near && !nearYou && Time.time - lastNearYou > Tuning.F("wave.hello.after", 45f) && Time.time - born > 20f && calm
                && UnityEngine.Random.value < Tuning.F("wave.hello.chance", 0.5f))
            {
                StartWave(2, Random(0.5f, 1.2f));
                Lab.Event("waves hello");
            }
            if (near)
                lastNearYou = Time.time;
            nearYou = near;

            if (waveDue >= 0f && Time.time >= waveDue)
            {
                waveDue = -1f;
                if (HandsFree)
                {
                    waveStart = Time.time;
                    waveUntil = Time.time + 0.35f + waveTimes * 0.7f;
                    WaveOfTheirs();
                    // It stops and looks at them as it waves.
                    Look(you.position, you, "you", waveUntil - Time.time + 0.6f);
                    if (mode == Mode.Wander || (mode == Mode.Brain && act != Act.Idle && nearestSeen.transform == null))
                    {
                        nav.Clear();
                        Idle(waveUntil - Time.time + Random(0.5f, 1.5f));
                    }
                }
            }
        }

        // How fast its head is falling, and the moment it lands.
        private void Fall(Transform head)
        {
            Vector3 at = head.position;
            float dt = Time.deltaTime;
            if (!fallHas || dt <= 0f || (at - fallLast).magnitude > 3f || climbing)
            {
                // (Just put somewhere, or lifted up a wall: no fall in that.)
                fallHas = true;
                fallLast = at;
                fallSpeed = 0f;
                falling = false;
                if (climbing)
                    noLandUntil = Time.time + 1f;
                return;
            }
            fallSpeed = Mathf.Lerp(fallSpeed, (at.y - fallLast.y) / dt, Mathf.Clamp01(dt * 12f));
            fallLast = at;
            if (!falling && fallSpeed < -Tuning.F("land.falling", 2.6f))
            {
                falling = true;
                fallTop = at.y;
                fallSince = Time.time;
            }
            else if (falling && fallSpeed > -0.8f)
            {
                falling = false;
                float drop = (fallTop - at.y + 0.3f) / Mathf.Max(0.4f, pose.height / 1.7f);
                if (drop > Tuning.F("land.from", 0.7f) && Time.time > noLandUntil && Time.time - jumpedAt > 0.2f)
                {
                    landAt = Time.time;
                    landSize = Mathf.Clamp01(drop / Tuning.F("land.full", 3f));
                    Lab.Event("landed from " + drop.ToString("0.0") + " m");
                    // From too high: it does not keep its feet.
                    if (drop > Tuning.F("trip.fall", 4.5f))
                        Trip(Mathf.InverseLerp(4.5f, 9f, drop), head.forward, "a landing from " + drop.ToString("0.0") + " m");
                }
            }
        }

        // Over its pose, each frame (after where it looks): the start at a bang, the fall, the landing.
        private void React(float dt)
        {
            float h = pose.height;
            // (Sized to its body: the places below are a 1.7 m body's.)
            float m = h / 1.7f;
            // Falling: arms out and up a little, for balance.
            if (falling && HandsFree)
            {
                float k = Mathf.Clamp01((Time.time - fallSince) / 0.25f) * Tuning.F("land.arms", 1f);
                pose.leftPos = Vector3.Lerp(pose.leftPos, new Vector3(-0.42f * m, h - 0.45f * m, 0.18f * m), k * 0.8f);
                pose.rightPos = Vector3.Lerp(pose.rightPos, new Vector3(0.42f * m, h - 0.45f * m, 0.18f * m), k * 0.8f);
            }
            // Landing: down into its knees at once, up again slower; arms out, eyes down; it
            // does not walk straight on out of a long drop.
            float since = Time.time - landAt;
            float longs = Mathf.Lerp(0.5f, 1.1f, landSize);
            if (since >= 0f && since < longs)
            {
                float u = since / longs;
                float e = u < 0.18f ? Mathf.SmoothStep(0f, 1f, u / 0.18f) : 1f - Mathf.SmoothStep(0f, 1f, (u - 0.18f) / 0.82f);
                float dip = Tuning.F("land.dip", 0.32f) * landSize * e * m;
                pose.height = h - dip;
                pose.head = Quaternion.Euler(14f * landSize * e, 0f, 0f) * pose.head;
                if (HandsFree)
                {
                    pose.leftPos = Vector3.Lerp(pose.leftPos, new Vector3(-0.4f * m, h - dip - 0.5f * m, 0.22f * m), e * 0.85f);
                    pose.rightPos = Vector3.Lerp(pose.rightPos, new Vector3(0.4f * m, h - dip - 0.5f * m, 0.22f * m), e * 0.85f);
                }
                else
                {
                    pose.leftPos.y -= dip;
                    pose.rightPos.y -= dip;
                }
                if (landSize > 0.4f)
                    walk *= 1f - e * landSize;
            }
            // A start: head down and away from the bang, shoulders up, hands in, and over at once.
            since = Time.time - flinchAt;
            if (since >= 0f && since < 0.55f && HandsFree)
            {
                float u = since / 0.55f;
                float e = (u < 0.14f ? u / 0.14f : 1f - Mathf.SmoothStep(0f, 1f, (u - 0.14f) / 0.86f)) * flinchSize * Tuning.F("flinch.size", 1f);
                pose.height -= 0.09f * e * m;
                pose.head = Quaternion.Euler(16f * e, -flinchSide * 22f * e, flinchSide * 6f * e) * pose.head;
                Vector3 inward = new Vector3(0f, 0.07f * e, 0.1f * e) * m;
                pose.leftPos += inward + new Vector3(0.05f * e * m, 0f, 0f);
                pose.rightPos += inward + new Vector3(-0.05f * e * m, 0f, 0f);
                curl = Mathf.Lerp(curl, 0.8f, e);
            }
        }
    }
}
