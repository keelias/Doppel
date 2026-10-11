using Il2CppSLZ.Marrow.Utilities;
using Il2CppSLZ.Marrow;
using UnityEngine;

namespace ThirdEyeBonelabMod.Bots
{
    /// <summary>
    /// Climbing up onto high ground, as the player does. When where it is going is up a wall
    /// too high to jump (or it is stuck against one), with room on top, it walks up to the
    /// wall and plays one of the player's own climbs (ClimbMoves): its head and hands where
    /// theirs were from the edge, its hands taking hold of the world (the game's world grip,
    /// as the player's do) when theirs did. The game's own climbing answers the player's real
    /// arms, not the bot's, and did not lift it: so its body is lifted over the edge here,
    /// along the path the player's head took in their climb (their timing, their way of going
    /// up and then over), scaled to the wall it is on. Until the player has climbed for it, a
    /// plain climb of its own: reach, take hold, pull, push up.
    /// </summary>
    public partial class Bot
    {
        private bool climbing;
        private int climbPhase;
        private Vector3 climbEdge;
        private Vector3 climbIn;
        private float climbHeight;
        private Quaternion climbFacing;
        private ClimbMoves.Climb climbMove;
        private float climbT;
        // Reaching up to where the player's hands took hold (their climbs start at the grab),
        // and waiting on its hands to take hold before the climb goes on.
        private float climbReach;
        private float climbWait;
        private Pose climbStartHead, climbStartLeft, climbStartRight;
        // Its body lifted over the edge: from where, and its parts (all but its hands, which hold).
        private bool climbLifting;
        private Vector3 climbLiftFrom;
        // Moved along the climb's path a little each physics step (its own body would not be
        // lifted: it keeps itself standing where its legs are): where its feet are on the path.
        private Vector3 climbFeetFrom;
        private Vector3 climbGlide;
        private bool climbGliding;
        // Its legs while it rises: not walking (the walking under its feet off, as the game does
        // for the player while their hands hold the world), tucked up as a climber's are.
        private bool climbLegsUp;
        private float climbGlideSince;
        private readonly List<Rigidbody> climbBodies = new List<Rigidbody>();
        // Tries at the same wall (it gives up on it a while after a few).
        private Vector3 climbLastEdge;
        private float climbLiftSaidAt;
        private float climbUpAt = -100f;
        private int climbTries;
        private float climbSince;
        private float climbLookAt;
        private Frame climbFrom;
        private readonly bool[] climbHolding = new bool[2];
        // Where it wants its head and hands this moment (in the world), and which hands holding.
        private Pose climbHead, climbLeft, climbRight;
        private bool climbWantLeft, climbWantRight;

        /// <summary>Whether to climb, looked at now and then as it travels: true once it has started.</summary>
        private bool TryStartClimb(Vector3 dir)
        {
            if (climbing || Time.time < climbLookAt || Playing)
                return false;
            climbLookAt = Time.time + 0.4f;
            PhysicsRig body = rig.physicsRig;
            Vector3 feet = body != null && body.centerOfPressure != null ? body.centerOfPressure.position : Feet;
            bool upThere = nav.HasGoal && nav.Goal.y - feet.y > 0.8f;
            if (!upThere && nav.StuckCount < 2)
                return false;
            Vector3 toGoal = nav.HasGoal ? Flat(nav.Goal - feet) : Vector3.zero;
            bool found = FindEdge(feet, dir, out Vector3 edge, out Vector3 inward, out float height);
            if (!found && upThere && toGoal.magnitude > 0.3f && toGoal.magnitude < 10f)
                found = FindEdge(feet, toGoal.normalized, out edge, out inward, out height);
            // Too high for its hands, and it has to get up there: with what the player climbed with, if anything.
            if ((!found || height > Tuning.F("climb.max", 2.5f)) && upThere
                && FindTallWall(feet, toGoal.sqrMagnitude > 0.09f ? toGoal.normalized : dir, 1.8f, out Vector3 foot, out Vector3 into, out float tall, out _))
            {
                if (ClimbWithTool(foot, into, tall))
                    return true;
            }
            if (!found)
                return false;
            if (height < Tuning.F("climb.min", 1.2f) || height > Tuning.F("climb.max", 2.5f))
                return false;
            // (Its hands are wanted for it: not while it holds a gun or anything else.)
            if ((LeftHand != null && LeftHand.m_CurrentAttachedGO != null) || (RightHand != null && RightHand.m_CurrentAttachedGO != null))
            {
                // (A weapon is put away for it; anything else in its hands, it does not climb with.)
                if (!WeaponInHand || !Stow())
                    return false;
            }
            if ((edge - climbLastEdge).magnitude < 1.5f || Time.time - climbUpAt < 10f)
            {
                if (++climbTries > 3)
                {
                    climbTries = 0;
                    climbLookAt = Time.time + 20f;
                    Lab.Event("climb: gave up on that wall for a while");
                    return false;
                }
            }
            else
            {
                climbTries = 1;
            }
            // (A wall it has failed at before, more than once: mostly left alone.)
            if (SelfTaught.ClimbFails(edge) >= 2 && UnityEngine.Random.value > 0.15f)
            {
                climbLookAt = Time.time + 6f;
                return false;
            }
            if (SomeoneClimbingAt(edge, Tuning.F("climb.apart", 1.8f)))
            {
                climbLookAt = Time.time + 1f;
                return false;
            }
            climbLastEdge = edge;
            climbing = true;
            climbLifting = false;
            climbPhase = 0;
            climbEdge = edge;
            climbIn = inward;
            climbHeight = height;
            climbFacing = Quaternion.LookRotation(inward, Vector3.up);
            climbSince = Time.time;
            climbMove = ClimbMoves.Pick(height);
            Lab.Event("climb: up a " + height.ToString("0.00") + " m wall (" + (climbMove != null ? "the player's way" : "its own way") + ")");
            return true;
        }

        /// <summary>Whether this one is going up a wall near that place now.</summary>
        public bool ClimbingNear(Vector3 at, float within)
        {
            if (climbing && Flat(climbEdge - at).magnitude < within)
                return true;
            return InLesson && cloneLesson != null && cloneLesson.purpose == "climb" && lessonPurposeful && Flat(lessonWallFoot - at).magnitude < within;
        }

        private bool SomeoneClimbingAt(Vector3 at, float within)
        {
            foreach (Bot other in BotManager.All)
            {
                if (other != this && !other.dead && other.ClimbingNear(at, within))
                    return true;
            }
            return false;
        }

        // A wall just ahead, its top edge, and room to stand on top.
        private bool FindEdge(Vector3 feet, Vector3 dir, out Vector3 edge, out Vector3 inward, out float height)
        {
            edge = inward = Vector3.zero;
            height = 0f;
            if (!Physics.Raycast(feet + Vector3.up * 0.6f, dir, out RaycastHit wall, 1.6f, ~0, QueryTriggerInteraction.Ignore))
                return false;
            if (wall.collider.GetComponentInParent<RigManager>() != null || wall.collider.attachedRigidbody != null)
                return false;
            inward = -Flat(wall.normal);
            if (inward.sqrMagnitude < 0.01f)
                return false;
            inward.Normalize();
            if (!Physics.Raycast(wall.point + inward * 0.3f + Vector3.up * 3.3f, Vector3.down, out RaycastHit top, 3.6f, ~0, QueryTriggerInteraction.Ignore))
                return false;
            if (top.normal.y < 0.7f || top.point.y < feet.y + 0.5f)
                return false;
            if (Physics.Raycast(top.point + Vector3.up * 0.05f, Vector3.up, 1.5f, ~0, QueryTriggerInteraction.Ignore))
                return false;
            height = top.point.y - feet.y;
            edge = new Vector3(wall.point.x, top.point.y, wall.point.z) + inward * 0.04f;
            return true;
        }

        /// <summary>Each frame while climbing (in place of travelling): up to the wall, the climb, and on over the top.</summary>
        private void ClimbStep(float dt)
        {
            if (Time.time - climbSince > 16f)
            {
                StopClimb("took too long");
                return;
            }
            switch (climbPhase)
            {
                case 0:
                    // Up to the wall, facing it.
                    Vector3 feet = rig.physicsRig != null && rig.physicsRig.centerOfPressure != null ? rig.physicsRig.centerOfPressure.position : Feet;
                    Vector3 to = Flat(climbEdge - feet);
                    if (to.magnitude > Tuning.F("climb.near", 0.45f) && Time.time - climbSince < 4f)
                    {
                        Go(to.normalized, MotionLibrary.WalkPush * 0.85f, dt);
                        return;
                    }
                    walk = Vector2.zero;
                    bodyYaw = Recorder.Yaw(climbIn);
                    climbPhase = 1;
                    climbT = 0f;
                    climbReach = 0f;
                    climbWait = 0f;
                    climbFrom = pose;
                    climbStartHead = Head != null ? Pose.Of(Head) : new Pose(climbEdge, climbFacing);
                    climbStartLeft = LeftHand != null ? Pose.Of(LeftHand.transform) : new Pose(climbEdge, climbFacing);
                    climbStartRight = RightHand != null ? Pose.Of(RightHand.transform) : new Pose(climbEdge, climbFacing);
                    break;
                case 1:
                    walk = Vector2.zero;
                    bodyYaw = Mathf.MoveTowardsAngle(bodyYaw, Recorder.Yaw(climbIn), 180f * dt);
                    float length = climbMove != null ? climbMove.Length : 2.6f;
                    // First its hands up to where the climb takes hold (from where they were).
                    float reach = Tuning.F("climb.reach", 0.5f);
                    if (climbReach < reach)
                    {
                        climbReach += dt;
                        ClimbAt(0f);
                        float k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(climbReach / reach));
                        climbHead = Pose.Lerp(climbStartHead, climbHead, k);
                        climbLeft = Pose.Lerp(climbStartLeft, climbLeft, k);
                        climbRight = Pose.Lerp(climbStartRight, climbRight, k);
                        bool late = k > 0.8f;
                        HoldWorld(0, late && climbWantLeft, climbLeft.position);
                        HoldWorld(1, late && climbWantRight, climbRight.position);
                        break;
                    }
                    ClimbAt(climbT);
                    HoldWorld(0, climbWantLeft && !climbGliding, climbLeft.position);
                    HoldWorld(1, climbWantRight && !climbGliding, climbRight.position);
                    // On only once its hands hold where the climb has them holding.
                    // (A moment only, and only for a hand that is nearly there: one that cannot
                    // reach the edge, on a wall higher than the player's was, is not waited on.)
                    bool waiting = !climbGliding && (HandNearlyThere(0, climbWantLeft, climbLeft.position) || HandNearlyThere(1, climbWantRight, climbRight.position));
                    if (waiting)
                    {
                        climbWait += dt;
                        if (climbWait > Tuning.F("climb.wait", 0.3f))
                            climbT += dt * Tuning.F("climb.speed", 0.7f);
                    }
                    else
                    {
                        climbWait = 0f;
                        climbT += dt * Tuning.F("climb.speed", 0.7f);
                    }
                    if (climbT >= length)
                    {
                        HoldWorld(0, false, Vector3.zero);
                        HoldWorld(1, false, Vector3.zero);
                        climbPhase = 2;
                        climbT = 0f;
                        LegsUp(false);
                        // (On its feet on the top, as it stands now: its own pose from here.)
                        Transform headNow = Head;
                        if (headNow != null)
                            bodyYaw = Recorder.Yaw(climbIn);
                    }
                    break;
                case 2:
                    // On over the top.
                    climbT += dt;
                    Go(climbIn, MotionLibrary.WalkPush * 0.7f, dt);
                    if (climbT > 0.6f)
                    {
                        PhysicsRig body = rig.physicsRig;
                        float up = body != null && body.centerOfPressure != null ? body.centerOfPressure.position.y - (climbEdge.y - climbHeight) : 0f;
                        StopClimb(up > climbHeight * 0.6f ? "up" : "did not get up");
                    }
                    break;
            }
        }

        /// <summary>Each physics step while it climbs: its body drawn up and over the edge, the way the player went.</summary>
        public void ClimbFixed(float dt)
        {
            // (Begun hanging on a wall: kept where it hangs till it is drawn up.)
            if (climbing && climbHang && climbPhase == 1 && !climbGliding && rig.physicsRig != null && !Bad(climbHangFeet))
                rig.Teleport(climbHangFeet, true);
            if (!climbing || climbPhase != 1 || climbReach < Tuning.F("climb.reach", 0.5f))
                return;
            PhysicsRig body = rig.physicsRig;
            Rigidbody pelvis = body != null && body.m_pelvis != null ? body.m_pelvis.GetComponent<Rigidbody>() : null;
            if (pelvis == null)
                return;
            if (!climbLifting)
            {
                climbLifting = true;
                climbGliding = false;
                climbLiftFrom = pelvis.position;
                climbFeetFrom = climbHang ? climbHangFeet : body.centerOfPressure != null ? body.centerOfPressure.position : pelvis.position - Vector3.up * 0.95f;
                climbGlide = climbFeetFrom;
                climbBodies.Clear();
                Rigidbody lh = LeftHand != null ? LeftHand.rb : null;
                Rigidbody rh = RightHand != null ? RightHand.rb : null;
                foreach (Rigidbody b in body.GetComponentsInChildren<Rigidbody>())
                {
                    if (b != null && !b.isKinematic && b != lh && b != rh)
                        climbBodies.Add(b);
                }
            }
            LiftShape(climbT, out float up, out float over);
            float rise = climbHeight + Tuning.F("climb.extra", 0.12f);
            float across = Flat(climbEdge - climbLiftFrom).magnitude + Tuning.F("climb.onto", 0.45f);
            Vector3 want = climbLiftFrom + Vector3.up * (up * rise) + climbIn * (over * across);
            if (Bad(want) || Bad(pelvis.position))
            {
                StopClimb("its body went wild");
                return;
            }
            if (Time.time - climbLiftSaidAt > 0.5f)
            {
                climbLiftSaidAt = Time.time;
                Lab.Event("climb lift: hips " + (pelvis.position.y - climbLiftFrom.y).ToString("0.00") + " m up of " + (up * rise).ToString("0.00")
                          + " wanted, " + Flat(pelvis.position - climbLiftFrom).magnitude.ToString("0.00") + " m over of " + (over * across).ToString("0.00")
                          + ", hands holding " + climbHolding[0] + "/" + climbHolding[1] + ", from the wall " + Flat(climbEdge - pelvis.position).magnitude.ToString("0.00") + " m");
            }
            if (Tuning.F("climb.glide", 1f) > 0.5f)
            {
                // Its whole body moved along the path, a little each step (its hands, posed on
                // the edge, let go as it starts up: held to the world they would be torn about).
                Vector3 feetWant = climbFeetFrom + Vector3.up * (up * rise) + climbIn * (over * (Flat(climbEdge - climbFeetFrom).magnitude + Tuning.F("climb.onto", 0.45f)));
                if (!climbGliding && up > 0.02f)
                {
                    climbGliding = true;
                    Call("heave");
                    climbGlideSince = Time.time;
                    HoldWorld(0, false, Vector3.zero);
                    HoldWorld(1, false, Vector3.zero);
                }
                // Its legs bent a little as it rises (its own crouch; the walking under its feet is
                // left on: switched off it fell over, switched back on in the air it was thrown off).
                if (climbGliding)
                {
                    // (Only once it is well off the ground, and out again before it stands on the top:
                    // bent from the start, it squatted down before it went up.)
                    float tuck = Mathf.Clamp01((up - 0.25f) / 0.25f) * (1f - Mathf.Clamp01((over - 0.3f) / 0.4f));
                    rig.remapHeptaRig._crouchTarget = Tuning.F("climb.tuck", -0.25f) * tuck;
                }
                climbGlide = Vector3.MoveTowards(climbGlide, feetWant, Tuning.F("climb.glide.step", 0.03f));
                if (climbGliding)
                    rig.Teleport(climbGlide, true);
                return;
            }
            Vector3 velocity = (want - pelvis.position) * Tuning.F("climb.pull", 8f);
            Vector3 change = Vector3.ClampMagnitude(velocity - pelvis.velocity, Tuning.F("climb.step", 0.35f));
            foreach (Rigidbody b in climbBodies)
            {
                if (b != null)
                    b.AddForce(change, ForceMode.VelocityChange);
            }
        }

        // How far up (0 to 1) and how far over the edge (0 to 1) the climb has it at that moment.
        // Up first, by the player's own timing (their head, from where it started to as high
        // as it got), never coming down again; and over the edge only once it is all the way
        // up. (Their head dipped on its way: followed down, and over the edge before it was up,
        // the bot's feet went into the ledge and it was thrown off.)
        private void LiftShape(float t, out float up, out float over)
        {
            if (climbMove == null || climbMove.frames.Count < 2)
            {
                up = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((t - 0.2f) / 1.3f));
                over = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((t - 1.5f) / 0.9f));
                return;
            }
            List<ClimbMoves.Frame> frames = climbMove.frames;
            float length = climbMove.Length;
            float first = ClimbMoves.Read(frames[0].head).position.y;
            float peak = first, upTo = first;
            float topAt = length;
            foreach (ClimbMoves.Frame f in frames)
                peak = Mathf.Max(peak, ClimbMoves.Read(f.head).position.y);
            foreach (ClimbMoves.Frame f in frames)
            {
                float y = ClimbMoves.Read(f.head).position.y;
                if (f.t <= t)
                    upTo = Mathf.Max(upTo, y);
                if (y >= first + (peak - first) * 0.92f)
                {
                    topAt = Mathf.Min(topAt, f.t);
                }
            }
            // (Time left to go over in, however late they got to the top; and not all at once at the start.)
            topAt = Mathf.Clamp(topAt, Mathf.Min(0.6f, length * 0.4f), Mathf.Max(0.6f, length - Tuning.F("climb.over.time", 0.7f)));
            float theirs = peak - first > 0.1f ? Mathf.Clamp01((upTo - first) / ((peak - first) * 0.92f)) : 0f;
            up = t >= topAt ? 1f : Mathf.Max(theirs * Mathf.Clamp01(t / topAt + 0.35f), Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t / topAt)) * 0.999f);
            up = Mathf.Clamp01(up);
            // (Over as it comes to the top, not after hanging in the air beside it: begun a
            // little before it is all the way up, once its feet are clear of the edge.)
            float lead = topAt * Tuning.F("climb.over.lead", 0.22f);
            over = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((t - (topAt - lead)) / Mathf.Max(0.2f, length - topAt + lead)));
            over = Mathf.Min(over, Mathf.Clamp01((up - 0.9f) / 0.08f));
        }

        // A hand that is to hold, does not yet, and is close enough to its spot that it soon may.
        private bool HandNearlyThere(int which, bool want, Vector3 target)
        {
            Hand hand = which == 0 ? LeftHand : RightHand;
            return want && !climbHolding[which] && hand != null && (hand.transform.position - target).magnitude < Tuning.F("climb.close", 0.45f);
        }

        /// <summary>For the lab: up onto the nearest high ground it can climb, now.</summary>
        public void LabClimb()
        {
            if (mode != Mode.Wander)
                SetMode(Mode.Wander);
            StopPlaying();
            Vector3 feet = rig.physicsRig != null && rig.physicsRig.centerOfPressure != null ? rig.physicsRig.centerOfPressure.position : Feet;
            climbLookAt = 0f;
            climbTries = 0;
            if (FindHighGround(feet, out Vector3 top))
            {
                nav.Clear();
                nav.SetGoal(top);
                act = Act.Climb;
                actUntil = Time.time + 40f;
                Lab.Event("climb: sent up to high ground " + Flat(top - feet).magnitude.ToString("0.0") + " m off, " + (top.y - feet.y).ToString("0.00") + " m up");
            }
            else
            {
                Lab.Event("climb: no wall it can climb within 15 m");
            }
        }

        // Its crouch let go again after the rise.
        private void LegsUp(bool up)
        {
            if (!up)
                rig.remapHeptaRig._crouchTarget = 0f;
        }

        // The climb at that moment: the player's, or its own.
        private void ClimbAt(float t)
        {
            if (climbMove != null)
                FromPlayer(t);
            else
                OwnClimb(t);
        }

        // Begun not from the ground but hanging on a wall (the end of a climb by its hands), and where its feet hang.
        private bool climbHang;
        private Vector3 climbHangFeet;

        private void StopClimb(string how)
        {
            climbHang = false;
            LegsUp(false);
            climbLifting = false;
            climbGliding = false;
            climbBodies.Clear();
            if (how == "up")
            {
                climbUpAt = Time.time;
                SelfTaught.Top(climbEdge);
            }
            else if (how == "took too long" || how == "did not get up" || how == "its body went wild")
            {
                SelfTaught.ClimbFailed(climbEdge);
            }
            HoldWorld(0, false, Vector3.zero);
            HoldWorld(1, false, Vector3.zero);
            climbing = false;
            walk = Vector2.zero;
            climbLookAt = Time.time + (how == "up" ? 3f : 1.5f);
            nav.Clear();
            Lab.Event("climb: " + how);
        }

        // The player's climb at that moment, put on its own wall (theirs and its edges laid one on the other).
        private void FromPlayer(float t)
        {
            List<ClimbMoves.Frame> frames = climbMove.frames;
            int i = 0;
            while (i < frames.Count - 2 && frames[i + 1].t < t)
                i++;
            ClimbMoves.Frame a = frames[i];
            ClimbMoves.Frame b = frames[Mathf.Min(i + 1, frames.Count - 1)];
            float k = b.t > a.t ? Mathf.Clamp01((t - a.t) / (b.t - a.t)) : 0f;
            Pose edge = new Pose(climbEdge, climbFacing);
            climbHead = edge * Pose.Lerp(ClimbMoves.Read(a.head), ClimbMoves.Read(b.head), k);
            climbLeft = edge * Pose.Lerp(ClimbMoves.Read(a.left), ClimbMoves.Read(b.left), k);
            climbRight = edge * Pose.Lerp(ClimbMoves.Read(a.right), ClimbMoves.Read(b.right), k);
            climbWantLeft = k < 0.5f ? a.lh : b.lh;
            climbWantRight = k < 0.5f ? a.rh : b.rh;
        }

        // A plain climb of its own: hands up onto the edge, take hold, pull down to the chest
        // (the body comes up), then push down on the top to get over it.
        private void OwnClimb(float t)
        {
            Vector3 side = Vector3.Cross(Vector3.up, climbIn);
            Vector3 l = climbEdge - side * 0.22f + Vector3.up * 0.03f;
            Vector3 r = climbEdge + side * 0.22f + Vector3.up * 0.03f;
            Quaternion palmsDown = climbFacing * Quaternion.Euler(70f, 0f, 0f);
            float reach = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t / 0.6f));
            float pull = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((t - 0.6f) / 1.2f));
            float push = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((t - 1.8f) / 0.6f));
            Vector3 down = Vector3.down * (0.9f * pull + 0.4f * push) + climbIn * (0.25f * push);
            climbLeft = new Pose(l + down, palmsDown);
            climbRight = new Pose(r + down, palmsDown);
            Vector3 headAt = climbEdge - climbIn * 0.3f + Vector3.up * Mathf.Lerp(-0.25f, 0.45f, pull) + climbIn * (0.5f * push) + Vector3.up * (0.4f * push);
            climbHead = new Pose(headAt, climbFacing * Quaternion.Euler(Mathf.Lerp(-25f, 15f, pull), 0f, 0f));
            bool hold = t > 0.5f && t < 2.35f;
            climbWantLeft = hold;
            climbWantRight = hold;
            // (Reaching up from where its hands were.)
            if (reach < 1f)
            {
                climbLeft.position = Vector3.Lerp(Head != null ? Head.position - Vector3.up * 0.4f - side * 0.2f : l, climbLeft.position, reach);
                climbRight.position = Vector3.Lerp(Head != null ? Head.position - Vector3.up * 0.4f + side * 0.2f : r, climbRight.position, reach);
            }
        }

        // A hand taking hold of the world (when it is there), or letting go.
        private void HoldWorld(int which, bool want, Vector3 target)
        {
            Hand hand = which == 0 ? LeftHand : RightHand;
            Grip world = rig.worldGrip;
            if (hand == null || world == null)
                return;
            if (want && !climbHolding[which])
            {
                float off = (hand.transform.position - target).magnitude;
                // (At the spot, or touching the wall up by its top: never low on the wall's face.)
                bool near = off < Tuning.F("climb.grab", 0.3f)
                            || (Touching(hand) && hand.transform.position.y > climbEdge.y - Tuning.F("climb.top", 0.3f));
                if (hand.m_CurrentAttachedGO == null && near && TakeWorld(hand))
                {
                    climbHolding[which] = true;
                    Lab.Event("climb: " + (which == 0 ? "left" : "right") + " hand took hold (" + off.ToString("0.00") + " m from the player's spot)");
                }
                else if (Time.time - climbSaidAt[which] > 0.5f)
                {
                    climbSaidAt[which] = Time.time;
                    Lab.Event("climb: " + (which == 0 ? "left" : "right") + " hand " + off.ToString("0.00") + " m from the edge"
                              + (near ? ", near but the grab did not take" : "") + (hand.m_CurrentAttachedGO != null ? ", holding something" : ""));
                }
            }
            else if (!want && climbHolding[which])
            {
                LetGoOf(world, hand);
                climbHolding[which] = false;
            }
        }

        private readonly float[] climbSaidAt = new float[2];

        // A wall or ledge right at the hand (within a hand's breadth).
        private bool Touching(Hand hand)
        {
            foreach (Collider c in Physics.OverlapSphere(hand.transform.position, Tuning.F("climb.touch", 0.1f), ~0, QueryTriggerInteraction.Ignore))
            {
                if (c != null && c.attachedRigidbody == null && c.GetComponentInParent<RigManager>() == null)
                    return true;
            }
            return false;
        }

        // The world grip taken as the player's hand takes it: hovering first (it feels for the
        // surface under the hand), then the grab.
        private bool TakeWorld(Hand hand)
        {
            WorldGrip world = rig.worldGrip;
            if (world == null)
                return false;
            try
            {
                hand.GrabLock = false;
                hand.HoverLock();
                SimpleTransform at = SimpleTransform.Create(hand.transform);
                world.OnHandHoverBegin(hand, true);
                world.OnHandHoverUpdate(hand);
                world.ValidateGripScore(hand, at);
                hand._mHoveringReceiver = world;
                world.OnGrabConfirm(hand, false);
                return hand.m_CurrentAttachedGO != null;
            }
            catch (Exception e)
            {
                Lab.Event("climb: the world grab failed: " + e.Message);
                return false;
            }
        }

        /// <summary>While climbing: its head and hands where the climb has them (over whatever it was doing).</summary>
        private void ClimbPose()
        {
            if (!climbing || climbPhase != 1)
                return;
            Transform root = controls.vrRoot;
            if (root == null)
                return;
            Quaternion body = Quaternion.Euler(0f, bodyYaw, 0f);
            Quaternion toBody = Quaternion.Inverse(body);
            float scale = Mathf.Max(0.01f, root.lossyScale.y);
            Vector3 Local(Vector3 world) => toBody * (world - root.position) / scale;
            Frame f = pose;
            f.height = Local(climbHead.position).y;
            f.head = toBody * climbHead.rotation;
            f.leftPos = Local(climbLeft.position);
            f.leftRot = toBody * climbLeft.rotation;
            f.rightPos = Local(climbRight.position);
            f.rightRot = toBody * climbRight.rotation;
            f.leftGrip = climbWantLeft ? 1f : 0.3f;
            f.rightGrip = climbWantRight ? 1f : 0.3f;
            // (As its body is lifted, its head goes with it: kept to the player's place for it
            // by the edge, it was left low and the body folded up under it.)
            if (climbGliding)
            {
                float eyes = StandingEyes() > 0.3f ? StandingEyes() : standingHeight;
                f.height = Mathf.Clamp(f.height, eyes * Tuning.F("climb.head.least", 0.78f), eyes);
            }
            // (From the pose it had, at the start.)
            float k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(climbReach / 0.35f));
            pose = k < 1f ? Blend(climbFrom, f, k) : f;
            curl = -1f;
        }
    }
}
