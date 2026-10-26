using System.Globalization;
using BoneLib;
using Il2CppTMPro;
using UnityEngine;

namespace ThirdEyeBonelabMod.Bots
{
    /// <summary>
    /// The bot's brain. It looks about (enemies and how they move, the player and what they
    /// shoot at, its own gun and ammo), remembers what it has seen, and every moment weighs what
    /// it could do: fight, back off, get to cover, arm itself, go and look where an enemy was,
    /// explore, keep up with the player, guard their back, play with things, practise, or stand
    /// about. Each is weighed by how fitting it is now and by what it has learned of times like
    /// these (see Knowledge: learned from every few seconds of doing it, and carried over to any
    /// situation like it). It fights the player's way (their shooting rhythm, the distance they
    /// keep, when they reload, from watching them), shoots what they shoot, looks round at
    /// gunfire, never stands in their line of fire, and goes for the most dangerous enemy first.
    /// </summary>
    public partial class Bot
    {
        public enum Goal
        {
            Idle,
            Fight,
            Retreat,
            TakeCover,
            ArmUp,
            Investigate,
            Explore,
            Follow,
            Guard,
            Play,
            Practice,
            /// <summary>Fists: closing on what is near and punching it, as the player punches.</summary>
            Brawl,
        }

        private const float ThinkEvery = 0.3f;
        private const float LearnEvery = 3f;
        private const float CloseCallRange = 1.6f;
        private const float CellSize = 5f;

        // What it sees.
        private readonly List<Targets.Target> hostiles = new List<Targets.Target>();
        private readonly List<Vector3> seenEnemies = new List<Vector3>();
        private Targets.Target nearestSeen;
        private Targets.Target mostDangerous;
        private float nearestSeenDistance = 999f;
        private float nearestAnyDistance = 999f;
        private Vector3 nearestAnyAt;
        private readonly float[] seen = new float[Knowledge.Features];

        // What it remembers.
        private Vector3 lastEnemyAt;
        private Vector3 lastEnemyVelocity;
        private float lastEnemyTime = -100f;
        private readonly HashSet<long> visited = new HashSet<long>();
        private bool wasSeen;
        // Something to look at (gunfire it heard), till when.
        private Vector3 lookAt;
        private float lookUntil;

        // What it is doing, and how that is going.
        public Goal goal = Goal.Idle;
        private float goalSince;
        private float goalReward;
        private float windowSince;
        private readonly float[] windowSeen = new float[Knowledge.Features];
        private float thinkAt;
        private float closeCallAt = -10f;
        private string why = "";
        private Vector3 goalPlace;
        private bool hasGoalPlace;
        private string heldBarcode;
        private float heldSince;
        // Targets it shot at lately (to credit it with the kill when they go down).
        private Targets.Target engaged;
        private float engagedAt = -10f;
        private Targets.Target brainTarget;
        private float coverReachedAt = -1f;

        private TextMeshPro thought;
        private static TMP_FontAsset font;

        private bool Armed => gun != null && Holding();

        /// <summary>A reward (or a cost) for whatever it is doing now.</summary>
        private void Feel(string what)
        {
            switch (what)
            {
                case "kill": goalReward += 1f; break;
                case "prop": goalReward += 0.15f; break;
                case "armed": goalReward += 0.4f; break;
                case "reloaded": goalReward += 0.15f; break;
                case "found": goalReward += 0.4f; break;
                case "new place": goalReward += 0.08f; break;
                case "caught up": goalReward += 0.15f; break;
                case "in cover": goalReward += 0.3f; break;
                case "close call": goalReward -= 0.5f; break;
                case "in the way": goalReward -= 0.2f; break;
            }
        }

        // ---------------------------------------------------------------- senses

        private void Sense()
        {
            Transform head = Head;
            if (head == null)
                return;
            Vector3 eye = head.position;
            hostiles.Clear();
            // (An enemy bot's one enemy is the player.)
            if (!enemy)
                hostiles.AddRange(Targets.Hostiles());
            else if (Targets.PlayerTarget(out Targets.Target playerTarget))
                hostiles.Add(playerTarget);
            // (And the bots of the other side.)
            hostiles.AddRange(Targets.OtherSide(rig));
            seenEnemies.Clear();
            nearestSeen = default;
            mostDangerous = default;
            nearestSeenDistance = 999f;
            nearestAnyDistance = 999f;
            float worst = -1f;
            foreach (Targets.Target t in hostiles)
            {
                Vector3 p = t.Point;
                float d = (p - eye).magnitude;
                if (d < nearestAnyDistance)
                {
                    nearestAnyDistance = d;
                    nearestAnyAt = p;
                }
                // (One right by the player is not shot at; with its hands or a thing to hit with, it goes to their help.)
                // (One right by the player is seen like any other: whether it may be shot at there is decided as it fires.)
                if (d > 45f)
                    continue;
                // (Seen a moment ago is still seen: a look that came and went three times a second took its mind with it.)
                IntPtr who = t.transform != null ? t.transform.Pointer : IntPtr.Zero;
                if (Targets.InSight(eye, p, t.transform, rig))
                    sightAt[who] = Time.time;
                else if (!sightAt.TryGetValue(who, out float sawAt) || Time.time - sawAt > Tuning.F("brain.sight", 1.3f))
                    continue;
                seenEnemies.Add(p);
                if (d < nearestSeenDistance)
                {
                    nearestSeenDistance = d;
                    nearestSeen = t;
                }
                float danger = Tactics.Danger(t, eye);
                if (danger > worst)
                {
                    worst = danger;
                    mostDangerous = t;
                }
            }
            if (nearestSeen.transform != null)
            {
                // Found what it went looking for.
                if (!wasSeen && goal == Goal.Investigate)
                    Feel("found");
                lastEnemyAt = mostDangerous.Point;
                lastEnemyVelocity = Tactics.Velocity(mostDangerous);
                lastEnemyTime = Time.time;
            }
            // (A call as it spots one, with none in sight before.)
            if (nearestSeen.transform != null && !wasSeen)
                Call("spot");
            wasSeen = nearestSeen.transform != null;

            // Gunfire heard (the player's): it looks where the shot went.
            if (Time.time - PlayerWatch.FiredAt < 0.5f && nearestSeen.transform == null)
            {
                lookAt = PlayerWatch.ShotAt;
                lookUntil = Time.time + 2.5f;
            }

            // Too close for comfort.
            if (nearestAnyDistance < CloseCallRange && Time.time - closeCallAt > 2.5f)
            {
                closeCallAt = Time.time;
                Feel("close call");
                Knowledge.CloseCall();
            }

            // Places it has been (for exploring somewhere new).
            if (visited.Add(Cell(Feet)) && goal == Goal.Explore)
                Feel("new place");

            // Its kill: what it was shooting at, gone down soon after.
            if (engaged.transform != null && !Targets.Alive(engaged))
            {
                if (Time.time - engagedAt < 2f)
                {
                    if (engaged.prop != null)
                        Feel("prop");
                    else
                    {
                        Feel("kill");
                        // (The last of them down.)
                        if (seenEnemies.Count <= 1)
                            Call("clear");
                        Knowledge.KilledWith(heldBarcode, (engaged.Point - eye).magnitude);
                    }
                }
                engaged = default;
            }

            // Time with its gun (for how well each gun does).
            string barcode = Armed ? BarcodeOf(gun) : null;
            if (barcode != heldBarcode)
            {
                if (heldBarcode != null)
                    Knowledge.HeldGun(heldBarcode, Time.time - heldSince);
                if (barcode != null && heldBarcode == null)
                    Feel("armed");
                heldBarcode = barcode;
                heldSince = Time.time;
            }

            // What it sees, as its choices are weighed by.
            float you = Player.Head != null ? Flat(Player.Head.position - eye).magnitude : 0f;
            seen[0] = 1f;
            seen[1] = nearestSeen.transform != null ? 1f : 0f;
            seen[2] = nearestSeenDistance < 5f ? 1f : 0f;
            seen[3] = Mathf.Clamp01(seenEnemies.Count / 4f);
            seen[4] = Armed ? 1f : 0f;
            seen[5] = Armed && AmmoFraction() < 0.3f ? 1f : 0f;
            seen[6] = you > 10f ? 1f : 0f;
            seen[7] = nearestSeen.transform == null && Time.time - lastEnemyTime < 25f ? 1f : 0f;
            seen[8] = Time.time - closeCallAt < 5f ? 1f : 0f;
            seen[9] = PlayerWatch.Fighting ? 1f : 0f;
        }

        private static long Cell(Vector3 p)
        {
            long x = Mathf.FloorToInt(p.x / CellSize);
            long z = Mathf.FloorToInt(p.z / CellSize);
            return (x << 32) ^ (z & 0xffffffffL);
        }

        private static string BarcodeOf(Il2CppSLZ.Marrow.Gun g)
        {
            try
            {
                Il2CppSLZ.Marrow.Pool.Poolee poolee = g.GetComponentInParent<Il2CppSLZ.Marrow.Pool.Poolee>();
                return poolee != null && poolee.SpawnableCrate != null ? poolee.SpawnableCrate.Barcode.ID : g.name;
            }
            catch (Exception)
            {
                return g.name;
            }
        }

        private float AmmoFraction()
        {
            try
            {
                int full = gun.defaultMagazine != null ? Mathf.Max(1, gun.defaultMagazine.rounds) : 15;
                return Mathf.Clamp01(gun.AmmoCount() / (float)full);
            }
            catch (Exception)
            {
                return 1f;
            }
        }

        /// <summary>When it changes magazines in a lull: the player's own habit, or at a quarter.</summary>
        private static float ReloadBelow => Mathf.Clamp(Knowledge.Style.Median(Knowledge.Style.reloadAt, 0.25f), 0.05f, 0.6f);

        // ---------------------------------------------------------------- choosing

        // ---------------------------------------------------------------- partner mode

        // Partner mode (BotManager.StayNear, for the player's own bots): it keeps within reach of the
        // player and does its own thinking inside that: fights what it sees, but does not chase it, or
        // wander, off out of reach. How far it lets itself get from them (flat, metres): comfortably,
        // and the most it will go before it comes back. (Both can be changed in lab/tuning.txt.)
        private bool Partnered => BotManager.StayNear && !this.enemy;
        private static float PartnerLeash => Tuning.F("partner.leash", 14f);
        private static float PartnerMax => Tuning.F("partner.max", 26f);

        /// <summary>How far the player is from it, along the ground (0 when there is no player to measure to).</summary>
        private float YouFar()
        {
            Transform me = Head;
            return Player.Head != null && me != null ? Flat(Player.Head.position - me.position).magnitude : 0f;
        }

        // How fitting each goal is right now (before what it has learned), and why.
        private float Fit(Goal g, out string reason)
        {
            bool enemy = nearestSeen.transform != null;
            bool remembered = !enemy && Time.time - lastEnemyTime < 25f;
            float you = Player.Head != null && Head != null ? Flat(Player.Head.position - Head.position).magnitude : 0f;
            bool low = Armed && AmmoFraction() < 0.3f;
            reason = "";
            switch (g)
            {
                case Goal.Fight:
                    reason = enemy ? "enemy " + nearestSeenDistance.ToString("0") + " m" + (seenEnemies.Count > 1 ? ", " + seenEnemies.Count + " seen" : "") : "";
                    // (A partner well out of reach of the player, with nothing on top of it: it goes back to them, not on after it.)
                    if (Partnered && enemy && you > PartnerMax && nearestSeenDistance > 6f)
                        return 0.5f;
                    if (enemy && Armed)
                        return 0.9f;
                    // (Out of sight a moment only: still at it, not off looking and back again three times a second.)
                    return Armed && Time.time - lastEnemyTime < Tuning.F("brain.hold", 1.8f) ? 0.72f : 0f;
                case Goal.Retreat:
                    // Backing off: reloading with one close, or bare-handed and outnumbered.
                    reason = Reloading ? "too close to reload" : "too many";
                    if (nearestAnyDistance < 3f && Armed && Reloading)
                        return 0.95f;
                    if (nearestAnyDistance < 3f && !Armed && seenEnemies.Count >= 3)
                        return 0.9f;
                    return nearestAnyDistance < 1.8f && Armed ? 0.6f : 0f;
                case Goal.TakeCover:
                    // Reloading, low, or outnumbered, under fire from what it can see.
                    reason = Reloading ? "reloading" : low ? "low on ammo" : "outnumbered";
                    bool needs = Reloading || low || seenEnemies.Count >= 3 || Time.time - closeCallAt < 4f;
                    return enemy && Armed && needs && nearestSeenDistance > 3.5f ? 0.8f : 0f;
                case Goal.ArmUp:
                    reason = enemy || remembered ? "needs a gun" : "wants a gun";
                    // (Not with something on top of it: no time to go and get one.)
                    // (Something to hit with in its hand is armed enough; and with one right on top of it there is no time to go and get anything.)
                    // (On its way to one: it sees that through.)
                    if (meleeWanted && Playing)
                        return 0.92f;
                    // (In with its hands to get something off the player: its gun stays away for that.)
                    if (Assisting)
                        return 0f;
                    // (One put away on its body: out with it, with something to fight in sight; left where it is while there is not.)
                    if (HasStowed)
                        return enemy ? 0.97f : 0f;
                    if (Armed || MeleeArmed || (enemy && nearestSeenDistance < Tuning.F("brain.tooclose", 2.2f) && seenEnemies.Count < 3))
                        return 0f;
                    // (No gun to be had, found a moment ago: not asked for again and again. It
                    // was going for one and giving it up three times a second.)
                    if (Time.time < noGunUntil)
                        return 0f;
                    if (Partnered && !enemy && you > PartnerLeash)
                        return 0f;
                    return enemy || remembered ? 0.85f : hostiles.Count > 0 ? 0.6f : 0.25f;
                case Goal.Investigate:
                    reason = this.enemy ? "after you" : "where it saw one";
                    // (An enemy bot is after the player wherever they have got to, not only where it last saw them.)
                    // (Seen or not: an unarmed one a way off has nothing better to do than get to them.)
                    if (this.enemy && Player.Head != null && Head != null && (Player.Head.position - Head.position).magnitude < Tuning.F("enemy.hunt", 90f))
                        return 0.62f;
                    if (Partnered && remembered && Player.Head != null && Flat(lastEnemyAt - Player.Head.position).magnitude > PartnerLeash)
                        return 0f;
                    return remembered ? 0.6f : 0f;
                case Goal.Explore:
                    reason = "somewhere new";
                    // (A partner does not go exploring: it stays by the player.)
                    return Partnered ? 0f : 0.3f;
                case Goal.Follow:
                    reason = "keeping up with you";
                    // (Only when asked to stay by the player: left to itself it goes where it likes.)
                    // (A partner out of reach comes back before anything else, short of what is on top of it.)
                    if (Partnered)
                        return you > PartnerMax ? 1.1f : you > PartnerLeash ? 0.8f : you > PartnerLeash * 0.6f ? 0.4f : 0.05f;
                    return !BotManager.StayNear ? 0f : you > 18f ? 0.7f : you > 10f ? 0.4f : 0.05f;
                case Goal.Guard:
                    // With the player, watching their back while they fight (or wait).
                    reason = PlayerWatch.Fighting ? "watching your back" : "staying by you";
                    // (A partner stays by the player, armed or not, and what it does about the player's fight is the rest of its brain's.)
                    if (Partnered)
                        return you < PartnerMax ? (PlayerWatch.Fighting && Armed ? 0.65f : hostiles.Count > 0 ? 0.35f : 0.2f) : 0f;
                    return Armed && you < 25f ? (PlayerWatch.Fighting ? 0.65f : !BotManager.StayNear ? 0f : hostiles.Count > 0 ? 0.35f : 0.12f) : 0f;
                case Goal.Brawl:
                    // Bare-handed with one or two on it: fists up and into them.
                    reason = "fists, " + nearestSeenDistance.ToString("0.0") + " m";
                    // (With something to hit with: at them with it, from wherever they are.)
                    if (MeleeArmed)
                        return enemy ? 0.95f : 0.5f;
                    if (Assisting && enemy)
                        return 0.98f;
                    // (Right on top of it: fists, now. Further off, it looks for a weapon first (see ArmUp).)
                    if (enemy && !Armed && nearestSeenDistance < Tuning.F("brain.tooclose", 2.2f) && seenEnemies.Count < 3)
                        return 0.95f;
                    // (No gun to be had, and one in sight: its fists are what it has.)
                    return enemy && !Armed && Time.time < noGunUntil && seenEnemies.Count < 3 && nearestSeenDistance < 20f ? 0.7f : 0f;
                case Goal.Play:
                    reason = "bored";
                    return !Armed && hostiles.Count == 0 && !Partnered ? 0.25f : 0f;
                case Goal.Practice:
                    reason = "target practice";
                    return Armed && !enemy && Targets.Practice && !Partnered ? 0.3f : 0f;
                default:
                    reason = "looking around";
                    return 0.15f;
            }
        }

        private readonly Dictionary<IntPtr, float> sightAt = new Dictionary<IntPtr, float>();
        private float climbAfterAt;

        // The one it is after (the player, for an enemy bot; whoever it fights or last saw, for the others) is
        // up on something near by, and there is a wall that way it can get up: its weapon away, and up it.
        // True if it has begun (or is walking to the foot of the wall).
        private bool ClimbAfter(float dt)
        {
            if (InLesson || down || Reloading || Time.time < climbAfterAt || rig.physicsRig == null || rig.physicsRig.centerOfPressure == null)
                return false;
            Vector3 target;
            if (this.enemy && Player.Head != null)
                target = Player.Head.position - Vector3.up * 1.4f;
            else if (nearestSeen.transform != null)
                target = nearestSeen.Point - Vector3.up * 1.3f;
            else if (Time.time - lastEnemyTime < 20f && lastEnemyTime > 0f)
                target = lastEnemyAt - Vector3.up * 1.3f;
            else
                return false;
            Vector3 feet = rig.physicsRig.centerOfPressure.position;
            float up = target.y - feet.y;
            Vector3 to = Flat(target - feet);
            if (up < Tuning.F("climbafter.up", 1.2f) || to.magnitude > Tuning.F("climbafter.near", 12f) || to.magnitude < 0.05f)
                return false;
            Vector3 dir = to.normalized;
            bool low = FindEdge(feet, dir, out _, out _, out float height) && height >= Tuning.F("climb.min", 1.2f) && height <= Tuning.F("climb.max", 2.5f);
            bool tall = !low && Lessons.For("climb") != null && FindTallWall(feet, dir, 1.8f, out _, out _, out _, out _);
            if (!low && !tall)
            {
                // (No wall in reach yet: on towards them, to the foot of whatever they are up on.)
                if (to.magnitude > 1.2f && !Playing)
                {
                    running = to.magnitude > 5f;
                    Go(dir, running ? MotionLibrary.RunPush : MotionLibrary.WalkPush, dt);
                    return true;
                }
                return false;
            }
            // Its hands free for it.
            if (WeaponInHand)
                Stow();
            if (Playing)
                StopPlaying();
            nav.SetGoal(target);
            walk = Vector2.zero;
            climbLookAt = 0f;
            if (TryStartClimb(dir))
            {
                Lab.Event("climb: up after " + (this.enemy ? "you" : "it") + " (" + up.ToString("0.0") + " m above, " + to.magnitude.ToString("0.0") + " m off)");
                return true;
            }
            climbAfterAt = Time.time + 1.5f;
            return false;
        }

        private float noGunUntil;
        private Vector3 huntedAt;

        private void Think()
        {
            Goal best = goal;
            float bestScore = float.MinValue;
            string bestWhy = "";
            foreach (Goal g in Enum.GetValues(typeof(Goal)))
            {
                float fit = Fit(g, out string reason);
                if (fit <= 0f)
                    continue;
                // What it has learned of this in times like these, and a little of its own whim.
                float learned = Knowledge.Worth(g.ToString(), seen);
                float score = fit * (1f + 0.6f * learned) + UnityEngine.Random.Range(-0.03f, 0.03f);
                // Sticking with what it is doing, for a while at least.
                if (g == goal)
                    score += Time.time - goalSince < 2.5f ? 0.25f : 0.08f;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = g;
                    bestWhy = reason;
                }
            }
            // (Not changing its mind every third of a second: what it has just taken up it keeps at for
            // a moment, unless something pressing has come up.)
            if (best != goal && Time.time - goalSince < Tuning.F("brain.dwell", 1.6f) && bestScore < 0.93f && Fit(goal, out _) > 0f)
                best = goal;
            else
                why = bestWhy;
            if (best != goal)
                SwitchGoal(best);
            else if (Time.time - windowSince > LearnEvery)
                LearnWindow();
        }

        // How the last few seconds of its goal went (reward per ten seconds, less a little for
        // dawdling), learned against what it saw at the start of them.
        private void LearnWindow()
        {
            float seconds = Mathf.Max(Time.time - windowSince, 0.5f);
            float outcome = goalReward / seconds * 10f - 0.02f * seconds / 10f;
            Knowledge.Learn(goal.ToString(), windowSeen, outcome);
            goalReward = 0f;
            windowSince = Time.time;
            Array.Copy(seen, windowSeen, seen.Length);
        }

        private void SwitchGoal(Goal next)
        {
            Lab.Event("goal " + goal + " -> " + next);
            LearnWindow();
            goal = next;
            goalSince = Time.time;
            hasGoalPlace = false;
            coverReachedAt = -1f;
            nav.Clear();
            following = false;
            // A bare-handed bot puts down what it was playing with to do anything else.
            if (next != Goal.Play && Playing && !Armed && !MeleeArmed && !meleeWanted)
                StopPlaying();
        }

        // ---------------------------------------------------------------- doing

        /// <summary>The brain's turn this frame: sense, maybe rethink, and carry on with its goal.</summary>
        private void BrainStep(Transform you, Transform head, float dt)
        {
            if (Time.time >= thinkAt)
            {
                thinkAt = Time.time + ThinkEvery;
                Sense();
                Think();
            }
            brainTarget = goal == Goal.Fight || goal == Goal.TakeCover || goal == Goal.Guard ? mostDangerous : default;
            running = false;
            // Going up a wall: that, till it is up (whatever it has since thought of doing).
            if (climbing)
            {
                handOverride = false;
                leftOverride = false;
                ClimbStep(dt);
                Show();
                return;
            }
            // What it is after is up above it, close by: up after it (its weapon put away for the climb).
            if (ClimbAfter(dt))
            {
                Show();
                return;
            }
            // (What its hands were doing last frame is not carried over: each step says again.)
            if (!Playing)
            {
                handOverride = false;
                leftOverride = false;
            }
            fists = false;

            // Armed, its gun does the fighting, moving and practising (see Shoot); the brain says
            // what to shoot and where to go.
            // Nothing to fight for a while (lost, or gone): its weapon is put away, its hands its own
            // again, to go after them (up a wall, if that is where they went) or about its business.
            if (WeaponInHand && nearestSeen.transform == null && Time.time - lastEnemyTime > Tuning.F("stow.after", 7f) && Time.time - drawnAt > 3f
                && !Reloading && !(Targets.Practice && !this.enemy))
                Stow();
            if (Armed && play == Play.Shoot)
            {
                if (hostiles.Count > 0)
                    shootUntil = float.MaxValue;
                PlaceFor(goal);
                PlayStep(you, head, dt);
                StayOutOfTheWay(dt);
                Show();
                return;
            }
            if (Playing && (goal == Goal.Play || goal == Goal.ArmUp || play == Play.Shoot || play == Play.Reach || play == Play.Melee || meleeWanted))
            {
                PlayStep(you, head, dt);
                Show();
                return;
            }

            switch (goal)
            {
                case Goal.ArmUp:
                    if (HasStowed)
                    {
                        if (!Draw())
                            DropStowed();
                    }
                    else if (!Playing && !GetAWeapon())
                    {
                        noGunUntil = Time.time + Tuning.F("brain.nogun", 20f);
                        Lab.Event("brain: no weapon to be had, its fists for a while");
                        thinkAt = 0f;
                    }
                    else if (Playing)
                        PlayStep(you, head, dt);
                    break;
                case Goal.Play:
                case Goal.Explore:
                    Life(you, head, dt);
                    why = ActLabel();
                    break;
                case Goal.Brawl:
                    Brawl(head, dt);
                    break;
                case Goal.Follow:
                    Follow(you, head, dt);
                    if (!following && Time.time - goalSince > 1f)
                        Feel("caught up");
                    break;
                case Goal.Retreat:
                case Goal.Investigate:
                case Goal.Guard:
                case Goal.TakeCover:
                    // (After the player, who has moved on: after where they are now.)
                    if (goal == Goal.Investigate && this.enemy && hasGoalPlace && Player.Head != null && (Player.Head.position - huntedAt).magnitude > 2.5f && !climbing)
                    {
                        hasGoalPlace = false;
                        nav.Clear();
                    }
                    PlaceFor(goal);
                    if (hasGoalPlace)
                    {
                        if (!nav.HasGoal)
                            nav.SetGoal(goalPlace);
                        running = goal == Goal.Retreat || goal == Goal.TakeCover;
                        if (Travel(running ? MotionLibrary.RunPush : MotionLibrary.WalkPush, 1f, dt) || nav.StuckCount >= 3)
                            Arrived();
                    }
                    else
                    {
                        walk = Vector2.zero;
                    }
                    break;
                default:
                    Life(you, head, dt);
                    why = ActLabel();
                    break;
            }
            StayOutOfTheWay(dt);
            Show();
        }

        // Got where its goal was taking it.
        private void Arrived()
        {
            hasGoalPlace = false;
            nav.Clear();
            if (goal == Goal.Investigate)
                lastEnemyTime = -100f;
            if (goal == Goal.TakeCover && coverReachedAt < 0f)
            {
                coverReachedAt = Time.time;
                Feel("in cover");
            }
        }

        // Where a goal takes it: away from the nearest enemy, into cover, to where one was seen
        // (and where it was heading), beside the player, or somewhere it has not been.
        private void PlaceFor(Goal g)
        {
            if (hasGoalPlace)
                return;
            Vector3 feet = Feet;
            switch (g)
            {
                case Goal.Retreat:
                    Vector3 away = Flat(feet - nearestAnyAt);
                    away = away.sqrMagnitude < 0.01f ? Quaternion.Euler(0f, bodyYaw + 180f, 0f) * Vector3.forward : away.normalized;
                    hasGoalPlace = Navigator.RandomPlace(feet + away * 7f, 0f, 2f, out goalPlace);
                    break;
                case Goal.TakeCover:
                    if (coverReachedAt < 0f && Tactics.FindCover(feet, seenEnemies, out Vector3 cover))
                    {
                        goalPlace = cover;
                        hasGoalPlace = true;
                    }
                    break;
                case Goal.Investigate:
                    // (An enemy bot: where the player is now, to its own side of them.)
                    if (this.enemy && nearestSeen.transform == null && Player.Head != null)
                    {
                        goalPlace = Player.Head.position - Vector3.up * 1.4f + OwnSide(1.4f);
                        hasGoalPlace = true;
                        huntedAt = Player.Head.position;
                        break;
                    }
                    // Where it was last seen, a little on along the way it was going.
                    goalPlace = lastEnemyAt + Vector3.ClampMagnitude(lastEnemyVelocity * 2f, 4f) - Vector3.up * 1.2f;
                    hasGoalPlace = true;
                    break;
                case Goal.Guard:
                    // Beside the player and a little behind them (out of their way), if not there already.
                    Transform you = Player.Head;
                    if (you != null)
                    {
                        Vector3 facing = Flat(PlayerWatch.Fighting ? PlayerWatch.AimAlong : you.forward);
                        facing = facing.sqrMagnitude < 0.01f ? Vector3.forward : facing.normalized;
                        Vector3 side = Vector3.Cross(Vector3.up, facing) * (Vector3.Dot(feet - you.position, Vector3.Cross(Vector3.up, facing)) >= 0f ? 1f : -1f);
                        Vector3 spot = you.position - Vector3.up * 1.4f + side * 2.2f - facing * 1.2f;
                        if ((Flat(spot - feet)).magnitude > 1.5f && Navigator.RandomPlace(spot, 0f, 0.8f, out Vector3 p))
                        {
                            goalPlace = p;
                            hasGoalPlace = true;
                        }
                    }
                    break;
                case Goal.Explore:
                case Goal.Practice:
                    // A few tries for somewhere it has not been yet.
                    for (int i = 0; i < 8 && !hasGoalPlace; i++)
                    {
                        if (Navigator.RandomPlace(feet, 5f, 18f, out Vector3 p) && (!visited.Contains(Cell(p)) || i == 7))
                        {
                            goalPlace = p;
                            hasGoalPlace = true;
                        }
                    }
                    break;
            }
        }

        // Out of the player's line of fire: a step across it.
        private void StayOutOfTheWay(float dt)
        {
            Transform head = Head;
            if (head == null || !Tactics.InPlayersLine(head.position - Vector3.up * 0.4f, out Vector3 outOfIt))
                return;
            Vector3 local = Quaternion.Euler(0f, -bodyYaw, 0f) * outOfIt;
            walk = new Vector2(local.x, local.z) * MotionLibrary.WalkPush;
            if (Time.time - closeCallAt > 1f && UnityEngine.Random.value < dt)
                Feel("in the way");
        }

        /// <summary>Where it looks while it has nothing to aim at: about it, or at gunfire it heard.</summary>
        private Vector3 Lookout(Transform head)
        {
            if (Time.time < lookUntil)
                return lookAt;
            if (goal == Goal.Guard && PlayerWatch.Fighting)
                return head.position - Flat(PlayerWatch.AimAlong).normalized * 8f - Vector3.up * 0.8f;
            float sweep = Mathf.Sin((Time.time - born) * 0.55f) * 40f;
            return head.position + Quaternion.Euler(0f, bodyYaw + sweep, 0f) * new Vector3(0f, -1.3f, 2.5f);
        }

        // ---------------------------------------------------------------- showing its thoughts

        private void Show()
        {
            if (!BotManager.ShowThoughts)
            {
                if (thought != null)
                    thought.gameObject.SetActive(false);
                return;
            }
            Transform head = Head;
            if (head == null)
                return;
            if (thought == null)
            {
                GameObject label = new GameObject("ThirdEyeBotThought");
                thought = label.AddComponent<TextMeshPro>();
                // (The mod ships no font: it borrows one the game has loaded.)
                if (font == null)
                {
                    foreach (TMP_FontAsset candidate in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
                    {
                        if (candidate != null)
                        {
                            font = candidate;
                            break;
                        }
                    }
                }
                if (font != null)
                    thought.font = font;
                thought.fontSize = 0.5f;
                thought.rectTransform.sizeDelta = new Vector2(3f, 0.6f);
                thought.alignment = TextAlignmentOptions.Bottom;
                thought.enableWordWrapping = false;
                thought.richText = true;
                thought.color = new Color(0.75f, 0.95f, 1f, 0.95f);
            }
            thought.gameObject.SetActive(true);
            Transform label2 = thought.transform;
            label2.position = head.position + Vector3.up * 0.55f;
            if (Player.Head != null)
                label2.rotation = Quaternion.LookRotation(label2.position - Player.Head.position, Vector3.up);
            string text = goal + (why.Length > 0 ? ": " + why : "");
            if (Reloading)
                text = "Reloading (" + reload + ")";
            float worth = Knowledge.Worth(goal.ToString(), seen);
            thought.text = text + "\n<size=60%>expects " + worth.ToString("+0.00;-0.00", CultureInfo.InvariantCulture)
                + (Armed ? "  ammo " + Mathf.RoundToInt(AmmoFraction() * 100f) + "%" : "") + "</size>";
        }

        private void HideThought()
        {
            if (thought != null)
                UnityEngine.Object.Destroy(thought.gameObject);
            thought = null;
        }
    }
}
