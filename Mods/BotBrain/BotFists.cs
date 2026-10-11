using BoneLib;
using Il2CppSLZ.Marrow;
using Il2CppSLZ.Marrow.Pool;
using UnityEngine;

namespace ThirdEyeBonelabMod.Bots
{
    /// <summary>
    /// The bot fighting hand to hand. With an enemy in sight and nothing in its hands it takes
    /// the first weapon it sees, a gun or something to hit with (a blade, a bat, a bar), and
    /// only with none about does it fight with its fists. Its punches, and its blows with a
    /// thing in its hand, are the player's own, as they recorded them in the Punch and Melee
    /// slots (see MoveSlots): their hands, from their head, laid on its own body and turned
    /// to what it is hitting. Till they have recorded any, a plain straight punch, and a plain
    /// chop. Its hands are its own body's: whatever they (or the thing in them) hit fast
    /// enough is hit.
    /// </summary>
    public partial class Bot
    {
        private Frame[] hitClip;
        private bool punchRight;
        private float punchAt = -10f;
        private float nextPunchAt, punchSaidAt = -10f;
        private int comboLeft;
        private readonly List<Frame[]> hitLately = new List<Frame[]>();

        // The thing it hits with: how far it reaches, and when it last saw anything to hit.
        private bool meleeWanted;
        private float meleeReach = 1.3f, meleeSeenAt, meleeSenseAt;

        /// <summary>Something to hit with in its hand (not a gun).</summary>
        private bool MeleeArmed => play == Play.Melee && Holding();

        // Fists only: after whatever enemy it sees (no gun, however near one lies).
        private void FistsOnly(Transform you, Transform head, float dt)
        {
            if (Time.time >= thinkAt)
            {
                thinkAt = Time.time + ThinkEvery;
                Sense();
            }
            handOverride = false;
            leftOverride = false;
            fists = false;
            running = false;
            if (nearestSeen.transform == null || nearestSeenDistance > 30f)
            {
                walk = Vector2.zero;
                Watch(you, head, dt, 90f);
                return;
            }
            Brawl(head, dt);
        }

        // What it fights is some way off, or up above it: to it by the level's own ways, round
        // what is between and up what is to be climbed, to its own side of it. False once it is close enough to close in.
        private float chaseSetAt;

        private bool Chase(Transform head, Vector3 point, float dt)
        {
            Vector3 feet = Feet;
            float flat = Flat(point - head.position).magnitude;
            float above = point.y - head.position.y;
            if (climbing)
            {
                Travel(MotionLibrary.RunPush, 0.8f, dt);
                return true;
            }
            if (flat < Tuning.F("chase.from", 3.2f) && above < Tuning.F("chase.above", 1.1f))
                return false;
            Vector3 to = new Vector3(point.x, point.y - 1.3f, point.z) + (flat > 4f ? OwnSide(1.3f) : Vector3.zero);
            if (!nav.HasGoal || (nav.Goal - to).magnitude > 1.2f || Time.time - chaseSetAt > 2f)
            {
                nav.SetGoal(to);
                chaseSetAt = Time.time;
            }
            running = flat > 5f;
            Travel(running ? MotionLibrary.RunPush : MotionLibrary.WalkPush, 0.8f, dt);
            return true;
        }

        // In on what it fights (running in from further off), not too close; its head on it. How far off it is.
        private float CloseIn(Transform head, Vector3 point, float reach, float dt)
        {
            Face(point, dt, 320f);
            Vector3 to = Flat(point - head.position);
            float far = to.magnitude;
            Vector3 dir = to.sqrMagnitude > 0.001f ? to.normalized : Quaternion.Euler(0f, bodyYaw, 0f) * Vector3.forward;
            Vector3 local = Quaternion.Euler(0f, -bodyYaw, 0f) * dir;
            if (far > reach * 0.85f)
            {
                running = far > 3f;
                float pace = running ? MotionLibrary.RunPush : MotionLibrary.WalkPush;
                Vector3 clear = KeepApart(dir, ref pace);
                local = Quaternion.Euler(0f, -bodyYaw, 0f) * clear;
                walk = new Vector2(local.x, local.z) * pace;
            }
            else
            {
                walk = far < reach * 0.4f ? -new Vector2(local.x, local.z) * MotionLibrary.WalkPush * 0.6f : Vector2.zero;
            }
            Vector3 look = Quaternion.Inverse(Quaternion.Euler(0f, bodyYaw, 0f)) * (point - head.position);
            playPitch = Mathf.Clamp(-Mathf.Atan2(look.y, Mathf.Max(new Vector2(look.x, look.z).magnitude, 0.1f)) * Mathf.Rad2Deg, -40f, 50f);
            playYaw = Mathf.Clamp(Mathf.Atan2(look.x, look.z) * Mathf.Rad2Deg, -40f, 40f);
            playHeight = standingHeight * 0.97f;
            return far;
        }

        // A move of the player's (a punch, a blow) put on its hands: theirs from their head, sized to it, turned to what it hits. False once it is over.
        private bool HitOfTheirs(float since) => HitOfTheirs(since, 1f, false, Vector3.zero);

        // Where its hands were last frame of a blow (the next one begins from there, not from its guard), and when.
        private Vector3 hitRightWas, hitLeftWas;
        private Quaternion hitRightTurnWas = Quaternion.identity, hitLeftTurnWas = Quaternion.identity;
        private float hitPosedAt = -10f;
        private Vector3 hitRightBefore, hitLeftBefore;
        private float hitFast, hitSpin, hitBody;

        // (pace: how many times faster than it was recorded. fist: a punch, its fist turned to
        // line up with the way it is going as it goes out, as a punch has to be to land as one.)
        private bool HitOfTheirs(float since, float pace, bool fist, Vector3 at)
        {
            if (hitClip == null || hitClip.Length < 2)
                return false;
            float length = hitClip[hitClip.Length - 1].time / Mathf.Max(0.2f, pace);
            try
            {
                Hand rh = RightHand, lh = LeftHand;
                Rigidbody hips = Pelvis();
                if (rh != null && rh.rb != null && lh != null && lh.rb != null && hips != null)
                {
                    hitFast = Mathf.Max(hitFast, Mathf.Max((rh.rb.velocity - hips.velocity).magnitude, (lh.rb.velocity - hips.velocity).magnitude));
                    hitSpin = Mathf.Max(hitSpin, Mathf.Max(rh.rb.angularVelocity.magnitude, lh.rb.angularVelocity.magnitude));
                    hitBody = Mathf.Max(hitBody, hips.velocity.magnitude);
                }
            }
            catch (Exception)
            {
            }
            if (since > length)
            {
                Lab.Event("fists: that blow (" + length.ToString("0.0") + " s): hands up to " + hitFast.ToString("0.0") + " m/s, turning " + (hitSpin * Mathf.Rad2Deg).ToString("0") + " deg/s, body " + hitBody.ToString("0.0") + " m/s");
                hitFast = hitSpin = hitBody = 0f;
                hitClip = null;
                return false;
            }
            // (Straight on from the blow before, if that has only just ended: from where its hands were, not back to its guard and out again.)
            if (Time.time - hitPosedAt < 0.12f)
            {
                handAt = hitRightWas;
                leftAt = hitLeftWas;
                handTurn = hitRightTurnWas;
                leftTurn = hitLeftTurnWas;
            }
            Frame f = MotionLibrary.Sample(hitClip, since * pace);
            float size = f.height > 0.3f ? playHeight / f.height : 1f;
            Quaternion headTurn = Quaternion.Euler(playPitch, playYaw, 0f);
            Vector3 headAt = new Vector3(0f, playHeight, 0f);
            Vector3 theirHead = new Vector3(0f, f.height * size, 0f);
            // (Eased in and out at its ends, from and to its guard.)
            float k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(Mathf.Min(since, length - since) / 0.15f));
            handAt = Vector3.Lerp(handAt, headAt + headTurn * (f.rightPos * size - theirHead), k);
            // (With a thing in its hand: turned for the way that thing really sits in it, so it points as the player's did.)
            handTurn = Quaternion.Slerp(handTurn, play == Play.Melee ? headTurn * f.rightRot * ToolFix : headTurn * f.rightRot, k);
            leftAt = Vector3.Lerp(leftAt, headAt + headTurn * (f.leftPos * size - theirHead), k);
            leftTurn = Quaternion.Slerp(leftTurn, headTurn * f.leftRot, k);
            if (fist)
            {
                // The hand going out fast: its fist (which lies along the forearm, the hand's own
                // "up") turned to the way it is going, the more the faster it goes.
                float dt = Mathf.Max(Time.deltaTime, 0.001f);
                handTurn = Lined(handTurn, (handAt - hitRightBefore) / dt, fistOffRight);
                leftTurn = Lined(leftTurn, (leftAt - hitLeftBefore) / dt, fistOffLeft);
            }
            hitRightBefore = handAt;
            hitLeftBefore = leftAt;
            hitRightWas = handAt;
            hitLeftWas = leftAt;
            hitRightTurnWas = handTurn;
            hitLeftTurnWas = leftTurn;
            hitPosedAt = Time.time;
            return true;
        }

        // A fist's turn, lined up with the way it is going (and towards what it hits), by how fast it goes out.
        // How each hand really sits on the turn it is given (found with its fists up and still): a
        // fist is lined up by where the hand is, not where it was told to be.
        private Quaternion fistOffRight = Quaternion.identity, fistOffLeft = Quaternion.identity;

        private void MeasureFists()
        {
            try
            {
                Quaternion body = Quaternion.Euler(0f, bodyYaw, 0f);
                Hand right = RightHand, left = LeftHand;
                if (right != null && right.rb != null && right.rb.angularVelocity.magnitude < 2f)
                    fistOffRight = Quaternion.Slerp(fistOffRight, Quaternion.Inverse(body * handTurn) * right.transform.rotation, 0.1f);
                if (left != null && left.rb != null && left.rb.angularVelocity.magnitude < 2f)
                    fistOffLeft = Quaternion.Slerp(fistOffLeft, Quaternion.Inverse(body * leftTurn) * left.transform.rotation, 0.1f);
            }
            catch (Exception)
            {
            }
        }

        private Quaternion Lined(Quaternion turn, Vector3 going, Quaternion sits)
        {
            float fast = going.magnitude;
            // (Only on its way out, forward: not drawing back.)
            if (fast < Tuning.F("punch.line.from", 1.2f) || going.z < 0.3f)
                return turn;
            Vector3 way = going / fast;
            float much = Mathf.Clamp01((fast - Tuning.F("punch.line.from", 1.2f)) / 2.5f) * Tuning.F("punch.line", 0f);
            Quaternion hand = turn * sits;
            Quaternion lined = Quaternion.FromToRotation(hand * Vector3.up, way) * hand * Quaternion.Inverse(sits);
            return Quaternion.Slerp(turn, lined, much);
        }

        private Frame[] PickHit(MoveSlots.Kind kind)
        {
            Frame[] clip = MoveSlots.Pick(kind, hitLately, avatarCode);
            if (clip != null)
            {
                hitLately.Add(clip);
                if (hitLately.Count > 1)
                    hitLately.RemoveAt(0);
            }
            return clip;
        }

        private void Brawl(Transform head, float dt)
        {
            Targets.Target t = nearestSeen;
            if (t.transform == null || head == null)
            {
                walk = Vector2.zero;
                hitClip = null;
                return;
            }
            Vector3 point = t.Point;
            if (Chase(head, point, dt))
            {
                hitClip = null;
                return;
            }
            float far = CloseIn(head, point, 1.05f, dt);
            // (In the middle of a blow it stands its ground: stepping in and out with every punch had it darting about.)
            if (hitClip != null && far < 1.3f)
            {
                walk = Vector2.zero;
                running = false;
            }

            // Fists up.
            handOverride = true;
            leftOverride = true;
            handAt = new Vector3(0.17f, playHeight - 0.3f, 0.28f);
            handTurn = Quaternion.Euler(-10f, -10f, 0f);
            leftAt = new Vector3(-0.17f, playHeight - 0.28f, 0.3f);
            leftTurn = Quaternion.Euler(-10f, 10f, 0f);
            fists = true;

            // A punch, in reach: the player's own (as recorded in the Punch slots), from start to end.
            // (Between blows, its fists up and still: how its hands really sit is looked at.)
            if (hitClip == null && Time.time - punchAt > 0.6f)
                MeasureFists();
            float pace = Tuning.F("punch.speed", 1.2f);
            if (hitClip == null && far < 1.05f && Time.time >= nextPunchAt)
            {
                punchRight = !punchRight;
                hitClip = PickHit(MoveSlots.Kind.Punch);
                punchAt = Time.time;
                if (Time.time - punchSaidAt > 3f)
                {
                    punchSaidAt = Time.time;
                    Lab.Event("fists: punches" + (hitClip != null ? " as you do (" + hitClip[hitClip.Length - 1].time.ToString("0.0") + " s)" : " (a plain jab: no Punch slot recorded)"));
                }
                float takes = hitClip != null ? hitClip[hitClip.Length - 1].time / pace : 0.32f;
                // (A run of them: the next straight on from this one, till the run is done; then a breath.)
                if (comboLeft <= 0)
                    comboLeft = UnityEngine.Random.value < 0.3f ? 1 : UnityEngine.Random.value < 0.6f ? 2 : 3;
                comboLeft--;
                nextPunchAt = Time.time + takes + (comboLeft > 0 ? 0.05f : UnityEngine.Random.Range(0.7f, 1.5f));
            }
            float since = Time.time - punchAt;
            if (HitOfTheirs(since, pace, true, point))
                return;
            if (since < 0.32f)
            {
                // None recorded yet: straight out at it and back.
                float s = since < 0.12f ? since / 0.12f : Mathf.Clamp01(1f - (since - 0.12f) / 0.2f);
                Vector3 at = controls.vrRoot.InverseTransformPoint(point);
                if (punchRight)
                {
                    handAt = Vector3.Lerp(handAt, at, Mathf.SmoothStep(0f, 1f, s));
                    handTurn = Quaternion.Euler(-5f, 0f, -80f);
                }
                else
                {
                    leftAt = Vector3.Lerp(leftAt, at, Mathf.SmoothStep(0f, 1f, s));
                    leftTurn = Quaternion.Euler(-5f, 0f, 80f);
                }
            }
        }

        // ---------------------------------------------------------------- something to hit with

        private static readonly string[] meleeWords =
        {
            "knife", "dagger", "sword", "katana", "machete", "cleaver", "axe", "hatchet", "bat", "crowbar", "hammer", "sledge", "pipe", "wrench", "shovel",
            "spear", "club", "baton", "mace", "pickaxe", "kunai", "blade", "pan", "bottle", "golf", "hook", "shiv", "chainsaw",
        };

        /// <summary>Whether a thing is one to hit with: said to be by the game (its tags), with a blade on it, or by its name.</summary>
        private static bool IsMelee(Rigidbody rb)
        {
            try
            {
                Poolee poolee = rb.GetComponentInParent<Poolee>();
                if (poolee == null || poolee.SpawnableCrate == null)
                    return false;
                if (poolee.GetComponentInChildren<Gun>() != null || poolee.GetComponent<Magazine>() != null)
                    return false;
                var tags = poolee.SpawnableCrate.Tags;
                if (tags != null)
                {
                    foreach (string tag in tags)
                    {
                        string low = (tag ?? "").ToLowerInvariant();
                        if (low.Contains("melee") || low.Contains("blade") || low.Contains("blunt"))
                            return true;
                    }
                }
                if (poolee.GetComponentInChildren<StabSlash>() != null)
                    return true;
                string name = (poolee.gameObject.name + " " + poolee.SpawnableCrate.Barcode.ID).ToLowerInvariant();
                foreach (string word in meleeWords)
                {
                    if (name.Contains(word))
                        return true;
                }
            }
            catch (Exception)
            {
            }
            return false;
        }

        // The grip a thing to hit with is held by: one called a handle, if it has one; if not, the
        // one furthest from its heavy end (a pan's handle, not its rim; a hammer's shaft, not its head).
        private static Grip HandleOf(Rigidbody rb)
        {
            Il2CppSLZ.Marrow.Interaction.MarrowEntity entity = rb.GetComponentInParent<Il2CppSLZ.Marrow.Interaction.MarrowEntity>();
            Component root = entity != null ? entity : (Component)rb;
            Grip best = null;
            float most = float.MinValue;
            foreach (Grip g in root.GetComponentsInChildren<Grip>(false))
            {
                if (g == null || !g.enabled || g.IsInteractionDisabled || g.HasAttachedHands() || g.TryCast<WorldGrip>() != null)
                    continue;
                string name = g.name.ToLowerInvariant();
                float score = (g.transform.position - rb.worldCenterOfMass).magnitude;
                if (name.Contains("handle") || name.Contains("hilt") || name.Contains("grip_main") || name.Contains("primary"))
                    score += 10f;
                // (Not the striking end, if it has a grip of its own.)
                if (name.Contains("blade") || name.Contains("head") || name.Contains("rim") || name.Contains("pan"))
                    score -= 5f;
                if (score > most)
                {
                    most = score;
                    best = g;
                }
            }
            return best;
        }

        // The nearest thing to hit with that it can see and reach.
        private Rigidbody FindMelee(out Grip itsGrip, out float far)
        {
            itsGrip = null;
            far = float.MaxValue;
            Transform head = Head;
            if (head == null)
                return null;
            Vector3 feet = Feet;
            Rigidbody best = null;
            HashSet<IntPtr> seenBodies = new HashSet<IntPtr>();
            foreach (Collider c in Physics.OverlapSphere(feet + Vector3.up * 0.5f, Tuning.F("melee.seek", 22f), ~0, QueryTriggerInteraction.Ignore))
            {
                Rigidbody rb = c != null ? c.attachedRigidbody : null;
                if (rb == null || rb.isKinematic || !seenBodies.Add(rb.Pointer) || rb.mass < 0.25f || rb.mass > 14f || rb.GetComponentInParent<RigManager>() != null)
                    continue;
                float up = rb.worldCenterOfMass.y - feet.y;
                float d = (rb.worldCenterOfMass - feet).magnitude;
                if (up < -0.5f || up > 1.6f || d >= far || !IsMelee(rb))
                    continue;
                Grip g = HandleOf(rb);
                if (g == null || HeldByAnyone(rb.transform.root == rb.transform ? rb.transform : (rb.GetComponentInParent<Poolee>() != null ? rb.GetComponentInParent<Poolee>().transform : rb.transform), null))
                    continue;
                if (!Targets.InSight(head.position, rb.worldCenterOfMass, rb.transform, rig))
                    continue;
                best = rb;
                itsGrip = g;
                far = d;
            }
            return best;
        }

        /// <summary>
        /// Arms itself with the first weapon it sees: a gun lying about or something to hit
        /// with, whichever is nearer; a gun of its own if it may have one and neither is about.
        /// False if there is nothing (its fists, then).
        /// </summary>
        private bool GetAWeapon()
        {
            if (spawningGun)
                return GetAGun();
            Rigidbody hitter = FindMelee(out Grip hitterGrip, out float hitterFar);
            Gun lying = NearestGun(out float gunFar);
            // (A gun is the better of the two unless it is a good deal further off.)
            if (hitter != null && (lying == null || hitterFar < gunFar * 0.6f))
            {
                item = hitter;
                grip = hitterGrip;
                gun = null;
                support = null;
                meleeWanted = true;
                tinkering = false;
                play = Play.GoTo;
                playAt = Time.time;
                nav.SetGoal(item.worldCenterOfMass);
                Lab.Event("weapon: going for the " + ThingName(hitter) + " (" + hitterFar.ToString("0") + " m) to hit with");
                return true;
            }
            meleeWanted = false;
            return GetAGun();
        }

        /// <summary>For the lab: something to hit with (by its name in the game's list, or its barcode) laid on the ground a little way in front of it.</summary>
        public void LabLay(string which)
        {
            string barcode = which;
            if (!which.Contains("."))
            {
                barcode = null;
                List<string> names = new List<string>();
                foreach (System.Reflection.FieldInfo field in typeof(CommonBarcodes.Melee).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
                {
                    if (field.FieldType != typeof(string))
                        continue;
                    names.Add(field.Name);
                    if (string.Equals(field.Name, which, StringComparison.OrdinalIgnoreCase))
                        barcode = field.GetValue(null) as string;
                }
                if (barcode == null)
                {
                    Lab.Event("lay: no such thing; there are " + string.Join(", ", names));
                    return;
                }
            }
            Vector3 feet = Feet;
            Vector3 at = feet + Quaternion.Euler(0f, bodyYaw, 0f) * Vector3.forward * 3f + Vector3.up * 0.4f;
            Clone.Suppress();
            HelperMethods.SpawnCrate(barcode, at, Quaternion.identity, Vector3.one, false, null, null);
            Lab.Event("lay: a " + which + " put 3 m in front of it");
        }

        /// <summary>For the lab: puts its gun aside (not to be taken again for a while) and goes for the nearest thing to hit with.</summary>
        public void LabArm()
        {
            if (gun != null)
                wontLoad[gun.Pointer] = Time.time;
            if (ownGun != null)
                wontLoad[ownGun.Pointer] = Time.time;
            StopPlaying();
            ownGun = null;
            if (mode != Mode.Brain)
                SetMode(Mode.Brain);
            Rigidbody hitter = FindMelee(out Grip hitterGrip, out float far);
            if (hitter == null)
            {
                Lab.Event("weapon: nothing to hit with in sight");
                return;
            }
            item = hitter;
            grip = hitterGrip;
            gun = null;
            support = null;
            meleeWanted = true;
            play = Play.GoTo;
            playAt = Time.time;
            nav.SetGoal(item.worldCenterOfMass);
            Lab.Event("weapon: going for the " + ThingName(hitter) + " (" + far.ToString("0") + " m) to hit with");
        }

        // Its hand's turn (in its body's frame) for the thing in it to point that way, its forearm lying that way.
        // (A thing held in the fist stands out of the thumb side of it: that is the hand's own "back", as a
        // gun's top is; the hand's own "up" lies along the forearm, as a gun's barrel does.)
        private static Quaternion ToolTurn(Vector3 points, Vector3 forearm)
        {
            return Quaternion.LookRotation(-points.normalized, forearm.normalized);
        }

        // Which way the thing in its hand really stands out of it, in the hand's own frame (found by
        // looking: a handle can be taken either way round, and things sit differently in the hand),
        // and what that makes of a turn meant for a thing standing out of the thumb side of the fist.
        private Vector3 toolLocal = Vector3.back;
        private Quaternion toolTurnWas = Quaternion.identity;
        private bool toolTurnHas;

        private Quaternion ToolFix => Quaternion.FromToRotation(toolLocal, Vector3.back);

        // Its hand's turn for the thing to point that way (in its body's frame), by where the thing has been seen to point for the turn it had.
        private Quaternion Pointing(Vector3 points, Vector3 forearm)
        {
            try
            {
                if (toolTurnHas && item != null && grip != null && Holding() && item.angularVelocity.magnitude < 3f)
                {
                    Vector3 does = Quaternion.Inverse(Quaternion.Euler(0f, bodyYaw, 0f)) * (item.worldCenterOfMass - grip.transform.position);
                    if (does.sqrMagnitude > 0.0004f)
                    {
                        Vector3 inHand = Quaternion.Inverse(toolTurnWas) * does.normalized;
                        toolLocal = Vector3.Slerp(toolLocal, inHand, Tuning.F("melee.fix", 0.12f)).normalized;
                    }
                }
            }
            catch (Exception)
            {
            }
            toolTurnWas = ToolTurn(points, forearm) * ToolFix;
            toolTurnHas = true;
            return toolTurnWas;
        }

        // How far the thing in its hand reaches past the hand.
        private void MeasureMelee()
        {
            float length = 0.3f;
            try
            {
                Vector3 from = grip.transform.position;
                foreach (Collider c in item.GetComponentsInChildren<Collider>())
                {
                    if (c == null || c.isTrigger)
                        continue;
                    Bounds b = c.bounds;
                    length = Mathf.Max(length, Mathf.Max((b.min - from).magnitude, (b.max - from).magnitude));
                }
            }
            catch (Exception)
            {
            }
            meleeReach = 0.75f + Mathf.Clamp(length, 0.2f, 1.6f) * 0.8f;
            meleeSeenAt = Time.time;
            toolLocal = Vector3.back;
            toolTurnHas = false;
            hitClip = null;
            punchAt = -10f;
            Lab.Event("weapon: has the " + ThingName(item) + " to hit with, by its " + (grip != null ? grip.name : "?") + " (reaches " + meleeReach.ToString("0.0") + " m)");
        }

        // Once a frame with something to hit with in its hand: at the nearest enemy, striking as the player strikes (the Melee slots).
        private bool MeleeStep(Transform you, Transform head, float dt)
        {
            if (!Holding() || head == null)
                return EndPlay();
            if (mode != Mode.Brain && Time.time >= meleeSenseAt)
            {
                meleeSenseAt = Time.time + ThinkEvery;
                Sense();
            }
            Targets.Target t = nearestSeen;
            handOverride = true;
            leftOverride = false;
            running = false;
            if (t.transform == null || nearestSeenDistance > 30f)
            {
                // Nothing to hit: carried at its side a while, then put down.
                walk = Vector2.zero;
                playHeight = standingHeight;
                playPitch = 0f;
                handAt = new Vector3(0.24f, standingHeight - 0.72f, 0.12f);
                handTurn = Pointing(new Vector3(0f, -0.35f, 0.94f), new Vector3(0f, -0.94f, -0.35f));
                hitClip = null;
                if (Time.time - meleeSeenAt > Tuning.F("melee.keep", 12f))
                    Next(Play.Drop);
                return true;
            }
            meleeSeenAt = Time.time;
            Vector3 point = t.Point;
            if (Chase(head, point, dt))
            {
                // (On its way: the thing carried at its side.)
                handAt = new Vector3(0.24f, standingHeight - 0.72f, 0.12f);
                handTurn = Pointing(new Vector3(0f, -0.35f, 0.94f), new Vector3(0f, -0.94f, -0.35f));
                playHeight = standingHeight;
                playPitch = 0f;
                hitClip = null;
                return true;
            }
            float far = CloseIn(head, point, meleeReach, dt);

            // On guard: the thing up in front of it.
            handAt = new Vector3(0.2f, playHeight - 0.32f, 0.3f);
            // (Only while it is not in the middle of a blow is the way the thing points checked and put right.)
            handTurn = Time.time - punchAt > 0.7f && hitClip == null ? Pointing(new Vector3(-0.1f, 0.85f, 0.5f), new Vector3(0f, -0.5f, 0.85f))
                : ToolTurn(new Vector3(-0.1f, 0.85f, 0.5f), new Vector3(0f, -0.5f, 0.85f)) * ToolFix;
            leftOverride = true;
            leftAt = new Vector3(-0.17f, playHeight - 0.3f, 0.26f);
            leftTurn = Quaternion.Euler(-10f, 10f, 0f);

            if (hitClip == null && far < meleeReach && Time.time >= nextPunchAt)
            {
                hitClip = PickHit(MoveSlots.Kind.Melee);
                punchAt = Time.time;
                Lab.Event("weapon: strikes with the " + ThingName(item) + (hitClip != null ? " as you do (" + hitClip[hitClip.Length - 1].time.ToString("0.0") + " s)" : " (a plain chop: no Melee slot recorded)") + ", " + far.ToString("0.0") + " m off");
                nextPunchAt = Time.time + (hitClip != null ? hitClip[hitClip.Length - 1].time : 0.55f) + UnityEngine.Random.Range(0.25f, 0.7f);
            }
            float since = Time.time - punchAt;
            if (HitOfTheirs(since))
                return true;
            if (since < 0.55f)
            {
                // None recorded yet: up over its shoulder, and down onto it.
                float back = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(since / 0.2f));
                float down = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((since - 0.2f) / 0.14f));
                float home = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((since - 0.36f) / 0.19f));
                Vector3 guard = handAt, up = new Vector3(0.28f, playHeight + 0.12f, -0.05f);
                Vector3 hit = new Vector3(0.14f, playHeight - 0.45f, 0.58f);
                Vector3 at = Vector3.Lerp(Vector3.Lerp(guard, up, back), hit, down);
                Quaternion turn = Quaternion.Slerp(Quaternion.Slerp(handTurn, ToolTurn(new Vector3(0f, 0.6f, -0.8f), new Vector3(0f, 0.8f, 0.6f)) * ToolFix, back),
                    ToolTurn(new Vector3(0f, -0.25f, 0.97f), new Vector3(0f, -0.8f, 0.6f)) * ToolFix, down);
                handAt = Vector3.Lerp(at, guard, home);
                handTurn = Quaternion.Slerp(turn, handTurn, home);
            }
            return true;
        }
    }
}
