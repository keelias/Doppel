using System.Globalization;
using System.IO;
using System.Text;
using BoneLib;
using Il2CppSLZ.Marrow;
using MelonLoader;
using UnityEngine;

namespace ThirdEyeBonelabMod.Bots
{
    /// <summary>
    /// One moment of a player's body, as the bot needs it: where the head and hands are and what
    /// the hands are doing, all in the body's own frame (so it can be put on any body, facing any
    /// way), plus where that body was in the world and what it held.
    /// </summary>
    public struct Frame
    {
        public float time;
        /// <summary>Which way the body faces in the world (degrees about up).</summary>
        public float yaw;
        public Vector3 headWorld;
        /// <summary>The head's height above the floor of the play space.</summary>
        public float height;
        /// <summary>The head's turn, relative to the way the body faces.</summary>
        public Quaternion head;
        /// <summary>The hands, from the point under the head, in the body's frame (y is height above the floor).</summary>
        public Vector3 leftPos, rightPos;
        public Quaternion leftRot, rightRot;
        public Vector2 stick, turnStick;
        public float leftGrip, rightGrip, leftTrigger, rightTrigger;
        public bool leftA, leftB, rightA, rightB;
        /// <summary>The body's two "A" buttons as the game reads them (jumping is one of them).</summary>
        public bool jump, otherA;
        /// <summary>How far the body crouches (legs bent, stick or real), and its feet's offset.</summary>
        public float crouch, feet;
        /// <summary>Whether it jumped (left the ground in a jump) since the frame before.</summary>
        public bool jumped;
        /// <summary>The two sticks clicked in (as the game reads them; running may be one).</summary>
        public bool primaryStick, secondaryStick;
        /// <summary>What was going on about them: enemies about, the nearest one's distance, and whether they held a gun.</summary>
        public int enemies;
        public float enemyDistance;
        public bool armed;
        public string leftHeld, rightHeld;
    }

    /// <summary>
    /// Records how the player plays, to files the bot learns from: about thirty times a second,
    /// their head and hands, sticks, triggers and grips, and what is in each hand.
    /// Kept in UserData/Bot Player/recordings, one file each time recording is switched on.
    /// </summary>
    public static class Recorder
    {
        private const float Interval = 1f / 30f;
        private const string Header = "time,yaw,hx,hy,hz,height,hqx,hqy,hqz,hqw,lpx,lpy,lpz,lqx,lqy,lqz,lqw,rpx,rpy,rpz,rqx,rqy,rqz,rqw,sx,sy,tx,ty,lgrip,rgrip,ltrig,rtrig,la,lb,ra,rb,lheld,rheld,jump,pa,crouch,feet,jumped,pstick,sstick,enemies,edist,armed";

        private static StreamWriter writer;
        private static string path;
        private static float started;
        private static float next;
        private static float flushed;
        private static int frames;

        public static bool Recording => writer != null;

        /// <summary>When the player last jumped (Time.time), seen by their body starting a jump.</summary>
        public static float PlayerJumpedAt = -10f;
        private static bool wasJumping;
        private static float recordedJumpAt = -10f;

        /// <summary>Every frame: notes the player's jumps as they happen (a recording is sparser).</summary>
        public static void Watch()
        {
            RemapRig remap = Player.RemapRig;
            bool jumping = remap != null && remap._jumping;
            if (jumping && !wasJumping)
                PlayerJumpedAt = Time.time;
            wasJumping = jumping;
        }

        public static string Folder => AssetFiles.SaveFolder("recordings");

        /// <summary>The file being recorded to (null when not recording).</summary>
        public static string CurrentFile => writer != null ? path : null;

        public static void Set(bool on)
        {
            if (on)
                Start();
            else
                Stop();
        }

        private static void Start()
        {
            if (writer != null)
                return;
            try
            {
                Directory.CreateDirectory(Folder);
                path = Path.Combine(Folder, "rec_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".csv");
                writer = new StreamWriter(path, false, new UTF8Encoding(false));
                writer.WriteLine(Header);
                started = Time.time;
                next = 0f;
                recordedJumpAt = PlayerJumpedAt;
                flushed = Time.time;
                frames = 0;
                Spawnables.Notify("Recording your play");
            }
            catch (Exception e)
            {
                writer = null;
                MelonLogger.Error("Recording could not start: " + e.Message);
            }
        }

        public static void Stop()
        {
            if (writer == null)
                return;
            try
            {
                writer.Dispose();
            }
            catch (Exception e)
            {
                MelonLogger.Warning("Recording could not be closed: " + e.Message);
            }
            writer = null;
            Spawnables.Notify("Recording saved: " + (Time.time - started).ToString("0", CultureInfo.InvariantCulture) + " seconds");
        }

        public static void Update()
        {
            if (writer == null)
                return;
            float now = Time.time - started;
            if (now < next)
                return;
            next = now + Interval;
            if (!Capture(out Frame frame))
                return;
            frame.time = now;
            Context(ref frame);
            frame.jumped = PlayerJumpedAt > recordedJumpAt;
            if (frame.jumped)
                recordedJumpAt = PlayerJumpedAt;
            frame.leftHeld = Held(Player.LeftHand);
            frame.rightHeld = Held(Player.RightHand);
            try
            {
                writer.WriteLine(Write(frame));
                frames++;
                if (Time.time - flushed > 2f)
                {
                    writer.Flush();
                    flushed = Time.time;
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error("Recording stopped: " + e.Message);
                writer = null;
            }
        }

        /// <summary>The player as they are right now (without the time or what they hold).</summary>
        public static bool Capture(out Frame frame)
        {
            frame = default;
            OpenControllerRig controls = Player.ControllerRig;
            if (controls == null || controls.vrRoot == null || controls.headset == null || controls.leftController == null || controls.rightController == null)
                return false;
            Transform root = controls.vrRoot;
            Transform headset = controls.headset;
            Quaternion rootRot = root.rotation;
            Quaternion toRoot = Quaternion.Inverse(rootRot);

            Vector3 head = root.InverseTransformPoint(headset.position);
            Quaternion headRot = toRoot * headset.rotation;
            float facing = Yaw(headRot * Vector3.forward);
            Quaternion body = Quaternion.Euler(0f, facing, 0f);
            Quaternion fromBody = Quaternion.Inverse(body);
            Vector3 under = new Vector3(head.x, 0f, head.z);

            Transform left = controls.leftController.transform;
            Transform right = controls.rightController.transform;
            frame.yaw = Yaw(rootRot * body * Vector3.forward);
            frame.headWorld = headset.position;
            frame.height = head.y;
            frame.head = fromBody * headRot;
            frame.leftPos = fromBody * (root.InverseTransformPoint(left.position) - under);
            frame.rightPos = fromBody * (root.InverseTransformPoint(right.position) - under);
            frame.leftRot = fromBody * (toRoot * left.rotation);
            frame.rightRot = fromBody * (toRoot * right.rotation);

            BaseController l = controls.leftController;
            BaseController r = controls.rightController;
            frame.stick = l.GetThumbStickAxis();
            frame.turnStick = r.GetThumbStickAxis();
            frame.leftGrip = l._solvedGrip;
            frame.rightGrip = r._solvedGrip;
            frame.leftTrigger = l._primaryAxis;
            frame.rightTrigger = r._primaryAxis;
            frame.leftA = l._aButton;
            frame.leftB = l._bButton;
            frame.rightA = r._aButton;
            frame.rightB = r._bButton;
            frame.jump = controls._secondaryAButton;
            frame.otherA = controls._primaryAButton;
            frame.primaryStick = controls._primaryStick;
            frame.secondaryStick = controls._secondaryStick;
            RemapRig remap = Player.RemapRig;
            if (remap != null)
            {
                frame.crouch = remap._crouchTarget;
                frame.feet = remap._feetOffset;
            }
            return true;
        }

        // The enemies about the player and whether they hold a gun (to learn how they fight).
        private static void Context(ref Frame frame)
        {
            frame.enemyDistance = -1f;
            Transform head = Player.Head;
            if (head == null)
                return;
            float nearest = float.MaxValue;
            foreach (Targets.Target t in Targets.Hostiles())
            {
                frame.enemies++;
                nearest = Mathf.Min(nearest, (t.Point - head.position).magnitude);
            }
            if (frame.enemies > 0)
                frame.enemyDistance = nearest;
            frame.armed = Player.GetComponentInHand<Gun>(Player.RightHand) != null || Player.GetComponentInHand<Gun>(Player.LeftHand) != null;
        }

        public static float Yaw(Vector3 forward)
        {
            forward.y = 0f;
            return forward.sqrMagnitude < 1e-6f ? 0f : Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
        }

        private static string Held(Hand hand)
        {
            GameObject held = hand != null ? Player.GetObjectInHand(hand) : null;
            if (held == null)
                return "";
            return held.name.Replace(",", " ").Replace("\n", " ");
        }

        internal static string Write(Frame f)
        {
            StringBuilder s = new StringBuilder(320);
            Add(s, f.time); Add(s, f.yaw);
            Add(s, f.headWorld.x); Add(s, f.headWorld.y); Add(s, f.headWorld.z);
            Add(s, f.height);
            Add(s, f.head);
            Add(s, f.leftPos); Add(s, f.leftRot);
            Add(s, f.rightPos); Add(s, f.rightRot);
            Add(s, f.stick.x); Add(s, f.stick.y); Add(s, f.turnStick.x); Add(s, f.turnStick.y);
            Add(s, f.leftGrip); Add(s, f.rightGrip); Add(s, f.leftTrigger); Add(s, f.rightTrigger);
            Add(s, f.leftA); Add(s, f.leftB); Add(s, f.rightA); Add(s, f.rightB);
            s.Append(f.leftHeld).Append(',').Append(f.rightHeld).Append(',');
            Add(s, f.jump); Add(s, f.otherA); Add(s, f.crouch); Add(s, f.feet);
            Add(s, f.jumped);
            Add(s, f.primaryStick);
            Add(s, f.secondaryStick);
            s.Append(f.enemies).Append(',');
            Add(s, f.enemyDistance);
            s.Append(f.armed ? '1' : '0');
            return s.ToString();
        }

        private static void Add(StringBuilder s, float v) => s.Append(v.ToString("0.####", CultureInfo.InvariantCulture)).Append(',');
        private static void Add(StringBuilder s, bool v) => s.Append(v ? '1' : '0').Append(',');
        private static void Add(StringBuilder s, Vector3 v) { Add(s, v.x); Add(s, v.y); Add(s, v.z); }
        private static void Add(StringBuilder s, Quaternion q) { Add(s, q.x); Add(s, q.y); Add(s, q.z); Add(s, q.w); }

        /// <summary>The newest recording, or null if there is none.</summary>
        public static string Latest()
        {
            if (!Directory.Exists(Folder))
                return null;
            string newest = null;
            DateTime when = DateTime.MinValue;
            foreach (string file in Directory.GetFiles(Folder, "rec_*.csv"))
            {
                if (file == path && writer != null)
                    continue;
                DateTime t = File.GetLastWriteTime(file);
                if (t > when)
                {
                    when = t;
                    newest = file;
                }
            }
            return newest;
        }

        /// <summary>
        /// Forgets the last stretch of the recording being made (or, if none is, of the newest):
        /// kept beside it (its name and ".forget": from and to, a line each), and left out when
        /// it is read. False if there is no recording.
        /// </summary>
        public static bool ForgetLast(float seconds)
        {
            try
            {
                string file = writer != null ? path : Latest();
                if (file == null)
                    return false;
                float end;
                if (writer != null)
                {
                    end = Time.time - started;
                }
                else
                {
                    end = 0f;
                    foreach (string line in File.ReadLines(file))
                    {
                        int comma = line.IndexOf(',');
                        if (comma > 0 && float.TryParse(line.Substring(0, comma), NumberStyles.Float, CultureInfo.InvariantCulture, out float t))
                            end = t;
                    }
                }
                File.AppendAllText(file + ".forget", (end - seconds).ToString(CultureInfo.InvariantCulture) + "," + (end + 1f).ToString(CultureInfo.InvariantCulture) + "\n");
                return true;
            }
            catch (Exception e)
            {
                MelonLogger.Warning("Bot Player: could not forget the last part: " + e.Message);
                return false;
            }
        }

        // The stretches of a recording that were forgotten.
        private static List<(float from, float to)> Forgotten(string file)
        {
            List<(float, float)> gone = new List<(float, float)>();
            string side = file + ".forget";
            if (!File.Exists(side))
                return gone;
            foreach (string line in File.ReadAllLines(side))
            {
                string[] p = line.Split(',');
                if (p.Length == 2 && float.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float a)
                    && float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float b))
                    gone.Add((a, b));
            }
            return gone;
        }

        public static List<Frame> Load(string file)
        {
            List<Frame> frames = new List<Frame>();
            List<(float from, float to)> gone = Forgotten(file);
            foreach (string line in File.ReadLines(file))
            {
                if (line.StartsWith("time", StringComparison.Ordinal))
                    continue;
                string[] c = line.Split(',');
                if (c.Length < 38)
                    continue;
                int i = 0;
                float F() => float.Parse(c[i++], CultureInfo.InvariantCulture);
                bool B() => c[i++] == "1";
                Frame f = new Frame();
                f.time = F(); f.yaw = F();
                f.headWorld = new Vector3(F(), F(), F());
                f.height = F();
                f.head = new Quaternion(F(), F(), F(), F());
                f.leftPos = new Vector3(F(), F(), F());
                f.leftRot = new Quaternion(F(), F(), F(), F());
                f.rightPos = new Vector3(F(), F(), F());
                f.rightRot = new Quaternion(F(), F(), F(), F());
                f.stick = new Vector2(F(), F());
                f.turnStick = new Vector2(F(), F());
                f.leftGrip = F(); f.rightGrip = F(); f.leftTrigger = F(); f.rightTrigger = F();
                f.leftA = B(); f.leftB = B(); f.rightA = B(); f.rightB = B();
                f.leftHeld = c[i++];
                f.rightHeld = c[i++];
                // (Older recordings stop here: what they lack is left as standing, with the right A button for jumping.)
                f.jump = f.rightA;
                if (c.Length >= 43)
                {
                    f.jump = B(); f.otherA = B(); f.crouch = F(); f.feet = F(); f.jumped = B();
                }
                if (c.Length >= 45)
                {
                    f.primaryStick = B(); f.secondaryStick = B();
                }
                if (c.Length >= 48)
                {
                    f.enemies = (int)F(); f.enemyDistance = F(); f.armed = B();
                }
                else
                {
                    f.enemyDistance = -1f;
                }
                bool forgotten = false;
                foreach ((float from, float to) in gone)
                    forgotten |= f.time >= from && f.time <= to;
                if (!forgotten)
                    frames.Add(f);
            }
            return frames;
        }
    }
}
