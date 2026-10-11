using Il2CppSLZ.Marrow;
using UnityEngine;

namespace ThirdEyeBonelabMod.Bots
{
    /// <summary>
    /// A bot left to itself ("Walk around on its own"): one thing after another, as a player
    /// passes the time, chosen by chance and by its personality. It stands about looking round,
    /// strolls, jogs or sprints somewhere, goes up somewhere high (climbing, if it has to) and
    /// comes down again, picks something up, gets a gun and shoots at things, or comes over to
    /// see what the player is doing.
    /// </summary>
    public partial class Bot
    {
        private enum Act { Idle, Stroll, Jog, Sprint, Climb, Play, Shoot, Visit, Explore, Use, Tinker, Go }

        /// <summary>What it is doing, in a few words (its thought, in brain mode).</summary>
        private string ActLabel()
        {
            if (climbing)
                return "climbing up";
            if (Playing)
                return play == Play.Shoot ? "shooting practice" : tinkering ? "trying out the " + tinkerName : "playing with something";
            if (Time.time < waveUntil)
                return "waving";
            if (fid != Fid.None)
                return FidgetLabel();
            switch (act)
            {
                case Act.Idle: return "looking around";
                case Act.Stroll: return "strolling";
                case Act.Jog: return "jogging";
                case Act.Sprint: return "sprinting";
                case Act.Climb: return "going up high";
                case Act.Visit: return "coming to see you";
                case Act.Explore: return "exploring somewhere new";
                case Act.Go: return thinking2 ? "thinking... " + planWhy : Time.time < pauseUntil ? "stopping to look" : planWhy;
                case Act.Use: return lessonNext != null ? "going to use the " + lessonNext.name : "";
                default: return "";
            }
        }

        private Act act = Act.Idle;
        private Act lastAct = Act.Idle;
        private float actUntil;

        private void Life(Transform you, Transform head, float dt)
        {
            visited.Add(Cell(Feet));
            if (Playing)
            {
                running = false;
                if (!PlayStep(you, head, dt))
                    Idle(Random(1.5f, 4f));
                return;
            }
            if (!climbing && Time.time >= actUntil)
                NextAct(you);
            if (act == Act.Go)
            {
                GoStep(you, head, dt);
                return;
            }
            switch (act)
            {
                case Act.Idle:
                    walk = Vector2.zero;
                    LookAbout(you, head, dt);
                    return;
                case Act.Use:
                    headYaw = 0f;
                    LessonAct(dt);
                    return;
                case Act.Tinker:
                    // (Waiting for the thing it spawned to try; once it is there, its hands take over.)
                    walk = Vector2.zero;
                    if (!TinkerPending())
                        Idle(Random(1f, 3f));
                    return;
                default:
                    headYaw = 0f;
                    if (!nav.HasGoal && !climbing)
                    {
                        Idle(Random(1f, 3f));
                        return;
                    }
                    running = act == Act.Sprint || (act == Act.Explore && personality.energy > 1.1f);
                    float speed = running ? MotionLibrary.RunPush
                        : act == Act.Stroll ? Tuning.F("life.stroll", 0.45f)
                        : MotionLibrary.WalkPush;
                    if (Travel(speed, act == Act.Visit ? BotManager.FollowDistance + 0.5f : 0.8f, dt) || nav.StuckCount >= 3)
                    {
                        running = false;
                        nav.Clear();
                        if (act == Act.Climb)
                            Lab.Event("life: up high");
                        // There: it stands a while (longer after a climb: it looks out from up there).
                        Idle(act == Act.Climb ? Random(4f, 10f) : Random(1.5f, 5f) * personality.calm);
                    }
                    return;
            }
        }

        private void Idle(float seconds)
        {
            act = Act.Idle;
            actUntil = Time.time + seconds;
            walk = Vector2.zero;
            running = false;
        }

        // Standing about: looking round, and at the player when they are near.
        private void LookAbout(Transform you, Transform head, float dt)
        {
            float near = you != null && head != null ? Flat(you.position - head.position).magnitude : 99f;
            if (near < 6f)
            {
                float off = Watch(you, head, dt, 90f);
                headYaw = Mathf.MoveTowards(headYaw, Mathf.Clamp(off, -60f, 60f), 120f * dt);
            }
            else
            {
                headYaw = Mathf.Sin((Time.time - born) * 0.5f) * 45f + Mathf.Sin((Time.time - born) * 0.17f) * 20f;
            }
        }

        // What next: by chance, weighed by its personality (and not the same thing twice running).
        private void NextAct(Transform you)
        {
            Vector3 feet = rig.physicsRig != null && rig.physicsRig.centerOfPressure != null ? rig.physicsRig.centerOfPressure.position : Feet;
            bool armed = gun != null;
            bool youNear = you != null && Flat(you.position - feet).magnitude < 25f;
            (Act what, float weight)[] choices =
            {
                // Mostly it goes to see something (BotRoam: what, and why, is chosen there, from
                // what there is about); the rest are what it does besides.
                (Act.Go, Tuning.F("life.go", 4f)),
                (Act.Idle, 0.45f * personality.calm),
                (Act.Play, 0.5f * personality.curiosity),
                (Act.Shoot, armed || BotManager.OwnSpawns ? 0.6f * personality.shooter : 0f),

                // Something the player showed it how to use (see Lessons).
                (Act.Use, Lessons.Count > 0 && !enemy ? Tuning.F("life.use", 0.8f) * personality.curiosity : 0f),
                // Something to find out about by trying it (see BotCurious).
                (Act.Tinker, !enemy && !armed ? Tuning.F("life.tinker", 1.7f) * personality.curiosity : 0f),
            };
            // Something another bot has just found out: that, first.
            if (tinkerWant != null && !enemy && Begin(Act.Tinker, you, feet))
            {
                lastAct = Act.Tinker;
                Lab.Event("life: Tinker (heard of it)");
                return;
            }
            for (int tries = 0; tries < 4; tries++)
            {
                float total = 0f;
                foreach ((Act what, float weight) in choices)
                {
                    if (what != lastAct || what == Act.Go)
                        total += weight;
                }
                float roll = UnityEngine.Random.value * total;
                Act pick = Act.Go;
                foreach ((Act what, float weight) in choices)
                {
                    if (what == lastAct && what != Act.Go)
                        continue;
                    roll -= weight;
                    if (roll <= 0f)
                    {
                        pick = what;
                        break;
                    }
                }
                if (Begin(pick, you, feet))
                {
                    lastAct = pick;
                    Lab.Event("life: " + pick);
                    return;
                }
            }
            Idle(Random(2f, 5f));
        }

        // Starts it; false if it cannot (no climb about, nothing to pick up, no gun to be had).
        private bool Begin(Act what, Transform you, Vector3 feet)
        {
            nav.Clear();
            act = what;
            actUntil = Time.time + 30f;
            switch (what)
            {
                case Act.Idle:
                    Idle(Random(3f, 8f) * personality.calm);
                    // (Now and then down into a crouch, as the player crouches, if they have shown it one.)
                    if (UnityEngine.Random.value < Tuning.F("life.crouch", 0.25f))
                        StartCrouchOfTheirs();
                    return true;
                case Act.Stroll:
                case Act.Jog:
                case Act.Sprint:
                    float far = what == Act.Stroll ? Random(4f, 12f) : what == Act.Jog ? Random(10f, 22f) : Random(15f, 35f);
                    if (!Navigator.RandomPlace(feet, far * 0.6f, far, out Vector3 place))
                        return false;
                    // (Not somewhere it has got stuck before, if there is anywhere else.)
                    for (int again = 0; again < 4 && SelfTaught.BadAt(place) >= 2; again++)
                    {
                        if (Navigator.RandomPlace(feet, far * 0.6f, far, out Vector3 other))
                            place = other;
                    }
                    nav.SetGoal(place);
                    return true;
                case Act.Visit:
                    if (you == null || !Navigator.RandomPlace(you.position - Vector3.up * 1.4f, 1.5f, 3f, out Vector3 near))
                        return false;
                    nav.SetGoal(near);
                    return true;
                case Act.Tinker:
                    return BeginTinker(feet);
                case Act.Go:
                    return BeginGo(you, feet);
                case Act.Climb:
                    // (Sometimes back up somewhere it has got up to before.)
                    if (UnityEngine.Random.value < 0.35f && SelfTaught.KnownTop(feet, 50f, out Vector3 known))
                    {
                        nav.SetGoal(known);
                        Lab.Event("life: back up to a high place it knows");
                        return true;
                    }
                    if (!FindHighGround(feet, out Vector3 top))
                        return false;
                    nav.SetGoal(top);
                    return true;
                case Act.Explore:
                    if (!FarPlace(feet, out Vector3 away))
                        return false;
                    nav.SetGoal(away);
                    Lab.Event("life: exploring " + Flat(away - feet).magnitude.ToString("0") + " m off" + (visited.Contains(Cell(away)) ? "" : ", somewhere new"));
                    return true;
                case Act.Use:
                    return BeginLessonAct(feet);
                case Act.Play:
                    return FindSomethingToPlayWith();
                case Act.Shoot:
                    return GetAGun();
            }
            return false;
        }

        /// <summary>
        /// Somewhere far off to explore (20 to 60 m), over hills, behind buildings, up slopes:
        /// anywhere with ground a body can stand on and room to stand (it need not be in sight:
        /// the way there is found as it goes). Somewhere it has never been comes first, then the
        /// further the better. (Places "in plain sight, the ground no lower than it" were all it
        /// had before, and on a map of buildings and hills those were all close by.)
        /// </summary>
        private bool FarPlace(Vector3 feet, out Vector3 place)
        {
            place = feet;
            float best = float.MinValue;
            float near = Tuning.F("explore.near", 20f), far = Tuning.F("explore.far", 60f);
            for (int i = 0; i < 28; i++)
            {
                Vector2 r = UnityEngine.Random.insideUnitCircle.normalized * Random(near, far);
                Vector3 p = feet + new Vector3(r.x, 0f, r.y);
                Vector3 ground;
                try
                {
                    if (UnityEngine.AI.NavMesh.SamplePosition(p, out UnityEngine.AI.NavMeshHit hit, 4f, UnityEngine.AI.NavMesh.AllAreas))
                        ground = Navigator.Surface(hit.position, feet.y);
                    else if (!Ground(p, out ground))
                        continue;
                }
                catch (Exception)
                {
                    if (!Ground(p, out ground))
                        continue;
                }
                float score = (visited.Contains(Cell(ground)) ? 0f : 3f) + Flat(ground - feet).magnitude / far + UnityEngine.Random.value * 0.6f
                              - Mathf.Abs(ground.y - feet.y) * 0.02f - SelfTaught.BadAt(ground) * 1.5f;
                if (score > best)
                {
                    best = score;
                    place = ground;
                }
            }
            return best > float.MinValue;
        }

        // Walkable ground under a point (from well above, down a long way), with room to stand.
        private static bool Ground(Vector3 p, out Vector3 ground)
        {
            ground = p;
            if (!Physics.Raycast(p + Vector3.up * 25f, Vector3.down, out RaycastHit hit, 60f, ~0, QueryTriggerInteraction.Ignore))
                return false;
            if (hit.normal.y < 0.65f || hit.collider.attachedRigidbody != null)
                return false;
            // (Room to stand: nothing solid where its body would be.)
            if (Physics.CheckCapsule(hit.point + Vector3.up * 0.4f, hit.point + Vector3.up * 1.6f, 0.25f, ~0, QueryTriggerInteraction.Ignore))
                return false;
            ground = hit.point;
            return true;
        }

        // Somewhere high nearby to go up onto: a wall it can climb (1.2 to 2.5 m), with room on top.
        private bool FindHighGround(Vector3 feet, out Vector3 top)
        {
            top = Vector3.zero;
            float start = UnityEngine.Random.Range(0f, 360f);
            for (int i = 0; i < 16; i++)
            {
                Vector3 dir = Quaternion.Euler(0f, start + i * 22.5f, 0f) * Vector3.forward;
                if (!Physics.Raycast(feet + Vector3.up * 0.6f, dir, out RaycastHit wall, 15f, ~0, QueryTriggerInteraction.Ignore))
                    continue;
                if (wall.distance < 1.5f || wall.collider.attachedRigidbody != null || wall.collider.GetComponentInParent<RigManager>() != null)
                    continue;
                Vector3 inward = -Flat(wall.normal);
                if (inward.sqrMagnitude < 0.01f)
                    continue;
                inward.Normalize();
                if (!Physics.Raycast(wall.point + inward * 0.6f + Vector3.up * 3.3f, Vector3.down, out RaycastHit onTop, 3.6f, ~0, QueryTriggerInteraction.Ignore))
                    continue;
                float height = onTop.point.y - feet.y;
                if (onTop.normal.y < 0.7f || height < Tuning.F("climb.min", 1.2f) || height > Tuning.F("climb.max", 2.5f))
                    continue;
                if (Physics.Raycast(onTop.point + Vector3.up * 0.05f, Vector3.up, 1.6f, ~0, QueryTriggerInteraction.Ignore))
                    continue;
                // (Not a wall it has failed at before, more than once.)
                if (SelfTaught.ClimbFails(onTop.point) >= 2)
                    continue;
                top = onTop.point + Vector3.up * 0.05f;
                return true;
            }
            return false;
        }

        private static float Random(float a, float b) => UnityEngine.Random.Range(a, b);
    }
}
