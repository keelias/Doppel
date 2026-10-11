using Il2CppSLZ.Marrow;
using UnityEngine;

namespace ThirdEyeBonelabMod.Bots
{
    /// <summary>
    /// Going about on its own as someone with a mind does, not as a thing that picks a spot
    /// and walks at it. It looks at what there is about it (a long way open ahead, something
    /// lying on the ground, somewhere up high, another bot, ground it has never stood on),
    /// stands a moment making up its mind, and goes to see: mostly onward the way it was
    /// heading, not to and fro; by a way that bends, not a ruled line; at a pace that suits
    /// how far it is; stopping now and then on the way to look at something. Something in its
    /// way, it goes round, and only gives the idea up when there is no way round. There, it
    /// stays a while and does what it came for (looks the thing over, looks out from the
    /// height, looks about the new place) before the next idea.
    /// </summary>
    public partial class Bot
    {
        private enum Sight { Open, Thing, High, Bot, New, You, Tall, Drive }

        private float cameOverAt = -1000f;
        private bool driveOnRoad;
        private string planWhy = "";
        private Sight planSight;
        private Vector3 planPlace, planLook;
        private float planPush = 0.6f;
        private bool planRun;
        private float thinkUntil;
        private bool thinking2;
        private int detours;
        private Vector3 detourFor;
        private bool onDetour;
        private Vector3 viaPlace;
        private bool viaLeft;
        private Vector3 roamHeading;
        private float pauseUntil, pauseNext;
        private float planSince;
        private Sight lastSight = Sight.New;

        // Something worth going to see, chosen among all there is to see from here. False if there is nothing.
        private bool BeginGo(Transform you, Vector3 feet)
        {
            Transform head = Head;
            if (head == null)
                return false;
            Vector3 eye = feet + Vector3.up * 1.3f;
            List<(Vector3 at, Vector3 look, Sight sight, string why, float worth)> seen = new List<(Vector3, Vector3, Sight, string, float)>();
            Vector3 ahead = roamHeading.sqrMagnitude > 0.01f ? roamHeading : Quaternion.Euler(0f, bodyYaw, 0f) * Vector3.forward;

            // A long way open: where does it go?
            float turn = UnityEngine.Random.Range(0f, 22.5f);
            for (int i = 0; i < 16; i++)
            {
                Vector3 dir = Quaternion.Euler(0f, turn + i * 22.5f, 0f) * Vector3.forward;
                float far = Physics.Raycast(eye, dir, out RaycastHit wall, 45f, ~0, QueryTriggerInteraction.Ignore) ? wall.distance : 45f;
                if (far < 9f)
                    continue;
                Vector3 p = eye + dir * (far * UnityEngine.Random.Range(0.5f, 0.85f));
                if (!Ground(p, out Vector3 ground) || Mathf.Abs(ground.y - feet.y) > 4f)
                    continue;
                seen.Add((ground, ground + dir * 6f + Vector3.up * 1.4f, Sight.Open, "seeing what is over there", 0.9f + far / 45f));
            }
            // Things lying about.
            HashSet<IntPtr> bodies = new HashSet<IntPtr>();
            int things = 0;
            foreach (Collider c in Physics.OverlapSphere(eye, Tuning.F("roam.thing.far", 40f), ~0, QueryTriggerInteraction.Ignore))
            {
                Rigidbody rb = c != null ? c.attachedRigidbody : null;
                if (rb == null || rb.isKinematic || !bodies.Add(rb.Pointer) || rb.mass > 60f || rb.GetComponentInParent<RigManager>() != null)
                    continue;
                // (Not the litter of a fight: spent cases, loose rounds, empty magazines.)
                string called = rb.name.ToLowerInvariant();
                if (rb.mass < 0.25f || called.Contains("cartridge") || called.Contains("casing") || called.Contains("shell") || called.Contains("bullet") || called.StartsWith("mag_"))
                    continue;
                if (NotForBots(rb))
                    continue;
                Vector3 at = rb.worldCenterOfMass;
                if ((at - feet).magnitude < 3f || Mathf.Abs(at.y - feet.y) > 2.5f || Targets.NearPlayer(at, 2f))
                    continue;
                Vector3 stand = at - Flat(at - feet).normalized * 1.1f;
                if (!Ground(stand, out Vector3 ground))
                    continue;
                seen.Add((ground, at, Sight.Thing, "having a look at the " + ThingName(rb), Tuning.F("roam.thing", 2.6f)));
                if (++things >= 6)
                    break;
            }
            // Up high.
            if (FindHighGround(feet, out Vector3 top))
                seen.Add((top, top + ahead * 8f + Vector3.up * 1.2f, Sight.High, "getting up there for a look", 1.2f * personality.curiosity));
            if (SelfTaught.KnownTop(feet, 50f, out Vector3 known))
                seen.Add((known, known + ahead * 8f + Vector3.up * 1.2f, Sight.High, "going back up to that high spot", 0.9f * personality.curiosity));
            // A wall too high for its hands, that it has been shown something to climb with.
            if (TallWallAbout(feet, out Vector3 foot, out Vector3 into, out float tall, out Vector3 wallTop))
            {
                Lessons.Lesson with = Lessons.For("climb");
                seen.Add((foot, wallTop, Sight.Tall, "going up that wall with the " + (with != null ? with.name : "thing you showed it"), Tuning.F("tool.climb.want", 1.8f) * personality.curiosity));
                planWall = (foot, into, tall);
            }
            // The others.
            foreach (Bot other in BotManager.All)
            {
                if (other == this || other.Head == null || other.dead || other.enemy != enemy)
                    continue;
                Vector3 to = other.Head.position - head.position;
                if (to.magnitude < 5f || to.magnitude > 45f)
                    continue;
                Vector3 stand = other.Head.position - Flat(to).normalized * 2f;
                if (Ground(stand, out Vector3 ground))
                    seen.Add((ground, other.Head.position, Sight.Bot, "going over to the other one", 0.8f));
            }
            // Somewhere it has never been.
            if (FarPlace(feet, out Vector3 away))
                seen.Add((away, away + Vector3.up * 1.4f, Sight.New, "going somewhere new", 1.1f * personality.curiosity));
            // Somewhere a long way off, in the thing it was shown how to drive.
            // (One at a time: several setting off together from the same spot ran into one another and turned over.)
            bool someoneDriving = false;
            foreach (Bot other in BotManager.All)
                someoneDriving |= other != this && other.DrivingNow;
            if (Time.time > driveNextAt && Tuning.F("drive.want", 1.3f) > 0f && !someoneDriving)
            {
                Lessons.Lesson car = Lessons.Drive();
                // (Left to what is on the map: only with one of those standing about, and it goes to that.)
                Il2CppSLZ.Marrow.Pool.Poolee standing = car != null && !BotManager.OwnSpawns ? AnyVehicle(Tuning.F("drive.seek", 60f)) : null;
                if (car != null && !BotManager.OwnSpawns)
                {
                    if (standing != null && Ground(standing.transform.position - Flat(standing.transform.right).normalized * 1.3f + Vector3.up, out Vector3 byIt))
                        seen.Add((byIt, standing.transform.position + Vector3.up * 0.6f, Sight.Drive, "going for a drive in the " + car.name + " over there", Tuning.F("drive.want.map", 2.6f) * personality.curiosity));
                }
                else if (car != null && FarPlace(feet, out Vector3 off) && Flat(off - feet).magnitude > 18f)
                    seen.Add((off, off + Vector3.up * 1.4f, Sight.Drive, DriveSkill.Practice ? "practising its driving" : "going for a drive in the " + car.name, DriveSkill.Practice ? 1000f : Tuning.F("drive.want", 1.3f) * personality.energy));
            }
            // The player, when it is the sort to (or is to keep near them).
            if (you != null && Flat(you.position - feet).magnitude > 6f && Flat(you.position - feet).magnitude < 40f)
            {
                Vector3 stand = you.position - Vector3.up * 1.4f - Flat(you.position - feet).normalized * 2.5f;
                if (Ground(stand, out Vector3 ground))
                    seen.Add((ground, you.position, Sight.You, YouStoodFor > Tuning.F("you.still", 50f) ? "coming over, you have been there a while" : "coming to see what you are doing",
                        BotManager.StayNear ? 1.6f : YouStoodFor > Tuning.F("you.still", 50f) && Time.time - cameOverAt > Tuning.F("you.still.every", 150f) ? 1.4f : 0.2f));
            }
            if (seen.Count == 0)
                return false;

            // Weighed: somewhere new over somewhere it has stood; onward over back the way it came; not
            // where it got stuck; not the same kind of thing as last time; a little chance.
            float total = 0f;
            float[] worth = new float[seen.Count];
            for (int i = 0; i < seen.Count; i++)
            {
                var s = seen[i];
                Vector3 to = Flat(s.at - feet);
                float w = s.worth;
                w *= visited.Contains(Cell(s.at)) ? 0.6f : 1.5f;
                w *= 1f + 0.55f * Vector3.Dot(ahead.normalized, to.normalized);
                w /= 1f + SelfTaught.BadAt(s.at);
                if (s.sight == lastSight && s.sight != Sight.Open)
                    w *= 0.45f;
                w *= UnityEngine.Random.Range(0.7f, 1.3f);
                worth[i] = w * w;
                total += worth[i];
            }
            float roll = UnityEngine.Random.value * total;
            int pick = seen.Count - 1;
            for (int i = 0; i < seen.Count; i++)
            {
                roll -= worth[i];
                if (roll <= 0f)
                {
                    pick = i;
                    break;
                }
            }
            if (seen[pick].sight == Sight.You)
                cameOverAt = Time.time;
            planPlace = seen[pick].at;
            planLook = seen[pick].look;
            planSight = seen[pick].sight;
            planWhy = seen[pick].why;
            lastSight = planSight;
            float far2 = Flat(planPlace - feet).magnitude;
            // Its pace: a stroll for something near, its usual walk for further, a run now and then if it is the restless sort.
            planRun = far2 > 22f && UnityEngine.Random.value < 0.18f * personality.energy;
            planPush = far2 < 9f ? Tuning.F("life.stroll", 0.45f) * UnityEngine.Random.Range(0.9f, 1.3f) : MotionLibrary.WalkPush * UnityEngine.Random.Range(0.8f, 1f);
            detours = 0;
            onDetour = false;
            // (A way that bends: by a point off to one side, when it is far and the way there is clear.)
            viaLeft = false;
            if (far2 > 9f && !Physics.Linecast(eye, planPlace + Vector3.up * 1.2f, ~0, QueryTriggerInteraction.Ignore))
            {
                Vector3 side = Vector3.Cross(Vector3.up, Flat(planPlace - feet).normalized) * (far2 * UnityEngine.Random.Range(-1f, 1f) * Tuning.F("roam.bend", 0.13f));
                Vector3 mid = feet + Flat(planPlace - feet) * UnityEngine.Random.Range(0.4f, 0.6f) + side;
                if (side.magnitude > 1.2f && Ground(mid, out Vector3 midGround) && Mathf.Abs(midGround.y - feet.y) < 1.5f
                    && !Physics.Linecast(eye, midGround + Vector3.up * 1.2f, ~0, QueryTriggerInteraction.Ignore))
                {
                    viaPlace = midGround;
                    viaLeft = true;
                }
            }
            // It makes up its mind first: stands, and looks that way.
            thinking2 = true;
            thinkUntil = Time.time + UnityEngine.Random.Range(0.9f, 2.4f) * Mathf.Lerp(0.8f, 1.3f, Mathf.InverseLerp(0.5f, 1.8f, personality.calm));
            actUntil = Time.time + 90f;
            planSince = Time.time;
            pauseNext = Time.time + UnityEngine.Random.Range(6f, 14f);
            Look(planLook, null, "where it means to go", thinkUntil - Time.time + 0.6f);
            Lab.Event("roam: " + planWhy + " (" + far2.ToString("0") + " m" + (viaLeft ? ", round by the side" : "") + (planRun ? ", at a run" : "") + ")");
            return true;
        }

        /// <summary>
        /// The player's own tools for making things (the spawn gun and its like): not for a bot to
        /// pick up and try. One trying the spawn gun's trigger filled the map with things nobody asked for.
        /// </summary>
        public static bool NotForBots(Rigidbody rb)
        {
            try
            {
                string called = (ThingName(rb) + " " + rb.name).ToLowerInvariant().Replace(" ", "").Replace("_", "");
                return called.Contains("spawngun") || called.Contains("nimbus") || called.Contains("devmanipulator") || called.Contains("utilitygun");
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string ThingName(Rigidbody rb)
        {
            try
            {
                Il2CppSLZ.Marrow.Pool.Poolee poolee = rb.GetComponentInParent<Il2CppSLZ.Marrow.Pool.Poolee>();
                if (poolee != null && poolee.SpawnableCrate != null)
                    return Lessons.NameOf(poolee);
            }
            catch (Exception)
            {
            }
            return "thing there";
        }

        // Once a frame while it goes to see something.
        private void GoStep(Transform you, Transform head, float dt)
        {
            headYaw = 0f;
            Vector3 feet = rig.physicsRig != null && rig.physicsRig.centerOfPressure != null ? rig.physicsRig.centerOfPressure.position : Feet;
            if (thinking2)
            {
                walk = Vector2.zero;
                running = false;
                Face(planPlace, dt, 70f);
                if (Time.time < thinkUntil)
                    return;
                thinking2 = false;
                nav.Clear();
                driveOnRoad = false;
                if (planSight == Sight.Drive && !BotManager.OwnSpawns)
                {
                    // (To the one standing on the map; it drives off from there.)
                }
                else if (planSight == Sight.Drive)
                {
                    // Mostly along the way the player drove here, if one is kept (it walks to the
                    // nearest of it first, and sets off from there); or to somewhere of its own.
                    Driving.Model road = Lessons.Road();
                    if (road != null && (DriveSkill.Practice || UnityEngine.Random.value < Tuning.F("drive.theirway", 0.7f)))
                    {
                        Vector3 nearest = road.road[0];
                        foreach (Vector3 p in road.road)
                        {
                            if (Flat(p - feet).sqrMagnitude < Flat(nearest - feet).sqrMagnitude)
                                nearest = p;
                        }
                        if (Flat(nearest - feet).magnitude < 8f)
                        {
                            if (!DriveTheirWay())
                                Idle(UnityEngine.Random.Range(1f, 2.5f));
                            return;
                        }
                        driveOnRoad = true;
                        viaLeft = false;
                        planPlace = Ground(nearest + Vector3.up, out Vector3 onGround) ? onGround : nearest;
                        Lab.Event("roam: to the way you drove first (" + Flat(planPlace - feet).magnitude.ToString("0") + " m), to set off from there");
                    }
                    else
                    {
                        if (!DriveTo(planPlace, planWhy))
                            Idle(UnityEngine.Random.Range(1f, 2.5f));
                        return;
                    }
                }
                nav.SetGoal(viaLeft ? viaPlace : planPlace);
            }
            // A stop on the way, now and then, to look at something.
            if (Time.time < pauseUntil)
            {
                walk = Vector2.zero;
                running = false;
                return;
            }
            if (Time.time >= pauseNext && !climbing)
            {
                pauseNext = Time.time + UnityEngine.Random.Range(7f, 16f);
                if (UnityEngine.Random.value < Tuning.F("roam.pause", 0.4f) && Flat(planPlace - feet).magnitude > 5f)
                {
                    pauseUntil = Time.time + UnityEngine.Random.Range(1.2f, 3f) * personality.calm;
                    gazeUntil = 0f;
                    return;
                }
            }
            if (viaLeft && Flat(viaPlace - feet).magnitude < Tuning.F("roam.bend.round", 4.5f))
            {
                viaLeft = false;
                nav.SetGoal(planPlace);
            }
            if (onDetour && Flat(nav.Goal - feet).magnitude < 1.5f)
            {
                onDetour = false;
                nav.SetGoal(detourFor);
            }
            if (!nav.HasGoal && !climbing)
                nav.SetGoal(planPlace);
            Vector3 before = feet;
            running = planRun && !onDetour;
            // (Slower as it comes up to the place.)
            float near = Flat(planPlace - feet).magnitude;
            float push = running ? MotionLibrary.RunPush : planPush * Mathf.Lerp(0.75f, 1f, Mathf.Clamp01(near / 3f));
            bool done = Travel(push, planSight == Sight.Bot || planSight == Sight.You ? 2.2f : 0.9f, dt) || nav.StuckCount >= 3;
            if (walk.sqrMagnitude > 0.01f)
                roamHeading = Quaternion.Euler(0f, bodyYaw, 0f) * new Vector3(walk.x, 0f, walk.y).normalized;
            if (!done || climbing)
                return;
            running = false;
            bool there = !viaLeft && !onDetour && Flat(planPlace - feet).magnitude < 3f && Mathf.Abs(planPlace.y - feet.y) < 1.6f;
            if (!there)
            {
                // Something in its way: round it, by the most open way that still goes that way.
                if (viaLeft)
                {
                    viaLeft = false;
                    nav.Clear();
                    nav.SetGoal(planPlace);
                    return;
                }
                Vector3 goal = onDetour ? detourFor : planPlace;
                if (detours < 3 && WayRound(feet, goal, out Vector3 side))
                {
                    detours++;
                    onDetour = true;
                    detourFor = goal;
                    nav.Clear();
                    nav.SetGoal(side);
                    Lab.Event("roam: something in the way, going round");
                    return;
                }
                // (Not got to the way the player drove, but near enough to it: off from here.)
                if (planSight == Sight.Drive && driveOnRoad && Flat(planPlace - feet).magnitude < 15f && DriveTheirWay())
                    return;
                Lab.Event("roam: no way there, another idea (" + Flat(planPlace - feet).magnitude.ToString("0") + " m short; walking ended: " + travelEnded + ", stuck " + nav.StuckCount + ")");
                SelfTaught.Bad(planPlace);
                nav.Clear();
                Idle(UnityEngine.Random.Range(1f, 2.5f));
                return;
            }
            // There: what it came for, for a while.
            nav.Clear();
            visited.Add(Cell(feet));
            float stay = UnityEngine.Random.Range(4f, 10f) * personality.calm;
            switch (planSight)
            {
                case Sight.Thing:
                    // (Often it picks the thing up.)
                    if (UnityEngine.Random.value < Tuning.F("roam.take", 0.9f) && FindSomethingToPlayWith())
                    {
                        act = Act.Play;
                        actUntil = Time.time + 30f;
                        return;
                    }
                    break;
                case Sight.High:
                    stay *= 1.5f;
                    break;
                case Sight.Drive:
                    // (At the way the player drove: off along it.)
                    if (driveOnRoad && DriveTheirWay())
                        return;
                    // (At the one standing on the map: along the player's way if one is kept, or off somewhere far.)
                    if (!BotManager.OwnSpawns)
                    {
                        if (Lessons.Road() != null && DriveTheirWay())
                            return;
                        if (FarPlace(feet, out Vector3 away) && DriveTo(away, "going for a drive"))
                            return;
                    }
                    break;
                case Sight.Tall:
                    // (Up it, with the thing for that.)
                    if (ClimbWithTool(planWall.foot, planWall.inward, planWall.height))
                        return;
                    break;
            }
            Idle(stay);
            Look(planLook, null, "what it came to see", Mathf.Min(stay, UnityEngine.Random.Range(2.5f, 5f)));
            Lab.Event("roam: there (" + planWhy + "), staying " + stay.ToString("0") + " s");
        }

        // A way round what is in front of it: the most open direction that still makes for where it is going.
        private bool WayRound(Vector3 feet, Vector3 goal, out Vector3 side)
        {
            side = feet;
            Vector3 eye = feet + Vector3.up * 1.2f;
            Vector3 to = Flat(goal - feet).normalized;
            float best = 0f;
            foreach (float angle in new[] { 40f, -40f, 70f, -70f, 100f, -100f, 130f, -130f })
            {
                Vector3 dir = Quaternion.Euler(0f, angle, 0f) * to;
                float clear = Physics.SphereCast(eye, 0.3f, dir, out RaycastHit hit, 8f, ~0, QueryTriggerInteraction.Ignore) ? hit.distance : 8f;
                if (clear < 2.5f)
                    continue;
                Vector3 p = feet + dir * Mathf.Min(clear * 0.75f, 6f);
                if (!Ground(p, out Vector3 ground) || Mathf.Abs(ground.y - feet.y) > 1.2f || SelfTaught.BadAt(ground) >= 2)
                    continue;
                // (Nearer straight on is better; and from there the place should be nearer to hand, or at least in view.)
                float score = clear * (1f - Mathf.Abs(angle) / 220f) + (Physics.Linecast(ground + Vector3.up * 1.2f, goal + Vector3.up * 1.2f, ~0, QueryTriggerInteraction.Ignore) ? 0f : 4f);
                if (score > best)
                {
                    best = score;
                    side = ground;
                }
            }
            return best > 0f;
        }
    }
}
