using System.IO;
using MelonLoader;
using UnityEngine;

namespace ThirdEyeBonelabMod.Bots
{
    /// <summary>
    /// How the player moves, learned from their recordings: stretches of them standing about,
    /// walking and running (with nothing in their hands), to play on the bot's head and hands
    /// while it does the same, and how hard they push the stick to walk and to run (and whether
    /// they click it to run).
    /// </summary>
    public static class MotionLibrary
    {
        public enum Gait
        {
            Idle,
            Walk,
            Run,
        }

        private const float WalkFrom = 0.12f;
        private const float RunFrom = 0.8f;
        private const float ShortestClip = 1.2f;

        private static readonly List<Frame[]>[] clips = { new List<Frame[]>(), new List<Frame[]>(), new List<Frame[]>() };
        private static int filesSeen = -1;

        // Each clip's name (its recording and where in it) and how new that recording is (newer counts more).
        private static readonly Dictionary<Frame[], (string name, float fresh)> about = new Dictionary<Frame[], (string, float)>();

        /// <summary>Reads the recordings again (a part of one forgotten, say).</summary>
        public static void Reload()
        {
            filesSeen = -1;
            Refresh();
        }

        /// <summary>How many moves it has to play, of every kind.</summary>
        public static string Summary() => MoveSlots.Summary() + "; from watching: " + clips[0].Count + " standing, " + clips[1].Count + " walking, " + clips[2].Count + " running moves";

        /// <summary>How hard the player pushes the stick to walk, and to run (their median).</summary>
        public static float WalkPush { get; private set; } = 0.6f;
        public static float RunPush { get; private set; } = 1f;

        /// <summary>How far from enemies the player fights (their median, shooting with a gun): -1 if never recorded.</summary>
        public static float PlayerFightDistance { get; private set; } = -1f;

        /// <summary>How often the player glances about, standing (seconds between turns of the head).</summary>
        public static float GlanceEvery { get; private set; } = 2.5f;

        /// <summary>How long the player takes to get going (still to jogging), and to stop (seconds).</summary>
        public static float StartTime { get; private set; } = 0.5f;
        public static float StopTime { get; private set; } = 0.35f;

        /// <summary>Whether the player clicks the stick when they run.</summary>
        public static bool ClicksToRun { get; private set; }

        public static int Count(Gait gait) => clips[(int)gait].Count + (MoveSlots.For(gait, false, null, null) != null ? MoveSlots.Each : 0);

        public static Gait For(float push)
        {
            return push >= RunFrom ? Gait.Run : push >= WalkFrom ? Gait.Walk : Gait.Idle;
        }

        /// <summary>Reads the recordings again if there are new ones.</summary>
        public static void Refresh()
        {
            try
            {
                // (No recordings at all, after a fresh start: nothing learned from watching.)
                string[] files = Directory.Exists(Recorder.Folder) ? Directory.GetFiles(Recorder.Folder, "rec_*.csv") : new string[0];
                if (files.Length == filesSeen)
                    return;
                filesSeen = files.Length;
                foreach (List<Frame[]> list in clips)
                    list.Clear();
                about.Clear();
                tags.Clear();
                Array.Clear(tenseCount, 0, 3);
                rejected = 0;
                // (Oldest first: newer recordings are worth more, up to three times the oldest.)
                Array.Sort(files, StringComparer.Ordinal);
                int rank = 0;
                List<float> walks = new List<float>();
                List<float> runs = new List<float>();
                List<float> fights = new List<float>();
                int runFrames = 0, clicked = 0;
                foreach (string file in files)
                {
                    if (Recorder.Recording && file == Recorder.CurrentFile)
                        continue;
                    List<Frame> frames;
                    try
                    {
                        frames = Recorder.Load(file);
                    }
                    catch (Exception)
                    {
                        continue;
                    }
                    float fresh = files.Length > 1 ? 0.5f + 1f * rank / (files.Length - 1) : 1f;
                    rank++;
                    Cut(frames, Path.GetFileNameWithoutExtension(file), fresh);
                    for (int j = 0; j < frames.Count; j++)
                    {
                        Frame f = frames[j];
                        if (f.armed && f.enemyDistance > 0f && f.rightTrigger > 0.5f)
                            fights.Add(f.enemyDistance);
                        float push = f.stick.magnitude;
                        // (Walking is moving without sprinting, however hard the stick is pushed.)
                        float speed = Speed(frames, j);
                        Gait gait = push < WalkFrom ? Gait.Idle : speed >= SprintFrom ? Gait.Run : speed > 0.3f ? Gait.Walk : For(push);
                        if (gait == Gait.Walk)
                            walks.Add(push);
                        else if (gait == Gait.Run)
                        {
                            runs.Add(push);
                            runFrames++;
                            if (f.primaryStick || f.secondaryStick)
                                clicked++;
                        }
                    }
                }
                Habits(files);
                Rank();
                WalkPush = Median(walks, 0.6f);
                RunPush = Median(runs, 1f);
                ClicksToRun = runFrames > 30 && clicked > runFrames / 2;
                PlayerFightDistance = fights.Count >= 20 ? Mathf.Clamp(Median(fights, 9f, 25f), 3f, 25f) : -1f;
                MelonLogger.Msg("Bot Player learned from " + files.Length + " recordings: " + Count(Gait.Idle) + " standing, "
                    + Count(Gait.Walk) + " walking, " + Count(Gait.Run) + " running clips (walks at " + WalkPush.ToString("0.00")
                    + ", runs at " + RunPush.ToString("0.00") + (ClicksToRun ? ", clicking the stick" : "") + "); "
                    + rejected + " stretches left out for bad tracking; with enemies about: " + tenseCount[0] + " standing, "
                    + tenseCount[1] + " walking, " + tenseCount[2] + " running");
                MelonLogger.Msg("Bot Player, to teach next: " + TeachNext());
            }
            catch (Exception e)
            {
                MelonLogger.Warning("Bot Player could not read the recordings: " + e.Message);
            }
        }

        // Splits a recording into clips of one gait, hands empty, on the ground and upright.
        private static void Cut(List<Frame> frames, string file, float fresh)
        {
            // Each moment's kind, by how fast the player really went (their head over the ground,
            // over half a second): they push the stick all the way whether they jog or sprint,
            // so the stick alone filed nearly all their moving as running.
            int[] kinds = new int[frames.Count];
            for (int j = 0; j < frames.Count; j++)
                kinds[j] = Kind(frames[j], Speed(frames, j));
            int start = 0;
            for (int i = 1; i <= frames.Count; i++)
            {
                bool split = i == frames.Count || kinds[i] != kinds[start] || frames[i].time - frames[i - 1].time > 0.2f;
                if (!split)
                    continue;
                int kind = kinds[start];
                if (kind >= 0 && frames[i - 1].time - frames[start].time >= ShortestClip)
                {
                    // (A long stretch, a piece at a time: Piece seconds each, starting every Step.)
                    int from = start;
                    while (from < i)
                    {
                        int to = from;
                        while (to < i && frames[to].time - frames[from].time < Piece)
                            to++;
                        if (to < i && frames[i - 1].time - frames[to].time < ShortestClip)
                            to = i;
                        if (frames[to - 1].time - frames[from].time >= ShortestClip)
                            Add(kind, frames, from, to, file, fresh);
                        if (to >= i)
                            break;
                        int next = from;
                        while (next < i && frames[next].time - frames[from].time < Step)
                            next++;
                        from = next;
                    }
                }
                start = i;
            }
        }

        private const float Piece = 4f;
        private const float Step = 3f;

        // What each clip was recorded in the middle of, and how much the hands move in it.
        private static readonly Dictionary<Frame[], (bool tense, float lively)> tags = new Dictionary<Frame[], (bool, float)>();
        private static readonly int[] tenseCount = new int[3];
        private static int rejected;
        private static int glancesSeen;

        /// <summary>
        /// Whether a stretch is fit to learn from: no tracking gone wrong in it (a hand or the
        /// head jumping, a hand frozen where its controller lost tracking or was put down).
        /// </summary>
        private static bool Good(List<Frame> frames, int from, int to)
        {
            float leftStill = 0f, rightStill = 0f;
            for (int j = from + 1; j < to; j++)
            {
                float dt = frames[j].time - frames[j - 1].time;
                if (dt <= 0f)
                    continue;
                float left = (frames[j].leftPos - frames[j - 1].leftPos).magnitude;
                float right = (frames[j].rightPos - frames[j - 1].rightPos).magnitude;
                if (left / dt > 7f || right / dt > 7f || Mathf.Abs(frames[j].height - frames[j - 1].height) / dt > 4f)
                    return false;
                leftStill = left < 0.00015f ? leftStill + dt : 0f;
                rightStill = right < 0.00015f ? rightStill + dt : 0f;
                if (leftStill > 0.6f || rightStill > 0.6f)
                    return false;
            }
            return true;
        }

        private static void Add(int kind, List<Frame> frames, int from, int to, string file, float fresh)
        {
            if (!Good(frames, from, to))
            {
                rejected++;
                return;
            }
            Frame[] clip = frames.GetRange(from, to - from).ToArray();
            // Its tags: enemies about for most of it, and how far its hands travel a second.
            int tense = 0;
            float travel = 0f;
            for (int k = 0; k < clip.Length; k++)
            {
                if (clip[k].enemies > 0 && clip[k].enemyDistance > 0f && clip[k].enemyDistance < 20f)
                    tense++;
                if (k > 0)
                    travel += (clip[k].leftPos - clip[k - 1].leftPos).magnitude + (clip[k].rightPos - clip[k - 1].rightPos).magnitude;
            }
            float seconds = Mathf.Max(0.5f, clip[clip.Length - 1].time - clip[0].time);
            tags[clip] = (tense * 2 > clip.Length, travel / seconds);
            if (tense * 2 > clip.Length)
                tenseCount[kind]++;
            about[clip] = (file + ":" + from, fresh);
            float t0 = clip[0].time;
            for (int k = 0; k < clip.Length; k++)
                clip[k].time -= t0;
            clips[kind].Add(clip);
        }

        // -1: not to learn from (holding something, jumping, crouched down, turning on the spot).
        // How the player glances about standing, and gets going and stops (from all recordings).
        private static void Habits(string[] files)
        {
            float still = 0f;
            int glances = 0;
            List<float> starts = new List<float>();
            List<float> stops = new List<float>();
            foreach (string file in files)
            {
                List<Frame> frames;
                try
                {
                    frames = Recorder.Load(file);
                }
                catch (Exception)
                {
                    continue;
                }
                float lastStill = -1f, lastMoving = -1f;
                bool wasMoving = false;
                lastGlance = -10f;
                for (int j = 1; j < frames.Count; j++)
                {
                    float dt = frames[j].time - frames[j - 1].time;
                    if (dt <= 0f || dt > 0.2f)
                        continue;
                    float speed = Speed(frames, j);
                    // Standing: a turn of the head of 15 degrees or more within a quarter second is a glance.
                    if (speed < 0.3f)
                    {
                        still += dt;
                        // (The way their head faces in the world: its turn "from the body" is
                        // always nothing, the body being taken to face where the head does. And
                        // not a turn made with the stick.)
                        int k = j;
                        bool byStick = Mathf.Abs(frames[j].turnStick.x) > 0.3f;
                        while (k > 0 && frames[j].time - frames[k - 1].time <= 0.25f)
                        {
                            k--;
                            byStick |= Mathf.Abs(frames[k].turnStick.x) > 0.3f;
                        }
                        float turned = byStick ? 0f : Mathf.Abs(Mathf.DeltaAngle(frames[k].yaw, frames[j].yaw));
                        if (turned > 15f && frames[j].time - lastGlance > 0.5f)
                        {
                            glances++;
                            lastGlance = frames[j].time;
                        }
                    }
                    // Getting going: last still to first at jogging pace; stopping, the other way.
                    if (speed < 0.3f)
                    {
                        if (wasMoving && lastMoving >= 0f && frames[j].time - lastMoving < 2f)
                            stops.Add(frames[j].time - lastMoving);
                        lastStill = frames[j].time;
                        wasMoving = false;
                    }
                    else if (speed > 1.5f)
                    {
                        if (!wasMoving && lastStill >= 0f && frames[j].time - lastStill < 2f)
                            starts.Add(frames[j].time - lastStill);
                        lastMoving = frames[j].time;
                        wasMoving = true;
                    }
                }
            }
            glancesSeen = glances;
            GlanceEvery = glances >= 10 ? Mathf.Clamp(still / glances, 0.8f, 6f) : 2.5f;
            StartTime = starts.Count >= 5 ? Mathf.Clamp(MedianOf(starts), 0.15f, 1.5f) : 0.5f;
            StopTime = stops.Count >= 5 ? Mathf.Clamp(MedianOf(stops), 0.15f, 1.5f) : 0.35f;
            MelonLogger.Msg("Bot Player learned your habits: a glance about every " + GlanceEvery.ToString("0.0") + " s standing, "
                + StartTime.ToString("0.00") + " s to get going, " + StopTime.ToString("0.00") + " s to stop");
        }

        private static float lastGlance = -10f;

        private static float MedianOf(List<float> values)
        {
            values.Sort();
            return values[values.Count / 2];
        }

        /// <summary>Above this (metres a second) the player is sprinting: their sprint is about 4, their jog about 2.</summary>
        private const float SprintFrom = 2.9f;

        // How fast the player's head went over the ground about that moment (a quarter second either side).
        private static float Speed(List<Frame> frames, int j)
        {
            int a = j, b = j;
            while (a > 0 && frames[j].time - frames[a - 1].time <= 0.25f)
                a--;
            while (b < frames.Count - 1 && frames[b + 1].time - frames[j].time <= 0.25f)
                b++;
            float dt = frames[b].time - frames[a].time;
            if (dt < 0.05f)
                return 0f;
            Vector3 d = frames[b].headWorld - frames[a].headWorld;
            return new Vector2(d.x, d.z).magnitude / dt;
        }

        private static int Kind(Frame f, float speed)
        {
            if (!string.IsNullOrEmpty(f.leftHeld) || !string.IsNullOrEmpty(f.rightHeld) || f.jump || f.jumped)
                return -1;
            if (Mathf.Abs(f.turnStick.x) > 0.5f || f.crouch < -0.25f || f.height < 1.1f)
                return -1;
            // Not hands up over the head, or out at arm's length (reaching, pointing at a menu).
            if (f.leftPos.y > f.height || f.rightPos.y > f.height
                || new Vector2(f.leftPos.x, f.leftPos.z).magnitude > 0.65f || new Vector2(f.rightPos.x, f.rightPos.z).magnitude > 0.65f)
                return -1;
            if (f.stick.magnitude < WalkFrom && speed < 0.5f)
                return (int)Gait.Idle;
            // (Moving with no stick: carried along, or a recording without positions: by the stick.)
            if (speed < 0.3f)
                return (int)For(f.stick.magnitude);
            return speed >= SprintFrom ? (int)Gait.Run : (int)Gait.Walk;
        }

        /// <summary>
        /// Arms relaxed: the player mostly holds the controllers up in front of the chest, close
        /// in ("T-rex" arms), and the bot, copying them, did too. Hands up there are shifted down
        /// a way, back toward the body and a little out (by so much: 0 none, 1 all of it). A
        /// shift, the same from one moment to the next, not a pull to one spot: their swing and
        /// timing are all kept.
        /// </summary>
        public static Frame Relax(Frame f, float amount)
        {
            if (amount <= 0f)
                return f;
            f.leftPos = Relax(f.leftPos, f.height, -1f, amount);
            f.rightPos = Relax(f.rightPos, f.height, 1f, amount);
            return f;
        }

        private static Vector3 Relax(Vector3 hand, float height, float side, float amount)
        {
            float below = height - hand.y;
            float reach = new Vector2(hand.x, hand.z).magnitude;
            // How much it is the chest-high, close-in hold (fading out gently at its edges).
            float w = Mathf.Clamp01((below - 0.05f) / 0.2f) * Mathf.Clamp01((0.75f - below) / 0.2f) * Mathf.Clamp01((0.55f - reach) / 0.15f);
            w *= amount;
            if (w <= 0f)
                return hand;
            float drop = Tuning.F("relax.drop", 0.22f);
            return hand + new Vector3(side * 0.05f * w, -drop * w, -Mathf.Max(0f, hand.z - 0.06f) * 0.5f * w);
        }

        private static float Median(List<float> values, float otherwise, float most = 1f)
        {
            if (values.Count < 20)
                return otherwise;
            values.Sort();
            return Mathf.Clamp(values[values.Count / 2], 0.2f, most);
        }

        /// <summary>
        /// A clip of the gait (or, if none was recorded, of the nearest one), not one of those
        /// played lately if there is any other; null if none at all. Chosen by chance, but more
        /// often one the bot likes (its personality) and one from a newer recording.
        /// </summary>
        public static Frame[] Pick(Gait gait, List<Frame[]> lately, Personality who, bool tense = false, bool slow = false, string avatar = null)
        {
            // The player's own chosen moves (see MoveSlots), where there are any for this.
            Frame[] chosen = MoveSlots.For(gait, slow, lately, avatar);
            if (chosen != null)
                return chosen;
            pickTense = tense;
            // The gait asked for, else the nearest one recorded (a player who only ever runs
            // walks the way they run, not the way they stand).
            int[] order = gait == Gait.Idle ? new[] { 0, 1, 2 } : gait == Gait.Walk ? new[] { 1, 2, 0 } : new[] { 2, 1, 0 };
            foreach (int g in order)
            {
                List<Frame[]> list = clips[g];
                if (list.Count == 0)
                    continue;
                float total = 0f;
                foreach (Frame[] clip in list)
                {
                    if (!lately.Contains(clip))
                        total += Worth(clip, who);
                }
                if (total <= 0f)
                    return list[UnityEngine.Random.Range(0, list.Count)];
                float roll = UnityEngine.Random.value * total;
                foreach (Frame[] clip in list)
                {
                    if (lately.Contains(clip))
                        continue;
                    roll -= Worth(clip, who);
                    if (roll <= 0f)
                        return clip;
                }
                return list[list.Count - 1];
            }
            return null;
        }

        private static float Worth(Frame[] clip, Personality who)
        {
            if (!about.TryGetValue(clip, out (string name, float fresh) a))
                return 1f;
            float worth = a.fresh * (who != null ? who.Likes(a.name) : 1f);
            if (tags.TryGetValue(clip, out (bool tense, float lively) tag))
            {
                // A move from a time like this one (enemies about, or none) far sooner than one
                // that is not; and a restless bot the livelier moves, a laid-back one the quiet.
                worth *= tag.tense == pickTense ? Tuning.F("learn.context", 3f) : 1f / Tuning.F("learn.context", 3f);
                if (who != null)
                    worth *= Mathf.Lerp(1.7f, 0.5f, Mathf.Abs(tag.lively - Mathf.InverseLerp(0.4f, 1.6f, who.energy)));
            }
            return worth;
        }

        private static bool pickTense;

        // How lively each clip is among those of its gait (0 the quietest, 1 the liveliest).
        private static void Rank()
        {
            foreach (List<Frame[]> list in clips)
            {
                List<float> all = new List<float>();
                foreach (Frame[] clip in list)
                    all.Add(tags[clip].lively);
                all.Sort();
                foreach (Frame[] clip in list)
                {
                    (bool tense, float lively) tag = tags[clip];
                    tags[clip] = (tag.tense, all.Count > 1 ? all.BinarySearch(tag.lively) / (float)(all.Count - 1) : 0.5f);
                }
            }
        }

        /// <summary>
        /// What it has least of, of all it learns from the player: what to show it next (the
        /// two things it most lacks, in plain words).
        /// </summary>
        public static string TeachNext()
        {
            string empty = MoveSlots.EmptyKinds();
            if (empty.Length > 0)
                return "record these in Moves: " + empty;
            GunMoves.Lessons(out int guns, out int reloads, out int punches);
            List<(float have, string what)> lacks = new List<(float, string)>
            {
                (Count(Gait.Idle) / 40f, "stand about, hands empty, looking round (" + Count(Gait.Idle) + " standing moves)"),
                (Count(Gait.Walk) / 40f, "walk about without sprinting (" + Count(Gait.Walk) + " walking moves)"),
                (Count(Gait.Run) / 30f, "sprint: click the stick and run (" + Count(Gait.Run) + " running moves)"),
                (glancesSeen / 30f, "stand still and look about you (" + glancesSeen + " glances seen)"),
                (ClimbMoves.Count / 5f, "climb up onto ledges (" + ClimbMoves.Count + " climbs)"),
                (punches / 20f, "throw punches, hands empty (" + punches + " punches)"),
                (guns / 3f, "aim and fire different guns (" + guns + " guns known)"),
                (reloads / 6f, "reload by hand (" + reloads + " reloads)"),
                ((tenseCount[0] + tenseCount[1] + tenseCount[2]) / 15f, "move about, hands empty, with enemies near ("
                    + (tenseCount[0] + tenseCount[1] + tenseCount[2]) + " such moves)"),
            };
            lacks.Sort((a, b) => a.have.CompareTo(b.have));
            if (lacks[0].have >= 1f)
                return "nothing lacking: just keep playing with Learn from me on";
            return lacks[0].what + (lacks[1].have < 1f ? "; then " + lacks[1].what : "");
        }

        /// <summary>The clip's pose at time t (held at its ends: clips are played through once, never looped).</summary>
        public static Frame Sample(Frame[] clip, float t)
        {
            float length = clip[clip.Length - 1].time;
            if (length <= 0f)
                return clip[0];
            t = Mathf.Clamp(t, 0f, length);
            int lo = 0, hi = clip.Length - 1;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) / 2;
                if (clip[mid].time <= t)
                    lo = mid;
                else
                    hi = mid;
            }
            float k = Mathf.InverseLerp(clip[lo].time, clip[hi].time, t);
            return Bot.Blend(clip[lo], clip[hi], k);
        }

        /// <summary>
        /// The pose as if done the other way round: left hand doing what the right did, and the
        /// head turned the other way (a second clip out of every one recorded).
        /// </summary>
        public static Frame Mirror(Frame f)
        {
            Frame m = f;
            m.head = Flip(f.head);
            m.leftPos = Flip(f.rightPos);
            m.rightPos = Flip(f.leftPos);
            m.leftRot = Flip(f.rightRot);
            m.rightRot = Flip(f.leftRot);
            m.leftGrip = f.rightGrip;
            m.rightGrip = f.leftGrip;
            return m;
        }

        private static Vector3 Flip(Vector3 v) => new Vector3(-v.x, v.y, v.z);

        private static Quaternion Flip(Quaternion q) => new Quaternion(q.x, -q.y, -q.z, q.w);
    }
}
