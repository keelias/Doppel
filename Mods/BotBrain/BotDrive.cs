using BoneLib;
using Il2CppSLZ.Marrow;
using Il2CppSLZ.Marrow.Pool;
using UnityEngine;
using UnityEngine.AI;

namespace ThirdEyeBonelabMod.Bots
{
    /// <summary>
    /// Driving somewhere. Getting into the thing is the lesson's (it takes one of its own, or
    /// one standing by with nobody in it, sits in it and puts its hands on it as the player
    /// did); from the moment the player drove off, the bot drives it itself, by what the lesson
    /// showed of how the thing is driven (see Driving), to where it wants to go: along the
    /// level's own ways where it has them, feeling ahead for walls and edges, slower through
    /// turns and as it arrives, backing out when it is stopped by something. There (or turned
    /// over, or stuck for good) it stops and gets out.
    /// </summary>
    public partial class Bot
    {
        private Driving.Model driveModel;
        // This lesson is being done to drive somewhere; and the bot has the driving of it now.
        private bool driveMeant, driving;
        private Vector3 driveGoal;
        private Transform driveAfter;
        private string driveWhy = "", driveEndWhy = "";
        private float driveWaitSince, driveTookAt, driveStuckFor, driveBackUntil, driveBackTurn, driveOverFor, driveOutAt, driveSaidAt, driveGrabAt;
        private float driveNextAt, driveFollowAt, driveYouOutFor, driveStopSince, driveFeelAt;
        private readonly float[] driveHandOff = new float[2];
        private int driveBacks;
        private Vector3 driveBackFrom;
        private bool driveStopping, driveOut;
        // What it has on its controls: go, back (0 to 1), and how much turning (now, and wanted).
        private float driveGo, driveBack, driveTurn, driveTurnNow;
        private Vector3[] driveWay = new Vector3[0];
        private int driveWayNext;
        private float driveWayAt, driveWayFound = -100f;
        private NavMeshPath drivePath;
        private static readonly float[] feelAngles = { 0f, 15f, -15f, 30f, -30f, 50f, -50f, 75f, -75f };
        private readonly float[] feelClear = new float[9];
        private float feelLength = 8f, feelBehind = 5f;
        private bool driveAbout;
        private float driveAboutAt;

        public bool Driving2 => driving;

        /// <summary>Driving, or getting into something to drive.</summary>
        public bool DrivingNow => InLesson && driveMeant;

        /// <summary>Gets into the thing it was shown how to drive, and drives it there (or after that, wherever it goes). False if it cannot.</summary>
        // A way to drive along (the player's own, from a lesson; or their trail, as it follows them), and how far along it it is.
        // Its settings for this drive (see DriveSkill), whether they are a change being tried, and how the drive goes.
        private float[] skill;
        private bool skillTrying, skillScored;
        private float skillFar;
        private int skillBacks, skillAbouts, skillRouteFrom;
        private float driveNoRoomSaid = -100f;
        private float driveClockAt;
        private int posed;
        private string poseWhy = "";
        private bool driveLiveSaid;
        private List<Vector3> driveRoute;
        private List<float> driveRouteSpeed;
        private int driveRouteAt;
        private bool driveTrail;

        /// <summary>Drives along the way the player drove on this level, as they did. False if none is kept, or it cannot.</summary>
        public bool DriveTheirWay()
        {
            Driving.Model road = Lessons.Road();
            if (road == null)
                return false;
            // (From the nearest point of it, on to its end.)
            Vector3 feet = Feet;
            int nearest = 0;
            for (int i = 1; i < road.road.Count; i++)
            {
                if (Flat(road.road[i] - feet).sqrMagnitude < Flat(road.road[nearest] - feet).sqrMagnitude)
                    nearest = i;
            }
            List<Vector3> way = road.road;
            List<float> speeds = road.roadSpeed;
            // (A way that comes back to where it began is a lap: once round it, from the nearest point of it.)
            bool lap = Flat(way[0] - way[way.Count - 1]).magnitude < 15f && way.Count > 30;
            if (lap)
            {
                List<Vector3> round = new List<Vector3>();
                List<float> roundSpeeds = new List<float>();
                for (int i = 0; i < way.Count; i++)
                {
                    int j = (nearest + i) % way.Count;
                    round.Add(way[j]);
                    roundSpeeds.Add(speeds[j]);
                }
                way = round;
                speeds = roundSpeeds;
                nearest = 0;
            }
            else if (nearest > way.Count - 8)
            {
                nearest = 0;
            }
            Vector3 sets = Flat(way[Mathf.Min(nearest + 4, way.Count - 1)] - way[nearest]);
            driveFacing = sets.sqrMagnitude > 0.01f ? sets.normalized : Vector3.zero;
            if (!DriveTo(way[way.Count - 1], lap ? "a lap of the way you drove" : "driving the way you drove"))
                return false;
            driveRoute = way;
            driveRouteSpeed = speeds;
            driveRouteAt = nearest;
            // (Its own one put down along the way, facing the way it goes.)
            Vector3 along = Flat(way[Mathf.Min(nearest + 4, way.Count - 1)] - way[nearest]);
            if (placeAhead.sqrMagnitude > 0.01f && along.sqrMagnitude > 0.01f)
            {
                // (On the way, at the first spot along it with nothing standing on it: the player's own, left where they got out, was just where it began.)
                int spot = Mathf.Min(nearest + 1, way.Count - 1);
                for (int i = spot; i < way.Count - 6 && i < spot + 14; i++)
                {
                    bool clearHere = true;
                    foreach (Collider c in Physics.OverlapSphere(way[i] + Vector3.up * 0.7f, 1.4f, ~0, QueryTriggerInteraction.Ignore))
                    {
                        if (c != null && c.GetComponentInParent<RigManager>() != rig && (c.attachedRigidbody != null || c.GetComponentInParent<RigManager>() != null))
                            clearHere = false;
                    }
                    // (Nor where another bot has just said it will put its own: several setting off in
                    // the same moment all saw the same empty spot, and their karts came into being inside one another.)
                    foreach (KeyValuePair<IntPtr, KeyValuePair<Vector3, float>> said in driveSpots)
                    {
                        if (said.Key != rig.Pointer && Time.time < said.Value.Value && Flat(said.Value.Key - way[i]).magnitude < Tuning.F("drive.apart", 4f))
                            clearHere = false;
                    }
                    if (clearHere)
                    {
                        spot = i;
                        break;
                    }
                }
                driveSpots[rig.Pointer] = new KeyValuePair<Vector3, float>(way[spot], Time.time + 10f);
                Vector3 on = Flat(way[Mathf.Min(spot + 4, way.Count - 1)] - way[spot]);
                placeAhead = on.sqrMagnitude > 0.01f ? on.normalized : along.normalized;
                placeAt = way[spot];
                placeAtSet = true;
                driveRouteAt = spot;
            }
            return true;
        }

        // The way a drive must set off (zero: any): one standing by is only taken if it faces about that way.
        private Vector3 driveFacing;

        public bool DriveTo(Vector3 goal, string why, Transform after = null)
        {
            Lessons.Lesson lesson = Lessons.Drive();
            Transform head = Head;
            if (lesson == null || InLesson || dead || enemy || climbing || head == null)
                return false;
            Driving.Model m = Driving.Of(lesson);
            Poolee spare = FreeVehicle(m.barcode, Tuning.F("drive.spare", 5f));
            // (Whichever kind of thing it was shown to drive stands by: not only the kind shown last.
            // Shown a kart after a little car, it would no longer get into the little car.)
            if (spare == null)
            {
                foreach (Lessons.Lesson other in Lessons.Drives())
                {
                    Driving.Model its = Driving.Of(other);
                    Poolee by = other != lesson ? FreeVehicle(its.barcode, Tuning.F("drive.spare", 5f)) : null;
                    if (by != null)
                    {
                        lesson = other;
                        m = its;
                        spare = by;
                        break;
                    }
                }
            }
            // (One facing the wrong way for the way it is to go is left: turning round where there is no room had it shunting to and fro for minutes.)
            if (BotManager.OwnSpawns && spare != null && driveFacing.sqrMagnitude > 0.01f && Vector3.Dot(Flat(spare.transform.forward).normalized * m.forward, driveFacing) < 0.5f)
                spare = null;
            // (None of its own brought into being: the one standing by, or it does not drive.)
            if (spare == null && !BotManager.OwnSpawns)
            {
                driveFacing = Vector3.zero;
                return false;
            }
            Vector3 facing = driveFacing;
            driveFacing = Vector3.zero;
            // (From just before they sat down in it; or, sat in it a long while first, from a moment before they drove off.)
            float startAt = Mathf.Max(0f, Mathf.Max(m.seatAt - 0.5f, m.driveFrom - 3f));
            // (Nowhere about it with room for one of its own: not here. It goes on on foot, and drives from somewhere more open.)
            Vector3 offWay = spare == null ? DriveOffWay(goal) : Vector3.zero;
            if (spare == null && facing.sqrMagnitude > 0.01f && offWay.sqrMagnitude > 0.01f)
                offWay = facing;
            if (spare == null && offWay.sqrMagnitude < 0.01f)
            {
                if (Time.time - driveNoRoomSaid > 20f)
                {
                    driveNoRoomSaid = Time.time;
                    Lab.Event("driving: no room here for a " + lesson.name + ", not from here");
                }
                driveNextAt = Time.time + 12f;
                return false;
            }
            // (One of its own to come: any left standing empty just here is cleared away first.
            // The new one was put down on top of the last, and it sat in the wrong one of the two, shut in by the other.)
            if (spare == null)
                ClearEmptyVehicles(m.barcode, 7f);
            if (!UseLesson(lesson, false, startAt, 0, null))
                return false;
            Clone.Frame begin = FrameAt(lesson.take, startAt);
            cloneFrom = begin.pelvis;
            cloneYaw = Mathf.DeltaAngle(Recorder.Yaw(begin.playspace * begin.head.rotation * Vector3.forward), Recorder.Yaw(head.forward));
            driveModel = m;
            driveMeant = true;
            driving = driveStopping = driveOut = false;
            driveGoal = goal;
            driveAfter = after;
            driveWhy = why;
            driveLiveSaid = false;
            driveRoute = after != null ? new List<Vector3>() : null;
            driveRouteSpeed = null;
            driveRouteAt = 0;
            driveTrail = after != null;
            driveWaitSince = -1f;
            driveNextAt = Time.time + (DriveSkill.Practice ? 4f : Tuning.F("drive.every", 150f) * Random(0.7f, 1.5f));
            placeAhead = offWay;
            if (spare != null && m.sit != null)
                lessonThings[m.sit.barcode + "#" + Mathf.Max(0, Clone.ThingNumber(m.sit))] = spare;
            Lab.Event("driving: " + why + " (" + Flat(goal - Feet).magnitude.ToString("0") + " m), in " + (spare != null ? "the " + lesson.name + " standing by" : "a " + lesson.name + " of its own"));
            return true;
        }

        // Which way to put its own one from it: where there is room for it and open ground on from there, towards where it is going if that may be.
        private Vector3 DriveOffWay(Vector3 goal)
        {
            Vector3 feet = rig.physicsRig != null && rig.physicsRig.centerOfPressure != null ? rig.physicsRig.centerOfPressure.position : Feet;
            Vector3 way = Flat(goal - feet);
            try
            {
                drivePath ??= new NavMeshPath();
                if (NavMesh.SamplePosition(feet, out NavMeshHit from, 2f, NavMesh.AllAreas) && NavMesh.SamplePosition(goal, out NavMeshHit to, 4f, NavMesh.AllAreas)
                    && NavMesh.CalculatePath(from.position, to.position, NavMesh.AllAreas, drivePath) && drivePath.status != NavMeshPathStatus.PathInvalid)
                {
                    Vector3[] found = drivePath.corners;
                    if (found != null && found.Length >= 2 && Flat(found[1] - feet).magnitude > 1f)
                        way = Flat(found[1] - feet);
                }
            }
            catch (Exception)
            {
            }
            if (way.sqrMagnitude < 0.01f)
                way = Quaternion.Euler(0f, bodyYaw, 0f) * Vector3.forward;
            way.Normalize();
            Vector3 low = feet + Vector3.up * 0.6f;
            Vector3 best = Vector3.zero;
            float most = float.MinValue;
            for (int i = 0; i < 16; i++)
            {
                Vector3 dir = Quaternion.Euler(0f, i * 22.5f, 0f) * way;
                float clear = 14f;
                foreach (RaycastHit hit in Physics.SphereCastAll(low, 0.45f, dir, 14f, ~0, QueryTriggerInteraction.Ignore))
                {
                    if (hit.collider == null || hit.distance <= 0f || hit.collider.GetComponentInParent<RigManager>() == rig || hit.normal.y > 0.55f)
                        continue;
                    clear = Mathf.Min(clear, hit.distance);
                }
                // (Room for the thing itself, and ground under it.)
                if (clear < 4.2f || !Physics.Raycast(feet + dir * 2.2f + Vector3.up * 1f, Vector3.down, 2.5f, ~0, QueryTriggerInteraction.Ignore))
                    continue;
                float score = clear / 14f + 0.8f * Vector3.Dot(dir, way);
                if (score > most)
                {
                    most = score;
                    best = dir;
                }
            }
            return best;
        }

        private void ClearEmptyVehicles(string barcode, float within)
        {
            try
            {
                Transform you = Player.Head;
                HashSet<IntPtr> looked = new HashSet<IntPtr>();
                foreach (Collider c in Physics.OverlapSphere(Feet, within, ~0, QueryTriggerInteraction.Ignore))
                {
                    Poolee p = c != null ? c.GetComponentInParent<Poolee>() : null;
                    if (p == null || !looked.Add(p.Pointer) || p.SpawnableCrate == null || p.SpawnableCrate.Barcode.ID != barcode)
                        continue;
                    bool taken = HeldByAnyone(p.transform, null);
                    foreach (Seat s in p.GetComponentsInChildren<Seat>(true))
                        taken |= s != null && s.rigManager != null;
                    // (Not one the player is by: that may be theirs.)
                    if (taken || (you != null && Flat(p.transform.position - you.position).magnitude < 4f))
                        continue;
                    // (Nor one another bot is getting into: four setting off together took one another's karts from under them.)
                    if (AnotherDriverBy(p.transform.position, Tuning.F("drive.theirs", 5f)))
                        continue;
                    p.Despawn();
                }
            }
            catch (Exception)
            {
            }
        }

        // Where each bot has said it will put its kart down, and till when that holds.
        private static readonly Dictionary<IntPtr, KeyValuePair<Vector3, float>> driveSpots = new Dictionary<IntPtr, KeyValuePair<Vector3, float>>();

        /// <summary>Whether this one is in a driving lesson (getting into something, or driving it).</summary>
        public bool DrivingLesson => InLesson && cloneLesson != null && cloneLesson.purpose == "drive";

        private bool AnotherDriverBy(Vector3 at, float within)
        {
            foreach (Bot other in BotManager.All)
            {
                if (other != this && !other.dead && other.DrivingLesson && Flat(other.Feet - at).magnitude < within)
                    return true;
            }
            return false;
        }

        // One of those standing near with nobody in it (and nobody's hands on it).
        private Poolee FreeVehicle(string barcode, float within)
        {
            if (string.IsNullOrEmpty(barcode))
                return null;
            Poolee best = null;
            float nearest = within;
            HashSet<IntPtr> looked = new HashSet<IntPtr>();
            foreach (Collider c in Physics.OverlapSphere(Feet, within, ~0, QueryTriggerInteraction.Ignore))
            {
                Poolee p = c != null ? c.GetComponentInParent<Poolee>() : null;
                if (p == null || !looked.Add(p.Pointer) || p.SpawnableCrate == null || p.SpawnableCrate.Barcode.ID != barcode)
                    continue;
                bool taken = false;
                foreach (Seat s in p.GetComponentsInChildren<Seat>(true))
                    taken |= s != null && s.rigManager != null;
                if (taken || HeldByAnyone(p.transform, null) || p.transform.up.y < 0.9f || NotItsToTake(p))
                    continue;
                if (AnotherDriverBy(p.transform.position, 2.5f))
                    continue;
                float d = Flat(p.transform.position - Feet).magnitude;
                if (d < nearest)
                {
                    nearest = d;
                    best = p;
                }
            }
            return best;
        }

        // The frame its driving is worked out in (see Driving): its seat, or (an older recording) its hips, facing as they face.
        private Pose DriveFrame(Seat seat)
        {
            if (driveModel.inSeat)
                return Pose.Of(seat.transform);
            Rigidbody pelvis = Pelvis();
            Vector3 ahead = pelvis != null ? Flat(pelvis.rotation * Vector3.forward) : Flat(seat.transform.forward);
            return new Pose(pelvis != null ? pelvis.position : seat.transform.position, ahead.sqrMagnitude > 0.001f ? Quaternion.LookRotation(ahead.normalized, Vector3.up) : Quaternion.identity);
        }

        /// <summary>
        /// Once a frame in a lesson done to drive somewhere. True while the bot has the driving
        /// (the lesson is held where the player drove off, and nothing more of it is played).
        /// </summary>
        private bool DriveStep(float dt)
        {
            Driving.Model m = driveModel;
            if (m == null)
                return false;
            Seat seat = rig.activeSeat;
            if (!driving)
            {
                driveClockAt = m.driveFrom;
                if (Time.time - cloneStart < m.driveFrom)
                    return false;
                // The moment they drove off: the driving is its own from here, once it is sat in it.
                cloneStart = Time.time - driveClockAt;
                if (driveWaitSince < 0f)
                    driveWaitSince = Time.time;
                if (seat == null || !seat.seated)
                {
                    if (Time.time - driveWaitSince > 10f)
                        EndLesson("driving: could not get into it");
                    return false;
                }
                driving = true;
                driveTookAt = Time.time;
                skill = DriveSkill.Begin(rig.Pointer, out skillTrying);
                // (Scored: a drive of its own to somewhere, far enough to tell anything by.)
                skillFar = Flat(driveGoal - seat.transform.position).magnitude;
                skillScored = driveAfter == null && (driveRoute != null || skillFar > 18f);
                skillRouteFrom = driveRouteAt;
                skillBacks = skillAbouts = 0;
                driveGo = driveBack = driveTurn = driveTurnNow = 0f;
                driveStuckFor = driveOverFor = driveYouOutFor = 0f;
                driveBackUntil = 0f;
                driveBacks = 0;
                driveWayAt = 0f;
                driveWay = new Vector3[0];
                driveWayFound = -100f;
                driveHandOff[0] = driveHandOff[1] = 0f;
                driveBackFrom = seat.transform.position;
                driveYawHas = false;
                driveAbout = false;
                driveAboutAt = Time.time - 2f;
                driveSpin = 0f;
                Lab.Event("driving: sat in it, off it goes");
            }
            cloneStart = Time.time - driveClockAt;
            // The player driving now, with it learning from them: how they are driving is taken up as they go.
            Driving.Model live = Driving.Live;
            if (live != null && live != m && live.ok && live.barcode == m.barcode)
            {
                if (!driveLiveSaid)
                    Lab.Event("driving: taking up how you are driving now (" + (live.inSeat ? "hands exact" : "hands rough") + ", " + live.top.ToString("0.0") + " m/s, turns " + live.turnLeft.ToString("0.00") + " to " + live.turnRight.ToString("0.00") + ")");
                driveLiveSaid = true;
                driveModel = m = live;
            }
            if (driveOut)
            {
                if (Time.time >= driveOutAt)
                    EndLesson("driving: " + driveEndWhy);
                return true;
            }
            if (seat == null)
            {
                EndLesson("driving: out of its seat");
                return true;
            }
            Vector3 ahead = Flat(seat.transform.forward).normalized * m.forward;
            Rigidbody body = seat.seatRb != null ? seat.seatRb : seat.GetComponentInParent<Rigidbody>();
            Vector3 at = seat.transform.position;
            float speed = body != null ? Vector3.Dot(body.velocity, ahead) : 0f;
            DriveHold(seat, dt);
            // How much it turns for the turning it puts on: found as it drives (and kept, for that kind of thing).
            float yawNow = Recorder.Yaw(ahead);
            if (driveYawHas && dt > 0.0001f)
            {
                float spin = Mathf.DeltaAngle(driveYawWas, yawNow) * Mathf.Deg2Rad / dt;
                driveSpin = Mathf.Lerp(driveSpin, spin, Mathf.Clamp01(dt * 4f));
                if (speed > 2f && Mathf.Abs(driveTurnNow) > 0.05f && Mathf.Abs(driveTurnNow - driveTurn) < 0.05f && Time.time > driveBackUntil + 1f)
                {
                    float answer = Mathf.Clamp(driveSpin / speed / driveTurnNow, 0.5f, 1.5f);
                    m.answers = Mathf.Lerp(m.answers, answer, Mathf.Clamp01(dt * 0.4f));
                }
            }
            driveYawWas = yawNow;
            driveYawHas = true;

            Transform you = Player.Head;
            bool youSat = Player.RigManager != null && Player.RigManager.activeSeat != null;
            if (driveAfter != null)
                driveGoal = driveAfter.position - Vector3.up * 1.2f;
            float far = Flat(driveGoal - at).magnitude;
            // (After the player: along the very way they go, round what they went round.)
            if (driveTrail && driveRoute != null && (driveRoute.Count == 0 || Flat(driveGoal - driveRoute[driveRoute.Count - 1]).magnitude > 1.5f))
            {
                driveRoute.Add(driveGoal);
                if (driveRoute.Count > 400)
                {
                    driveRoute.RemoveAt(0);
                    driveRouteAt = Mathf.Max(0, driveRouteAt - 1);
                }
            }
            bool onRoad = driveRoute != null && !driveTrail && driveRouteAt < driveRoute.Count - 3;

            // Stopping, then out of it.
            if (driveStopping)
            {
                driveGo = 0f;
                driveBack = m.hasBack && speed > 1f ? 0.7f : 0f;
                driveTurn = 0f;
                if (Mathf.Abs(speed) < 0.8f || Time.time - driveStopSince > 3f)
                    DriveGetOut(seat, false);
                return true;
            }
            // Turned over.
            driveOverFor = seat.transform.up.y < 0.4f ? driveOverFor + dt : 0f;
            if (driveOverFor > 1.5f)
            {
                driveEndWhy = "it turned over";
                DriveGetOut(seat, true);
                return true;
            }
            if (Time.time - driveTookAt > Tuning.F("drive.longest", 150f))
            {
                DriveStop("long enough");
                return true;
            }
            // There.
            if (driveAfter == null && !onRoad && far < Tuning.F("drive.arrive", 3.5f))
            {
                DriveStop("there (" + driveWhy + ")");
                return true;
            }
            if (driveAfter != null)
            {
                driveYouOutFor = youSat ? 0f : driveYouOutFor + dt;
                float ring = Tuning.F("drive.near", 6f);
                if (far < ring)
                {
                    // Up with them: it waits, sat in it, for as long as they sit in theirs.
                    driveGo = 0f;
                    driveBack = m.hasBack && speed > 1f ? 0.7f : 0f;
                    driveTurn = 0f;
                    if (driveYouOutFor > 1.5f && Mathf.Abs(speed) < 0.8f)
                        DriveStop("with you");
                    return true;
                }
            }

            // Its way there, and the point on it that it steers for.
            float reach = Mathf.Clamp(2.5f + Mathf.Abs(speed) * skill[DriveSkill.Look], 3f, 12f);
            Vector3 aim = DriveAim(at, reach);
            Vector3 to = Flat(aim - at);
            float off = to.sqrMagnitude > 0.01f ? Vector3.SignedAngle(ahead, to.normalized, Vector3.up) : 0f;
            // What is ahead: how far it is clear, each way it might turn.
            if (Time.time >= driveFeelAt)
            {
                driveFeelAt = Time.time + 0.1f;
                feelLength = Mathf.Clamp(4f + Mathf.Abs(speed) * 1.6f, 5f, 22f);
                DriveFeel(seat, at, ahead, feelLength);
            }
            float most = Tuning.F("drive.turn", 1.45f);
            float tightLeft = m.turnLeft * most, tightRight = m.turnRight * most;
            float clearAhead = feelClear[0];

            // Backing out of something.
            if (Time.time < driveBackUntil)
            {
                // (Backwards at a walk, no faster.)
                driveGo = speed < -Tuning.F("drive.back.fast", 2.5f) - 1f ? 0.4f : 0f;
                driveBack = speed > -Tuning.F("drive.back.fast", 2.5f) ? 0.75f : 0f;
                driveTurn = driveBackTurn;
                // (Nothing more to be had backwards; or, turning about, round far enough.)
                driveStuckFor = Mathf.Abs(speed) < 0.25f ? driveStuckFor + dt : 0f;
                if (driveStuckFor > 1.2f || feelBehind < 1.3f || (driveAbout && Mathf.Abs(off) < 25f))
                {
                    driveBackUntil = 0f;
                    driveAboutAt = Time.time;
                    driveStuckFor = 0f;
                }
                DriveSay(far, speed, clearAhead, off, seat);
                return true;
            }
            bool pressed = driveGo > 0.45f && Mathf.Abs(speed) < 0.35f;
            driveStuckFor = pressed ? driveStuckFor + dt : Mathf.Max(0f, driveStuckFor - dt * 2f);
            bool walled = clearAhead < Tuning.F("drive.wall", 1.5f) && speed < 1.5f && Mathf.Abs(off) < 60f && feelBehind > 1.5f;
            if (driveStuckFor > 1.3f || (walled && Time.time - driveTookAt > 1.5f && Time.time - driveAboutAt > 1.5f))
            {
                if (Flat(at - driveBackFrom).magnitude > 6f)
                    driveBacks = 0;
                driveBackFrom = at;
                driveBacks++;
                skillBacks++;
                driveStuckFor = 0f;
                if (!m.hasBack || driveBacks > Tuning.F("drive.backs", 5f))
                {
                    SelfTaught.Bad(at);
                    DriveStop(m.hasBack ? "it cannot get on" : "stopped by something (it was never shown how to back it)");
                    return true;
                }
                // (Backwards, its nose swinging to the more open side, or towards where it is going.)
                float open = 0f;
                for (int i = 1; i < feelAngles.Length; i++)
                    open += feelClear[i] * Mathf.Sign(feelAngles[i]);
                float swing = Mathf.Abs(open) > 2f ? Mathf.Sign(open) : off >= 0f ? 1f : -1f;
                driveBackTurn = swing > 0f ? tightLeft : tightRight;
                driveAbout = false;
                driveBackUntil = Time.time + Tuning.F("drive.back", 1.7f);
                Lab.Event("driving: stopped by something, backing out (" + driveBacks + ")");
                return true;
            }

            // Where it is going is off to the side or behind, nearer than it can turn to: it
            // backs round (its nose swinging that way) till it faces it, then goes on.
            float tightest = Mathf.Max(0.05f, (off > 0f ? tightRight : -tightLeft) * Mathf.Clamp(m.answers, 0.5f, 1f));
            float needs = 2f * Mathf.Sin(Mathf.Min(90f, Mathf.Abs(off)) * Mathf.Deg2Rad) / Mathf.Max(2f, to.magnitude);
            float aboutAt = driveRoute != null ? Tuning.F("drive.about.road", 120f) : 75f;
            if (m.hasBack && Mathf.Abs(off) > aboutAt && needs > tightest * 1.2f && Time.time - driveAboutAt > 2.5f && feelBehind > 2.5f && speed < 4f)
            {
                driveAbout = true;
                driveBackTurn = off > 0f ? tightLeft : tightRight;
                driveBackUntil = Time.time + Tuning.F("drive.about", 3.5f);
                driveStuckFor = 0f;
                skillAbouts++;
                // (Round and round: it has lost its way. Out, and begun again from somewhere better.)
                if (skillAbouts > Tuning.F("drive.lost", 8f))
                {
                    DriveStop("it cannot get on (lost its way)");
                    return true;
                }
                Lab.Event("driving: its way is " + off.ToString("0") + " deg off, backing round to face it");
                return true;
            }
            // (Too fast to back round yet: slower first.)
            bool slowFor = m.hasBack && Mathf.Abs(off) > 75f && needs > tightest * 1.2f;

            // Which way: for its point, if that is clear; if not, the clear way nearest to it.
            float need = Mathf.Min(feelLength, Mathf.Max(3f, to.magnitude));
            float want = Mathf.Clamp(off, -75f, 75f);
            int pick = 0;
            float best = float.MinValue;
            for (int i = 0; i < feelAngles.Length; i++)
            {
                float score = Mathf.Min(feelClear[i], need) / need * 1.6f - Mathf.Abs(feelAngles[i] - want) / 75f;
                if (score > best)
                {
                    best = score;
                    pick = i;
                }
            }
            float steerFor = off, over = Mathf.Max(3f, to.magnitude);
            bool dodging = Mathf.Abs(feelAngles[pick] - want) > 10f;
            if (dodging)
            {
                steerFor = feelAngles[pick];
                over = Mathf.Max(2.5f, feelClear[pick] * 0.7f);
            }
            float bend = Mathf.Abs(steerFor) > 100f ? Mathf.Sign(steerFor) * 10f : 2f * Mathf.Sin(steerFor * Mathf.Deg2Rad) / over;
            // (What it takes for it to go straight is found as it goes: one that pulls to a side is held against that.)
            float asked = bend * skill[DriveSkill.Steer] / Mathf.Clamp(m.answers, 0.6f, 1.5f);
            // (Only going steadily and near enough straight; and only a little for one whose hands are placed exactly: through bends it chased its own tail.)
            float trimMost = m.inSeat ? 0.05f : 0.3f;
            if (speed > 2.5f && Mathf.Abs(bend) < 0.05f && Mathf.Abs(driveTurnNow - driveTurn) < 0.02f && Time.time > driveBackUntil + 2f)
                m.trim = Mathf.Clamp(m.trim + (bend - driveSpin / speed) * Tuning.F("drive.trim", 0.9f) * (m.inSeat ? 0.3f : 1f) * dt, -trimMost, trimMost);
            m.trim = Mathf.Clamp(m.trim, -trimMost, trimMost);
            driveTurn = Mathf.Clamp(asked + m.trim, tightLeft, tightRight);
            // (No more turning than it can take at the speed it is going: turned hard at speed, it spins round.)
            float holds = m.side * skill[DriveSkill.Grip] / Mathf.Max(4f, speed * speed);
            driveTurn = Mathf.Clamp(driveTurn, -holds, holds);

            // How fast: no faster than it goes, than its turn is taken, than what is ahead allows, than coming up to where it is going.
            float pace = m.top * skill[DriveSkill.Pace];
            float through = Mathf.Sqrt(m.side * skill[DriveSkill.Side] / Mathf.Max(0.02f, Mathf.Abs(driveTurnNow)));
            // (It only leaves the line it is on as fast as it turns: what is dead ahead counts, even turning away from it.)
            float openWay = pick == 0 ? feelClear[0] : Mathf.Lerp(feelClear[0], feelClear[pick], 0.5f);
            float room = Mathf.Sqrt(2f * skill[DriveSkill.Brake] * Mathf.Max(0.15f, openWay - 2.5f));
            float arriving = 1f + Mathf.Sqrt(2f * 2.5f * Mathf.Max(0f, far - (driveAfter != null ? Tuning.F("drive.near", 6f) : Tuning.F("drive.arrive", 3.5f))));
            if (onRoad)
                arriving = 99f;
            float fast = Mathf.Min(Mathf.Min(pace, through), Mathf.Min(room, arriving));
            // (Along the player's way: no faster than they took that part of it.)
            if (onRoad && driveRouteSpeed != null && driveRouteAt < driveRouteSpeed.Count)
            {
                float theirs = driveRouteSpeed[driveRouteAt];
                for (int i = driveRouteAt; i < driveRouteSpeed.Count && i < driveRouteAt + 5; i++)
                    theirs = Mathf.Min(theirs, driveRouteSpeed[i]);
                fast = Mathf.Min(fast, Mathf.Max(2f, theirs + 0.5f));
            }
            if (slowFor)
                fast = Mathf.Min(fast, 2.5f);
            // (Just backed out of something: gently on from there, not straight back into it at speed.)
            if (Time.time - driveAboutAt < Tuning.F("drive.careful", 3.5f) && driveBacks > 0)
                fast = Mathf.Min(fast, 2.8f);
            // The bends to come: slowed for in good time, not found out about in the middle of them.
            fast = Mathf.Min(fast, BendsAhead(at, speed, m));
            float push = Mathf.Clamp((fast - speed) * 0.6f, -1f, 1f);
            float go = Mathf.Max(0f, push);
            if (speed < 0.6f && fast > 0.6f)
                go = Mathf.Max(go, 0.6f);
            driveGo = Mathf.MoveTowards(driveGo, go, 3f * dt);
            driveBack = m.hasBack && speed > fast + 0.8f && speed > 1f ? Mathf.Clamp01((speed - fast) * 0.5f) : 0f;
            if (driveBack > 0f)
                driveGo = 0f;
            DriveSay(far, speed, clearAhead, off, seat);
            return true;
        }

        private Vector3 driveAimLeft, driveAimRight;
        // The thing it last got out of, and when (still rolling, it is not knocked down by it).
        private Transform leftVehicle;
        private float leftVehicleAt = -100f;
        private float driveYawWas, driveSpin;
        private bool driveYawHas;

        private void DriveSay(float far, float speed, float clear, float off, Seat seat)
        {
            if (Time.time - driveSaidAt < 2f)
                return;
            driveSaidAt = Time.time;
            Transform root = controls.vrRoot;
            Lab.Event("driving: " + far.ToString("0") + " m to go, " + speed.ToString("0.0") + " m/s, go " + driveGo.ToString("0.0") + " back " + driveBack.ToString("0.0")
                      + ", turning " + driveTurnNow.ToString("0.00") + " (wants " + driveTurn.ToString("0.00") + "), its way " + off.ToString("0") + " deg off, clear " + clear.ToString("0.0") + " m, way of " + driveWay.Length + ""
                      + ", behind " + feelBehind.ToString("0.0")
                      + (BotManager.Missed > 0 ? ", put on by hand " + BotManager.Missed : "")
                      + ", answers " + driveModel.answers.ToString("0.00") + " trim " + driveModel.trim.ToString("0.00") + (driveRoute != null ? ", along a way " + driveRouteAt + "/" + driveRoute.Count : "")
                      + (Time.time < driveBackUntil ? ", backing" : "") + ", hands " + (LeftHand != null && LeftHand.m_CurrentAttachedGO != null ? "L" : "-") + (RightHand != null && RightHand.m_CurrentAttachedGO != null ? "R" : "-")
                      + ", play space " + Mathf.DeltaAngle(seat.transform.eulerAngles.y, root.eulerAngles.y).ToString("0") + " deg from its seat");
        }

        private void DriveStop(string why)
        {
            if (driveStopping)
                return;
            driveStopping = true;
            driveStopSince = Time.time;
            driveEndWhy = why;
        }

        private void DriveGetOut(Seat seat, bool atOnce)
        {
            // How the drive went, for getting better at it.
            if (skillScored)
            {
                skillScored = false;
                bool failed = atOnce || driveEndWhy.Contains("cannot get on") || driveEndWhy.Contains("stopped by something");
                float covered = driveRoute != null ? Mathf.Max(0, driveRouteAt - skillRouteFrom) * 1.5f : Mathf.Max(0f, skillFar - Flat(driveGoal - seat.transform.position).magnitude);
                // (Never got going at all, shut in where it began: nothing to tell about how it drives.)
                // (Nor one given up near where it began: that says where it began, not how it drives.)
                if (covered > 12f && !(failed && covered < 60f))
                    DriveSkill.End(rig.Pointer, skillTrying, covered, Time.time - driveTookAt, skillBacks, skillAbouts, failed);
            }
            driveGo = driveBack = 0f;
            foreach (Hand hand in new[] { LeftHand, RightHand })
            {
                if (hand != null && hand.m_CurrentAttachedGO != null)
                    FreeHand(hand);
            }
            cloneHeld[0] = cloneHeld[1] = null;
            try
            {
                seat.EgressRig(atOnce);
            }
            catch (Exception)
            {
            }
            Poolee was = seat.GetComponentInParent<Poolee>();
            leftVehicle = was != null ? was.transform : seat.transform.root;
            // (Not left standing in the road for the next one to run into: cleared away after a while, unless someone gets into it.)
            if (was != null)
                Lessons.Litter(was, Tuning.F("drive.litter", 5f));
            leftVehicleAt = Time.time;
            driveOut = true;
            driveOutAt = Time.time + (atOnce ? 0.4f : 1.3f);
        }

        // Its hands on the thing where the player had theirs (taken again, if they come off).
        private void DriveHold(Seat seat, float dt)
        {
            if (Time.time < driveGrabAt)
                return;
            driveGrabAt = Time.time + 0.25f;
            Poolee poolee = seat.GetComponentInParent<Poolee>();
            Transform thing = poolee != null ? poolee.transform : seat.transform.root;
            Hand[] hands = { LeftHand, RightHand };
            Clone.Event[] grabs = { driveModel.leftGrab, driveModel.rightGrab };
            for (int i = 0; i < 2; i++)
            {
                Hand hand = hands[i];
                if (hand == null || grabs[i] == null)
                    continue;
                if (hand.m_CurrentAttachedGO != null)
                {
                    driveHandOff[i] = 0f;
                    continue;
                }
                driveHandOff[i] += 0.25f;
                Grip grip = null;
                Grip[] grips = Clone.GripsOf(thing);
                string name = Clone.GripName(grabs[i]);
                foreach (Grip g in grips)
                {
                    if (g != null && name != null && g.name == name && (grip == null || !g.HasAttachedHands()))
                        grip = g;
                }
                if (grip == null && grabs[i].grip >= 0 && grabs[i].grip < grips.Length)
                    grip = grips[grabs[i].grip];
                if (grip == null)
                    continue;
                // (When the hand has come to it; or, never quite coming to it, after a moment anyway.)
                float apart = (grip.transform.position - hand.transform.position).magnitude;
                if (apart > Tuning.F("lesson.reach", 0.4f) && driveHandOff[i] < 1.5f)
                    continue;
                if (Grab(hand, grip))
                {
                    cloneHeld[i] = grip;
                    Lab.Event("driving: its " + (i == 0 ? "left" : "right") + " hand on the " + grip.name + " (" + apart.ToString("0.00") + " m off)");
                }
            }
        }

        // How fast it may go now for the bends of its way ahead: each bend has a speed it can be
        // taken at, and it must be down to that by the time it is there (braking as hard as it
        // brakes, begun as early as it has found it needs to).
        private float BendsAhead(Vector3 at, float speed, Driving.Model m)
        {
            float limit = 99f;
            float side = m.side * skill[DriveSkill.Side];
            float brake = skill[DriveSkill.Brake] / Mathf.Max(0.3f, skill[DriveSkill.Early]);
            float see = 6f + Mathf.Abs(speed) * 3f;
            List<Vector3> way = driveRoute;
            int from = driveRouteAt;
            if (way == null || way.Count < 3)
            {
                way = bendWay;
                way.Clear();
                way.Add(at);
                for (int i = driveWayNext; i < driveWay.Length; i++)
                    way.Add(driveWay[i]);
                from = 0;
            }
            float gone = Flat(way[Mathf.Min(from, way.Count - 1)] - at).magnitude;
            for (int i = Mathf.Max(1, from); i < way.Count - 1 && gone < see; i++)
            {
                // The turn of the way at this point, over a few steps of it either side.
                int a = Mathf.Max(0, i - 2), b = Mathf.Min(way.Count - 1, i + 2);
                Vector3 before = Flat(way[i] - way[a]), after = Flat(way[b] - way[i]);
                if (i > from)
                    gone += Flat(way[i] - way[i - 1]).magnitude;
                if (before.sqrMagnitude < 0.04f || after.sqrMagnitude < 0.04f)
                    continue;
                float turn = Vector3.Angle(before, after) * Mathf.Deg2Rad;
                float over = Mathf.Max(3f, (before.magnitude + after.magnitude) * 0.5f);
                float bend = turn / over;
                if (bend < 0.04f)
                    continue;
                float through = Mathf.Sqrt(side / bend);
                float now = Mathf.Sqrt(through * through + 2f * brake * Mathf.Max(0f, gone - 1.5f));
                limit = Mathf.Min(limit, now);
            }
            return Mathf.Max(2f, limit);
        }

        private readonly List<Vector3> bendWay = new List<Vector3>();

        // The point so far on along its way: over the level's own ways where it has them, straight there where not.
        private Vector3 DriveAim(Vector3 at, float reach)
        {
            // Along a way it has (the player's): the point so far on along that.
            if (driveRoute != null && driveRoute.Count > 1)
            {
                // (Past each point as it comes near it, or nearer the next.)
                // (And past any it has gone by, the way the way goes: steering back for a point
                // behind it, it turned right round and met the way coming the other way.)
                while (driveRouteAt < driveRoute.Count - 1
                       && (Flat(driveRoute[driveRouteAt] - at).magnitude < 3f || Flat(driveRoute[driveRouteAt + 1] - at).magnitude < Flat(driveRoute[driveRouteAt] - at).magnitude
                           || Vector3.Dot(Flat(driveRoute[driveRouteAt + 1] - driveRoute[driveRouteAt]), Flat(at - driveRoute[driveRouteAt])) > 0f))
                    driveRouteAt++;
                Vector3 fromR = at;
                float leftR = reach;
                for (int i = driveRouteAt; i < driveRoute.Count; i++)
                {
                    Vector3 step = Flat(driveRoute[i] - fromR);
                    float d = step.magnitude;
                    if (d >= leftR && d > 0.001f)
                        return fromR + step / d * leftR;
                    leftR -= d;
                    fromR = new Vector3(driveRoute[i].x, fromR.y, driveRoute[i].z);
                }
                // (At the end of a trail: straight for the one it is after.)
                return driveTrail ? driveGoal : driveRoute[driveRoute.Count - 1];
            }
            if (Time.time >= driveWayAt || driveWay.Length == 0)
            {
                driveWayAt = Time.time + 1f;
                bool foundWay = false;
                try
                {
                    drivePath ??= new NavMeshPath();
                    // (Only a way that begins where it is and goes all the way: one that stops short would have it circling the end of it.)
                    if (NavMesh.SamplePosition(at, out NavMeshHit from, 2f, NavMesh.AllAreas) && Mathf.Abs(from.position.y - at.y) < 1.5f
                        && NavMesh.SamplePosition(driveGoal, out NavMeshHit to, 4f, NavMesh.AllAreas)
                        && NavMesh.CalculatePath(from.position, to.position, NavMesh.AllAreas, drivePath)
                        && drivePath.status == NavMeshPathStatus.PathComplete)
                    {
                        Vector3[] found = drivePath.corners;
                        if (found != null && found.Length >= 2 && Flat(found[found.Length - 1] - driveGoal).magnitude < 4f)
                        {
                            driveWay = found;
                            driveWayNext = 1;
                            driveWayFound = Time.time;
                            foundWay = true;
                        }
                    }
                }
                catch (Exception)
                {
                }
                // (No way found this time: the one it was on is kept a while, not swapped to and
                // fro with going straight there, which had it turning one way then the other.)
                if (!foundWay && (driveWay.Length == 0 || Time.time - driveWayFound > 6f || driveAfter != null))
                {
                    driveWay = new[] { at, driveGoal };
                    driveWayNext = 1;
                }
            }
            while (driveWayNext < driveWay.Length - 1 && Flat(driveWay[driveWayNext] - at).magnitude < 2.5f)
                driveWayNext++;
            Vector3 from2 = at;
            float left = reach;
            for (int i = driveWayNext; i < driveWay.Length; i++)
            {
                Vector3 step = Flat(driveWay[i] - from2);
                float d = step.magnitude;
                if (d >= left && d > 0.001f)
                    return from2 + step / d * left;
                left -= d;
                from2 = new Vector3(driveWay[i].x, from2.y, driveWay[i].z);
            }
            return driveWay[driveWay.Length - 1];
        }

        // How far it is clear each way ahead: nothing standing in the way, and ground to drive on.
        private void DriveFeel(Seat seat, Vector3 at, Vector3 ahead, float length)
        {
            Poolee poolee = seat.GetComponentInParent<Poolee>();
            Transform thing = poolee != null ? poolee.transform : seat.transform.root;
            Vector3 from = at + Vector3.up * Tuning.F("drive.feel.up", 0.35f);
            for (int i = 0; i < feelAngles.Length; i++)
            {
                Vector3 dir = Quaternion.Euler(0f, feelAngles[i], 0f) * ahead;
                float clear = length;
                foreach (RaycastHit hit in Physics.SphereCastAll(from, 0.25f, dir, length, ~0, QueryTriggerInteraction.Ignore))
                {
                    Collider c = hit.collider;
                    if (c == null || hit.distance >= clear || hit.distance <= 0f)
                        continue;
                    if (c.transform.IsChildOf(thing) || c.GetComponentInParent<RigManager>() == rig)
                        continue;
                    // (Not ground it can drive up, nor small things it will knock aside.)
                    if (hit.normal.y > 0.55f)
                        continue;
                    Rigidbody rb = c.attachedRigidbody;
                    if (rb != null && !rb.isKinematic && rb.mass < 8f && c.GetComponentInParent<RigManager>() == null)
                        continue;
                    clear = hit.distance;
                }
                // (Ground to drive on, a little way out.)
                float out2 = Mathf.Min(clear, 4f);
                if (out2 > 1.5f && !Physics.Raycast(from + dir * out2 + Vector3.up * 0.8f, Vector3.down, 3.2f, ~0, QueryTriggerInteraction.Ignore))
                    clear = Mathf.Min(clear, 1.2f);
                feelClear[i] = clear;
            }
            // (And behind it, for backing.)
            feelBehind = 5f;
            foreach (RaycastHit hit in Physics.SphereCastAll(from, 0.3f, -ahead, 5f, ~0, QueryTriggerInteraction.Ignore))
            {
                Collider c = hit.collider;
                if (c == null || hit.distance <= 0f || c.transform.IsChildOf(thing) || c.GetComponentInParent<RigManager>() == rig || hit.normal.y > 0.55f)
                    continue;
                feelBehind = Mathf.Min(feelBehind, hit.distance);
            }
            if (!Physics.Raycast(from - ahead * 2.5f + Vector3.up * 0.8f, Vector3.down, 3.2f, ~0, QueryTriggerInteraction.Ignore))
                feelBehind = Mathf.Min(feelBehind, 1f);
        }

        /// <summary>Its head and hands and what it has on its controls, put on it each frame it drives (in place of the lesson's).</summary>
        private void DrivePose()
        {
            Driving.Model m = driveModel;
            Seat seat = rig.activeSeat;
            poseWhy = m == null ? "no model" : seat == null ? "no seat" : "";
            if (m == null || seat == null || !CloneAt(driveClockAt, out Clone.Frame a, out Clone.Frame b, out float k))
            {
                if (poseWhy.Length == 0)
                    poseWhy = "no frame at " + driveClockAt.ToString("0.0");
                return;
            }
            posed++;
            rig.remapHeptaRig.inWeight = 1f;
            Pose frame = DriveFrame(seat);
            float swing = (m.turnRight - m.turnLeft) * Tuning.F("drive.hands", 1.6f);
            driveTurnNow = Mathf.MoveTowards(driveTurnNow, driveTurn, swing * Time.deltaTime);
            m.Hands(driveTurnNow, out Pose l, out Pose r);
            Pose lw = frame * l, rw = frame * r;
            if (!Bad(lw.position) && !Bad(rw.position))
            {
                controls.leftController.transform.SetPositionAndRotation(lw.position, lw.rotation);
                controls.rightController.transform.SetPositionAndRotation(rw.position, rw.rotation);
            }
            // Its head as theirs was sat in it, looking a little into its turn.
            Vector3 headAt = a.head.position;
            if (Mathf.Abs(cloneSize - 1f) > 0.02f)
                headAt.y *= cloneSize;
            controls.headset.localPosition = headAt;
            float look = Mathf.Clamp(driveTurnNow * Tuning.F("drive.look", 70f), -35f, 35f);
            // (Facing the way the seat faces, nodding as theirs did: its hips turn in the seat with its head.)
            Vector3 seatAhead = Flat(seat.transform.forward);
            Vector3 theirs = m.head * Vector3.forward;
            float nod = -Mathf.Asin(Mathf.Clamp(theirs.y, -1f, 1f)) * Mathf.Rad2Deg;
            if (seatAhead.sqrMagnitude > 0.001f)
                controls.headset.rotation = Quaternion.LookRotation(seatAhead.normalized * m.forward, Vector3.up) * Quaternion.Euler(Mathf.Clamp(nod, -25f, 35f), look, 0f);
            driveAimLeft = lw.position;
            driveAimRight = rw.position;

            // Its fingers as theirs; its sticks and triggers its own.
            PutInput(controls.leftController, a.leftIn, b.leftIn, k, false);
            PutInput(controls.rightController, a.rightIn, b.rightIn, k, false);
            rig.remapHeptaRig._crouchTarget = a.crouch;
            rig.remapHeptaRig._feetOffset = a.feet;
            Vector2 leftStick = Vector2.zero, rightStick = Vector2.zero;
            float leftPull = 0f, rightPull = 0f;
            void Put(Driving.Channel c, float v)
            {
                switch (c)
                {
                    case Driving.Channel.LeftStickY: leftStick.y += v; break;
                    case Driving.Channel.RightStickY: rightStick.y += v; break;
                    case Driving.Channel.LeftStickX: leftStick.x += v; break;
                    case Driving.Channel.RightStickX: rightStick.x += v; break;
                    case Driving.Channel.LeftTrigger: leftPull += v; break;
                    case Driving.Channel.RightTrigger: rightPull += v; break;
                }
            }
            Put(m.go, m.goSign * driveGo);
            if (m.hasBack)
                Put(m.back, m.backSign * driveBack);
            if (m.stickSteer && Mathf.Abs(m.steerGain) > 0.001f)
                Put(m.steer, Mathf.Clamp(driveTurnNow / m.steerGain, -1f, 1f));
            Pull(controls.leftController, Mathf.Clamp01(leftPull));
            Pull(controls.rightController, Mathf.Clamp01(rightPull));
            leftStick = Vector2.ClampMagnitude(leftStick, 1f);
            rightStick = Vector2.ClampMagnitude(rightStick, 1f);
            if (controls.leftController != null)
                controls.leftController._thumbstickAxis = leftStick;
            if (controls.rightController != null)
                controls.rightController._thumbstickAxis = rightStick;
            controls._axisPrimary = leftStick;
            controls._axisPrimaryDirty = leftStick != Vector2.zero;
        }

        private static void Pull(BaseController c, float pull)
        {
            if (c == null)
                return;
            bool was = c._primaryInteractionButton, on = pull > 0.5f;
            c._primaryAxis = pull;
            c._primaryInteractionButton = on;
            c._primaryInteractionButtonDown = on && !was;
            c._primaryInteractionButtonUp = !on && was;
        }

        /// <summary>Something it was shown how to drive, standing free within so far (the nearest); null if none.</summary>
        private Poolee AnyVehicle(float within)
        {
            Poolee best = null;
            float nearest = within;
            foreach (Lessons.Lesson l in Lessons.Drives())
            {
                Poolee p = FreeVehicle(Driving.Of(l).barcode, within);
                if (p == null)
                    continue;
                float d = Flat(p.transform.position - Feet).magnitude;
                if (d < nearest)
                {
                    nearest = d;
                    best = p;
                }
            }
            return best;
        }

        private float kartSeekAt;
        private Poolee kartSought;

        // The player driving off, and nothing to drive right by it: to one standing a little way off, at a run, to get in.
        private bool ToAVehicle(Transform you, float far, float dt)
        {
            if (BotManager.OwnSpawns || you == null || far < Tuning.F("drive.follow", 10f) || InLesson)
                return false;
            RigManager yours = Player.RigManager;
            if (yours == null || yours.activeSeat == null)
            {
                kartSought = null;
                return false;
            }
            if (Time.time >= kartSeekAt)
            {
                kartSeekAt = Time.time + 2f;
                kartSought = AnyVehicle(Tuning.F("drive.follow.seek", 35f));
                if (kartSought != null)
                    nav.SetGoal(kartSought.transform.position - Flat(kartSought.transform.right).normalized * 1.2f);
            }
            if (kartSought == null)
                return false;
            if (Flat(kartSought.transform.position - Feet).magnitude < Tuning.F("drive.spare", 5f) - 1f)
            {
                driveFollowAt = 0f;
                return DriveAfterYou(you, far);
            }
            following = true;
            running = true;
            Travel(MotionLibrary.RunPush, 1.5f, dt);
            return true;
        }

        // The player sat in something and well off: after them, in its own (when it is following them).
        private bool DriveAfterYou(Transform you, float far)
        {
            if (you == null || far < Tuning.F("drive.follow", 10f) || Time.time < driveFollowAt)
                return false;
            driveFollowAt = Time.time + 4f;
            RigManager yours = Player.RigManager;
            if (yours == null || yours.activeSeat == null)
                return false;
            return DriveTo(you.position - Vector3.up * 1.2f, "after you", you);
        }

        /// <summary>For the lab: drives somewhere far ("" ), to the player ("me"), or after them as they go ("follow").</summary>
        public void LabDrive(string where)
        {
            Lessons.Lesson lesson = Lessons.Drive();
            if (lesson == null)
            {
                Lab.Event("driving: nothing it has been shown how to drive (" + Lessons.DriveSaid() + ")");
                return;
            }
            Vector3 feet = Feet;
            Transform you = Player.Head;
            bool begun;
            if (where == "skill" || where == "practice" || where == "practice off" || where == "skill reset")
            {
                if (where.StartsWith("practice"))
                    DriveSkill.Practice = where == "practice";
                if (where == "skill reset")
                    DriveSkill.Reset();
                if (DriveSkill.Practice && mode != Mode.Wander && mode != Mode.Brain)
                    SetMode(Mode.Wander);
                Lab.Event("drive skill: " + DriveSkill.Said());
                return;
            }
            if (where == "tidy")
            {
                // (Those left standing about with nobody in them, not by the player.)
                int cleared = 0;
                HashSet<IntPtr> done = new HashSet<IntPtr>();
                foreach (Seat seat in UnityEngine.Object.FindObjectsOfType<Seat>())
                {
                    Poolee p = seat != null ? seat.GetComponentInParent<Poolee>() : null;
                    if (p == null || !done.Add(p.Pointer) || seat.rigManager != null || !p.gameObject.activeInHierarchy)
                        continue;
                    if (you != null && Flat(p.transform.position - you.position).magnitude < 6f)
                        continue;
                    p.Despawn();
                    cleared++;
                }
                Lab.Event("driving: cleared away " + cleared + " left standing about");
                return;
            }
            if (where == "road")
            {
                begun = DriveTheirWay();
                if (!begun)
                    Lab.Event("driving: no way of yours kept on this level");
                return;
            }
            if ((where == "me" || where == "follow") && you != null)
            {
                begun = DriveTo(you.position - Vector3.up * 1.2f, where == "me" ? "to you" : "after you", where == "follow" ? you : null);
            }
            else
            {
                if (!FarPlace(feet, out Vector3 away))
                    away = feet + Quaternion.Euler(0f, bodyYaw, 0f) * Vector3.forward * 30f;
                begun = DriveTo(away, "going for a drive");
            }
            if (!begun)
                Lab.Event("driving: could not set off (" + (InLesson ? "in a lesson" : dead ? "dead" : enemy ? "an enemy" : climbing ? "climbing" : "?") + ")");
        }
    }
}
