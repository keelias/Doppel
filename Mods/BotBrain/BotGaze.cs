using BoneLib;
using Il2CppSLZ.Marrow;
using UnityEngine;

namespace ThirdEyeBonelabMod.Bots
{
    /// <summary>
    /// Where it looks, as a person does: at what is worth a look (the player, another bot,
    /// something moving or falling, a shot it heard, the way ahead as it walks, now and then
    /// just something about), for a moment each, a quick turn of the head to it and then still,
    /// never quite fixed. Its head turns first and its body follows when it has to turn far.
    /// How often it glances about is the player's own (MotionLibrary.GlanceEvery). While its
    /// hands are busy (aiming, reloading, holding) its head is theirs, not this.
    /// </summary>
    public partial class Bot
    {
        private Vector3 gazeAt;
        private Transform gazeFollow;
        private string gazeOn = "";
        private float gazeUntil;
        private float gazeYaw, gazePitch;
        private float gazeYawSpeed, gazePitchSpeed;
        private float gazeFarSince = -1f;
        private Vector3 heardAt;
        private float heardTime = -100f;

        /// <summary>Whether this gun is the one in its hands.</summary>
        public bool Holds(Il2CppSLZ.Marrow.Gun g) => gun != null && g != null && gun.Pointer == g.Pointer;

        /// <summary>A shot (or bang) heard somewhere: it looks that way at once, if it is not too far.</summary>
        public void Heard(Vector3 at)
        {
            Transform head = Head;
            if (head == null)
                return;
            float far = (at - head.position).magnitude;
            Startle(at, far);
            if (far > 45f || far < 1.5f)
                return;
            heardAt = at;
            heardTime = Time.time;
            gazeUntil = 0f;
        }

        /// <summary>Over its pose, each frame: its head where it is looking (when its hands leave it free).</summary>
        private void Gaze(Transform you, bool moving, float dt)
        {
            Transform head = Head;
            if (head == null || handOverride || Playing || climbing || brainTarget.transform != null)
                return;
            if (Time.time >= gazeUntil)
                ChooseGaze(you, head, moving);
            Vector3 at = gazeFollow != null ? gazeFollow.position : gazeAt;
            if (gazeOn == "ahead" && moving)
                at = nav.HasGoal ? nav.LookAhead(Feet, 4f) + Vector3.up * (head.position.y - Feet.y - 0.15f) : head.position + Quaternion.Euler(0f, bodyYaw, 0f) * Vector3.forward * 4f;

            Vector3 to = at - head.position;
            float flat = Flat(to).magnitude;
            float wantYaw = Mathf.Clamp(Mathf.DeltaAngle(bodyYaw, Recorder.Yaw(to)), -80f, 80f);
            float wantPitch = Mathf.Clamp(-Mathf.Atan2(to.y, Mathf.Max(flat, 0.1f)) * Mathf.Rad2Deg, -35f, 45f);
            // A quick turn of the head, then still (a stiff spring, well damped), and never quite fixed.
            float stiff = Tuning.F("gaze.quick", 9f);
            gazeYaw = Mathf.SmoothDamp(gazeYaw, wantYaw, ref gazeYawSpeed, 1f / stiff, 400f, dt);
            gazePitch = Mathf.SmoothDamp(gazePitch, wantPitch, ref gazePitchSpeed, 1f / stiff, 300f, dt);
            float drift = Tuning.F("gaze.drift", 1.5f);
            float yaw = gazeYaw + (Mathf.PerlinNoise(Time.time * 0.6f, born) - 0.5f) * 2f * drift;
            float pitch = gazePitch + (Mathf.PerlinNoise(born, Time.time * 0.5f) - 0.5f) * 2f * drift;

            // Its body comes round after its head when it has looked far to one side a while.
            if (!moving && Mathf.Abs(gazeYaw) > 55f)
            {
                if (gazeFarSince < 0f)
                    gazeFarSince = Time.time;
                if (Time.time - gazeFarSince > 0.6f)
                {
                    float turn = Mathf.Sign(gazeYaw) * Mathf.Min(Mathf.Abs(gazeYaw) - 20f, 90f * dt);
                    bodyYaw += turn;
                    gazeYaw -= turn;
                }
            }
            else
            {
                gazeFarSince = -1f;
            }
            // (A little of the player's own way of holding their head, from the move it plays.)
            pose.head = Quaternion.Euler(pitch, yaw, 0f) * Quaternion.Slerp(Quaternion.identity, pose.head, 0.25f);
        }

        // Something to look at next, by how much it draws the eye, and for how long.
        private void ChooseGaze(Transform you, Transform head, bool moving)
        {
            gazeFollow = null;
            float every = Mathf.Min(MotionLibrary.GlanceEvery, Tuning.F("gaze.every.most", 3.5f)) * Random(0.6f, 1.6f);
            // A shot heard just now: that way, first.
            if (Time.time - heardTime < 1.5f)
            {
                Look(heardAt, null, "a shot", Random(1.2f, 2.5f));
                heardTime = -100f;
                return;
            }
            List<(Vector3 at, Transform follow, string what, float weight)> options = new List<(Vector3, Transform, string, float)>();
            if (moving)
                options.Add((Vector3.zero, null, "ahead", 3f));
            if (you != null)
            {
                float d = (you.position - head.position).magnitude;
                if (d < 40f)
                    options.Add((you.position, you, "you", (d < 8f ? 2.2f : 1f) / (1f + d / 20f) * (moving ? 0.4f : 1f)));
            }
            foreach (Bot other in BotManager.All)
            {
                if (other == this || other.Head == null)
                    continue;
                float d = (other.Head.position - head.position).magnitude;
                if (d < 25f)
                    options.Add((other.Head.position, other.Head, "a bot", 0.7f / (1f + d / 10f)));
            }
            // Something moving: it catches the eye.
            foreach (Collider c in Physics.OverlapSphere(head.position, 14f, ~0, QueryTriggerInteraction.Ignore))
            {
                Rigidbody rb = c != null ? c.attachedRigidbody : null;
                if (rb == null || rb.isKinematic || rb.velocity.magnitude < 1.5f || rb.GetComponentInParent<RigManager>() != null)
                    continue;
                options.Add((rb.worldCenterOfMass, rb.transform, "something moving", 2f));
                break;
            }
            // Nothing in particular: somewhere about it.
            Vector3 about = head.position + Quaternion.Euler(Random(-8f, 18f), bodyYaw + Random(-110f, 110f), 0f) * Vector3.forward * Random(5f, 15f);
            options.Add((about, null, "about", moving ? 0.3f : 1.1f));

            float total = 0f;
            foreach (var o in options)
                total += o.weight;
            float roll = UnityEngine.Random.value * total;
            foreach (var o in options)
            {
                roll -= o.weight;
                if (roll > 0f)
                    continue;
                float dwell = o.what == "ahead" ? Random(0.8f, 2f) : o.what == "you" ? every * 1.4f : every;
                Look(o.at, o.follow, o.what, dwell);
                return;
            }
        }

        private void Look(Vector3 at, Transform follow, string what, float seconds)
        {
            gazeAt = at;
            gazeFollow = follow;
            gazeOn = what;
            gazeUntil = Time.time + Mathf.Max(0.4f, seconds);
        }
    }
}
