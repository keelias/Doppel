using UnityEngine;

namespace ThirdEyeBonelabMod.Bots
{
    /// <summary>
    /// The small things a person does standing about with nothing to do: looks at a hand and
    /// turns it over, scratches their head, rubs their neck, stretches, puts their hands on
    /// their hips, shakes their hands out, looks down at the ground, looks back over a
    /// shoulder. One now and then (each bot has those it does most, and none twice running),
    /// over the player's own standing moves, eased in and out, and dropped at once when there
    /// is something to do.
    /// </summary>
    public partial class Bot
    {
        private enum Fid { None, Hand, Scratch, Neck, Stretch, Hips, Shake, Down, Behind }

        private Fid fid = Fid.None, lastFid = Fid.None;
        private float fidStart, fidLong, fidNext = -1f, fidLevel;
        private float fidSide = 1f;

        /// <summary>What it is fidgeting at, in a few words ("" when it is not).</summary>
        private string FidgetLabel()
        {
            switch (fid)
            {
                case Fid.Hand: return "looking at its hand";
                case Fid.Scratch: return "scratching its head";
                case Fid.Neck: return "rubbing its neck";
                case Fid.Stretch: return "stretching";
                case Fid.Hips: return "hands on hips";
                case Fid.Shake: return "shaking its hands out";
                case Fid.Down: return "looking at the ground";
                case Fid.Behind: return "looking behind it";
                default: return "";
            }
        }

        // How much this bot does each (its own habits, the same every time it is met).
        private float FidgetTaste(Fid what)
        {
            float f = (personality.seed % 9973) * 0.6180339f * ((int)what + 1);
            return 0.35f + 1.3f * (f - Mathf.Floor(f));
        }

        // Over its pose, each frame, before where it looks.
        private void Fidget(Transform you, bool moving, float dt)
        {
            bool can = !moving && HandsFree && Time.time >= waveUntil && waveDue < 0f && !falling
                       && nearestSeen.transform == null && brainTarget.transform == null
                       && Time.time - flinchAt > 0.6f && Time.time - heardTime > 1.5f && Time.time - landAt > 1.2f
                       && (mode == Mode.Stand || mode == Mode.Follow || mode == Mode.Wander || mode == Mode.Brain);
            if (fid == Fid.None)
            {
                if (!can || Tuning.F("fidget.every", 0f) <= 0f)
                {
                    fidNext = -1f;
                    return;
                }
                if (fidNext < 0f)
                    fidNext = Time.time + Tuning.F("fidget.every", 0f) * Random(0.5f, 1.6f) * Mathf.Lerp(0.8f, 1.3f, Mathf.InverseLerp(0.5f, 1.8f, personality.calm));
                if (Time.time < fidNext)
                    return;
                BeginFidget();
            }
            float t = Time.time - fidStart;
            bool on = can && t < fidLong;
            fidLevel = Mathf.MoveTowards(fidLevel, on ? 1f : 0f, dt / (on ? 0.45f : can ? 0.4f : 0.2f));
            if (!on && fidLevel <= 0f)
            {
                fid = Fid.None;
                fidNext = -1f;
                return;
            }
            float e = Mathf.SmoothStep(0f, 1f, fidLevel);
            float h = pose.height;
            float s = fidSide;
            // (Sized to its body: the places below are a 1.7 m body's.)
            float m = h / 1.7f;
            Vector3 P(float x, float fromHead, float z) => new Vector3(x * m, h + fromHead * m, z * m);
            Transform root = controls.vrRoot;
            switch (fid)
            {
                case Fid.Hand:
                {
                    // A hand up in front of it, turned over and back, its eyes on it.
                    Vector3 at = P(s * 0.13f, -0.36f, 0.3f);
                    Quaternion turn = Quaternion.Euler(-35f, -s * 15f, s * (-70f + Mathf.Sin(t * 1.9f) * 75f));
                    OneHand(s, at, turn, e);
                    if (root != null && fidLevel > 0.5f)
                        Look(root.TransformPoint(at), null, "its hand", 0.3f);
                    curl = Mathf.Lerp(curl, 0.12f, e);
                    break;
                }
                case Fid.Scratch:
                {
                    Vector3 at = P(s * 0.12f + Mathf.Sin(t * 31f) * 0.012f, 0.05f + Mathf.Cos(t * 27f) * 0.012f, -0.05f);
                    OneHand(s, at, Quaternion.Euler(-105f, -s * 40f, 0f), e);
                    pose.head = Quaternion.Euler(9f * e, 0f, -s * 6f * e) * pose.head;
                    curl = Mathf.Lerp(curl, 0.55f, e);
                    break;
                }
                case Fid.Neck:
                {
                    Vector3 at = P(s * 0.09f + Mathf.Sin(t * 8f) * 0.02f, -0.1f, -0.1f);
                    OneHand(s, at, Quaternion.Euler(-125f, -s * 35f, 0f), e);
                    pose.head = Quaternion.Euler(-8f * e, s * 10f * Mathf.Sin(t * 1.6f) * e, s * 8f * e) * pose.head;
                    curl = Mathf.Lerp(curl, 0.45f, e);
                    break;
                }
                case Fid.Stretch:
                {
                    // Both arms up over its head and out, back arched a little, then down.
                    float wide = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(fidLong * 0.45f, fidLong * 0.9f, t));
                    Vector3 up = P(0.24f + 0.3f * wide, 0.4f - 0.3f * wide, 0.02f - 0.1f * wide);
                    pose.rightPos = Vector3.Lerp(pose.rightPos, up, e);
                    pose.leftPos = Vector3.Lerp(pose.leftPos, new Vector3(-up.x, up.y, up.z), e);
                    pose.rightRot = Quaternion.Slerp(pose.rightRot, Quaternion.Euler(-85f + 50f * wide, 30f * wide, 0f), e);
                    pose.leftRot = Quaternion.Slerp(pose.leftRot, Quaternion.Euler(-85f + 50f * wide, -30f * wide, 0f), e);
                    pose.head = Quaternion.Euler(-14f * e * (1f - wide), 0f, 0f) * pose.head;
                    curl = Mathf.Lerp(curl, 0.05f, e);
                    break;
                }
                case Fid.Hips:
                {
                    pose.rightPos = Vector3.Lerp(pose.rightPos, new Vector3(0.23f * m, h * 0.61f, -0.01f), e);
                    pose.leftPos = Vector3.Lerp(pose.leftPos, new Vector3(-0.23f * m, h * 0.61f, -0.01f), e);
                    pose.rightRot = Quaternion.Slerp(pose.rightRot, Quaternion.Euler(50f, -75f, 0f), e);
                    pose.leftRot = Quaternion.Slerp(pose.leftRot, Quaternion.Euler(50f, 75f, 0f), e);
                    curl = Mathf.Lerp(curl, 0.5f, e);
                    break;
                }
                case Fid.Shake:
                {
                    Vector3 shake = new Vector3(Mathf.Sin(t * 44f) * 0.03f, 0.1f + Mathf.Cos(t * 39f) * 0.03f, 0.08f) * (e * m);
                    pose.rightPos += shake;
                    pose.leftPos += new Vector3(-shake.x, shake.y, shake.z);
                    curl = Mathf.Lerp(curl, 0.05f, e);
                    break;
                }
                case Fid.Down:
                    if (root != null && fidLevel > 0.3f && on)
                        Look(root.TransformPoint(new Vector3(s * 0.2f * m, 0f, 1.1f * m)), null, "the ground", 0.3f);
                    break;
                case Fid.Behind:
                    if (root != null && on)
                        Look(root.TransformPoint(new Vector3(s * 5f, h, -3f)), null, "behind it", 0.3f);
                    break;
            }
        }

        // One hand's part of a fidget (the other goes on as it was).
        private void OneHand(float side, Vector3 at, Quaternion turn, float e)
        {
            if (side > 0f)
            {
                pose.rightPos = Vector3.Lerp(pose.rightPos, at, e);
                pose.rightRot = Quaternion.Slerp(pose.rightRot, turn, e);
            }
            else
            {
                pose.leftPos = Vector3.Lerp(pose.leftPos, at, e);
                pose.leftRot = Quaternion.Slerp(pose.leftRot, turn, e);
            }
        }

        private void BeginFidget()
        {
            (Fid what, float weight, float least, float most)[] all =
            {
                (Fid.Hand, 1f, 2.4f, 4.2f),
                (Fid.Scratch, 1f, 1.4f, 2.4f),
                (Fid.Neck, 0.8f, 1.8f, 3f),
                (Fid.Stretch, 0.6f, 2.6f, 3.4f),
                (Fid.Hips, 1f, 3f, 7f),
                (Fid.Shake, 0.6f, 0.9f, 1.4f),
                (Fid.Down, 1f, 1.4f, 2.6f),
                (Fid.Behind, 0.9f, 1.3f, 2f),
            };
            float total = 0f;
            foreach ((Fid what, float weight, float least, float most) in all)
            {
                if (what != lastFid)
                    total += weight * FidgetTaste(what);
            }
            float roll = UnityEngine.Random.value * total;
            foreach ((Fid what, float weight, float least, float most) in all)
            {
                if (what == lastFid)
                    continue;
                roll -= weight * FidgetTaste(what);
                if (roll > 0f)
                    continue;
                fid = lastFid = what;
                fidLong = Random(least, most);
                break;
            }
            if (fid == Fid.None)
                return;
            fidStart = Time.time;
            fidLevel = 0f;
            // (Mostly with the hand it favours.)
            bool lefty = personality.mirror > 0.5f;
            fidSide = (UnityEngine.Random.value < 0.75f) == lefty ? -1f : 1f;
            Lab.Event("fidget: " + FidgetLabel());
        }
    }
}
