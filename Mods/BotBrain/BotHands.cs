using BoneLib;
using Il2CppSLZ.Marrow;
using Il2CppSLZ.Marrow.Interaction;
using Il2CppSLZ.Marrow.Utilities;
using UnityEngine;

namespace ThirdEyeBonelabMod.Bots
{
    /// <summary>
    /// The bot's hands: it goes over to something lying about, bends down and reaches for it,
    /// picks it up (the game's own grab, as a player's hand makes it), looks it over, and then
    /// throws it, tosses it to the player, or puts it down.
    /// </summary>
    public partial class Bot
    {
        private enum Play
        {
            None,
            GoTo,
            Reach,
            Hold,
            WindUp,
            Throw,
            Drop,
            Shoot,
            /// <summary>Trying what the thing in its hand does (BotCurious).</summary>
            Tinker,
            /// <summary>Fighting with the thing in its hand (BotFists).</summary>
            Melee,
        }

        private enum Then
        {
            Throw,
            TossToYou,
            Drop,
        }

        private const float SeekRadius = 10f;
        private const float MaxMass = 15f;

        private Play play;
        private Then then;
        private Rigidbody item;
        private Grip grip;
        private float playAt;
        private float holdFor;
        private Vector3 throwDir;
        private bool released;
        // Fists made (fighting with its hands).
        private bool fists;
        // Asked (by the lab) to toss the next thing it holds to the player.
        private bool tossNext;
        private bool forceToss;

        // What the hands' play puts over the walking and standing pose this frame.
        private bool handOverride;
        private Vector3 handAt;
        private Quaternion handTurn;
        private float playHeight;
        private float playPitch;
        private float playYaw;
        // And over the other hand (on a gun's front grip).
        private bool leftOverride;
        private Vector3 leftAt;
        private Quaternion leftTurn;

        public bool Playing => play != Play.None;

        private Hand RightHand => rig.physicsRig != null ? rig.physicsRig.rightHand : null;

        /// <summary>Looks for something to pick up near it; true if it is going for one.</summary>
        private bool FindSomethingToPlayWith()
        {
            Vector3 feet = Feet;
            Rigidbody best = null;
            Grip bestGrip = null;
            float bestScore = float.MaxValue;
            HashSet<IntPtr> seen = new HashSet<IntPtr>();
            foreach (Collider c in Physics.OverlapSphere(feet + Vector3.up * 0.5f, SeekRadius, ~0, QueryTriggerInteraction.Ignore))
            {
                Rigidbody rb = c != null ? c.attachedRigidbody : null;
                if (rb == null || rb.isKinematic || !seen.Add(rb.Pointer))
                    continue;
                if (rb.mass < 0.05f || rb.mass > MaxMass || rb.GetComponentInParent<RigManager>() != null)
                    continue;
                Grip g = GripOf(rb);
                if (g == null || NotForBots(rb))
                    continue;
                // Not up a wall or on a high shelf: something it can stand by and reach.
                float up = rb.worldCenterOfMass.y - feet.y;
                if (up < -0.5f || up > 1.6f)
                    continue;
                float score = (rb.worldCenterOfMass - feet).magnitude + UnityEngine.Random.value * 3f;
                if (score < bestScore)
                {
                    bestScore = score;
                    best = rb;
                    bestGrip = g;
                }
            }
            if (best == null)
                return false;
            item = best;
            grip = bestGrip;
            gun = null;
            // A gun is picked up by its handle, to shoot with.
            Gun found = best.GetComponentInParent<Gun>();
            if (found == null)
            {
                MarrowEntity entity = best.GetComponentInParent<MarrowEntity>();
                found = entity != null ? entity.GetComponentInChildren<Gun>() : null;
            }
            if (Usable(found))
                Take(found);
            play = Play.GoTo;
            playAt = Time.time;
            nav.SetGoal(item.worldCenterOfMass);
            return true;
        }

        // A grip of the thing that nobody holds and that can be held.
        private static Grip GripOf(Rigidbody rb)
        {
            MarrowEntity entity = rb.GetComponentInParent<MarrowEntity>();
            Component root = entity != null ? entity : (Component)rb;
            Grip best = null;
            float nearest = float.MaxValue;
            foreach (Grip g in root.GetComponentsInChildren<Grip>(false))
            {
                if (g == null || !g.enabled || g.IsInteractionDisabled || g.HasAttachedHands())
                    continue;
                if (g.TryCast<WorldGrip>() != null)
                    continue;
                float d = (g.transform.position - rb.worldCenterOfMass).sqrMagnitude;
                if (d < nearest)
                {
                    nearest = d;
                    best = g;
                }
            }
            return best;
        }

        /// <summary>Once a frame while it plays. False once it is done.</summary>
        private bool PlayStep(Transform you, Transform head, float dt)
        {
            handOverride = false;
            leftOverride = false;
            playYaw = 0f;
            if (item == null || grip == null)
                return EndPlay();
            float since = Time.time - playAt;
            switch (play)
            {
                case Play.GoTo:
                    if (grip.HasAttachedHands() || since > 20f)
                        return EndPlay();
                    nav.SetGoal(item.worldCenterOfMass);
                    if (Travel(MotionLibrary.WalkPush, 0.7f, dt) || nav.Remaining(Feet) < 0.7f)
                        Next(Play.Reach);
                    else if (nav.StuckCount >= 3)
                        return EndPlay();
                    return true;

                case Play.Reach:
                    walk = Vector2.zero;
                    // (The player's own way of picking a thing up, if they have recorded one: chosen as it begins to reach.)
                    if (pickFor != grip.Pointer || Time.time - pickChosenAt > 6f)
                        ChoosePickUp();
                    float takesAt = pickClip != null ? pickTakeAt : 0.35f;
                    if (grip.HasAttachedHands() || since > Mathf.Max(3f, takesAt + 1.8f))
                    {
                        Lab.Event("hands: gave up reaching for the " + item.name + (grip.HasAttachedHands() ? " (a hand is on it)" : " (never got to it in " + since.ToString("0.0") + " s)"));
                        // (Somebody else having it is not its failing.)
                        if (!grip.HasAttachedHands())
                            GrabFailed();
                        return EndPlay();
                    }
                    Vector3 target = grip.transform.position;
                    Face(target, dt, 240f);
                    if (pickClip != null)
                        ReachOfTheirs(target, since);
                    else
                        Reach(target, Mathf.Clamp01(since / 0.6f));
                    Hand hand = RightHand;
                    if (hand != null && since > takesAt && ((hand.transform.position - target).magnitude < 0.2f || since > takesAt + 0.85f))
                    {
                        // (A thing to hit with: by its handle the right way round, its heavy end out of the thumb side of the fist.)
                        Quaternion? turned = null;
                        if (meleeWanted && item != null)
                        {
                            Vector3 way = item.worldCenterOfMass - grip.transform.position;
                            if (way.sqrMagnitude > 0.0004f)
                                turned = Quaternion.FromToRotation(hand.transform.rotation * Vector3.back, way.normalized) * hand.transform.rotation;
                        }
                        if (Grab(hand, grip, turned))
                        {
                            if (gun != null)
                                grabFails.Remove(gun.Pointer);
                            holdFor = UnityEngine.Random.Range(2f, 5f);
                            if (gun != null)
                            {
                                StartShooting();
                            }
                            else if (meleeWanted)
                            {
                                meleeWanted = false;
                                MeasureMelee();
                                Next(Play.Melee);
                            }
                            else if (tinkering)
                                StartTrials();
                            else
                                Next(Play.Hold);
                        }
                        else
                        {
                            Lab.Event("hands: could not take the " + grip.name + " of the " + item.name + " (hand " + (hand.transform.position - target).magnitude.ToString("0.00") + " m from it, "
                                      + (hand.m_CurrentAttachedGO != null ? "holding " + hand.m_CurrentAttachedGO.name : "empty") + (grip.HasAttachedHands() ? ", a hand on it already" : "") + ", after " + since.ToString("0.0") + " s)");
                            if (!grip.HasAttachedHands())
                                GrabFailed();
                            return EndPlay();
                        }
                    }
                    return true;

                case Play.Hold:
                    walk = Vector2.zero;
                    if (!Holding())
                        return EndPlay();
                    // Holds it up in front and looks it over, turning it in its hand.
                    handOverride = true;
                    handAt = new Vector3(0.18f, standingHeight - 0.42f + Mathf.Sin(since * 1.3f) * 0.03f, 0.36f);
                    handTurn = Quaternion.Euler(20f + Mathf.Sin(since * 0.9f) * 25f, -20f, Mathf.Sin(since * 0.7f) * 40f);
                    playHeight = standingHeight;
                    playPitch = 32f;
                    if (since > holdFor)
                    {
                        float near = you != null && head != null ? Flat(you.position - head.position).magnitude : 99f;
                        float r = UnityEngine.Random.value;
                        then = forceToss && you != null ? Then.TossToYou : near < 9f && r < 0.35f ? Then.TossToYou : r < 0.75f ? Then.Throw : Then.Drop;
                        forceToss = false;
                        Vector3 forward = Quaternion.Euler(0f, bodyYaw + UnityEngine.Random.Range(-70f, 70f), 0f) * Vector3.forward;
                        throwFar = 0f;
                        // (A throw is at something: that is how it gets better at throwing.)
                        throwDir = then == Then.TossToYou && you != null ? Flat(you.position - head.position).normalized
                            : then == Then.Throw && head != null ? PickThrowTarget(head) : forward;
                        Next(then == Then.Drop ? Play.Drop : Play.WindUp);
                    }
                    return true;

                case Play.WindUp:
                    walk = Vector2.zero;
                    if (!Holding())
                        return EndPlay();
                    // Turns to where it will throw, and draws its arm back over its shoulder.
                    Face(head != null ? head.position + throwDir : Feet + throwDir, dt, 300f);
                    handOverride = true;
                    float w = Mathf.SmoothStep(0f, 1f, since / 0.45f);
                    handAt = Vector3.Lerp(new Vector3(0.18f, standingHeight - 0.42f, 0.36f), new Vector3(0.3f, standingHeight - 0.12f, -0.22f), w);
                    handTurn = Quaternion.Euler(-30f * w, 0f, 0f);
                    playHeight = standingHeight;
                    playPitch = then == Then.TossToYou ? LookPitch(you, head) : -5f;
                    if (since > 0.5f && Mathf.Abs(Mathf.DeltaAngle(bodyYaw, Recorder.Yaw(throwDir))) < 20f)
                    {
                        released = false;
                        Next(Play.Throw);
                    }
                    else if (since > 2f)
                    {
                        Next(Play.Drop);
                    }
                    return true;

                case Play.Throw:
                    walk = Vector2.zero;
                    // The arm comes over and through, letting go on the way.
                    handOverride = true;
                    float s = Mathf.Clamp01(since / 0.16f);
                    handAt = Vector3.Lerp(new Vector3(0.3f, standingHeight - 0.12f, -0.22f), new Vector3(0.24f, standingHeight - 0.3f, 0.62f), s);
                    handTurn = Quaternion.Euler(Mathf.Lerp(-30f, 40f, s), 0f, 0f);
                    playHeight = standingHeight;
                    playPitch = then == Then.TossToYou ? LookPitch(you, head) : -5f;
                    if (!released && s > 0.55f)
                    {
                        released = true;
                        Let(go: true, you);
                    }
                    if (since > 0.45f)
                        return EndPlay();
                    return true;

                case Play.Shoot:
                    return Shoot(you, head, dt);

                case Play.Tinker:
                    return TinkerStep(you, head, dt);

                case Play.Melee:
                    return MeleeStep(you, head, dt);

                case Play.Drop:
                    walk = Vector2.zero;
                    LetGoOfSupport();
                    Pull(false);
                    // Puts it down in front of itself.
                    handOverride = true;
                    float d = Mathf.SmoothStep(0f, 1f, since / 0.7f);
                    handAt = Vector3.Lerp(new Vector3(0.18f, standingHeight - 0.42f, 0.36f), new Vector3(0.15f, 0.35f, 0.45f), d);
                    handTurn = Quaternion.Euler(60f, 0f, 0f);
                    playHeight = Mathf.Lerp(standingHeight, Mathf.Max(0.95f, standingHeight * 0.62f), d);
                    playPitch = 45f;
                    if (since > 0.8f)
                    {
                        Let(go: false, you);
                        return EndPlay();
                    }
                    return true;
            }
            return EndPlay();
        }

        // A gun it reached for and could not get hold of: after a few goes (gun.grabfails, 4) it leaves that one alone
        // for a couple of minutes (as one that would not load), instead of standing over it reaching again and again.
        private static readonly Dictionary<IntPtr, int> grabFails = new Dictionary<IntPtr, int>();

        private void GrabFailed()
        {
            if (gun == null)
                return;
            IntPtr key = gun.Pointer;
            grabFails.TryGetValue(key, out int tries);
            tries++;
            if (tries >= Mathf.Max(1, Mathf.RoundToInt(Tuning.F("gun.grabfails", 4f))))
            {
                grabFails.Remove(key);
                wontLoad[key] = Time.time;
                Lab.Event("hands: gave up on the " + gun.name + " after " + tries + " tries at getting hold of it");
            }
            else
            {
                grabFails[key] = tries;
            }
        }

        private void Next(Play state)
        {
            play = state;
            playAt = Time.time;
        }

        private bool EndPlay()
        {
            meleeWanted = false;
            LetGoOfSupport();
            Pull(false);
            if (Reloading)
            {
                DropNewMag();
                reload = Reload.None;
                learnedReload = null;
            }
            if (Holding())
                Release();
            gun = null;
            support = null;
            tinkering = false;
            play = Play.None;
            item = null;
            grip = null;
            handOverride = false;
            nav.Clear();
            return false;
        }

        /// <summary>Stops whatever its hands are doing (and lets go).</summary>
        public void StopPlaying()
        {
            if (play != Play.None)
                EndPlay();
        }

        // Turns its body to face a point.
        private void Face(Vector3 point, float dt, float speed)
        {
            Transform head = Head;
            Vector3 to = Flat(point - (head != null ? head.position : Feet));
            if (to.sqrMagnitude > 0.0001f)
                bodyYaw = Mathf.MoveTowardsAngle(bodyYaw, Recorder.Yaw(to), speed * dt);
        }

        // Its hand out to a point in the world (k: how far out, 0 to 1), bending down for low ones.
        private void Reach(Vector3 target, float k)
        {
            Transform root = controls.vrRoot;
            Vector3 feet = Feet;
            float low = target.y - feet.y;
            playHeight = Mathf.Clamp(low + 0.85f, Mathf.Max(0.9f, standingHeight * 0.55f), standingHeight);
            // (Its frame stays on the floor as its head drops: heights in it are from the floor.)
            Vector3 local = root.InverseTransformPoint(target);
            Vector3 rest = new Vector3(0.2f, playHeight - 0.5f, 0.25f);
            handOverride = true;
            handAt = Vector3.Lerp(rest, local, Mathf.SmoothStep(0f, 1f, k));
            Vector3 shoulder = new Vector3(0.18f, playHeight - 0.25f, 0f);
            Vector3 along = handAt - shoulder;
            handTurn = along.sqrMagnitude > 0.0001f ? Quaternion.LookRotation(along.normalized, Vector3.up) : Quaternion.identity;
            playPitch = Mathf.Clamp(-Mathf.Atan2(low - playHeight, Mathf.Max(Flat(local).magnitude, 0.2f)) * Mathf.Rad2Deg, -20f, 70f);
        }

        // ---- picking a thing up as the player does (the Pick up slots, see MoveSlots)
        private Frame[] pickClip;
        private IntPtr pickFor;
        // Whether they did it with their left hand (it does it with its right: theirs, the other way round), when
        // in it their hand was on the thing, and how low their hand went.
        private bool pickMirror;
        private float pickTakeAt, pickLow, pickChosenAt = -10f;

        private void ChoosePickUp()
        {
            pickFor = grip.Pointer;
            pickChosenAt = Time.time;
            pickClip = MoveSlots.Pick(MoveSlots.Kind.PickUp, null, avatarCode);
            if (pickClip == null || pickClip.Length < 4)
            {
                pickClip = null;
                return;
            }
            // The hand that went down for it, and the moment it was lowest.
            float lowLeft = float.MaxValue, lowRight = float.MaxValue, atLeft = 0f, atRight = 0f;
            foreach (Frame f in pickClip)
            {
                if (f.leftPos.y < lowLeft)
                {
                    lowLeft = f.leftPos.y;
                    atLeft = f.time;
                }
                if (f.rightPos.y < lowRight)
                {
                    lowRight = f.rightPos.y;
                    atRight = f.time;
                }
            }
            pickMirror = lowLeft < lowRight - 0.05f;
            pickTakeAt = Mathf.Max(0.25f, pickMirror ? atLeft : atRight);
            pickLow = pickMirror ? lowLeft : lowRight;
            Lab.Event("picks it up as you do (" + pickClip[pickClip.Length - 1].time.ToString("0.0") + " s, hand on it at " + pickTakeAt.ToString("0.0") + " s" + (pickMirror ? ", your left hand's, mirrored" : "") + ")");
        }

        private static Vector3 Across(Vector3 p) => new Vector3(-p.x, p.y, p.z);
        private static Quaternion Across(Quaternion q) => new Quaternion(q.x, -q.y, -q.z, q.w);

        // Down to the thing as the player goes down: their head and both hands, sized to it, the
        // reaching hand drawn onto the thing itself by the moment theirs was on theirs; as far
        // down as the thing is low (a thing on a table is not knelt for).
        private void ReachOfTheirs(Vector3 target, float since)
        {
            Transform root = controls.vrRoot;
            float length = pickClip[pickClip.Length - 1].time;
            Frame f = MotionLibrary.Sample(pickClip, Mathf.Min(since, Mathf.Min(length, pickTakeAt + 0.1f)));
            Frame first = pickClip[0];
            float size = first.height > 0.3f ? standingHeight / first.height : 1f;
            Vector3 theirRight = pickMirror ? Across(f.leftPos) : f.rightPos, theirLeft = pickMirror ? Across(f.rightPos) : f.leftPos;
            Quaternion theirRightTurn = pickMirror ? Across(f.leftRot) : f.rightRot, theirLeftTurn = pickMirror ? Across(f.rightRot) : f.leftRot;
            // How far down: theirs, by how low its thing is against how low theirs was.
            float low = target.y - Feet.y;
            float hangs = standingHeight - 0.62f;
            float deep = Mathf.Clamp((hangs - low) / Mathf.Max(0.15f, hangs - pickLow * size), 0f, 1.25f);
            float dropped = Mathf.Max(0f, first.height - f.height) * size * deep;
            float onto0 = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(since / pickTakeAt));
            playHeight = Mathf.Clamp(standingHeight - dropped, Mathf.Max(0.75f, standingHeight * 0.45f), standingHeight);
            // (Down far enough for its hand to get there, however little the player bent for theirs: one
            // recorded picking a thing off a table left it standing over a thing on the ground, reaching at the air.)
            float needs = Mathf.Clamp(low + 0.85f, Mathf.Max(0.9f, standingHeight * 0.55f), standingHeight);
            playHeight = Mathf.Min(playHeight, Mathf.Lerp(standingHeight, needs, onto0));
            Vector3 headAt = new Vector3(0f, playHeight, 0f);
            Vector3 theirHead = new Vector3(0f, f.height * size, 0f);
            Vector3 local = root.InverseTransformPoint(target);
            float onto = onto0 * onto0;
            handOverride = true;
            handAt = Vector3.Lerp(headAt + (theirRight * size - theirHead), local, onto);
            handTurn = theirRightTurn;
            leftOverride = true;
            leftAt = headAt + (theirLeft * size - theirHead);
            leftTurn = theirLeftTurn;
            Vector3 looks = f.head * Vector3.forward;
            playPitch = Mathf.Clamp(-Mathf.Asin(Mathf.Clamp(looks.y, -1f, 1f)) * Mathf.Rad2Deg, -20f, 70f);
        }

        // The game's own grab, as a player's hand makes it on what it hovers over.
        private static bool Grab(Hand hand, Grip grip) => Grab(hand, grip, null);

        // (With a turn given: taken as a hand turned so would take it. A handle can be taken either way round; this says which.)
        private static bool Grab(Hand hand, Grip grip, Quaternion? turned)
        {
            try
            {
                if (hand.m_CurrentAttachedGO != null)
                    return false;
                hand.GrabLock = false;
                InventoryHand inventory = hand.GetComponent<InventoryHand>();
                if (inventory != null)
                    inventory.IgnoreUnlock();
                hand.HoverLock();
                SimpleTransform at = SimpleTransform.Create(hand.transform);
                if (turned.HasValue)
                    at.rotation = turned.Value;
                grip.ValidateGripScore(hand, at);
                grip.OnHandHoverBegin(hand, true);
                grip.ValidateGripScore(hand, at);
                hand._mHoveringReceiver = grip;
                grip.OnGrabConfirm(hand, false);
                return hand.m_CurrentAttachedGO != null;
            }
            catch (Exception e)
            {
                // (Something the game does on a grab can fail for a bot, a magazine adding to an
                // ammo pouch it has not got, say, with the grab itself made all the same.)
                bool held = false;
                try
                {
                    held = hand.m_CurrentAttachedGO != null && grip.attachedHands != null && grip.attachedHands.Contains(hand);
                }
                catch (Exception)
                {
                }
                if (!held)
                    MelonLoader.MelonLogger.Warning("Bot Player: could not grab " + grip.name + ": " + e.Message);
                return held;
            }
        }

        private bool Holding()
        {
            Hand hand = RightHand;
            return hand != null && grip != null && hand.m_CurrentAttachedGO != null && grip.attachedHands != null && grip.attachedHands.Contains(hand);
        }

        private void Release()
        {
            Hand hand = RightHand;
            try
            {
                if (hand != null && grip != null)
                    grip.ForceDetach(hand);
            }
            catch (Exception)
            {
            }
        }

        // Lets go: thrown (sent off at a throw's speed, or in an arc to the player), or just put down.
        private void Let(bool go, Transform you)
        {
            Rigidbody thing = item;
            Hand hand = RightHand;
            Vector3 handVelocity = hand != null && hand.rb != null ? hand.rb.velocity : Vector3.zero;
            Release();
            if (thing == null || !go)
                return;
            Vector3 velocity;
            if (then == Then.TossToYou && you != null)
                velocity = Lob(thing.worldCenterOfMass, you.position - Vector3.up * 0.35f, 0.75f);
            else if (throwFar > 0f)
                velocity = ThrowVelocity(thing);
            else
                velocity = throwDir * UnityEngine.Random.Range(7f, 11f) + Vector3.up * UnityEngine.Random.Range(2f, 4f);
            // (Light things fly from the hand as they would; it only helps the throw along. A
            // throw at something goes just as it meant it to: that is what it is learning.)
            thing.velocity = flying != thing && Vector3.Dot(handVelocity, velocity.normalized) > velocity.magnitude ? handVelocity : velocity;
            thing.angularVelocity = UnityEngine.Random.insideUnitSphere * 4f;
        }

        // The speed that carries a thing from a to b in time t, under gravity.
        private static Vector3 Lob(Vector3 a, Vector3 b, float t)
        {
            Vector3 g = Physics.gravity;
            return (b - a) / t - 0.5f * g * t;
        }

        /// <summary>Puts the hands' pose over the walking and standing pose.</summary>
        private void OverHands()
        {
            if (!handOverride)
                return;
            pose.height = playHeight;
            pose.rightPos = handAt;
            pose.rightRot = handTurn;
            pose.head = Quaternion.Euler(playPitch, playYaw, 0f);
            if (leftOverride)
            {
                pose.leftPos = leftAt;
                pose.leftRot = leftTurn;
            }
            else
            {
                // The other hand in close, out of the way.
                pose.leftPos = new Vector3(-0.2f, playHeight - 0.62f, 0.12f);
                pose.leftRot = Quaternion.Euler(60f, 10f, 0f);
            }
            curl = Holding() || fists ? 1f : 0.3f;
        }
    }
}
