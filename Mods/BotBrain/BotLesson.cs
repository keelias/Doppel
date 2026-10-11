using BoneLib;
using Il2CppSLZ.Marrow;
using Il2CppSLZ.Marrow.Pool;
using UnityEngine;

namespace ThirdEyeBonelabMod.Bots
{
    /// <summary>
    /// The bot using a thing just as the player used it (a lesson, see Lessons): it takes one of
    /// its own in the hand they took theirs in, and does all they did with it, every move,
    /// press of a button and pull of the trigger at its moment, then puts it down and goes back
    /// to what it was at. It is a clone recording played, but from where the bot stands and the
    /// way it faces (or, on the level it was learned on with the place near, from the very
    /// spot), in the bot's own body, sized to it. Where it is carried off somewhere else on the
    /// way (through a portal it opened, as the player went through theirs), it goes on from
    /// where it comes out: it is never put back on the player's path by force.
    /// </summary>
    public partial class Bot
    {
        private Lessons.Lesson cloneLesson;
        private Mode lessonReturn = Mode.Stand;
        // Where the recording's world is from the bot's: turned so much about up, this point there being that point here.
        private float cloneYaw;
        private Vector3 cloneFrom, cloneTo;
        private float cloneSize = 1f;
        private bool lessonRootSet;
        private float lessonRootYaw;
        private Vector3 lessonPelvisWas;
        private bool lessonPelvisHas;
        private int lessonJumpAt = -1;
        // When in the recording the player was carried off (through a portal), and how far its turns have been looked at.
        private readonly List<float> lessonJumps = new List<float>();
        private int lessonTurnsSeen;
        private int cloneDriven = -1;
        private float lessonSenseAt;
        private string lastLessonItem;
        private readonly Dictionary<Clone.Event, int> cloneStandIns = new Dictionary<Clone.Event, int>();

        // The lesson it is on its way to do, and whether at the spot it was learned at.
        private Lessons.Lesson lessonNext;
        private bool lessonAtSpot;

        public bool InLesson => mode == Mode.Clone && cloneLesson != null;

        /// <summary>The mode to keep for it from one build to the next (the one it goes back to, in a lesson).</summary>
        public Mode KeptMode => InLesson ? lessonReturn : mode;

        /// <summary>Does a lesson now, where it stands (or exactly where the player did it, if it is there).</summary>
        public bool UseLesson(Lessons.Lesson lesson, bool atSpot) => UseLesson(lesson, atSpot, 0f, 0, null);

        // How many more times its climbing stretch is to be done, and the easing over the join.
        private int lessonLoops;
        private float lessonSeamAt = -10f;
        private Pose seamHead, seamLeft, seamRight, lastHeadPose, lastLeftPose, lastRightPose;
        private bool lessonPurposeful;
        private bool lessonGliding;
        // How high the top of its wall is (set by whoever sends it up one), and the things it has for the lesson, one for each the player had.
        private float wallTopY = float.MinValue, lessonTopY = float.MinValue;
        private readonly Dictionary<string, Il2CppSLZ.Marrow.Pool.Poolee> lessonThings = new Dictionary<string, Il2CppSLZ.Marrow.Pool.Poolee>();
        private float regrabAt;
        // The foot of its wall and the way into it; and its being set down on the top at the end.
        private Vector3 lessonWallFoot, lessonWallIn, lessonLandAt;
        private bool lessonLanding;
        private float lessonLandSince;

        // At the end of a climb: on its feet on the top of the wall (taken there, if the climb left it short). True once it is.
        private bool LandedOnTop()
        {
            if (rig.physicsRig.centerOfPressure == null || lessonTopY == float.MinValue)
                return true;
            Vector3 feet = rig.physicsRig.centerOfPressure.position;
            float over = Vector3.Dot(feet - lessonWallFoot, lessonWallIn);
            if (!lessonLanding)
            {
                // (Past the edge and standing: nothing to do.)
                if (over > Tuning.F("tool.climb.stand", 0.45f) + 0.5f && feet.y > lessonTopY - 0.2f && FeetOnGround())
                    return true;
                lessonLanding = true;
                lessonLandSince = Time.time;
                Vector3 onTop = lessonWallFoot + lessonWallIn * (Tuning.F("tool.climb.stand", 0.45f) + Tuning.F("tool.climb.onto", 0.9f));
                lessonLandAt = new Vector3(onTop.x, lessonTopY + 0.05f, onTop.z);
                if (carried.Count == 0)
                    CarryThings(true);
                Lab.Event("lesson: set down on the top of its wall (" + (lessonLandAt - feet).magnitude.ToString("0.0") + " m to go)");
                return false;
            }
            if ((feet - lessonLandAt).magnitude > 0.08f && Time.time - lessonLandSince < 5f)
                return false;
            lessonLanding = false;
            return true;
        }
        private int lessonLoopsDone;
        // Its own one of each of the player's things as asked for (before it has come), and which of its things are too big for a hand.
        private readonly Dictionary<string, int> lessonStandIns = new Dictionary<string, int>();
        private readonly HashSet<int> lessonBig = new HashSet<int>();
        private readonly HashSet<int> lessonBigAsked = new HashSet<int>();

        // How big a thing of that kind is (the longest way across it), by what the game knows of it; 0 if it does not say.
        private static float SizeOf(string barcode)
        {
            try
            {
                var warehouse = Il2CppSLZ.Marrow.Warehouse.AssetWarehouse.Instance;
                if (warehouse != null && warehouse.TryGetCrate(new Il2CppSLZ.Marrow.Warehouse.Barcode(barcode), out Il2CppSLZ.Marrow.Warehouse.Crate crate) && crate != null)
                {
                    Il2CppSLZ.Marrow.Warehouse.GameObjectCrate made = crate.TryCast<Il2CppSLZ.Marrow.Warehouse.GameObjectCrate>();
                    if (made != null)
                        return made.ColliderBounds.size.magnitude;
                }
            }
            catch (Exception)
            {
            }
            return 0f;
        }

        // A thing too big for a hand (a kart, a crate): not into its hand, into its body, but
        // on the ground in front of it, a step and a half off, clear of it; where the recording
        // says which way it faced and where it was from the player, just so, if that is clear of it.
        private void PlaceBigThing(Clone.Event e, ref Vector3 where, ref Quaternion faces)
        {
            float size = SizeOf(e.barcode);
            if (size < Tuning.F("lesson.big", 1.1f))
                return;
            Transform head = Head;
            Rigidbody pelvis = Pelvis();
            if (head == null || pelvis == null)
                return;
            Vector3 feet = rig.physicsRig.centerOfPressure != null ? rig.physicsRig.centerOfPressure.position : Feet;
            Vector3 ahead = Flat(head.forward).sqrMagnitude > 0.01f ? Flat(head.forward).normalized : Quaternion.Euler(0f, bodyYaw, 0f) * Vector3.forward;
            float clear = Tuning.F("lesson.front", 1.5f) + size * 0.25f;
            bool known = e.turn != Quaternion.identity && e.at != Vector3.zero;
            // (To be driven off somewhere: put where there is room for it, facing the way it is to go.)
            if (placeAhead.sqrMagnitude > 0.01f)
            {
                ahead = placeAhead;
                known = false;
            }
            // (Or at a very spot: on the way it is to drive along, facing along it.)
            if (placeAtSet && placeAhead.sqrMagnitude > 0.01f)
            {
                Vector3 spot = placeAt;
                if (Physics.Raycast(spot + Vector3.up * 2f, Vector3.down, out RaycastHit under, 6f, ~0, QueryTriggerInteraction.Ignore))
                    spot = under.point;
                where = spot + Vector3.up * 0.15f;
                faces = Quaternion.LookRotation(placeAhead, Vector3.up);
                lessonBigAsked.Add(-1000 - cloneStandIns.Count);
                Lab.Event("lesson: the " + Clone.ThingName(e) + " put on the way it is to drive, " + Flat(spot - feet).magnitude.ToString("0.0") + " m off");
                return;
            }
            Vector3 at = known ? LessonPlace(e.at) : feet + ahead * clear;
            if (known && Flat(at - feet).magnitude < 0.9f)
                known = false;
            if (!known)
            {
                at = feet + ahead * clear;
                if (Physics.Raycast(at + Vector3.up * 2f, Vector3.down, out RaycastHit ground, 6f, ~0, QueryTriggerInteraction.Ignore))
                    at = ground.point + Vector3.up * 0.15f;
                faces = Quaternion.LookRotation(ahead, Vector3.up);
            }
            else
            {
                faces = LessonTurn(e.turn);
            }
            where = at;
            lessonBigAsked.Add(-1000 - cloneStandIns.Count);
            Lab.Event("lesson: the " + Clone.ThingName(e) + " is " + size.ToString("0.0") + " m across: put " + Flat(at - feet).magnitude.ToString("0.0") + " m off" + (known ? ", where yours was from you" : ", in front of it"));
        }

        // Which way a big thing of the lesson's is to be put from it, and face (zero: in front of it).
        private Vector3 placeAhead, placeAt;
        private bool placeAtSet;
        private Clone.Event seatWaiting;
        private float seatWaitUntil, seatSitAt, seatAskedAt;
        private int seatMoves, seatRetries;
        private Seat seatAsked;
        private Clone.Event seatAskedOf;

        // Sat down a second ago: is it in its seat, and at it? If not, out and in again, from beside it.
        private void SeatCheck()
        {
            try
            {
                if (seatAsked == null || Time.time - seatAskedAt < 1f)
                    return;
                Seat seat = seatAsked;
                seatAsked = null;
                Rigidbody hips = Pelvis();
                float off = hips != null ? Flat(hips.position - seat.transform.position).magnitude : 0f;
                bool good = rig.activeSeat == seat && off < Tuning.F("seat.off", 0.45f);
                if (good || !InLesson)
                {
                    seatRetries = 0;
                    return;
                }
                if (seatRetries >= 2)
                {
                    Lab.Event("lesson: still not sat right (" + off.ToString("0.00") + " m from its seat), left at that");
                    seatRetries = 0;
                    return;
                }
                seatRetries++;
                Lab.Event("lesson: not sat right (" + (rig.activeSeat == seat ? "" : "not in its seat, ") + off.ToString("0.00") + " m from it): out and in again (" + seatRetries + ")");
                if (rig.activeSeat != null)
                    rig.activeSeat.EgressRig(true);
                else if (rig.bodyState == RigManager.BodyState.Seated)
                    rig.SwitchBodyState(RigManager.BodyState.OnFoot);
                Vector3 beside = seat.transform.position - Flat(seat.transform.right).normalized * 1.1f;
                if (Physics.Raycast(beside + Vector3.up * 1.5f, Vector3.down, out RaycastHit ground, 4f, ~0, QueryTriggerInteraction.Ignore))
                    beside = ground.point;
                rig.Teleport(beside, true);
                lessonPelvisHas = false;
                seatMoves = 2;
                seatWaiting = seatAskedOf;
                seatWaitUntil = Time.time + 4f;
                seatSitAt = Time.time + 0.4f;
            }
            catch (Exception)
            {
            }
        }

        // Sitting down in a seat of its own thing as the player sat in theirs, and getting out.
        private void LessonSeat(Clone.Event e)
        {
            try
            {
                if (e.hand == 0)
                {
                    Seat sat = rig.activeSeat;
                    if (sat != null)
                        sat.EgressRig(true);
                    Lab.Event("lesson: gets out");
                    return;
                }
                string key = e.barcode + "#" + Mathf.Max(0, Clone.ThingNumber(e));
                if (!lessonThings.TryGetValue(key, out Il2CppSLZ.Marrow.Pool.Poolee thing) || thing == null)
                {
                    // (Its own one of the thing, whichever it has, if it was not taken hold of first.)
                    foreach (Il2CppSLZ.Marrow.Pool.Poolee p in cloneSpawns.Values)
                    {
                        if (p != null && p.SpawnableCrate != null && p.SpawnableCrate.Barcode.ID == e.barcode)
                            thing = p;
                    }
                }
                if (thing == null)
                {
                    // (Its own one has not come yet, or was never asked for (the player was
                    // sat in theirs before they took hold of anything): asked for, and waited for.)
                    if (seatWaiting == null)
                    {
                        seatWaiting = e;
                        seatWaitUntil = Time.time + 4f;
                        if (cloneSpawns.Count == 0 && clonePending.Count == 0 && e.barcode.Length > 0)
                        {
                            Vector3 where = Head != null ? Head.position : Feet;
                            Quaternion faces = Quaternion.identity;
                            PlaceBigThing(e, ref where, ref faces);
                            int id = -1000 - cloneStandIns.Count - 500;
                            CloneSpawn(id, e.barcode, where, faces);
                            lessonStandIns[key] = id;
                        }
                    }
                    else if (Time.time > seatWaitUntil)
                    {
                        seatWaiting = null;
                        Lab.Event("lesson: nothing of its own to sit in");
                    }
                    return;
                }
                if (Time.time < seatSitAt)
                    return;
                seatWaiting = null;
                Seat[] seats = thing.GetComponentsInChildren<Seat>(true);
                Seat seat = e.grip >= 0 && e.grip < seats.Length ? seats[e.grip] : seats.Length > 0 ? seats[0] : null;
                if (seat == null)
                    return;
                // (Too far off to get in from where it stands: beside it first.)
                Vector3 feetNow = rig.physicsRig.centerOfPressure != null ? rig.physicsRig.centerOfPressure.position : Feet;
                if (Flat(seat.transform.position - feetNow).magnitude > 3.5f)
                {
                    Vector3 beside = seat.transform.position - Flat(seat.transform.right).normalized * 1.1f;
                    if (Physics.Raycast(beside + Vector3.up * 1.5f, Vector3.down, out RaycastHit ground, 4f, ~0, QueryTriggerInteraction.Ignore))
                        beside = ground.point;
                    rig.Teleport(beside, true);
                    lessonPelvisHas = false;
                    // (And sat down a moment after, not in the same breath: the game moves it there a
                    // frame later, and sat down first it was then carried off again, sat in the air a
                    // metre to the left of its seat, its right arm at full stretch to the wheel.)
                    if (seatMoves < 2)
                    {
                        seatMoves++;
                        seatWaiting = e;
                        seatWaitUntil = Time.time + 4f;
                        seatSitAt = Time.time + 0.35f;
                        return;
                    }
                }
                seatMoves = 0;
                seat.IngressRig(rig);
                seatAsked = seat;
                seatAskedAt = Time.time;
                seatAskedOf = e;
                Lab.Event("lesson: sits down in the " + thing.name);
            }
            catch (Exception ex)
            {
                Lab.Event("lesson: could not sit: " + ex.Message);
            }
        }

        /// <summary>
        /// Does a lesson: from so far into it (what came before that is done at once: taking
        /// the thing in hand), with its repeatable stretch done so many extra times, and, for a
        /// climb, with where the player began to go up laid on the foot of its own wall.
        /// </summary>
        public bool UseLesson(Lessons.Lesson lesson, bool atSpot, float startAt, int loops, (Vector3 foot, Vector3 inward)? wall)
        {
            if (lesson == null || lesson.take == null || lesson.take.frames.Count < 2 || dead || (enemy && !wall.HasValue) || climbing)
                return false;
            Rigidbody pelvis = Pelvis();
            Transform head = Head;
            if (pelvis == null || head == null)
                return false;
            Mode back = InLesson ? lessonReturn : mode == Mode.Clone ? Mode.Stand : mode;
            // (Its weapon put away for it, not dropped: driving, climbing, a tool to use.)
            if (WeaponInHand)
                Stow();
            StopPlaying();
            ReleaseCloneHands();
            SetMode(Mode.Clone);
            FreeHand(LeftHand);
            FreeHand(RightHand);
            cloneTake = lesson.take;
            cloneLesson = lesson;
            cloneLoop = false;
            cloneDressing = false;
            lessonReturn = back;
            lastLessonItem = lesson.item;
            cloneStart = Time.time - startAt;
            lessonLoops = loops;
            lessonTopY = wall.HasValue ? wallTopY : float.MinValue;
            lessonThings.Clear();
            lessonStandIns.Clear();
            seatWaiting = null;
            seatAsked = null;
            seatRetries = 0;
            seatMoves = 0;
            seatSitAt = 0f;
            lessonBig.Clear();
            lessonBigAsked.Clear();
            lessonLoopsDone = 0;
            lessonLanding = false;
            wallHeld[0] = wallHeld[1] = false;
            driveMeant = driving = false;
            placeAhead = Vector3.zero;
            placeAtSet = false;
            if (wall.HasValue)
            {
                lessonWallFoot = wall.Value.foot;
                lessonWallIn = wall.Value.inward;
            }
            lessonSeamAt = -10f;
            lessonPurposeful = wall.HasValue;
            lessonGliding = false;
            cloneEvent = 0;
            cloneFrame = 0;
            cloneDriven = -1;
            cloneSpawns.Clear();
            clonePending.Clear();
            cloneGrabs.Clear();
            cloneStandIns.Clear();
            lessonRootSet = false;
            lessonPelvisHas = false;
            lessonJumpAt = -1;
            lessonSenseAt = Time.time + 1f;

            lessonJumps.Clear();
            lessonTurnsSeen = 0;
            List<Clone.Frame> all = lesson.take.frames;
            for (int i = 1; i < all.Count; i++)
            {
                if ((all[i].pelvis - all[i - 1].pelvis).magnitude > 1.5f)
                    lessonJumps.Add(all[i].t);
            }
            Clone.Frame first = lesson.take.frames[0];
            cloneFrom = first.pelvis;
            if (atSpot)
            {
                cloneTo = cloneFrom;
                cloneYaw = 0f;
            }
            else
            {
                cloneTo = pelvis.position;
                cloneYaw = Mathf.DeltaAngle(Recorder.Yaw(first.playspace * first.head.rotation * Vector3.forward), Recorder.Yaw(head.forward));
            }
            if (wall.HasValue && !atSpot)
            {
                // The moment they began to go up, at the foot of its wall, facing into it.
                Clone.Frame begin = FrameAt(lesson.take, lesson.riseAt);
                cloneFrom = begin.pelvis;
                cloneTo = new Vector3(wall.Value.foot.x, pelvis.position.y, wall.Value.foot.z);
                cloneYaw = Mathf.DeltaAngle(Recorder.Yaw(begin.playspace * begin.head.rotation * Vector3.forward), Recorder.Yaw(wall.Value.inward));
            }
            float eyes = StandingEyes();
            cloneSize = lesson.take.eyes > 0.3f && eyes > 0.3f ? Mathf.Clamp(eyes / lesson.take.eyes, 0.35f, 3f) : 1f;
            Lab.Event("lesson: using the " + lesson.name + " as you did (" + lesson.length.ToString("0.0") + " s"
                + (atSpot ? ", where you did" : ", where it stands, turned " + cloneYaw.ToString("0") + " deg") + ", size " + cloneSize.ToString("0.00") + ")");
            return true;
        }

        private static Clone.Frame FrameAt(Clone.Take take, float t)
        {
            List<Clone.Frame> frames = take.frames;
            int i = 0;
            while (i < frames.Count - 1 && frames[i + 1].t <= t)
                i++;
            return frames[i];
        }

        // Its climbing stretch done again: back to the start of it, from where it has got to (so each time takes it higher).
        private void LessonLoop()
        {
            if (cloneLesson == null)
                return;
            float t = Time.time - cloneStart;
            Rigidbody hips = Pelvis();
            bool wall = lessonPurposeful && lessonTopY > float.MinValue && cloneLesson.purpose == "climb" && rig.physicsRig.centerOfPressure != null && hips != null;
            if (wall)
            {
                float feetY = rig.physicsRig.centerOfPressure.position.y;
                // By its bare hands, and the edge now in reach of them: the last of it is its own
                // getting up onto an edge (hands on the edge, up, over, on its feet). The player's
                // own last moves, made at their wall, had its hands going on up past the edge of its own.
                if (cloneLesson.item == "wall" && t > cloneLesson.riseAt && lessonTopY - feetY <= Tuning.F("wall.finish", 1.55f) && lessonTopY - feetY > 0.3f)
                {
                    FinishOverEdge();
                    return;
                }
                // Near the top of its own wall before the player was near theirs: on to their getting over the top, from here.
                if (t > cloneLesson.riseAt && t < cloneLesson.topAt - 0.2f && feetY + cloneLesson.overRise >= lessonTopY + 0.1f)
                {
                    cloneStart -= cloneLesson.topAt - t;
                    LessonFromHere(FrameAt(cloneTake, cloneLesson.topAt).pelvis, hips.position);
                    lessonLoops = 0;
                    cloneFrame = 0;
                    cloneDriven = -1;
                    lessonTurnsSeen = 0;
                    cloneEvent = 0;
                    while (cloneEvent < cloneTake.events.Count && cloneTake.events[cloneEvent].t < cloneLesson.topAt)
                        cloneEvent++;
                    seamHead = lastHeadPose;
                    seamLeft = lastLeftPose;
                    seamRight = lastRightPose;
                    lessonSeamAt = Time.time;
                    Lab.Event("lesson: at the top of its wall, on to getting over it");
                    return;
                }
                // As many more goes of its climbing stretch as its own wall takes (not a number fixed beforehand).
                if (cloneLesson.loopRise > 0.2f && t >= cloneLesson.loopTo && t < cloneLesson.loopTo + 0.5f)
                    lessonLoops = feetY + cloneLesson.afterLoop < lessonTopY - 0.15f && lessonLoopsDone < 60 ? 1 : 0;
            }
            if (lessonLoops <= 0 || cloneLesson.loopRise <= 0f)
                return;
            if (t < cloneLesson.loopTo)
                return;
            lessonLoops--;
            lessonLoopsDone++;
            cloneStart += cloneLesson.loopTo - cloneLesson.loopFrom;
            Rigidbody pelvis = Pelvis();
            if (pelvis != null)
                LessonFromHere(FrameAt(cloneTake, cloneLesson.loopFrom).pelvis, pelvis.position);
            // (What it did in that stretch, it does again.)
            cloneEvent = 0;
            while (cloneEvent < cloneTake.events.Count && cloneTake.events[cloneEvent].t < cloneLesson.loopFrom)
                cloneEvent++;
            cloneFrame = 0;
            cloneDriven = -1;
            lessonTurnsSeen = 0;
            seamHead = lastHeadPose;
            seamLeft = lastLeftPose;
            seamRight = lastRightPose;
            lessonSeamAt = Time.time;
            Lab.Event("lesson: that stretch again (" + lessonLoops + " more to go)");
        }

        // Its hands on its wall, in a climb by its bare hands: which have hold, and where on the wall each took it.
        private readonly bool[] wallHeld = new bool[2], wallPinned = new bool[2];
        private readonly Vector3[] wallPin = new Vector3[2];

        // A hand that has hold of the wall is flat on the face of its own wall, and stays at the
        // very spot it took hold while its body goes up past it (as a hand that holds does),
        // till it lets go. The player's hand, at their wall, was only roughly where its own wall is.
        private void WallHands()
        {
            if (cloneLesson == null || cloneLesson.item != "wall" || !lessonPurposeful || lessonTopY == float.MinValue)
            {
                wallHeld[0] = wallHeld[1] = false;
                return;
            }
            Transform head = controls.headset;
            Vector3 face = lessonWallFoot + lessonWallIn * Tuning.F("tool.climb.stand", 0.45f);
            BaseController[] sticks = { controls.leftController, controls.rightController };
            for (int i = 0; i < 2; i++)
            {
                if (!wallHeld[i] || sticks[i] == null)
                    continue;
                Transform c = sticks[i].transform;
                if (!wallPinned[i])
                {
                    // Where it comes to the wall: straight in to its face, a finger's width off it, no higher than its top.
                    Vector3 p = c.position;
                    p -= lessonWallIn * (Vector3.Dot(p - face, lessonWallIn) + Tuning.F("wall.off", 0.06f));
                    p.y = Mathf.Min(p.y, lessonTopY - 0.03f);
                    wallPin[i] = p;
                    wallPinned[i] = true;
                }
                // (Left behind further than its arm goes: let go, as it would have to.)
                if (head != null && (wallPin[i] - head.position).magnitude > Tuning.F("wall.arm", 0.85f) && wallPin[i].y < head.position.y - 0.3f)
                {
                    wallHeld[i] = false;
                    continue;
                }
                if (!Bad(wallPin[i]))
                    c.position = wallPin[i];
            }
        }

        // Hanging on a wall with its top in reach: the lesson is over, and it gets up onto the edge its own way (see BotClimb), from where it hangs.
        private void FinishOverEdge()
        {
            Vector3 feet = rig.physicsRig.centerOfPressure.position;
            float left = lessonTopY - feet.y;
            Vector3 inward = lessonWallIn;
            Vector3 face = lessonWallFoot + inward * Tuning.F("tool.climb.stand", 0.45f);
            Vector3 edge = new Vector3(face.x, lessonTopY, face.z);
            Pose headWas = Head != null ? Pose.Of(Head) : new Pose(edge, Quaternion.LookRotation(inward, Vector3.up));
            Pose leftWas = LeftHand != null ? Pose.Of(LeftHand.transform) : headWas;
            Pose rightWas = RightHand != null ? Pose.Of(RightHand.transform) : headWas;
            EndLesson("at the edge of its wall (" + left.ToString("0.0") + " m to the top): up onto it its own way");
            lessonDueAt = Time.time + 20f;
            nav.Clear();
            climbing = true;
            climbLifting = false;
            climbGliding = false;
            climbPhase = 1;
            climbEdge = edge;
            climbIn = inward;
            climbHeight = left;
            climbFacing = Quaternion.LookRotation(inward, Vector3.up);
            climbSince = Time.time;
            climbMove = ClimbMoves.Pick(left);
            climbLastEdge = edge;
            climbT = 0f;
            climbReach = 0f;
            climbWait = 0f;
            climbFrom = pose;
            climbStartHead = headWas;
            climbStartLeft = leftWas;
            climbStartRight = rightWas;
            bodyYaw = Recorder.Yaw(inward);
            walk = Vector2.zero;
            climbHang = true;
            climbHangFeet = feet;
        }

        private Rigidbody Pelvis() => rig.physicsRig != null && rig.physicsRig.m_pelvis != null ? rig.physicsRig.m_pelvis.GetComponent<Rigidbody>() : null;

        // A place (and a turn) of the recording's, where it is for the bot.
        private Vector3 LessonPlace(Vector3 p) => cloneLesson == null ? p : cloneTo + Quaternion.Euler(0f, cloneYaw, 0f) * (p - cloneFrom);
        private Quaternion LessonTurn(Quaternion q) => cloneLesson == null ? q : Quaternion.Euler(0f, cloneYaw, 0f) * q;

        /// <summary>
        /// The player's turns that a portal gave them (their play space turned at a stroke, at
        /// about the moment they were carried off) are not the bot's to make: its own portal
        /// turns it as it goes through. Taken out as the recording comes to them. True if the
        /// step being played now (to the frame after this one) is such a turn.
        /// </summary>
        private bool LessonPortalTurns()
        {
            List<Clone.Frame> frames = cloneTake.frames;
            bool now = false;
            for (int j = Mathf.Max(1, lessonTurnsSeen + 1); j <= cloneFrame + 1 && j < frames.Count; j++)
            {
                lessonTurnsSeen = j;
                float turn = Mathf.DeltaAngle(frames[j - 1].playspace.eulerAngles.y, frames[j].playspace.eulerAngles.y);
                if (Mathf.Abs(turn) < 12f)
                    continue;
                bool portal = false;
                foreach (float at in lessonJumps)
                    portal |= Mathf.Abs(at - frames[j].t) < 0.3f;
                if (!portal)
                    continue;
                cloneYaw -= turn;
                now |= j == cloneFrame + 1;
            }
            if (lessonTurnsSeen == cloneFrame + 1 && cloneFrame + 1 < frames.Count)
            {
                float turn = Mathf.DeltaAngle(frames[cloneFrame].playspace.eulerAngles.y, frames[cloneFrame + 1].playspace.eulerAngles.y);
                if (Mathf.Abs(turn) >= 12f)
                {
                    foreach (float at in lessonJumps)
                        now |= Mathf.Abs(at - frames[cloneFrame + 1].t) < 0.3f;
                }
            }
            return now;
        }

        // From here on, the recording's "there" is the bot's "here" (it has been carried off, or cannot get on).
        private void LessonFromHere(Vector3 there, Vector3 here)
        {
            cloneFrom = there;
            cloneTo = here;
        }

        /// <summary>The lesson over (done, or broken off): hands empty, what it spawned left to be cleared away, back to what it was at.</summary>
        private void EndLesson(string how)
        {
            Lab.Event("lesson: " + how);
            lessonGliding = false;
            // (A while before the next thing it does with a tool: not one straight on top of another.)
            lessonDueAt = Mathf.Max(lessonDueAt, Time.time + Random(25f, 50f));
            foreach (Poolee thing in cloneSpawns.Values)
                Lessons.Litter(thing);
            Mode back = lessonReturn;
            // (SetMode lets go of what it holds and takes its buttons off.)
            SetMode(back);
            Transform head = Head;
            if (head != null)
                bodyYaw = Recorder.Yaw(head.forward);
            pose = Standing(0f, 0f, 0f, 0f);
            if (back == Mode.Wander || back == Mode.Brain)
                Idle(Random(1.5f, 4f));
        }

        // Leaving a clone or a lesson for something else: nothing left in its hands, no button left down.
        private void LeaveClone()
        {
            lessonLanding = false;
            // (Out of whatever it sat in.)
            try
            {
                if (rig.activeSeat != null)
                    rig.activeSeat.EgressRig(true);
            }
            catch (Exception)
            {
            }
            CarryThings(false);
            driveMeant = driving = false;
            ReleaseCloneHands();
            cloneTake = null;
            cloneLesson = null;
            lessonNext = null;
            ClearButtons(controls.leftController);
            ClearButtons(controls.rightController);
            try
            {
                rig.remapHeptaRig._crouchTarget = 0f;
                rig.remapHeptaRig._feetOffset = 0f;
            }
            catch (Exception)
            {
            }
        }

        private static void ClearButtons(BaseController c)
        {
            if (c == null)
                return;
            c._primaryAxis = 0f;
            c._primaryInteractionButton = c._primaryInteractionButtonDown = c._primaryInteractionButtonUp = false;
            c._secondaryInteractionButton = c._secondaryInteractionButtonDown = c._secondaryInteractionButtonUp = false;
            c._aButton = c._aButtonDown = c._aButtonUp = false;
            c._bButton = c._bButtonDown = c._bButtonUp = false;
            c._thumbstick = c._thumbstickDown = c._thumbstickUp = false;
            c._touchPad = c._touchPadDown = c._touchPadUp = false;
            c._menuTap = false;
            c._touchPadAxis = Vector2.zero;
        }

        // The things in its hands while its body is carried up a wall. Moving its body a step at
        // a time lets go of whatever it holds (every step), so for as long as that goes on its
        // things are not held as things are, but kept just where they were in its hands, moved
        // with them; and taken properly again once it is on its feet.
        private readonly List<(Rigidbody body, Hand hand, Grip grip, Pose inHand, bool wasKinematic)> carried = new List<(Rigidbody, Hand, Grip, Pose, bool)>();
        private float glidedAt;

        private void CarryThings(bool on)
        {
            if (on)
            {
                if (carried.Count > 0)
                    return;
                Hand[] hands = { LeftHand, RightHand };
                for (int i = 0; i < 2; i++)
                {
                    Grip grip = cloneHeld[i];
                    Hand hand = hands[i];
                    Rigidbody body = grip != null ? grip.GetComponentInParent<Rigidbody>() : null;
                    if (hand == null || body == null)
                        continue;
                    // (One thing in both hands is carried by the first.)
                    bool already = false;
                    foreach (var c in carried)
                        already |= c.body == body;
                    Pose inHand = Pose.Of(hand.transform).Inverse() * Pose.Of(body.transform);
                    if (hand.m_CurrentAttachedGO != null)
                        FreeHand(hand);
                    if (already)
                        continue;
                    carried.Add((body, hand, grip, inHand, body.isKinematic));
                    body.isKinematic = true;
                }
                if (carried.Count > 0)
                    Lab.Event("lesson: its " + carried.Count + " thing(s) carried in its hands while it is taken up");
                return;
            }
            foreach (var c in carried)
            {
                try
                {
                    if (c.body == null)
                        continue;
                    c.body.isKinematic = c.wasKinematic;
                    c.body.velocity = Vector3.zero;
                    c.body.angularVelocity = Vector3.zero;
                    if (c.hand != null && c.grip != null && c.hand.m_CurrentAttachedGO == null && mode == Mode.Clone && cloneLesson != null)
                        Grab(c.hand, c.grip);
                }
                catch (Exception)
                {
                }
            }
            carried.Clear();
        }

        // Each step and each frame while they are carried: where its hands are.
        private void CarriedFollow()
        {
            foreach (var c in carried)
            {
                if (c.body == null || c.hand == null)
                    continue;
                Pose at = Pose.Of(c.hand.transform) * c.inHand;
                if (Bad(at.position) || Bad(at.rotation))
                    continue;
                c.body.position = at.position;
                c.body.rotation = at.rotation;
                c.body.transform.SetPositionAndRotation(at.position, at.rotation);
            }
        }

        // A thing of the lesson's that has come out of its hand when the player never let it go
        // (torn from it, left stuck somewhere): back into its hand.
        private void LessonKeepHold()
        {
            CarriedFollow();
            if (Time.time < regrabAt || carried.Count > 0)
                return;
            regrabAt = Time.time + 0.3f;
            Hand[] hands = { LeftHand, RightHand };
            for (int i = 0; i < 2; i++)
            {
                Grip held = cloneHeld[i];
                Hand hand = hands[i];
                if (held == null || hand == null || hand.m_CurrentAttachedGO != null)
                    continue;
                try
                {
                    Rigidbody body = held.GetComponentInParent<Rigidbody>();
                    if (body != null)
                    {
                        Vector3 by = held.transform.position - body.position;
                        body.velocity = Vector3.zero;
                        body.angularVelocity = Vector3.zero;
                        body.position = hand.transform.position - by;
                        body.transform.position = hand.transform.position - by;
                    }
                    if (Grab(hand, held))
                        Lab.Event("lesson: the " + held.name + " had come out of its " + (i == 0 ? "left" : "right") + " hand, taken again");
                }
                catch (Exception)
                {
                }
            }
        }

        // While it does a lesson on its own (its brain's): an enemy turning up breaks it off.
        private void LessonWatch()
        {
            // (Not one it is doing for a reason: that, it sees through.)
            if (lessonReturn != Mode.Brain || Time.time < lessonSenseAt || lessonPurposeful)
                return;
            lessonSenseAt = Time.time + 0.5f;
            Sense();
            if (nearestSeen.transform != null && nearestSeenDistance < 25f)
                EndLesson("broken off: an enemy " + nearestSeenDistance.ToString("0") + " m off");
        }

        // A real gun in that hand pointing the player's way: its trigger is not pulled.
        private bool LessonUnsafe(Hand hand)
        {
            GameObject held = hand != null ? hand.m_CurrentAttachedGO : null;
            Gun g = held != null ? held.GetComponentInParent<Gun>() : null;
            Transform you = Player.Head;
            if (g == null || g.firePointTransform == null || you == null)
                return false;
            Vector3 to = you.position - Vector3.up * 0.3f - g.firePointTransform.position;
            return to.magnitude < 60f && Vector3.Angle(g.firePointTransform.forward, to) < Mathf.Max(10f, Mathf.Atan2(0.6f, to.magnitude) * Mathf.Rad2Deg);
        }

        // The buttons of the frames gone by since the last one put on it (none of a tap missed
        // when the game runs slower than the recording), and the taps among them, once each.
        private Clone.HandInput LessonInput(bool left, Clone.Frame now, out bool tapped)
        {
            Clone.HandInput h = left ? now.leftIn : now.rightIn;
            tapped = false;
            List<Clone.Frame> frames = cloneTake.frames;
            int from = Mathf.Max(cloneDriven + 1, cloneFrame - 30);
            for (int i = from; i <= cloneFrame && i < frames.Count; i++)
            {
                Clone.HandInput g = left ? frames[i].leftIn : frames[i].rightIn;
                tapped |= g.menu;
                if (i == cloneFrame)
                    break;
                h.primary |= g.primary;
                h.secondary |= g.secondary;
                h.a |= g.a;
                h.b |= g.b;
                h.click |= g.click;
                h.pad |= g.pad;
            }
            return h;
        }

        private static void Edge(bool now, bool was, out bool down, out bool up)
        {
            down = now && !was;
            up = !now && was;
        }

        // Every button of the hand, as the player had it.
        private void PutButtons(BaseController c, Clone.HandInput h, bool tapped, bool hold)
        {
            if (c == null)
                return;
            bool was = c._secondaryInteractionButton;
            Edge(h.secondary, was, out bool down, out bool up);
            c._secondaryInteractionButton = h.secondary;
            c._secondaryInteractionButtonDown = down;
            c._secondaryInteractionButtonUp = up;
            Edge(h.a, c._aButton, out down, out up);
            c._aButton = h.a;
            c._aButtonDown = down;
            c._aButtonUp = up;
            Edge(h.b, c._bButton, out down, out up);
            c._bButton = h.b;
            c._bButtonDown = down;
            c._bButtonUp = up;
            Edge(h.pad, c._touchPad, out down, out up);
            c._touchPad = h.pad;
            c._touchPadDown = down;
            c._touchPadUp = up;
            c._touchPadAxis = h.padAxis;
            c._menuTap = tapped;
            // (The left stick walks it, and its walking is the player's path: only the right one's click and push are put on.)
            if (c == controls.rightController)
            {
                Edge(h.click, c._thumbstick, out down, out up);
                c._thumbstick = h.click;
                c._thumbstickDown = down;
                c._thumbstickUp = up;
                // (Its push is not: the game turns the body by it, and its turning is the recording's.)
                c._thumbstickAxis = Vector2.zero;
            }
        }

        // ---------------------------------------------------------------- of its own accord

        private float lessonDueAt = -1f;

        /// <summary>Something new was just learned: it tries it out before long.</summary>
        public void LessonSoon() => lessonDueAt = Time.time + Random(4f, 16f);

        // Now and then, with nothing pressing (following and caught up, wandering, playing on
        // its own with no enemy about): it uses something the player showed it how to use.
        private void MaybeLesson()
        {
            if (Lessons.Count == 0 || enemy || Tuning.F("lesson.every", 90f) <= 0f)
                return;
            if (lessonDueAt < 0f)
                lessonDueAt = Time.time + Random(20f, 60f);
            if (Time.time < lessonDueAt)
                return;
            // (Not on its way to do something with a tool already.)
            if (act == Act.Go && planSight == Sight.Tall)
                return;
            bool free = !Playing && !climbing && !Reloading && !portalGoing && !falling && Time.time >= waveUntil
                        && nearestSeen.transform == null && brainTarget.transform == null
                        && (mode == Mode.Wander || mode == Mode.Brain || (mode == Mode.Follow && !following));
            if (!free)
                return;
            lessonDueAt = Time.time + Tuning.F("lesson.every", 90f) * Random(0.6f, 1.6f);
            Lessons.Lesson lesson = Lessons.Pick(lastLessonItem);
            if (lesson != null && (BotManager.OwnSpawns || !NeedsThings(lesson)))
                UseLesson(lesson, false);
        }


        // Going to do a lesson (left to itself): to the spot first, if it was learned near here.
        private bool BeginLessonAct(Vector3 feet)
        {
            lessonNext = Lessons.Pick(lastLessonItem);
            if (lessonNext == null || enemy || (!BotManager.OwnSpawns && NeedsThings(lessonNext)))
            {
                lessonNext = null;
                return false;
            }
            Vector3 spot = lessonNext.take.startFeet;
            lessonAtSpot = lessonNext.level.Length > 0 && lessonNext.level == Lessons.Level
                           && Flat(spot - feet).magnitude < Tuning.F("lesson.spot", 60f) && Mathf.Abs(spot.y - feet.y) < 6f
                           && UnityEngine.Random.value < Tuning.F("lesson.spot.chance", 0.6f);
            if (lessonAtSpot)
                nav.SetGoal(spot);
            actUntil = Time.time + 45f;
            return true;
        }

        private void LessonAct(float dt)
        {
            if (lessonNext == null)
            {
                Idle(Random(1f, 3f));
                return;
            }
            if (lessonAtSpot)
            {
                running = false;
                if (!Travel(MotionLibrary.WalkPush, 0.5f, dt) && nav.StuckCount < 3)
                    return;
            }
            walk = Vector2.zero;
            Lessons.Lesson l = lessonNext;
            bool there = lessonAtSpot && Flat(l.take.startFeet - Feet).magnitude < 1.5f;
            lessonNext = null;
            nav.Clear();
            if (!UseLesson(l, there))
                Idle(Random(1f, 3f));
        }
    }
}
