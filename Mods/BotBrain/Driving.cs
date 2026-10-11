using UnityEngine;
using Pose = ThirdEyeBonelabMod.Bots.Bot.Pose;

namespace ThirdEyeBonelabMod.Bots
{
    /// <summary>
    /// How a thing with a seat is driven, worked out from a lesson in which the player drove
    /// one (see Lessons): nothing about any one kind of vehicle is built in. From what the
    /// player did with their sticks, triggers and hands, set against what the thing then did:
    /// <list type="bullet">
    /// <item>what makes it go, and what slows it or takes it backwards (which stick or trigger,
    /// pushed which way), from how its speed changed as each was used;</item>
    /// <item>how it is steered: by a stick, or by the hands (a wheel, handlebars), and then
    /// where the hands are for each amount of turning, from where the player's were as it
    /// turned so;</item>
    /// <item>how fast it goes, how tightly it turns, and how fast the player took its turns.</item>
    /// </list>
    /// With that a bot drives it to wherever it wants to go (see BotDrive), not along the
    /// player's own route.
    /// </summary>
    public static class Driving
    {
        public enum Channel { LeftStickY, RightStickY, LeftTrigger, RightTrigger, LeftStickX, RightStickX }

        private const int Bins = 11;

        public class Model
        {
            public bool ok;
            public string why = "";
            public string barcode = "";
            /// <summary>When in the lesson they sat down, and the stretch of it in which they drove.</summary>
            public float seatAt, driveFrom, driveTo;
            /// <summary>The grabs of the vehicle that each hand had it by as they drove (null: that hand was off it).</summary>
            public Clone.Event leftGrab, rightGrab;
            /// <summary>Their sitting down in it.</summary>
            public Clone.Event sit;
            public Channel go, back, steer;
            public float goSign = 1f, backSign = 1f, goPull;
            public bool hasBack, stickSteer;
            /// <summary>Its turning (1 / the radius of the turn, right of straight on above 0) for a full push of its steering stick.</summary>
            public float steerGain;
            /// <summary>Its top speed, the tightest it turns to the left (below 0) and right, and how hard the player took its turns (sideways, m/s2).</summary>
            public float top, turnLeft, turnRight, side;
            /// <summary>Whether its seat's forward is the way it goes (1), or its back (-1).</summary>
            public float forward = 1f;
            /// <summary>Whether places are from its seat (a recording that kept the seat), or from the driver's hips (an older one).</summary>
            public bool inSeat;
            public Pose[] left = new Pose[Bins], right = new Pose[Bins];
            public Quaternion head = Quaternion.identity;
            public float fit;
            /// <summary>How much of the turning asked of it it really does (found as bots drive it).</summary>
            public float answers = 1f;
            /// <summary>The turning to put on for it to go straight (found as bots drive it).</summary>
            public float trim;
            /// <summary>The way the player drove it (where its seat went, every step and a half, and how fast there): to drive along as they did.</summary>
            public List<Vector3> road = new List<Vector3>();
            public List<float> roadSpeed = new List<float>();
            /// <summary>The way it was hardly shown turning ("left", "right"): made up from the other way, mirrored.</summary>
            public string mirrored = "";

            /// <summary>Where its hands are (from the seat) for that much turning.</summary>
            public void Hands(float turning, out Pose l, out Pose r)
            {
                // (Past the most the player was seen to turn it, a little way: the hands go on the way they were going.)
                float span = turnRight - turnLeft;
                float u = (Mathf.Clamp(turning, turnLeft - span * Tuning.F("drive.past", 0.25f), turnRight + span * Tuning.F("drive.past", 0.25f)) - turnLeft) / span * (Bins - 1);
                int i = Mathf.Clamp(Mathf.FloorToInt(u), 0, Bins - 2);
                float k = u - i;
                l = new Pose(Vector3.LerpUnclamped(left[i].position, left[i + 1].position, k), Quaternion.SlerpUnclamped(left[i].rotation, left[i + 1].rotation, k));
                r = new Pose(Vector3.LerpUnclamped(right[i].position, right[i + 1].position, k), Quaternion.SlerpUnclamped(right[i].rotation, right[i + 1].rotation, k));
            }

            public string Said()
            {
                if (!ok)
                    return "cannot be driven from this lesson: " + why;
                return "goes by " + Name(go, goSign) + " (" + goPull.ToString("0.0") + " m/s2)"
                       + (hasBack ? ", back by " + Name(back, backSign) : ", no going back shown")
                       + ", steered by " + (stickSteer ? Name(steer, 1f) : "its " + (leftGrab != null && rightGrab != null ? "two hands" : leftGrab != null ? "left hand" : "right hand") + " (fit " + fit.ToString("0.00") + ")")
                       + ", " + top.ToString("0.0") + " m/s at most, turns as tight as " + (1f / Mathf.Max(0.01f, Mathf.Max(-turnLeft, turnRight))).ToString("0.0") + " m"
                       + ", driven from " + driveFrom.ToString("0.0") + " to " + driveTo.ToString("0.0") + " s"
                       + (mirrored.Length > 0 ? ", turning " + mirrored + " hardly shown (made up from the other way)" : "")
                       + (road.Count > 1 ? ", its way kept (" + road.Count + " points)" : "")
                       + (inSeat ? "" : " (an older recording: hands placed roughly)");
            }
        }

        private static string Name(Channel c, float sign)
        {
            switch (c)
            {
                case Channel.LeftStickY: return "the left stick " + (sign > 0f ? "forward" : "back");
                case Channel.RightStickY: return "the right stick " + (sign > 0f ? "forward" : "back");
                case Channel.LeftTrigger: return "the left trigger";
                case Channel.RightTrigger: return "the right trigger";
                case Channel.LeftStickX: return "the left stick, side to side";
                default: return "the right stick, side to side";
            }
        }

        private static readonly Dictionary<string, Model> models = new Dictionary<string, Model>();

        public static void Forget() => models.Clear();

        /// <summary>How the player is driving right now, worked out afresh every few seconds as they drive with the bots learning from them (null when they are not).</summary>
        public static Model Live;

        /// <summary>From a recording still being made: how the thing is driven, by what there is of it so far.</summary>
        public static Model LearnNow(Clone.Take take)
        {
            Model m = new Model();
            try
            {
                Learn(take, m);
            }
            catch (Exception e)
            {
                m.ok = false;
                m.why = e.Message;
            }
            return m;
        }

        /// <summary>How the thing of a lesson is driven (worked out once).</summary>
        public static Model Of(Lessons.Lesson lesson)
        {
            if (lesson == null || lesson.take == null)
                return null;
            string key = lesson.file ?? lesson.name;
            if (!models.TryGetValue(key, out Model m))
            {
                m = new Model();
                try
                {
                    Learn(lesson.take, m);
                }
                catch (Exception e)
                {
                    m.ok = false;
                    m.why = e.Message;
                }
                models[key] = m;
                Lab.Event("driving: the " + lesson.name + " " + m.Said());
            }
            return m;
        }

        public static float Read(Clone.Frame f, Channel c)
        {
            switch (c)
            {
                case Channel.LeftStickY: return f.leftIn.stick.y;
                case Channel.RightStickY: return f.rightIn.stick.y;
                case Channel.LeftTrigger: return f.leftIn.trigger;
                case Channel.RightTrigger: return f.rightIn.trigger;
                case Channel.LeftStickX: return f.leftIn.stick.x;
                default: return f.rightIn.stick.x;
            }
        }

        // The frame things are placed from: the seat, or (an older recording) the hips, turned as they are turned about up.
        private static Pose From(Clone.Frame f, bool inSeat)
        {
            if (inSeat)
                return f.seat;
            Vector3 ahead = f.pelvisTurn * Vector3.forward;
            ahead.y = 0f;
            return new Pose(f.pelvis, ahead.sqrMagnitude > 0.001f ? Quaternion.LookRotation(ahead.normalized, Vector3.up) : Quaternion.identity);
        }

        // Where the play space was (an older recording does not say: the head is taken to be over the hips).
        private static Vector3 Root(Clone.Frame f, bool inSeat)
        {
            return inSeat ? f.root : f.pelvis + Vector3.up * 0.55f - f.playspace * f.head.position;
        }

        private static Pose World(Clone.Frame f, Pose local, bool inSeat) => new Pose(Root(f, inSeat) + f.playspace * local.position, f.playspace * local.rotation);

        private static void Learn(Clone.Take take, Model m)
        {
            List<Clone.Frame> f = take.frames;
            // When they sat down, and in what; when they got out.
            float seatAt = -1f, outAt = float.MaxValue;
            foreach (Clone.Event e in take.events)
            {
                if (e.kind != Clone.Kind.Seat)
                    continue;
                if (e.hand == 1 && seatAt < 0f)
                {
                    seatAt = e.t;
                    m.sit = e;
                    m.barcode = e.barcode;
                }
                else if (e.hand == 0 && seatAt >= 0f && outAt == float.MaxValue)
                {
                    outAt = e.t;
                }
            }
            if (seatAt < 0f)
            {
                m.why = "you did not sit in anything";
                return;
            }
            m.seatAt = seatAt;
            int first = 0;
            while (first < f.Count && f[first].t < seatAt + 0.4f)
                first++;
            int last = f.Count - 1;
            while (last > first && f[last].t > outAt - 0.1f)
                last--;
            int n = last - first + 1;
            if (n < 150)
            {
                m.why = "you were hardly in it";
                return;
            }
            m.inSeat = f[first].seated && f[last].seated;

            // Where it was and which way it faced, each frame; from that, how fast it went
            // (forward), how fast it turned, and how its speed was changing.
            Vector3[] at = new Vector3[n];
            Vector3[] ahead = new Vector3[n];
            float[] yaw = new float[n], speed = new float[n], spin = new float[n], gain = new float[n], bend = new float[n];
            bool[] moving = new bool[n];
            for (int i = 0; i < n; i++)
            {
                Pose p = From(f[first + i], m.inSeat);
                at[i] = p.position;
                Vector3 a = p.rotation * Vector3.forward;
                a.y = 0f;
                ahead[i] = a.sqrMagnitude > 0.001f ? a.normalized : Vector3.forward;
                yaw[i] = Mathf.Atan2(ahead[i].x, ahead[i].z) * Mathf.Rad2Deg;
            }
            const int w = 6;
            float sum = 0f;
            for (int i = w; i < n - w; i++)
            {
                float dt = f[first + i + w].t - f[first + i - w].t;
                if (dt < 0.01f)
                    continue;
                Vector3 v = (at[i + w] - at[i - w]) / dt;
                v.y = 0f;
                speed[i] = Vector3.Dot(v, ahead[i]);
                spin[i] = Mathf.DeltaAngle(yaw[i - w], yaw[i + w]) * Mathf.Deg2Rad / dt;
                sum += speed[i];
            }
            // (Its seat may face backwards in it: the way it mostly went is forward.)
            if (sum < 0f)
            {
                m.forward = -1f;
                for (int i = 0; i < n; i++)
                    speed[i] = -speed[i];
            }
            int moved = 0, from = -1, to = -1;
            const int w2 = 12;
            for (int i = w2 + w; i < n - w2 - w; i++)
            {
                float dt = f[first + i + w2].t - f[first + i - w2].t;
                gain[i] = dt > 0.01f ? (speed[i + w2] - speed[i - w2]) / dt : 0f;
                if (Mathf.Abs(speed[i]) > 0.7f)
                {
                    if (from < 0)
                        from = i;
                    to = i;
                }
                moving[i] = Mathf.Abs(speed[i]) > 1.2f;
                if (moving[i])
                {
                    moved++;
                    bend[i] = spin[i] / speed[i];
                }
            }
            if (moved < 90)
            {
                m.why = "you did not drive it anywhere (drive it about for a while: forward, turning both ways, stopping, backing)";
                return;
            }
            m.driveFrom = f[first + from].t;
            m.driveTo = f[first + to].t + 0.3f;
            if (m.inSeat)
            {
                for (int i = from; i <= to; i++)
                {
                    // (Only where they were going forward at some pace: not their shuffling about at the start and the end of it.)
                    if (speed[i] < 1.8f)
                        continue;
                    if (m.road.Count > 0 && (at[i] - m.road[m.road.Count - 1]).magnitude < 1.5f)
                        continue;
                    m.road.Add(at[i]);
                    m.roadSpeed.Add(Mathf.Abs(speed[i]));
                }
            }

            // ---- what makes it go: its change of speed, as made up of each stick and trigger (and its own slowing).
            Channel[] pushes = { Channel.LeftStickY, Channel.RightStickY, Channel.LeftTrigger, Channel.RightTrigger };
            const int k = 6;
            double[,] A = new double[k, k];
            double[] b = new double[k];
            double[] x = new double[k];
            int counted = 0;
            for (int i = w2 + w; i < n - w2 - w; i++)
            {
                Clone.Frame fr = f[first + i];
                for (int j = 0; j < 4; j++)
                    x[j] = Read(fr, pushes[j]);
                x[4] = speed[i];
                x[5] = 1.0;
                for (int r = 0; r < k; r++)
                {
                    b[r] += x[r] * gain[i];
                    for (int c = 0; c < k; c++)
                        A[r, c] += x[r] * x[c];
                }
                counted++;
            }
            for (int r = 0; r < k; r++)
                A[r, r] += 0.02 * counted;
            double[] pull = Solve(A, b, k);
            float best = 0.4f, worst = -0.4f;
            bool found = false;
            for (int j = 0; j < 4; j++)
            {
                List<float> up = new List<float>(), down = new List<float>();
                for (int i = 0; i < n; i++)
                {
                    float u = Read(f[first + i], pushes[j]);
                    up.Add(Mathf.Max(0f, u));
                    down.Add(Mathf.Max(0f, -u));
                }
                float most = Part(up, 0.97f), least = Part(down, 0.97f);
                for (int s = 0; s < 2; s++)
                {
                    float used = s == 0 ? most : least;
                    if (used < 0.3f)
                        continue;
                    float effect = (float)pull[j] * (s == 0 ? 1f : -1f);
                    if (effect * used > best)
                    {
                        best = effect * used;
                        m.go = pushes[j];
                        m.goSign = s == 0 ? 1f : -1f;
                        m.goPull = effect;
                        found = true;
                    }
                    if (effect * used < worst)
                    {
                        worst = effect * used;
                        m.back = pushes[j];
                        m.backSign = s == 0 ? 1f : -1f;
                        m.hasBack = true;
                    }
                }
            }
            if (!found)
            {
                m.why = "could not tell what makes it go";
                return;
            }

            // ---- how fast, and how hard through its turns.
            List<float> speeds = new List<float>(), sides = new List<float>(), bends = new List<float>();
            for (int i = 0; i < n; i++)
            {
                if (!moving[i])
                    continue;
                speeds.Add(speed[i]);
                sides.Add(Mathf.Abs(bend[i]) * speed[i] * speed[i]);
                bends.Add(bend[i]);
            }
            m.top = Mathf.Max(2f, Part(speeds, 0.95f));
            m.side = Mathf.Clamp(Part(sides, 0.9f), 2f, 9f);

            // ---- how it is steered. By a stick, if its turning goes with one.
            foreach (Channel c in new[] { Channel.LeftStickX, Channel.RightStickX })
            {
                List<float> us = new List<float>(), ks = new List<float>(), mags = new List<float>();
                for (int i = 0; i < n; i++)
                {
                    if (!moving[i])
                        continue;
                    us.Add(Read(f[first + i], c));
                    ks.Add(bend[i]);
                    mags.Add(Mathf.Abs(us[us.Count - 1]));
                }
                float goes = Together(us, ks);
                if (Mathf.Abs(goes) > 0.6f && Part(mags, 0.95f) > 0.3f && Mathf.Abs(goes) > m.fit)
                {
                    float uu = 0f, uk = 0f;
                    for (int i = 0; i < us.Count; i++)
                    {
                        uu += us[i] * us[i];
                        uk += us[i] * ks[i];
                    }
                    m.stickSteer = true;
                    m.steer = c;
                    m.steerGain = uk / Mathf.Max(0.001f, uu);
                    m.fit = Mathf.Abs(goes);
                }
            }

            // Which grabs of the vehicle each hand had it by, for most of the driving.
            m.leftGrab = HeldMost(take, 0, m.barcode, m.driveFrom, m.driveTo);
            m.rightGrab = HeldMost(take, 1, m.barcode, m.driveFrom, m.driveTo);

            // Where the hands and head were, from the seat, each frame.
            Pose[] lh = new Pose[n], rh = new Pose[n];
            Vector4 headSum = Vector4.zero;
            for (int i = 0; i < n; i++)
            {
                Clone.Frame fr = f[first + i];
                Pose inv = From(fr, m.inSeat).Inverse();
                lh[i] = inv * World(fr, fr.left, m.inSeat);
                rh[i] = inv * World(fr, fr.right, m.inSeat);
                Quaternion h = (inv * World(fr, fr.head, m.inSeat)).rotation;
                Add(ref headSum, h);
            }
            m.head = Mean(headSum);

            float[] turning = new float[n];
            if (m.stickSteer)
            {
                m.turnRight = Mathf.Abs(m.steerGain);
                m.turnLeft = -m.turnRight;
                for (int i = 0; i < n; i++)
                    turning[i] = m.steerGain * Read(f[first + i], m.steer);
            }
            else
            {
                if (m.leftGrab == null && m.rightGrab == null)
                {
                    m.why = "could not tell how you steer it (no stick turned it, and your hands were not on it)";
                    return;
                }
                // Its turning as made up of where the hands on it were.
                const int d = 6;
                double[] mean = new double[d];
                int count = 0;
                for (int i = 0; i < n; i++)
                {
                    if (!moving[i])
                        continue;
                    double[] hv = Feature(lh[i], rh[i], m);
                    for (int j = 0; j < d; j++)
                        mean[j] += hv[j];
                    count++;
                }
                for (int j = 0; j < d; j++)
                    mean[j] /= count;
                double[,] H = new double[d, d];
                double[] g = new double[d];
                double trace = 0.0;
                for (int i = 0; i < n; i++)
                {
                    if (!moving[i])
                        continue;
                    double[] hv = Feature(lh[i], rh[i], m);
                    for (int r = 0; r < d; r++)
                    {
                        g[r] += (hv[r] - mean[r]) * bend[i];
                        for (int c = 0; c < d; c++)
                            H[r, c] += (hv[r] - mean[r]) * (hv[c] - mean[c]);
                    }
                }
                for (int r = 0; r < d; r++)
                    trace += H[r, r];
                for (int r = 0; r < d; r++)
                    H[r, r] += 0.01 * trace / d + 1e-6;
                double[] weight = Solve(H, g, d);
                List<float> said = new List<float>(), was = new List<float>(), all = new List<float>();
                for (int i = 0; i < n; i++)
                {
                    double[] hv = Feature(lh[i], rh[i], m);
                    double s = 0.0;
                    for (int j = 0; j < d; j++)
                        s += weight[j] * (hv[j] - mean[j]);
                    turning[i] = (float)s;
                    if (f[first + i].t >= m.driveFrom && f[first + i].t <= m.driveTo)
                        all.Add(turning[i]);
                    if (moving[i])
                    {
                        said.Add(turning[i]);
                        was.Add(bend[i]);
                    }
                }
                m.fit = Together(said, was);
                if (m.fit < 0.45f)
                {
                    m.why = "could not tell how you steer it (drive it about longer, turning both ways)";
                    return;
                }
                m.turnLeft = Mathf.Min(-0.02f, Part(all, 0.03f));
                m.turnRight = Mathf.Max(0.02f, Part(all, 0.97f));
            }

            // The hands for each amount of turning: where the player's were, as it turned so.
            Vector3[] lp = new Vector3[Bins], rp = new Vector3[Bins];
            Vector4[] lq = new Vector4[Bins], rq = new Vector4[Bins];
            int[] have = new int[Bins];
            for (int i = 0; i < n; i++)
            {
                float t = f[first + i].t;
                if (t < m.driveFrom || t > m.driveTo)
                    continue;
                float u = Mathf.InverseLerp(m.turnLeft, m.turnRight, turning[i]) * (Bins - 1);
                int bin = Mathf.Clamp(Mathf.RoundToInt(u), 0, Bins - 1);
                lp[bin] += lh[i].position;
                rp[bin] += rh[i].position;
                Add(ref lq[bin], lh[i].rotation);
                Add(ref rq[bin], rh[i].rotation);
                have[bin]++;
            }
            int filled = 0;
            for (int bin = 0; bin < Bins; bin++)
            {
                if (have[bin] < 3)
                {
                    have[bin] = 0;
                    continue;
                }
                m.left[bin] = new Pose(lp[bin] / have[bin], Mean(lq[bin]));
                m.right[bin] = new Pose(rp[bin] / have[bin], Mean(rq[bin]));
                filled++;
            }
            if (filled == 0)
            {
                m.why = "nothing kept of where your hands were";
                return;
            }
            // (Amounts of turning never seen: between the nearest that were, or as the nearest.)
            for (int bin = 0; bin < Bins; bin++)
            {
                if (have[bin] > 0)
                    continue;
                int lo = bin, hi = bin;
                while (lo >= 0 && have[lo] == 0)
                    lo--;
                while (hi < Bins && have[hi] == 0)
                    hi++;
                if (lo >= 0 && hi < Bins)
                {
                    float u = (bin - lo) / (float)(hi - lo);
                    m.left[bin] = Pose.Lerp(m.left[lo], m.left[hi], u);
                    m.right[bin] = Pose.Lerp(m.right[lo], m.right[hi], u);
                }
                else
                {
                    int near = lo >= 0 ? lo : hi;
                    m.left[bin] = m.left[near];
                    m.right[bin] = m.right[near];
                }
            }
            // (Only as tight as hands were seen to turn it.)
            if (!m.stickSteer)
            {
                int lowest = 0, highest = Bins - 1;
                while (lowest < Bins - 1 && have[lowest] == 0)
                    lowest++;
                while (highest > 0 && have[highest] == 0)
                    highest--;
                float l = Mathf.Lerp(m.turnLeft, m.turnRight, lowest / (float)(Bins - 1));
                float r = Mathf.Lerp(m.turnLeft, m.turnRight, highest / (float)(Bins - 1));
                if (lowest > 0 || highest < Bins - 1)
                {
                    Pose[] nl = new Pose[Bins], nr = new Pose[Bins];
                    for (int bin = 0; bin < Bins; bin++)
                    {
                        float u = Mathf.Lerp(lowest, highest, bin / (float)(Bins - 1));
                        int i0 = Mathf.Clamp(Mathf.FloorToInt(u), 0, Bins - 2);
                        nl[bin] = Pose.Lerp(m.left[i0], m.left[i0 + 1], u - i0);
                        nr[bin] = Pose.Lerp(m.right[i0], m.right[i0 + 1], u - i0);
                    }
                    m.left = nl;
                    m.right = nr;
                    m.turnLeft = Mathf.Min(-0.02f, l);
                    m.turnRight = Mathf.Max(0.02f, r);
                }
            }
            // Shown turning much more one way than the other: the other way is the same thing
            // the other way round (the left hand where the right was, across the middle of the seat).
            float strong = Mathf.Max(-m.turnLeft, m.turnRight), weak = Mathf.Min(-m.turnLeft, m.turnRight);
            if (!m.stickSteer && weak < strong * 0.75f)
            {
                bool leftStrong = -m.turnLeft >= m.turnRight;
                Pose[] nl = new Pose[Bins], nr = new Pose[Bins];
                for (int bin = 0; bin < Bins; bin++)
                {
                    float turn = Mathf.Lerp(-strong, strong, bin / (float)(Bins - 1));
                    m.Hands(turn, out Pose l, out Pose r);
                    bool seen = leftStrong ? turn <= 0f : turn >= 0f;
                    if (!seen)
                    {
                        m.Hands(-turn, out Pose ol, out Pose or);
                        float by = Mathf.Clamp01(Mathf.Abs(turn) / Mathf.Max(0.02f, weak));
                        l = Pose.Lerp(l, Mirror(or), by);
                        r = Pose.Lerp(r, Mirror(ol), by);
                    }
                    nl[bin] = l;
                    nr[bin] = r;
                }
                m.left = nl;
                m.right = nr;
                m.turnLeft = -strong;
                m.turnRight = strong;
                m.mirrored = leftStrong ? "right" : "left";
            }
            m.ok = true;
        }

        private static Pose Mirror(Pose p) => new Pose(new Vector3(-p.position.x, p.position.y, p.position.z), new Quaternion(p.rotation.x, -p.rotation.y, -p.rotation.z, p.rotation.w));

        private static double[] Feature(Pose l, Pose r, Model m)
        {
            Vector3 a = m.leftGrab != null ? l.position : Vector3.zero;
            Vector3 b = m.rightGrab != null ? r.position : Vector3.zero;
            return new double[] { a.x, a.y, a.z, b.x, b.y, b.z };
        }

        // The grab of the vehicle a hand had it by for the longest of that stretch (if for a good part of it).
        private static Clone.Event HeldMost(Clone.Take take, int hand, string barcode, float from, float to)
        {
            Clone.Event holding = null, most = null;
            float since = 0f, longest = 0f;
            void Close(float at)
            {
                if (holding == null)
                    return;
                float span = Mathf.Min(at, to) - Mathf.Max(since, from);
                if (span > longest)
                {
                    longest = span;
                    most = holding;
                }
                holding = null;
            }
            foreach (Clone.Event e in take.events)
            {
                if (e.hand != hand)
                    continue;
                if (e.kind == Clone.Kind.Grab)
                {
                    Close(e.t);
                    if (e.barcode == barcode && barcode.Length > 0)
                    {
                        holding = e;
                        since = e.t;
                    }
                }
                else if (e.kind == Clone.Kind.Release)
                {
                    Close(e.t);
                }
            }
            Close(take.Length);
            return longest > (to - from) * 0.4f ? most : null;
        }

        // ---------------------------------------------------------------- sums

        private static void Add(ref Vector4 sum, Quaternion q)
        {
            Vector4 v = new Vector4(q.x, q.y, q.z, q.w);
            if (Vector4.Dot(sum, v) < 0f)
                v = -v;
            sum += v;
        }

        private static Quaternion Mean(Vector4 sum)
        {
            if (sum.sqrMagnitude < 1e-8f)
                return Quaternion.identity;
            sum.Normalize();
            return new Quaternion(sum.x, sum.y, sum.z, sum.w);
        }

        // The value that part of them are under.
        private static float Part(List<float> values, float part)
        {
            if (values.Count == 0)
                return 0f;
            List<float> sorted = new List<float>(values);
            sorted.Sort();
            return sorted[Mathf.Clamp(Mathf.RoundToInt(part * (sorted.Count - 1)), 0, sorted.Count - 1)];
        }

        // How far two things go together (-1 to 1).
        private static float Together(List<float> a, List<float> b)
        {
            int n = Mathf.Min(a.Count, b.Count);
            if (n < 10)
                return 0f;
            double ma = 0, mb = 0;
            for (int i = 0; i < n; i++)
            {
                ma += a[i];
                mb += b[i];
            }
            ma /= n;
            mb /= n;
            double ab = 0, aa = 0, bb = 0;
            for (int i = 0; i < n; i++)
            {
                ab += (a[i] - ma) * (b[i] - mb);
                aa += (a[i] - ma) * (a[i] - ma);
                bb += (b[i] - mb) * (b[i] - mb);
            }
            return aa < 1e-9 || bb < 1e-9 ? 0f : (float)(ab / Math.Sqrt(aa * bb));
        }

        private static double[] Solve(double[,] a, double[] b, int n)
        {
            double[,] m = (double[,])a.Clone();
            double[] v = (double[])b.Clone();
            for (int c = 0; c < n; c++)
            {
                int pivot = c;
                for (int r = c + 1; r < n; r++)
                {
                    if (Math.Abs(m[r, c]) > Math.Abs(m[pivot, c]))
                        pivot = r;
                }
                if (Math.Abs(m[pivot, c]) < 1e-12)
                    continue;
                if (pivot != c)
                {
                    for (int j = 0; j < n; j++)
                        (m[c, j], m[pivot, j]) = (m[pivot, j], m[c, j]);
                    (v[c], v[pivot]) = (v[pivot], v[c]);
                }
                for (int r = 0; r < n; r++)
                {
                    if (r == c)
                        continue;
                    double by = m[r, c] / m[c, c];
                    if (by == 0.0)
                        continue;
                    for (int j = c; j < n; j++)
                        m[r, j] -= by * m[c, j];
                    v[r] -= by * v[c];
                }
            }
            double[] x = new double[n];
            for (int i = 0; i < n; i++)
                x[i] = Math.Abs(m[i, i]) < 1e-12 ? 0.0 : v[i] / m[i, i];
            return x;
        }
    }
}
