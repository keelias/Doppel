using UnityEngine;
using UnityEngine.AI;

namespace ThirdEyeBonelabMod.Bots
{
    /// <summary>
    /// Finds the bot its way about a level: along the level's own navmesh (what the game's NPCs
    /// walk by), round walls and up stairs, or, where a level has none, straight there,
    /// feeling ahead for walls and drops. Also notices when it is stuck.
    /// </summary>
    public class Navigator
    {
        private const float Repath = 0.5f;
        private const float CornerReached = 0.45f;

        private readonly NavMeshPath path = new NavMeshPath();
        private Vector3[] corners = new Vector3[0];
        private int next;
        private float repathAt;

        public Vector3 Goal { get; private set; }
        public bool HasGoal { get; private set; }

        /// <summary>Whether the way it is on was found on the navmesh (false: straight there, by feel).</summary>
        public bool OnMesh { get; private set; }

        // Stuck: pushing on, but hardly moving.
        private Vector3 lastPlace;
        private float lastPlaceAt;
        private float pushedFor;
        public int StuckCount { get; private set; }

        public void SetGoal(Vector3 goal)
        {
            // (A goal that has only shifted a little: the way to it is not thrown away, only looked at again soon.)
            bool shifted = HasGoal && Flat(goal - Goal).magnitude < 3f && corners.Length >= 2;
            Goal = goal;
            HasGoal = true;
            repathAt = shifted ? Mathf.Min(repathAt, Time.time + 0.25f) : 0f;
            if (!shifted)
                StuckCount = 0;
        }

        /// <summary>For the lab: how many times its way has changed its mind about the first turn, and gone on and off the level's ways.</summary>
        public int Swerves { get; private set; }
        public int MeshFlips { get; private set; }

        private Vector3 firstLeg;
        private float wayKeptSince;

        public void Clear()
        {
            HasGoal = false;
            corners = new Vector3[0];
            StuckCount = 0;
        }

        /// <summary>How far it still has to go, along the way (roughly, where it walks by feel).</summary>
        public float Remaining(Vector3 feet)
        {
            if (!HasGoal)
                return 0f;
            float left = 0f;
            Vector3 from = feet;
            for (int i = next; i < corners.Length; i++)
            {
                left += Flat(corners[i] - from).magnitude;
                from = corners[i];
            }
            return next >= corners.Length ? Flat(Goal - feet).magnitude : left;
        }

        /// <summary>
        /// Which way to walk now (flat, unit length; zero once there), and how far the ground
        /// ahead rises (to jump up onto it).
        /// </summary>
        public Vector3 Steer(Vector3 feet, Vector3 chest, Vector3 facing, out float rise, out bool drop)
        {
            rise = 0f;
            drop = false;
            DropDepth = 0f;
            if (!HasGoal)
                return Vector3.zero;
            // Its way is found once and kept. It is looked for again only when there is none, when
            // where it is going has moved, when it has strayed from it, or now and then (and then
            // the one it is on is kept unless the new one is a good deal shorter).
            bool none = corners.Length < 2 || next >= corners.Length;
            bool moved = Flat(Goal - wayGoal).magnitude > Tuning.F("nav.regoal", 1.5f);
            bool strayed = !none && OffWay(feet) > Tuning.F("nav.stray", 2f);
            if (none || moved || strayed || Time.time >= repathAt)
            {
                repathAt = Time.time + Tuning.F("nav.again", 2.5f);
                FindWay(feet, none || strayed);
                wayGoal = Goal;
            }
            // (A corner is done with once it is near or passed: it is not walked right onto and turned on.)
            while (next < corners.Length - 1 && (Flat(corners[next] - feet).magnitude < Tuning.F("nav.corner", 0.8f)
                   || Vector3.Dot(Flat(corners[next] - feet), Flat(corners[next + 1] - corners[next])) < 0f && Flat(corners[next] - feet).magnitude < 1.6f))
                next++;
            Vector3 corner = next < corners.Length ? corners[next] : Goal;
            float flat = Flat(corner - feet).magnitude;
            // It makes for a point a little way on along its way, not for the corner itself: so it
            // comes round a corner in a curve, as someone walking does.
            Vector3 toward = OnMesh ? LookAhead(feet, Tuning.F("nav.ahead", 1.3f)) : corner;
            Vector3 dir = Flat(toward - feet);
            if (dir.sqrMagnitude < 0.0025f)
                dir = Flat(corner - feet);
            if (dir.sqrMagnitude < 0.0025f)
                return Vector3.zero;
            dir.Normalize();
            // (A rise far off is only a slope or stairs to walk up; close ahead, it may need a jump.)
            rise = flat < 1.2f ? corner.y - feet.y : 0f;

            Vector3 before = dir;
            if (!OnMesh)
            {
                dir = Feel(feet, chest, dir, ref rise, out drop);
                if (drop)
                    DropDepth = Depth(feet, dir);
            }
            // The way it is told to go never jumps: it comes round to the new way at a turn a
            // walker can make (a way straight back is a turn about, made by its body, see Bot.Go).
            if (lastDir.sqrMagnitude > 0.01f && Time.time - lastDirAt < 0.3f && !drop)
            {
                float most = Tuning.F("nav.turn", 170f) * Mathf.Max(Time.deltaTime, 0.005f);
                dir = Flat(Vector3.RotateTowards(lastDir, dir, most * Mathf.Deg2Rad, 0f)).normalized;
            }
            // (For the lab: a sudden change of the way it is told to go, and what made it.)
            if (lastDir.sqrMagnitude > 0.01f && Time.time - lastDirAt < 0.3f && Vector3.Angle(lastDir, dir) > 50f && Time.time - turnSaidAt > 0.4f)
            {
                turnSaidAt = Time.time;
                Lab.Event("nav: its way turned " + Vector3.SignedAngle(lastDir, dir, Vector3.up).ToString("0") + " deg at once: " + (OnMesh ? "on the level's ways" : "by feel")
                          + (wasMesh != OnMesh ? " (was " + (wasMesh ? "on them" : "by feel") + ")" : "") + ", corner " + next + " of " + corners.Length + (lastNext != next ? " (was " + lastNext + ")" : "")
                          + (lastCorners != corners.Length ? ", new way" : "") + (Vector3.Angle(before, dir) > 10f ? ", turned " + Vector3.Angle(before, dir).ToString("0") + " by a wall" : "")
                          + ", " + flat.ToString("0.0") + " m to the corner, goal " + Flat(Goal - feet).magnitude.ToString("0") + " m" + (Flat(Goal - lastGoal).magnitude > 0.5f ? ", goal moved " + Flat(Goal - lastGoal).magnitude.ToString("0.0") + " m" : ""));
            }
            lastDir = dir;
            lastDirAt = Time.time;
            wasMesh = OnMesh;
            lastNext = next;
            lastCorners = corners.Length;
            lastGoal = Goal;
            return dir;
        }

        private Vector3 wayGoal;

        // How far it is, flat, from the stretch of its way it is on.
        private float OffWay(Vector3 feet)
        {
            if (next <= 0 || next >= corners.Length)
                return 0f;
            Vector3 a = corners[next - 1], c = corners[next];
            Vector3 ac = Flat(c - a);
            float along = ac.sqrMagnitude > 0.0001f ? Mathf.Clamp01(Vector3.Dot(Flat(feet - a), ac) / ac.sqrMagnitude) : 0f;
            return Flat(feet - (a + (c - a) * along)).magnitude;
        }

        private static float Length(Vector3[] way, int from, Vector3 start)
        {
            float sum = 0f;
            Vector3 at = start;
            for (int i = from; i < way.Length; i++)
            {
                sum += Flat(way[i] - at).magnitude;
                at = way[i];
            }
            return sum;
        }

        private void FindWay(Vector3 feet, bool afresh = true)
        {
            bool wasOnMesh = OnMesh;
            OnMesh = false;
            try
            {
                if (NavMesh.SamplePosition(feet, out NavMeshHit from, 2.2f, NavMesh.AllAreas)
                    && NavMesh.SamplePosition(Goal, out NavMeshHit to, 3f, NavMesh.AllAreas)
                    && NavMesh.CalculatePath(from.position, to.position, NavMesh.AllAreas, path)
                    && path.status != NavMeshPathStatus.PathInvalid)
                {
                    Vector3[] found = path.corners;
                    if (found != null && found.Length >= 2)
                    {
                        // (A new way that sets off quite another way from the one it is on, a moment
                        // after that one was taken: the one it is on is kept. Two ways round a thing,
                        // much the same length, had it turning for one and then the other, twice a second.)
                        Vector3 leg = Flat(found[1] - found[0]);
                        Vector3 onNow = wasOnMesh && corners.Length >= 2 && next < corners.Length ? Flat(corners[next] - feet) : Vector3.zero;
                        // (The one it is on still ends about where it is going, and the new one sets off
                        // another way without being much shorter: the one it is on is kept.)
                        bool swerve = !afresh && onNow.sqrMagnitude > 0.2f && leg.sqrMagnitude > 0.01f
                                      && Vector3.Angle(onNow, leg) > Tuning.F("nav.swerve", 40f)
                                      && Flat(corners[corners.Length - 1] - Goal).magnitude < Tuning.F("nav.keep.end", 5f)
                                      && Length(found, 1, found[0]) > Length(corners, next, feet) * Tuning.F("nav.keep.shorter", 0.75f) - 1f;
                        if (swerve)
                        {
                            Swerves++;
                            OnMesh = true;
                            return;
                        }
                        if (firstLeg.sqrMagnitude < 0.01f || Vector3.Angle(firstLeg, leg) > 55f)
                            wayKeptSince = Time.time;
                        firstLeg = leg;
                        corners = found;
                        next = 1;
                        OnMesh = true;
                        return;
                    }
                }
            }
            catch (Exception)
            {
            }
            // (Off the level's ways for a moment only: the way it was on is kept a little, not swapped for a straight line and back.)
            if (wasOnMesh)
            {
                MeshFlips++;
                if (corners.Length >= 2 && next < corners.Length && Time.time - offMeshSince > 1.5f)
                {
                    if (offMeshFrom < 0f)
                        offMeshFrom = Time.time;
                    if (Time.time - offMeshFrom < 1.2f)
                    {
                        OnMesh = true;
                        return;
                    }
                    offMeshSince = Time.time;
                }
            }
            offMeshFrom = -1f;
            firstLeg = Vector3.zero;
            corners = new[] { feet, Goal };
            next = 1;
        }

        private float offMeshFrom = -1f, offMeshSince = -10f;
        private Vector3 lastDir, lastGoal;
        private float lastDirAt, turnSaidAt;
        private bool wasMesh;
        private int lastNext, lastCorners;

        // Without a navmesh: steer off walls just ahead, and stop short of drops.
        /// <summary>The point so far on along its way (round the corners to come): where it looks as it walks.</summary>
        public Vector3 LookAhead(Vector3 feet, float distance)
        {
            if (!HasGoal)
                return feet;
            Vector3 from = feet;
            float left = distance;
            for (int i = next; i < corners.Length; i++)
            {
                Vector3 step = Flat(corners[i] - from);
                float d = step.magnitude;
                if (d >= left && d > 0.001f)
                    return from + step / d * left;
                left -= d;
                from = new Vector3(corners[i].x, from.y, corners[i].z);
            }
            return Goal;
        }

        /// <summary>How far down the ground is past the edge ahead (a drop it is at), or a long way if none is found.</summary>
        public float DropDepth { get; private set; }

        private static float Depth(Vector3 feet, Vector3 dir)
        {
            Vector3 past = feet + dir * 0.9f + Vector3.up * 0.5f;
            if (Physics.Raycast(past, Vector3.down, out RaycastHit ground, 30f, ~0, QueryTriggerInteraction.Ignore))
                return feet.y - ground.point.y;
            return 99f;
        }

        // Which side it is going round the thing in its way (-1, 1; 0: none), the way it is sliding, and till when that holds.
        private float slideSide, slideUntil, sideUntil;
        private Vector3 slideDir;

        private Vector3 Feel(Vector3 feet, Vector3 chest, Vector3 dir, ref float rise, out bool drop)
        {
            drop = false;
            if (Physics.SphereCast(chest, 0.18f, dir, out RaycastHit wall, 0.9f, ~0, QueryTriggerInteraction.Ignore) && !IsBot(wall))
            {
                Vector3 away = Flat(wall.normal);
                if (away.sqrMagnitude > 0.01f)
                {
                    // Slide along the wall, the way that keeps closer to where it is going; and the
                    // side once taken is kept (square on to a wall, "closer" was now left, now right,
                    // frame by frame: it went along the wall in a zigzag).
                    Vector3 along = Vector3.Cross(Vector3.up, away.normalized);
                    float side = Time.time < sideUntil && slideSide != 0f ? slideSide : Vector3.Dot(along, dir) < 0f ? -1f : 1f;
                    if (Time.time >= sideUntil || slideSide == 0f)
                    {
                        // (Square on: the side with more room.)
                        if (Mathf.Abs(Vector3.Dot(along, dir)) < 0.3f)
                        {
                            bool rightShut = Physics.SphereCast(chest, 0.18f, along, out RaycastHit r, 3f, ~0, QueryTriggerInteraction.Ignore);
                            bool leftShut = Physics.SphereCast(chest, 0.18f, -along, out RaycastHit l, 3f, ~0, QueryTriggerInteraction.Ignore);
                            float right = rightShut ? r.distance : 3f, left = leftShut ? l.distance : 3f;
                            if (Mathf.Abs(right - left) > 0.3f)
                                side = right > left ? 1f : -1f;
                        }
                        slideSide = side;
                    }
                    sideUntil = Time.time + Tuning.F("nav.side.keep", 2.5f);
                    dir = (along * side + away.normalized * 0.4f).normalized;
                    slideDir = dir;
                    slideUntil = Time.time + Tuning.F("nav.slide.on", 0.35f);
                }
            }
            else if (Time.time < slideUntil && slideDir.sqrMagnitude > 0.01f)
            {
                // (Just clear of it: on along it a moment more, to be past its corner, not straight back into it.)
                dir = (slideDir + dir * 0.5f).normalized;
            }
            Vector3 ahead = feet + dir * 0.7f + Vector3.up * 1.4f;
            if (Physics.Raycast(ahead, Vector3.down, out RaycastHit ground, 3.4f, ~0, QueryTriggerInteraction.Ignore))
                rise = Mathf.Max(rise, ground.point.y - feet.y);
            else
                drop = true;
            return dir;
        }

        private static bool IsBot(RaycastHit hit)
        {
            return hit.collider != null && hit.collider.GetComponentInParent<Il2CppSLZ.Marrow.RigManager>() != null;
        }

        /// <summary>Once a frame: true when it has been pushing on for a while without getting anywhere.</summary>
        public bool CheckStuck(Vector3 feet, float push, float dt)
        {
            if (push < 0.25f)
            {
                pushedFor = 0f;
                lastPlace = feet;
                lastPlaceAt = Time.time;
                return false;
            }
            pushedFor += dt;
            if (Time.time - lastPlaceAt < 1f)
                return false;
            bool stuck = pushedFor > 0.9f && Flat(feet - lastPlace).magnitude < 0.2f * push;
            lastPlace = feet;
            lastPlaceAt = Time.time;
            if (stuck)
                StuckCount++;
            else
                StuckCount = 0;
            return stuck;
        }

        /// <summary>A random place it can walk to, between near and far from around.</summary>
        /// <summary>
        /// A place of the level's own ways, on the ground that is really there: on some levels
        /// those ways lie a way under the ground one walks on (under a road), and a place taken
        /// from them was under it: nothing could be sent there, and what was put there was lost.
        /// The ground first met coming down from a little above the level one stands at.
        /// </summary>
        public static Vector3 Surface(Vector3 place, float level)
        {
            try
            {
                if (Mathf.Abs(place.y - level) < 0.6f)
                    return place;
                Vector3 from = new Vector3(place.x, Mathf.Max(level, place.y) + 2.5f, place.z);
                foreach (RaycastHit hit in Physics.RaycastAll(from, Vector3.down, 9f, ~0, QueryTriggerInteraction.Ignore))
                {
                    if (hit.collider == null || hit.collider.attachedRigidbody != null || hit.normal.y < 0.6f)
                        continue;
                    // (The highest ground at or under the level it stands at, not the way under it.)
                    if (hit.point.y > place.y + 0.4f && hit.point.y < level + 2.5f)
                        place = hit.point;
                }
            }
            catch (Exception)
            {
            }
            return place;
        }

        public static bool RandomPlace(Vector3 around, float near, float far, out Vector3 place)
        {
            for (int tries = 0; tries < 20; tries++)
            {
                Vector2 r = UnityEngine.Random.insideUnitCircle.normalized * UnityEngine.Random.Range(near, far);
                Vector3 p = around + new Vector3(r.x, 0f, r.y);
                try
                {
                    if (NavMesh.SamplePosition(p, out NavMeshHit hit, 2.5f, NavMesh.AllAreas))
                    {
                        place = Surface(hit.position, around.y);
                        return true;
                    }
                }
                catch (Exception)
                {
                }
                // No navmesh: somewhere with ground under it, in plain sight from about chest height.
                Vector3 eye = around + Vector3.up * 1.2f;
                Vector3 to = p + Vector3.up * 1.2f;
                if (Physics.Linecast(eye, to, ~0, QueryTriggerInteraction.Ignore))
                    continue;
                if (Physics.Raycast(to, Vector3.down, out RaycastHit ground, 3f, ~0, QueryTriggerInteraction.Ignore))
                {
                    place = ground.point;
                    return true;
                }
            }
            place = around;
            return false;
        }

        private static Vector3 Flat(Vector3 v)
        {
            v.y = 0f;
            return v;
        }
    }
}
