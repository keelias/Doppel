using Il2CppSLZ.Marrow;
using UnityEngine;

namespace ThirdEyeBonelabMod.Bots
{
    /// <summary>
    /// The bot handling a gun the player's way (see GunMoves): holding it where they hold it
    /// (each controller where theirs was, from its head), aiming it by looking as they do (its
    /// head turned so the barrel comes on the target as the player's does), and reloading with
    /// their reload, played back, the magazine out, the new one taken, in, and the slide worked
    /// at the moments they did it.
    /// </summary>
    public partial class Bot
    {
        // A little learned on top: how far its barrel still comes out from where it looks for it.
        private Vector2 aimFix;
        private GunMoves.Reload learnedReload;
        private bool learnedInsertTried;

        private string GunKey => gun != null ? GunMoves.KeyOf(gun, support != null) : "";

        /// <summary>The player's hold for this gun (aimed or low), if it has watched them enough.</summary>
        private GunMoves.Hold LearnedHold(bool aimed)
        {
            if (gun == null || Tuning.F("learned.hold", 1f) < 0.5f)
                return null;
            return GunMoves.Held(GunKey, support != null, aimed);
        }

        /// <summary>
        /// The gun held as the player holds it, aimed at point (or low): its head turned so the
        /// barrel comes on the point as theirs does, its controllers where theirs were from their
        /// head. False if it has not learned how.
        /// </summary>
        private bool HoldLearned(Vector3 point, Transform head, bool aimed)
        {
            GunMoves.Hold learned = LearnedHold(aimed);
            if (learned == null && !aimed)
                learned = LearnedHold(true);
            holdAimedNow = aimed && learned != null;
            Transform muzzle = gun != null ? gun.firePointTransform : null;
            if (learned == null || muzzle == null || head == null)
                return false;
            Quaternion body = Quaternion.Euler(0f, bodyYaw, 0f);
            Pose right = GunMoves.U(learned.right, Pose.identity);
            Quaternion barrelInHead = GunMoves.UQ(learned.muzzle, Quaternion.identity);

            // Which way to look for the barrel to come on the point (aimed), or straight on (low).
            float pitch, yaw;
            if (aimed)
            {
                Vector3 want = Quaternion.Inverse(body) * (point - muzzle.position).normalized;
                Vector3 along = barrelInHead * Vector3.forward;
                // The gun kept fast to its hand (see GunFixed): where its barrel points from
                // its head is then known outright (the hand where the player's was, and the
                // gun as it sits in the hand), not something to be felt for a little at a time.
                bool known = lockSet && item != null;
                if (known)
                {
                    Quaternion muzzleInGun = Quaternion.Inverse(item.rotation) * muzzle.rotation;
                    along = right.rotation * (gunInController.rotation * muzzleInGun) * Vector3.forward;
                }
                // The nod and the turn of its head that bring the barrel onto the point exactly
                // (worked out, not taken from a turn with its tilt thrown away: for a hold with
                // the barrel well off where the head faces, that left it a steady ten degrees out).
                if (!SolveLook(along.normalized, want, out pitch, out yaw))
                {
                    Quaternion look = Quaternion.LookRotation(want, Vector3.up) * Quaternion.Inverse(Quaternion.LookRotation(along, Vector3.up));
                    Vector3 e = look.eulerAngles;
                    pitch = Mathf.DeltaAngle(0f, e.x);
                    yaw = Mathf.DeltaAngle(0f, e.y);
                }
                pitch += aimFix.x;
                yaw += aimFix.y;
                if (Time.time - aimSaidAt > 15f)
                {
                    aimSaidAt = Time.time;
                    Vector3 act = Quaternion.Inverse(body) * muzzle.forward;
                    Lab.Event("aim: wants pitch " + PitchOf(want).ToString("0") + " yaw " + YawOf(want).ToString("0") + ", barrel at pitch " + PitchOf(act).ToString("0") + " yaw " + YawOf(act).ToString("0")
                        + "; head asked " + pitch.ToString("0") + "/" + yaw.ToString("0") + " (has " + aimPitchNow.ToString("0") + "/" + aimYawNow.ToString("0") + "), fix " + aimFix.x.ToString("0.0") + "/" + aimFix.y.ToString("0.0")
                        + ", barrel in head " + PitchOf(along).ToString("0") + "/" + YawOf(along).ToString("0") + (known ? " (known)" : " (learned)") + ", step " + step);
                }
                // What is still off, learned a little at a time (while the gun is steady).
                // (Steady, or near enough: a hold that never came quite to rest was never put
                // right, and the bot stood with its gun a few degrees off for good. And as far
                // as it takes: a hold learned while firing can be well off where the gun points.)
                bool steadyEnough = item == null || (item.angularVelocity.magnitude < Tuning.F("hold.steady.spin", 1.5f) * 2.5f && item.velocity.magnitude < Tuning.F("hold.steady.speed", 0.8f) * 2f);
                if (Mathf.Abs(aimFix.y) > 30f || Mathf.Abs(aimFix.x) > 30f)
                    aimFix = Vector2.zero;
                if (steadyEnough && Time.time - firedAt > 0.35f)
                {
                    Vector3 actual = Quaternion.Inverse(body) * muzzle.forward;
                    float k = Mathf.Clamp01(Time.deltaTime * Tuning.F("learned.aimfix.rate", 2.5f) * (GunSteady() ? 1f : 0.4f));
                    float most = Tuning.F("learned.aimfix.most", 25f);
                    aimFix.x = Mathf.Clamp(aimFix.x + k * (PitchOf(want) - PitchOf(actual)), -most, most);
                    aimFix.y = Mathf.Clamp(aimFix.y + k * Mathf.DeltaAngle(YawOf(actual), YawOf(want)), -most, most);
                }
            }
            else
            {
                pitch = 15f;
                yaw = 0f;
            }
            pitch = Mathf.Clamp(pitch, -60f, 70f);
            yaw = Mathf.Clamp(yaw, -60f, 60f);
            // Where its head looks: at what it aims at (or a little down, gun low). Its hold is
            // turned onto the point about its head, but its head is not turned with it: the
            // player may hold a gun with its barrel well off where they look, and with its
            // head bent that far down to make up for it, its body squatted to shoot.
            float lookPitch = 6f, lookYaw = 0f;
            if (aimed)
            {
                Vector3 to = Quaternion.Inverse(body) * (point - head.position).normalized;
                lookPitch = Mathf.Clamp(PitchOf(to), -40f, 40f);
                lookYaw = Mathf.Clamp(YawOf(to), -50f, 50f);
            }
            // (Eased, as a head turns.)
            float ease = Mathf.Clamp01(Time.deltaTime * Tuning.F("learned.look.ease", 8f));
            // (Kept from one frame to the next here: the turn of its head that its hands' play
            // keeps is set back to straight ahead every frame, so eased from that, its head
            // only ever turned a tenth of the way, and its barrel stayed off to one side.)
            aimPitchNow = Mathf.LerpAngle(aimPitchNow, pitch, ease);
            aimYawNow = Mathf.LerpAngle(aimYawNow, yaw, ease);
            lookPitchNow = Mathf.LerpAngle(lookPitchNow, lookPitch, ease);
            lookYawNow = Mathf.LerpAngle(lookYawNow, lookYaw, ease);
            playPitch = lookPitchNow;
            playYaw = lookYawNow;
            playHeight = standingHeight * 0.985f;

            // (The turn of its hold: "head" below is the head the hold was learned from, not its own.)
            Quaternion headTurn = Quaternion.Euler(aimPitchNow, aimYawNow, 0f);
            Vector3 headAt = new Vector3(0f, playHeight, 0f);
            handOverride = true;
            learnedHoldAt = Time.time;
            handAt = headAt + headTurn * right.position;
            handTurn = headTurn * right.rotation;
            // The kick of a shot: the gun up and back a little, and down again at once (the gun
            // itself is kept fast to the hand, so its own kick is not seen).
            float kicked = Time.time - firedAt;
            if (lockSet && kicked >= 0f && kicked < 0.4f)
            {
                float kick = Mathf.Exp(-kicked / Tuning.F("gun.kick.time", 0.07f)) * (support != null ? 0.6f : 1f);
                handTurn = handTurn * Quaternion.Euler(-Tuning.F("gun.kick", 4f) * kick, 0f, 0f);
                handAt -= headTurn * Vector3.forward * (0.02f * kick);
            }
            // Its front hand, once it is on the gun: where it sits on the gun (the gun where it
            // is meant to be), so it goes with the gun and never pulls it about.
            if (lockSet && leftOnGunSet && supportHeld && step == Step.Ready && item != null)
            {
                Transform root = controls.vrRoot;
                Pose trigger = new Pose(root.TransformPoint(handAt), body * handTurn);
                Pose front = trigger * gunInController * leftOnGun;
                leftOverride = true;
                leftAt = root.InverseTransformPoint(front.position);
                leftTurn = Quaternion.Inverse(body) * front.rotation;
                return true;
            }
            if (support != null && learned.left != null && step != Step.OneHand)
            {
                Pose left = GunMoves.U(learned.left, Pose.identity);
                Vector3 leftWant = headAt + headTurn * left.position;
                Quaternion leftTurnWant = headTurn * left.rotation;
                leftOverride = true;
                if (step == Step.Reach)
                {
                    // Over to it from where the hand was, eased.
                    float k = Mathf.SmoothStep(0f, 1f, (Time.time - stepAt) / Tuning.F("hold.reach.time", 0.7f));
                    Vector3 from = controls.vrRoot.InverseTransformPoint(leftStart.position);
                    Quaternion fromTurn = Quaternion.Inverse(body) * leftStart.rotation;
                    leftAt = Vector3.Lerp(from, leftWant, k) + Quaternion.Inverse(body) * reachFix;
                    leftTurn = Quaternion.Slerp(fromTurn, leftTurnWant, k);
                }
                else
                {
                    leftAt = leftWant;
                    leftTurn = leftTurnWant;
                }
            }
            return true;
        }

        // The nod (about its ears) and then the turn (about up) that carry one direction onto another. False if none does.
        private static bool SolveLook(Vector3 from, Vector3 to, out float pitch, out float yaw)
        {
            pitch = yaw = 0f;
            float r = Mathf.Sqrt(from.y * from.y + from.z * from.z);
            if (r < 1e-4f || Mathf.Abs(to.y) > r)
                return false;
            // from.y cos p - from.z sin p = to.y
            float phi = Mathf.Atan2(from.z, from.y);
            float reach = Mathf.Acos(Mathf.Clamp(to.y / r, -1f, 1f));
            float best = float.MaxValue;
            foreach (float p in new[] { reach - phi, -reach - phi })
            {
                float z = from.y * Mathf.Sin(p) + from.z * Mathf.Cos(p);
                float deg = Mathf.DeltaAngle(0f, p * Mathf.Rad2Deg);
                // (The one that keeps it pointing forward, and the lesser nod of the two.)
                float cost = Mathf.Abs(deg) + (z < 0f ? 1000f : 0f);
                if (cost < best)
                {
                    best = cost;
                    pitch = deg;
                    float x = from.x;
                    yaw = Mathf.DeltaAngle(Mathf.Atan2(x, z) * Mathf.Rad2Deg, Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg);
                }
            }
            return best < 1000f;
        }

        // ---------------------------------------------------------------- the gun fast to its hand

        // How the gun sits from its trigger hand's controller, and its front hand's from the
        // gun: taken once it has settled in the hand, and kept to from then on.
        private float aimSaidAt;
        private float lockSaidAt = -10f;
        private float learnedHoldAt = -10f;
        private float aimPitchNow, aimYawNow;
        private float lookPitchNow, lookYawNow;
        private bool lockSet;
        private Vector3 reachFix;
        private Pose gunInController;
        private bool leftOnGunSet;
        private Pose leftOnGun;
        private IntPtr lockGun;
        private float lockSince;

        /// <summary>
        /// Each physics step while it holds a gun: the gun is kept just where its hand has it.
        /// Held by a hand that follows its controller on a spring, with a second hand on it, a
        /// gun swung and shook, and the bot stood correcting an aim that never came to rest. So
        /// the gun is carried straight to its place from the controller (as fast as it takes,
        /// each step), and the hands on it go with it.
        /// </summary>
        public void GunFixed(float dt)
        {
            if (gun == null || item == null || dead || down || mode == Mode.Clone || Tuning.F("gun.lock", 1f) < 0.5f)
            {
                lockSet = false;
                leftOnGunSet = false;
                lockGun = IntPtr.Zero;
                return;
            }
            BaseController right = controls.rightController;
            if (right == null || !Holding())
            {
                lockSet = false;
                leftOnGunSet = false;
                lockGun = IntPtr.Zero;
                return;
            }
            // (Only a hold of the player's is kept so: its own plain hold steers the gun by its
            // hands' pull, and kept fast as well, the two fought.)
            if (lockGun != gun.Pointer)
            {
                lockGun = gun.Pointer;
                lockSince = Time.time;
                lockSet = false;
                leftOnGunSet = false;
            }
            // (Not while someone else has a hand on it: it is theirs to pull about.)
            if (OthersHoldIt())
                return;
            // (Only while it holds the gun the player's way: its own plain hold steers the gun
            // by its hands' pull, and kept fast as well, the two fought. How the gun sits in
            // its hand, once taken, is kept all the while it has the gun.)
            if (Time.time - learnedHoldAt > 0.3f && !Reloading)
                return;
            Transform ctl = right.transform;
            if (!lockSet)
            {
                float held = Time.time - lockSince;
                if (held < 0.5f || (!GunSteady() && held < 1.5f))
                    return;
                Pose sits = Pose.Of(ctl).Inverse() * Pose.Of(item.transform);
                // (Only as a gun sits in a hand: taken while it was still on its way into the
                // hand, half a metre off, it was then kept half a metre off for good.)
                Hand holding = RightHand;
                // (By where its handle is: a gun's own middle may be anywhere, a long way off on some.)
                float handle = gun.triggerGrip != null ? (gun.triggerGrip.transform.position - ctl.position).magnitude : sits.position.magnitude;
                if (handle > Tuning.F("gun.lock.most", 0.35f) || holding == null || (holding.transform.position - ctl.position).magnitude > 0.25f)
                {
                    if (held > 4f && Time.time - lockSaidAt > 5f)
                    {
                        lockSaidAt = Time.time;
                        Lab.Event("gun: not kept fast (its handle is " + handle.ToString("0.00") + " m from the controller, the hand "
                            + (holding != null ? (holding.transform.position - ctl.position).magnitude.ToString("0.00") : "?") + " m)");
                    }
                    return;
                }
                gunInController = sits;
                lockSet = true;
                Lab.Event("gun: kept fast to its hand from here (" + gunInController.position.magnitude.ToString("0.00") + " m from the controller)");
            }
            if (supportHeld && step == Step.Ready && !leftOnGunSet && controls.leftController != null)
            {
                // (Its controller where it would have to be for the hand to be just where it is
                // on the grip: so the hand asks nothing of the gun. The controller as it was,
                // off at the player's spot for it, kept pulling the gun that way, and the gun,
                // kept to its place, shook.)
                Hand front = LeftHand;
                Pose handNow = front != null ? Pose.Of(front.transform) : Pose.Of(controls.leftController.transform);
                Pose controllerFor = freeLeftSet && front != null ? handNow * freeLeft.Inverse() : Pose.Of(controls.leftController.transform);
                leftOnGun = Pose.Of(item.transform).Inverse() * controllerFor;
                leftOnGunSet = true;
            }
            else if (!supportHeld)
            {
                leftOnGunSet = false;
            }
            Pose want = Pose.Of(ctl) * gunInController;
            if (Bad(want.position) || Bad(want.rotation))
                return;
            float k = Tuning.F("gun.lock.pull", 0.55f);
            Vector3 move = (want.position - item.position) / dt * k;
            item.velocity = Vector3.ClampMagnitude(move, 15f);
            Quaternion turn = want.rotation * Quaternion.Inverse(item.rotation);
            turn.ToAngleAxis(out float angle, out Vector3 axis);
            if (angle > 180f)
                angle -= 360f;
            Vector3 spin = float.IsNaN(axis.x) || float.IsInfinity(axis.x) ? Vector3.zero : axis * (angle * Mathf.Deg2Rad / dt * k);
            item.angularVelocity = Vector3.ClampMagnitude(spin, 30f);
        }

        // A hand on the gun that is not this bot's own.
        private bool OthersHoldIt()
        {
            try
            {
                Il2CppSLZ.Marrow.Interaction.MarrowEntity entity = item.GetComponentInParent<Il2CppSLZ.Marrow.Interaction.MarrowEntity>();
                Component root = entity != null ? entity : (Component)item;
                foreach (Grip g in root.GetComponentsInChildren<Grip>(false))
                {
                    if (g == null || g.attachedHands == null)
                        continue;
                    foreach (Hand h in g.attachedHands)
                    {
                        if (h != null && h != LeftHand && h != RightHand)
                            return true;
                    }
                }
            }
            catch (Exception)
            {
            }
            return false;
        }

        // The gun's grip nearest the hand (not its handle, magazine, or the slide or bolt).
        private Grip NearestGripTo(Hand hand)
        {
            if (item == null)
                return null;
            Il2CppSLZ.Marrow.Interaction.MarrowEntity entity = item.GetComponentInParent<Il2CppSLZ.Marrow.Interaction.MarrowEntity>();
            Component root = entity != null ? entity : (Component)item;
            Grip best = null;
            float nearest = float.MaxValue;
            foreach (Grip g in root.GetComponentsInChildren<Grip>(false))
            {
                if (g == null || g == gun.triggerGrip || g.HasAttachedHands() || g.GetComponentInParent<Magazine>() != null)
                    continue;
                string name = g.name.ToLowerInvariant();
                // (Nor the grip at the magazine well, "capsule": taken hold of, it draws the
                // magazine out, and the bot stood there taking its own magazine out and reloading.)
                if (name.Contains("slide") || name.Contains("charg") || name.Contains("bolt") || name.Contains("mag") || name.Contains("capsule") || name.Contains("stock"))
                    continue;
                if (gun.ammoSocket != null && g.transform.IsChildOf(gun.ammoSocket.transform))
                    continue;
                float d = (hand.transform.position - HandOnGrip(g, hand).position).magnitude;
                if (d < nearest)
                {
                    nearest = d;
                    best = g;
                }
            }
            return best;
        }

        private static float PitchOf(Vector3 v) => -Mathf.Asin(Mathf.Clamp(v.normalized.y, -1f, 1f)) * Mathf.Rad2Deg;

        private static float YawOf(Vector3 v) => Mathf.Atan2(v.x, v.z) * Mathf.Rad2Deg;

        /// <summary>With a learned two-handed hold, the other hand takes hold once its learned place has brought it to the grip.</summary>
        private void HoldSupportLearned()
        {
            Hand left = LeftHand;
            if (support == null || left == null)
                return;
            supportHeld = left.m_CurrentAttachedGO != null && support.attachedHands != null && support.attachedHands.Contains(left);
            float since = Time.time - stepAt;
            switch (step)
            {
                case Step.OneHand:
                    leftOverride = false;
                    // (Once the gun is still in its hand, or soon anyway: a long gun held out
                    // in one hand never comes quite still, and it stood so for many seconds.)
                    if (since > 0.6f && (GunSteady() || since > Tuning.F("hold.onehand.most", 1.4f)))
                        NextStep(Step.Reach);
                    break;
                case Step.Reach:
                    if (supportHeld)
                    {
                        NextStep(Step.Settle);
                        break;
                    }
                    // (The gun is still now: its hand is brought the last of the way onto the
                    // grip itself, not left a hand's breadth off at the player's spot for it.)
                    if (lockSet && since > Tuning.F("hold.reach.time", 0.7f) && controls.leftController != null && !supportHeld)
                    {
                        Vector3 off = HandOnGrip(support, left).position - left.transform.position;
                        reachFix = Vector3.ClampMagnitude(reachFix + off * Mathf.Clamp01(Time.deltaTime * 6f), 0.35f);
                    }
                    // (Its front hand empty for it: a magazine left in it from a reload is let go.)
                    if (left.m_CurrentAttachedGO != null && !Reloading && !supportHeld)
                        FreeHand(left);
                    float gap = (left.transform.position - HandOnGrip(support, left).position).magnitude;
                    // The hand where the player's was: the part of the gun it is at is the one they
                    // held (not always the one first picked as the front grip).
                    if (since > 0.8f && gap >= Tuning.F("learned.grab", 0.12f))
                    {
                        Grip nearer = NearestGripTo(left);
                        if (nearer != null && nearer != support)
                        {
                            float other = (left.transform.position - HandOnGrip(nearer, left).position).magnitude;
                            if (other < gap)
                            {
                                Lab.Event("front hand: nearer to " + nearer.name + " (" + other.ToString("0.00") + " m) than " + support.name + " (" + gap.ToString("0.00") + " m)");
                                support = nearer;
                                gap = other;
                            }
                        }
                    }
                    // (A try now and then, not every frame; three misses, and it holds the gun one-handed.)
                    if (since > 0.5f && gap < Tuning.F("learned.grab", 0.12f) + (since > 1.5f ? 0.06f : 0f) && Time.time - supportTriedAt > 0.4f)
                    {
                        supportTriedAt = Time.time;
                        supportHeld = Grab(left, support);
                        Lab.Event((supportHeld ? "front hand on " : "front hand missed ") + support.name + " (learned hold, gap " + gap.ToString("0.00") + " m"
                            + (supportHeld ? "" : ", hand holds " + (left.m_CurrentAttachedGO != null ? left.m_CurrentAttachedGO.name : "nothing")
                               + ", grip held by " + (support.attachedHands != null ? support.attachedHands.Count : -1) + " hands, " + support.GetIl2CppType().Name) + ")");
                        if (supportHeld)
                            NextStep(Step.Settle);
                        else if (++supportTries >= 3)
                        {
                            support = null;
                            leftOverride = false;
                            NextStep(Step.Ready);
                        }
                    }
                    else if (since > 3f)
                    {
                        Lab.Event("front hand gave up (learned hold, gap " + gap.ToString("0.00") + " m)");
                        support = null;
                        leftOverride = false;
                        NextStep(Step.Ready);
                    }
                    break;
                case Step.Settle:
                    if (!supportHeld)
                        NextStep(Step.Reach);
                    else if (since > 0.4f)
                        NextStep(Step.Ready);
                    break;
                default:
                    if (!supportHeld)
                        NextStep(Step.Reach);
                    break;
            }
        }

        // ---------------------------------------------------------------- the player's reload

        private float chargedAt = -10f;

        /// <summary>Starts the player's own reload, if it has one for this gun. False if not.</summary>
        private bool StartLearnedReload()
        {
            if (gun == null || Tuning.F("learned.reload", 1f) < 0.5f)
                return false;
            learnedReload = GunMoves.ReloadFor(GunKey, support != null);
            learnedInsertTried = false;
            // (Its other hand empty for it, whatever was left in it.)
            Hand free = LeftHand;
            if (learnedReload != null && free != null && free.m_CurrentAttachedGO != null && !supportHeld)
                FreeHand(free);
            if (learnedReload != null)
                Lab.Event("reload the player's way (" + learnedReload.end.ToString("0.0") + " s)");
            return learnedReload != null;
        }

        /// <summary>Once a frame of a learned reload: the player's moves, and the real thing at their moments.</summary>
        private void LearnedReloadStep(Transform head)
        {
            GunMoves.Reload clip = learnedReload;
            AmmoSocket socket = gun.ammoSocket;
            Hand left = LeftHand;
            float t = (Time.time - reloadAt) * Tuning.F("learned.reload.speed", 1f);

            // Their moves at this moment: head, and each controller from it.
            List<GunMoves.ReloadFrame> frames = clip.frames;
            int i = 0;
            while (i < frames.Count - 2 && frames[i + 1].t <= t)
                i++;
            GunMoves.ReloadFrame a = frames[i];
            GunMoves.ReloadFrame b = frames[Mathf.Min(i + 1, frames.Count - 1)];
            float k = b.t > a.t ? Mathf.Clamp01((t - a.t) / (b.t - a.t)) : 0f;
            Pose headTurn = Pose.Lerp(GunMoves.U(a.head, Pose.identity), GunMoves.U(b.head, Pose.identity), k);
            Pose right = Pose.Lerp(GunMoves.U(a.right, Pose.identity), GunMoves.U(b.right, Pose.identity), k);
            Pose leftPose = Pose.Lerp(GunMoves.U(a.left, Pose.identity), GunMoves.U(b.left, Pose.identity), k);
            Vector3 e = headTurn.rotation.eulerAngles;
            playPitch = Mathf.DeltaAngle(0f, e.x);
            playYaw = Mathf.DeltaAngle(0f, e.y);
            playHeight = standingHeight * 0.985f;
            Vector3 headAt = new Vector3(0f, playHeight, 0f);
            handOverride = true;
            handAt = headAt + right.position;
            handTurn = right.rotation;
            leftOverride = true;
            leftAt = headAt + leftPose.position;
            leftTurn = leftPose.rotation;

            if (socket == null)
            {
                learnedReload = null;
                FinishReload(quick: true);
                return;
            }
            // The magazine out.
            if (!ejected && t >= clip.eject)
            {
                ejected = true;
                try
                {
                    if (gun.HasMagazine())
                    {
                        Magazine old = socket._magazinePlug != null ? socket._magazinePlug.magazine : null;
                        socket.EjectMagazine();
                        if (old != null)
                            dropped.Add((old, Time.time));
                    }
                }
                catch (Exception)
                {
                }
            }
            // A fresh one into the hand as theirs took one (from its belt pouch).
            float grabAt = clip.grab >= 0f ? clip.grab : Mathf.Max(0.3f, clip.insert - 0.8f);
            if (!magAsked && t >= grabAt - 0.25f && left != null)
            {
                magAsked = true;
                reload = Reload.Fetch;
                SpawnMag(left.transform.position, left.transform.rotation);
            }
            if (newMag == null && magAsked && Time.time - magAskedAt > 0.15f)
                newMag = SpawnedMag();
            if (newMag != null && newMag.grip != null && left != null && left.m_CurrentAttachedGO == null && !gun.HasMagazine() && t >= grabAt - 0.1f)
                Grab(left, newMag.grip);
            // In, as theirs went in (helped home if it is close, made to if it is late).
            if (newMag != null && !gun.HasMagazine() && t >= clip.insert && newMag.magazinePlug != null)
            {
                reload = Reload.Insert;
                float gap = (newMag.magazinePlug.transform.position - socket.transform.position).magnitude;
                try
                {
                    // (In at the moment theirs went in: by its own way in if it is at the well,
                    // and put in outright if it is not quite: no hanging about beside the gun.)
                    if (gap < 0.15f && !learnedInsertTried)
                    {
                        learnedInsertTried = true;
                        newMag.magazinePlug.InsertPlug(socket);
                    }
                    else if (t > clip.insert + 0.12f)
                    {
                        newMag.magazinePlug.ForceInSocket(socket);
                    }
                }
                catch (Exception)
                {
                }
            }
            if (newMag != null && gun.HasMagazine() && left != null)
            {
                LetGoOf(newMag.grip, left);
                newMag = null;
            }
            // The slide worked, as they worked it (the other hand on it, and away).
            if (clip.rack >= 0f && gun.HasMagazine() && gun.cartridgeState != Gun.CartridgeStates.UNSPENT && left != null)
            {
                reload = Reload.Rack;
                if (slide == null)
                    slide = SlideGrip(gun);
                if (slide != null && !slideHeld && t >= clip.rack - 0.3f && t < clip.rack
                    && (left.transform.position - slide.transform.position).magnitude < 0.15f && left.m_CurrentAttachedGO == null)
                    slideHeld = Grab(left, slide);
            }
            if (slideHeld && t >= clip.rack + 0.1f)
            {
                LetGoOf(slide, left);
                slideHeld = false;
            }
            // (A round chambered at the moment theirs was, whether its hand worked the slide or not.)
            if (clip.rack >= 0f && t >= clip.rack + 0.15f && gun.HasMagazine() && gun.cartridgeState != Gun.CartridgeStates.UNSPENT && !slideHeld && Time.time - chargedAt > 0.6f)
            {
                chargedAt = Time.time;
                TryCharge();
            }
            // Its end: whatever did not happen by hand is done for it.
            if (t >= clip.end)
            {
                learnedReload = null;
                if (!gun.HasMagazine() && newMag != null && newMag.magazinePlug != null)
                {
                    try
                    {
                        newMag.magazinePlug.ForceInSocket(socket);
                    }
                    catch (Exception)
                    {
                    }
                }
                DropNewMag();
                if (gun.HasMagazine() && gun.cartridgeState != Gun.CartridgeStates.UNSPENT)
                    TryCharge();
                FinishReload(quick: !gun.HasMagazine());
            }
        }
    }
}
