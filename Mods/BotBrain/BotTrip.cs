using BoneLib;
using Il2CppSLZ.Marrow;
using UnityEngine;

namespace ThirdEyeBonelabMod.Bots
{
    /// <summary>
    /// A bot knocked off its feet, and getting up again, as the game's own creatures do. What
    /// takes it down: something heavy or fast hitting it (a thrown thing, a hard blow, a
    /// kick at its legs), a hit that takes a good part of its health (or one in the leg), the
    /// player taking hold of it and pulling it over, a landing from too high. Down, its body is
    /// limp (the game's own ragdoll) and it does nothing; after a moment (longer the harder it
    /// went down) it gets up: onto its hands, up through a crouch, and on with what it was at.
    /// </summary>
    public partial class Bot
    {
        private bool down;
        private float downAt, downFor;
        private bool rising;
        private float riseAt;
        private float upAt = -10f;
        private float bumpSaidAt = -10f;

        /// <summary>Whether it is on the ground (limp, or getting up).</summary>
        public bool Down => down || rising;

        /// <summary>Knocked down: how hard (0 to 1), and which way it is sent.</summary>
        public void Trip(float hard, Vector3 push, string why)
        {
            if (dead || down || rising || climbing || Time.time - upAt < Tuning.F("trip.rest", 1.5f) || Tuning.F("trip.on", 1f) <= 0f)
                return;
            hard = Mathf.Clamp01(hard);
            try
            {
                if (InLesson)
                    EndLesson("broken off: knocked down");
                else if (mode == Mode.Clone)
                    SetMode(Mode.Stand);
                portalGoing = false;
                Pull(false);
                walk = Vector2.zero;
                stick = Vector2.zero;
                rig.physicsRig.RagdollRig();
                Rigidbody pelvis = Pelvis();
                if (pelvis != null && push.sqrMagnitude > 0.0001f)
                    pelvis.AddForce(Flat(push).normalized * Mathf.Lerp(0.6f, 2.2f, hard) * Tuning.F("trip.shove", 1f), ForceMode.VelocityChange);
            }
            catch (Exception e)
            {
                Lab.Event("trip: could not be knocked down: " + e.Message);
                return;
            }
            down = true;
            downAt = Time.time;
            downFor = Mathf.Lerp(Tuning.F("trip.least", 1.6f), Tuning.F("trip.most", 4.5f), hard) * Random(0.85f, 1.2f);
            Voice(hard > 0.6f, false);
            Lab.Event("trip: down (" + why + "), for " + downFor.ToString("0.0") + " s");
        }

        /// <summary>Something has run into a part of its body (the game's own collision, on the parts that take damage).</summary>
        public void Bumped(PlayerDamageReceiver.BodyPart part, Collision c)
        {
            if (dead || down || rising || c == null)
                return;
            // (Nothing that hits a hand or a forearm takes it off its feet; nor does a thing a
            // bot has just spawned, which comes into being in its hand, against its body.)
            if (part == PlayerDamageReceiver.BodyPart.HandLf || part == PlayerDamageReceiver.BodyPart.HandRt
                || part == PlayerDamageReceiver.BodyPart.ArmLowerLf || part == PlayerDamageReceiver.BodyPart.ArmLowerRt)
                return;
            if (Time.time < Clone.SuppressUntil || spawningGun || Time.time - spawnAskedAt < 1.5f)
                return;
            // (Nor do its own things for what it is doing: the kart it is getting into.)
            if (InLesson)
                return;
            Rigidbody other = c.rigidbody;
            // (Not the ground or a wall, which it lands on and walks into; not its own body, nor what it holds.)
            if (other == null || other.isKinematic)
                return;
            RigManager whose = other.GetComponentInParent<RigManager>();
            if (whose == rig)
                return;
            // (Nor another bot's body walked into: a crowd of them after the same thing kept knocking one another flat. Their blows still count, as hits.)
            if (whose != null && BotRig.IsBot(whose))
                return;
            // (Nor the thing it is taking up or fighting with.)
            if (item != null && (Playing || Time.time - playAt < 2f))
            {
                Il2CppSLZ.Marrow.Interaction.MarrowEntity its = item.GetComponentInParent<Il2CppSLZ.Marrow.Interaction.MarrowEntity>();
                if (other == item || (its != null && other.transform.IsChildOf(its.transform)))
                    return;
            }
            // (Nor the thing it has just got out of, still rolling.)
            if (leftVehicle != null && Time.time - leftVehicleAt < 5f && other.transform.IsChildOf(leftVehicle))
                return;
            foreach (Hand hand in new[] { LeftHand, RightHand })
            {
                GameObject held = hand != null ? hand.m_CurrentAttachedGO : null;
                if (held != null && other.transform.IsChildOf(held.transform.root) && held.transform.root != rig.transform)
                    return;
            }
            // (Nor a loose thing it has walked into: a gun on the ground, a cone. What knocks it over comes at it.)
            if (whose == null && other.velocity.magnitude < Tuning.F("lying.speed", 1.5f))
                return;
            float speed = c.relativeVelocity.magnitude;
            float impulse = c.impulse.magnitude;
            bool legs = part >= PlayerDamageReceiver.BodyPart.LegUpperLf;
            bool head = part == PlayerDamageReceiver.BodyPart.Head || part == PlayerDamageReceiver.BodyPart.Neck;
            // (Its size counts: what floors a small one only shoves a big one.)
            float size = Mathf.Clamp(pose.height / 1.7f, 0.5f, 2f);
            float need = Tuning.F("trip.impulse", 30f) * Mathf.Max(0.5f, size * size) * (legs ? Tuning.F("trip.legs", 0.6f) : head ? 0.8f : 1f);
            if (impulse > need * 0.4f && speed > 1.5f && Time.time - bumpSaidAt > 0.5f)
            {
                bumpSaidAt = Time.time;
                Lab.Event("trip: bumped on " + part + " by " + other.name + ", " + impulse.ToString("0") + " Ns at " + speed.ToString("0.0") + " m/s (down at " + need.ToString("0") + ")");
            }
            if (speed < Tuning.F("trip.speed", 2.2f) || impulse < need)
                return;
            // (The thing came at it: walking into something heavy does not take it off its feet.)
            if (other.velocity.magnitude < Tuning.F("trip.speed", 2.2f) * 0.7f && whose == null)
                return;
            Trip(Mathf.InverseLerp(need, need * 3f, impulse), -c.relativeVelocity, "hit on " + part + " by " + other.name);
        }

        // A hit that hurts enough (or one in the leg, often) takes it off its feet.
        private void TripByHit(float damage, PlayerDamageReceiver.BodyPart part, Il2CppSLZ.Marrow.Combat.Attack attack)
        {
            float share = damage / Mathf.Max(0.01f, MaxHp);
            bool legs = part >= PlayerDamageReceiver.BodyPart.LegUpperLf;
            Vector3 push = attack != null ? attack.direction : Vector3.zero;
            if (share > Tuning.F("trip.damage", 0.3f))
                Trip(share, push, "a hard hit");
            else if (legs && share > 0.06f && UnityEngine.Random.value < Tuning.F("trip.leg.chance", 0.6f))
                Trip(0.3f + share, push, "hit in the leg");
        }

        // The player with a hand on it, pulling it about hard: over it goes.
        private void TripByPull()
        {
            if (down || rising || dead)
                return;
            foreach (Hand hand in new[] { Player.LeftHand, Player.RightHand })
            {
                GameObject held = hand != null ? hand.m_CurrentAttachedGO : null;
                if (held == null || hand.rb == null || !held.transform.IsChildOf(rig.transform))
                    continue;
                Rigidbody pelvis = Pelvis();
                Vector3 pull = hand.rb.velocity - (pelvis != null ? Flat(pelvis.velocity) * 0.5f : Vector3.zero);
                if (pull.magnitude > Tuning.F("trip.pull", 2.4f))
                {
                    Trip(Mathf.InverseLerp(2.4f, 6f, pull.magnitude), pull, "you pulled it over");
                    return;
                }
            }
        }

        /// <summary>Once a frame, before anything else it does: lying there, and getting up. True while it is down.</summary>
        private bool DownStep(float dt)
        {
            if (!down && !rising)
            {
                TripByPull();
                if (!down)
                    return false;
            }
            walk = Vector2.zero;
            stick = Vector2.zero;
            running = false;
            jump = otherA = false;
            Transform head = Head;
            if (down)
            {
                Rigidbody pelvis = Pelvis();
                bool still = pelvis == null || pelvis.velocity.magnitude < 1.2f;
                // (Held down by the player, it stays down.)
                bool heldDown = false;
                foreach (Hand hand in new[] { Player.LeftHand, Player.RightHand })
                {
                    GameObject held = hand != null ? hand.m_CurrentAttachedGO : null;
                    heldDown |= held != null && held.transform.IsChildOf(rig.transform);
                }
                if (Time.time - downAt < downFor || !still || (heldDown && Time.time - downAt < downFor + 6f))
                    return true;
                try
                {
                    rig.physicsRig.UnRagdollRig();
                }
                catch (Exception e)
                {
                    Lab.Event("trip: could not get up: " + e.Message);
                }
                down = false;
                rising = true;
                riseAt = Time.time;
                // (Facing the way it lies: its head's way, flat.)
                if (head != null && pelvis != null && Flat(head.position - pelvis.position).magnitude > 0.25f)
                    bodyYaw = Recorder.Yaw(Flat(head.position - pelvis.position));
                droveHas = false;
                droveHeadHas = false;
                portalHeadHas = false;
                fallHas = false;
                noLandUntil = Time.time + 2.5f;
                nav.Clear();
                steer = Vector3.zero;
                Lab.Event("trip: getting up");
                Call("up");
                return true;
            }
            // Getting up: from its hands and knees, up through a crouch, to standing.
            float longs = Tuning.F("trip.rise", 1.7f);
            float u = Mathf.Clamp01((Time.time - riseAt) / longs);
            float eyes = StandingEyes();
            float h = eyes > 0.3f ? eyes * 0.97f : standingHeight;
            float m = h / 1.7f;
            float up = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.15f, 1f, u));
            float height = Mathf.Lerp(h * Tuning.F("trip.low", 0.38f), h, up);
            // Hands on the ground in front at first (pushing up), coming back to its sides.
            float push = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.35f, 0.85f, u));
            Frame f = Standing(Time.time - born, Mathf.Lerp(35f, 0f, up), 0f, 0f);
            f.height = height;
            float handY = Mathf.Lerp(height - 0.78f * m, Mathf.Max(0.08f * m, height - 0.62f * m), push);
            f.leftPos = new Vector3(-0.24f * m, handY, Mathf.Lerp(0.06f, 0.34f, push) * m);
            f.rightPos = new Vector3(0.24f * m, handY, Mathf.Lerp(0.06f, 0.34f, push) * m);
            f.leftRot = Quaternion.Euler(Mathf.Lerp(65f, 80f, push), 10f, 0f);
            f.rightRot = Quaternion.Euler(Mathf.Lerp(65f, 80f, push), -10f, 0f);
            pose = f;
            curl = Mathf.Lerp(0.25f, 0.05f, push);
            if (u >= 1f)
            {
                rising = false;
                upAt = Time.time;
                Lab.Event("trip: up");
                return false;
            }
            return true;
        }

        /// <summary>For the lab: knocked down now (so hard).</summary>
        public void LabTrip(float hard)
        {
            Trip(hard, Quaternion.Euler(0f, bodyYaw, 0f) * Vector3.back, "the lab");
        }
    }
}
