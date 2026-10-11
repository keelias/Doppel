using BoneLib;
using Il2CppSLZ.Marrow;
using Il2CppSLZ.Marrow.Interaction;
using Il2CppSLZ.Marrow.Pool;
using Il2CppSLZ.Marrow.Warehouse;
using UnityEngine;

namespace ThirdEyeBonelabMod.Bots
{
    /// <summary>
    /// A bot finding things out for itself (see SelfTaught for what it keeps).
    /// <para>Trying things: it picks up something it does not know well (one lying about, or
    /// its own copy of something the player has had in their hands), and tries what can be done
    /// with it, one thing at a time: the trigger, each button, held or tapped, pointing ahead
    /// or at the ground, a shake, a swing. Before and after each it looks at what there is
    /// about it, and what changed is what that did: things appearing, things knocked about,
    /// the thing itself changing or sounding, a portal opening, itself carried off. What does
    /// something, it does again another time; what it has just found out, the others come and
    /// try.</para>
    /// <para>Throwing: each throw is at something (a thing lying about, a spot on the ground),
    /// thrown its usual way for that distance with a little chance in it, and watched to see
    /// how near it came; a throw that came nearer than usual is the way it throws from then on.</para>
    /// </summary>
    public partial class Bot
    {
        // Spawns lately (anyone's but the bots' own), for seeing what a try did.
        private static readonly List<(Vector3 at, float t)> recentSpawns = new List<(Vector3, float)>();

        public static void NoteSpawn(Poolee poolee)
        {
            if (poolee == null || Time.time < Clone.SuppressUntil)
                return;
            recentSpawns.Add((poolee.transform.position, Time.time));
            while (recentSpawns.Count > 0 && Time.time - recentSpawns[0].t > 6f)
                recentSpawns.RemoveAt(0);
        }

        private bool tinkering;
        private string tinkerCode = "", tinkerName = "";
        private int trialN, trialsWanted;
        private string trialAction = "", lastTrialAction = "";
        private int trialPhase;
        private float trialAt;
        private readonly List<(Rigidbody rb, Vector3 at)> trialBodies = new List<(Rigidbody, Vector3)>();
        private int trialParts;
        private bool trialSound;
        private Vector3 trialHead;
        private int trialPortals;
        private float trialBegan;
        // Buttons of its right hand pressed this frame (while it tries a thing).
        private bool pressA, pressB, pressMenu, pressClick;
        // Something to try as soon as it is free (news of what another found out).
        private string tinkerWant;
        private float newsSeen;
        private string pendingCode;
        private Vector3 pendingAt;
        private float pendingUntil;

        // Its right hand's buttons, as it presses them (put on its controller with its trigger).
        private void PutPresses(BaseController c)
        {
            bool was = c._aButton;
            c._aButton = pressA;
            c._aButtonDown = pressA && !was;
            c._aButtonUp = !pressA && was;
            was = c._bButton;
            c._bButton = pressB;
            c._bButtonDown = pressB && !was;
            c._bButtonUp = !pressB && was;
            was = c._thumbstick;
            c._thumbstick = pressClick;
            c._thumbstickDown = pressClick && !was;
            c._thumbstickUp = !pressClick && was;
            c._menuTap = pressMenu && !menuWas;
            menuWas = pressMenu;
        }

        private bool menuWas;

        // The thing's barcode and name, if it is one to try (not a gun, a body, a magazine).
        private static bool Tryable(Rigidbody rb, out string code, out string name)
        {
            code = name = "";
            Poolee poolee = rb.GetComponentInParent<Poolee>();
            if (poolee == null || poolee.SpawnableCrate == null)
                return false;
            code = poolee.SpawnableCrate.Barcode.ID;
            name = Lessons.NameOf(poolee);
            string upper = poolee.gameObject.name.ToUpperInvariant();
            if (Clone.IsBody(code, poolee.gameObject.name) || upper.Contains("SPAWN") || upper.Contains("NIMBUS"))
                return false;
            if (poolee.GetComponentInChildren<Gun>() != null || poolee.GetComponentInChildren<Magazine>() != null || poolee.GetComponentInChildren<Il2CppSLZ.Marrow.AI.AIBrain>() != null)
                return false;
            return true;
        }

        /// <summary>Goes for something to try: one about (the one asked for, or the least known), or its own copy of something the player has had.</summary>
        private bool BeginTinker(Vector3 feet)
        {
            string wanted = string.IsNullOrEmpty(tinkerWant) ? null : tinkerWant;
            tinkerWant = null;
            pendingCode = null;
            if (FindTinker(feet, wanted, wanted != null ? Tuning.F("point.thing.far", 40f) : BotManager.OwnSpawns ? 10f : Tuning.F("tinker.seek", 25f)))
                return true;
            // (Nothing brought into being for it: only what is on the map.)
            if (!BotManager.OwnSpawns)
                return false;
            // None about: one of its own, of something the player has had in their hands.
            Lessons.TellSeen();
            string code = wanted;
            if (code == null)
            {
                List<string> choices = new List<string>();
                List<float> weights = new List<float>();
                float total = 0f;
                foreach (string seen in SelfTaught.Data.seen.Keys)
                {
                    if (SelfTaught.Dull(seen) || SelfTaught.IsNever(seen) || !Spawnable(seen))
                        continue;
                    float weight = 1.2f - SelfTaught.Known(seen) + (SelfTaught.Best(seen, out _, out _) >= 0.35f ? 0.6f : 0f);
                    choices.Add(seen);
                    weights.Add(weight);
                    total += weight;
                }
                if (choices.Count == 0)
                    return false;
                float roll = UnityEngine.Random.value * total;
                code = choices[choices.Count - 1];
                for (int i = 0; i < choices.Count; i++)
                {
                    roll -= weights[i];
                    if (roll <= 0f)
                    {
                        code = choices[i];
                        break;
                    }
                }
            }
            try
            {
                AssetWarehouse warehouse = AssetWarehouse.Instance;
                Transform head = Head;
                if (warehouse == null || head == null || !warehouse.HasCrate(new Barcode(code)))
                    return false;
                Vector3 at = head.position + Quaternion.Euler(0f, bodyYaw, 0f) * new Vector3(0f, -0.5f, 1f);
                Clone.Suppress();
                HelperMethods.SpawnCrate(code, at, Quaternion.identity, Vector3.one, false, null, null);
                pendingCode = code;
                pendingAt = at;
                pendingUntil = Time.time + 4f;
                Lab.Event("curious: spawned a " + code + " to try");
                return true;
            }
            catch (Exception e)
            {
                Lab.Event("curious: could not spawn " + code + ": " + e.Message);
                return false;
            }
        }

        // A thing of the game's that is there to be spawned: in its lists, and not one kept out of the spawn menu.
        private static bool Spawnable(string code)
        {
            try
            {
                AssetWarehouse warehouse = AssetWarehouse.Instance;
                if (warehouse == null || !warehouse.TryGetCrate(new Barcode(code), out Crate crate) || crate == null)
                    return false;
                return !crate.Redacted && code.IndexOf("FusionContent", StringComparison.OrdinalIgnoreCase) < 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // The thing it spawned to try, once it is there. True while it waits (or has begun on it).
        private bool TinkerPending()
        {
            if (pendingCode == null)
                return false;
            if (FindTinker(pendingAt - Vector3.up * 0.5f, pendingCode, 3.5f))
            {
                Poolee mine = item != null ? item.GetComponentInParent<Poolee>() : null;
                Lessons.Litter(mine);
                pendingCode = null;
                return true;
            }
            if (Time.time > pendingUntil)
            {
                Lab.Event("curious: the " + pendingCode + " it spawned is not something it can pick up and try: cleared away, and not spawned again");
                SelfTaught.Never(pendingCode);
                // (Not left lying there.)
                try
                {
                    HashSet<IntPtr> gone = new HashSet<IntPtr>();
                    foreach (Collider c in Physics.OverlapSphere(pendingAt, 2.5f, ~0, QueryTriggerInteraction.Collide))
                    {
                        Il2CppSLZ.Marrow.Pool.Poolee p = c != null ? c.GetComponentInParent<Il2CppSLZ.Marrow.Pool.Poolee>() : null;
                        if (p != null && gone.Add(p.Pointer) && p.SpawnableCrate != null && p.SpawnableCrate.Barcode.ID == pendingCode)
                            p.Despawn();
                    }
                }
                catch (Exception)
                {
                }
                pendingCode = null;
                return false;
            }
            return true;
        }

        private bool FindTinker(Vector3 feet, string wanted, float within)
        {
            Rigidbody best = null;
            Grip bestGrip = null;
            string bestCode = "", bestName = "";
            float bestScore = float.MaxValue;
            HashSet<IntPtr> seen = new HashSet<IntPtr>();
            foreach (Collider c in Physics.OverlapSphere(feet + Vector3.up * 0.5f, within, ~0, QueryTriggerInteraction.Ignore))
            {
                Rigidbody rb = c != null ? c.attachedRigidbody : null;
                if (rb == null || rb.isKinematic || !seen.Add(rb.Pointer))
                    continue;
                if (rb.mass < 0.05f || rb.mass > MaxMass || rb.GetComponentInParent<RigManager>() != null)
                    continue;
                if (!Tryable(rb, out string code, out string name) || (wanted != null ? code != wanted : SelfTaught.Dull(code)) || NotForBots(rb))
                    continue;
                Grip g = GripOf(rb);
                if (g == null)
                    continue;
                float up = rb.worldCenterOfMass.y - feet.y;
                if (up < -0.5f || up > 1.8f)
                    continue;
                // (The less it knows a thing, the further it will go for it.)
                float score = (rb.worldCenterOfMass - feet).magnitude + SelfTaught.Known(code) * 8f + UnityEngine.Random.value * 2f;
                if (score < bestScore)
                {
                    bestScore = score;
                    best = rb;
                    bestGrip = g;
                    bestCode = code;
                    bestName = name;
                }
            }
            if (best == null)
                return false;
            item = best;
            grip = bestGrip;
            gun = null;
            tinkering = true;
            tinkerCode = bestCode;
            tinkerName = bestName;
            play = Play.GoTo;
            playAt = Time.time;
            nav.SetGoal(item.worldCenterOfMass);
            Lab.Event("curious: going to try the " + bestName + " (known " + (SelfTaught.Known(bestCode) * 100f).ToString("0") + "%)");
            return true;
        }

        // In its hand: the tries begin.
        private void StartTrials()
        {
            SelfTaught.Of(tinkerCode, tinkerName).sessions++;
            trialN = 0;
            trialsWanted = Mathf.RoundToInt(Tuning.F("curious.tries", 5f));
            lastTrialAction = "";
            NextTrial();
            Next(Play.Tinker);
        }

        private void NextTrial()
        {
            trialAction = SelfTaught.Choose(tinkerCode, tinkerName, lastTrialAction);
            lastTrialAction = trialAction;
            trialPhase = 0;
            trialAt = Time.time;
        }

        // What there is about it, before a try.
        private void TrialBefore()
        {
            Transform head = Head;
            trialBodies.Clear();
            HashSet<IntPtr> seen = new HashSet<IntPtr>();
            Transform mine = item != null ? item.transform.root : null;
            foreach (Collider c in Physics.OverlapSphere(head.position, 12f, ~0, QueryTriggerInteraction.Ignore))
            {
                Rigidbody rb = c != null ? c.attachedRigidbody : null;
                if (rb == null || rb.isKinematic || !seen.Add(rb.Pointer) || rb.transform.root == mine || rb.GetComponentInParent<RigManager>() != null)
                    continue;
                // (Things lying still: one already on its way somewhere says nothing.)
                if (rb.velocity.magnitude > 0.3f)
                    continue;
                trialBodies.Add((rb, rb.worldCenterOfMass));
                if (trialBodies.Count >= 40)
                    break;
            }
            trialParts = Parts(out trialSound);
            trialHead = head.position;
            trialPortals = PortalWays.All.Count;
            trialBegan = Time.time;
        }

        private int Parts(out bool sound)
        {
            sound = false;
            if (item == null)
                return 0;
            Transform root = item.transform.root;
            foreach (AudioSource a in root.GetComponentsInChildren<AudioSource>())
                sound |= a != null && a.isPlaying;
            return root.GetComponentsInChildren<Transform>(false).Length;
        }

        // What changed since: how much (0 to 1), and what, in words.
        private float TrialAfter(out string what)
        {
            List<string> said = new List<string>();
            float effect = 0f;
            Transform head = Head;
            // (Things that came into being by the thing in its hand, since the try; and not if
            // things were appearing just before it too: someone else is making them.)
            int spawned = 0, before = 0;
            Vector3 thing = item != null ? item.worldCenterOfMass : head.position;
            foreach ((Vector3 at, float t) in recentSpawns)
            {
                if ((at - thing).magnitude > Tuning.F("curious.near", 3f))
                    continue;
                spawned += t >= trialBegan ? 1 : 0;
                before += t < trialBegan && t > trialBegan - 2f ? 1 : 0;
            }
            if (spawned > 0 && before == 0)
            {
                effect += 0.6f;
                said.Add("makes things appear");
            }
            int moved = 0;
            foreach ((Rigidbody rb, Vector3 at) in trialBodies)
                moved += rb != null && (rb.worldCenterOfMass - at).magnitude > 0.4f ? 1 : 0;
            if (moved > 0)
            {
                effect += 0.25f * Mathf.Min(moved, 3);
                said.Add("moves things about");
            }
            int parts = Parts(out bool sound);
            if (parts != trialParts || (sound && !trialSound))
            {
                effect += 0.4f;
                said.Add(parts != trialParts ? "it changes" : "it makes a sound");
            }
            if (PortalWays.All.Count != trialPortals)
            {
                effect += 0.8f;
                said.Add("opens a portal");
            }
            if ((head.position - trialHead).magnitude > 1.5f)
            {
                effect += 0.6f;
                said.Add("carries it off");
            }
            what = string.Join(", ", said);
            return Mathf.Clamp01(effect);
        }

        // Once a frame while it tries the thing in its hand. False once it is done.
        private bool TinkerStep(Transform you, Transform head, float dt)
        {
            walk = Vector2.zero;
            trigger = 0f;
            if (!Holding() || head == null)
                return EndPlay();
            float t = Time.time - trialAt;
            string kind = trialAction.Split('/')[0];
            bool down = trialAction.EndsWith("/down", StringComparison.Ordinal);
            float h = standingHeight;
            float m = h / 1.7f;
            Vector3 hold = new Vector3(0.2f * m, h - 0.38f * m, 0.42f * m);
            Quaternion aim = Quaternion.Euler(down ? 42f : 0f, 0f, 0f);
            handOverride = true;
            playHeight = h;
            playPitch = down ? 32f : 10f;
            handAt = hold;
            handTurn = aim;
            switch (trialPhase)
            {
                case 0:
                    // Looks it over (longer the first time), then brings it to bear.
                    float look = trialN == 0 ? 1.1f : 0.45f;
                    if (trialN == 0 && t < look - 0.3f)
                    {
                        handAt = new Vector3(0.16f * m, h - 0.36f * m, 0.34f * m);
                        handTurn = Quaternion.Euler(20f + Mathf.Sin(t * 5f) * 25f, -20f, Mathf.Sin(t * 4f) * 45f);
                        playPitch = 34f;
                    }
                    if (t >= look)
                    {
                        TrialBefore();
                        trialPhase = 1;
                        trialAt = Time.time;
                    }
                    return true;
                case 1:
                    float longs = kind == "hold trigger" ? 1.1f : kind == "shake" ? 0.9f : kind == "swing" ? 0.55f : 0.25f;
                    switch (kind)
                    {
                        case "trigger":
                        case "hold trigger":
                            trigger = 1f;
                            break;
                        case "A":
                            pressA = true;
                            break;
                        case "B":
                            pressB = true;
                            break;
                        case "menu":
                            pressMenu = true;
                            break;
                        case "click":
                            pressClick = true;
                            break;
                        case "shake":
                            handAt = hold + new Vector3(Mathf.Sin(t * 38f) * 0.07f, Mathf.Cos(t * 33f) * 0.07f, 0f) * m;
                            break;
                        case "swing":
                            float s = Mathf.Clamp01(t / longs);
                            handAt = Vector3.Lerp(new Vector3(0.3f * m, h - 0.1f * m, -0.15f * m), new Vector3(0.1f * m, h - 0.7f * m, 0.55f * m), s);
                            handTurn = Quaternion.Euler(Mathf.Lerp(-50f, 60f, s), 0f, 0f);
                            break;
                    }
                    if (t >= longs)
                    {
                        trialPhase = 2;
                        trialAt = Time.time;
                    }
                    return true;
                default:
                    // Watches what comes of it.
                    if (t < Tuning.F("curious.watch", 1.3f))
                        return true;
                    float effect = TrialAfter(out string what);
                    bool found = SelfTaught.Tried(tinkerCode, tinkerName, trialAction, effect, what, head.position);
                    Lab.Event("curious: " + tinkerName + ", " + trialAction + ": " + (effect > 0.05f ? what + " (" + effect.ToString("0.00") + ")" : "nothing"));
                    // (Its own news is not news to it.)
                    newsSeen = Mathf.Max(newsSeen, SelfTaught.NewsTime);
                    if (found)
                        Spawnables.Notify("A bot worked out the " + tinkerName + " by itself: " + Said(trialAction) + " " + what);
                    trialN++;
                    if (trialN >= trialsWanted)
                    {
                        // (And then the player's own way with it, if they showed one: both hands as
                        // theirs were, where theirs were. Its own tries are one hand, held out in front.)
                        if (TheirWayWith())
                            return true;
                        then = Then.Drop;
                        Next(Play.Drop);
                        return true;
                    }
                    NextTrial();
                    return true;
            }
        }

        // The thing held still where it is let go of, till the lesson's hand has taken it (or a few seconds are up).
        private Rigidbody heldForLesson;
        private bool heldForLessonWasKinematic;
        private float heldForLessonUntil;
        private Mode pointBackTo = Mode.Stand;
        private float pointBackAt = -1f;

        // Does with the thing in its hand what the player was recorded doing with one. False: nothing was shown.
        private bool TheirWayWith()
        {
            try
            {
                Lessons.Lesson shown = Lessons.With(tinkerCode);
                Hand hand = RightHand;
                GameObject held = hand != null ? hand.m_CurrentAttachedGO : null;
                Poolee thing = held != null ? held.GetComponentInParent<Poolee>() : null;
                if (shown == null || thing == null || enemy)
                    return false;
                Rigidbody body = thing.GetComponentInChildren<Rigidbody>();
                string name = tinkerName;
                // (The lesson takes its hands for its own: the thing is let go of, and stays where it
                // is, in the air, for the lesson's hand to take again as the player's did.)
                if (!UseLesson(shown, false))
                    return false;
                foreach (Clone.Event e in shown.take.events)
                {
                    if (e.barcode == tinkerCode)
                        lessonThings[e.barcode + "#" + Mathf.Max(0, Clone.ThingNumber(e))] = thing;
                }
                if (body != null)
                {
                    heldForLesson = body;
                    heldForLessonWasKinematic = body.isKinematic;
                    heldForLessonUntil = Time.time + Tuning.F("curious.theirway.hold", 4f);
                    body.velocity = Vector3.zero;
                    body.angularVelocity = Vector3.zero;
                    body.isKinematic = true;
                }
                Lab.Event("curious: " + name + ", now as you did with it (" + shown.take.frames[shown.take.frames.Count - 1].t.ToString("0") + " s)");
                return true;
            }
            catch (Exception e)
            {
                Lab.Event("curious: could not do it your way: " + e.Message);
                return false;
            }
        }

        // Once a frame: the thing held still for a lesson let go again (taken, or time up); and back to following, after something pointed at.
        private void TheirWayStep()
        {
            try
            {
                if (heldForLesson != null)
                {
                    Poolee whole = heldForLesson.GetComponentInParent<Poolee>();
                    bool taken = whole != null && HeldByAnyone(whole.transform, null);
                    if (taken || Time.time > heldForLessonUntil || !InLesson)
                    {
                        heldForLesson.isKinematic = heldForLessonWasKinematic;
                        heldForLesson = null;
                    }
                }
            }
            catch (Exception)
            {
                heldForLesson = null;
            }
            if (pointBackAt > 0f && Time.time > pointBackAt && !Playing && !InLesson && act != Act.Tinker && mode == Mode.Wander)
            {
                pointBackAt = -1f;
                SetMode(pointBackTo);
            }
        }

        /// <summary>The player pointed at a thing: it goes and takes it up and tries it, now.</summary>
        public void TryPointedAt(string code, string name)
        {
            if (mode != Mode.Wander && mode != Mode.Brain)
            {
                pointBackTo = mode;
                pointBackAt = Time.time + 6f;
                SetMode(Mode.Wander);
            }
            StopPlaying();
            tinkerWant = code;
            actUntil = 0f;
            lessonDueAt = Mathf.Max(lessonDueAt, Time.time + 60f);
            Lab.Event("curious: you pointed at the " + name + ", going to try it");
        }

        private static string Said(string action)
        {
            string kind = action.Split('/')[0];
            string how = kind == "trigger" ? "pulling the trigger" : kind == "hold trigger" ? "holding the trigger" : kind == "shake" ? "shaking it" : kind == "swing" ? "swinging it"
                : kind == "menu" ? "the menu button" : kind == "click" ? "clicking the stick" : "the " + kind + " button";
            return how + (action.EndsWith("/down", StringComparison.Ordinal) ? " (pointed at the ground)" : "");
        }

        // What another bot has just found out: a look that way, and it wants to try the thing too.
        private void HearNews()
        {
            if (SelfTaught.NewsTime <= newsSeen || enemy)
                return;
            newsSeen = SelfTaught.NewsTime;
            Transform head = Head;
            if (head == null || (SelfTaught.NewsAt - head.position).magnitude > Tuning.F("curious.news", 40f))
                return;
            Look(SelfTaught.NewsAt, null, "what it found", Random(1.5f, 3f));
            tinkerWant = SelfTaught.NewsItem;
            if ((mode == Mode.Wander || mode == Mode.Brain) && !Playing && !climbing)
                actUntil = Mathf.Min(actUntil, Time.time + Random(3f, 10f));
            Lab.Event("curious: heard about the " + SelfTaught.NewsName + ", wants to try it");
        }

        /// <summary>For the lab: goes and tries something now (the thing named by its barcode, or whatever it knows least).</summary>
        public void LabCurious(string wanted)
        {
            if (mode != Mode.Wander && mode != Mode.Brain)
                SetMode(Mode.Wander);
            StopPlaying();
            tinkerWant = wanted;
            actUntil = 0f;
            if (tinkerWant == null)
                tinkerWant = "";
        }

        // ---------------------------------------------------------------- throwing at something

        private float throwFar;
        private Vector3 throwAt;
        private Rigidbody flying;
        private Vector3 flyTarget;
        private float flySpeed, flyLoft, flyFar, flySince, flyNearest;

        // Something to throw at: a thing lying about in front of it, else a spot on the ground. The way to it.
        private Vector3 PickThrowTarget(Transform head)
        {
            Vector3 eye = head.position;
            Vector3 facing = Quaternion.Euler(0f, bodyYaw, 0f) * Vector3.forward;
            Vector3 best = Vector3.zero;
            float bestScore = float.MaxValue;
            foreach (Collider c in Physics.OverlapSphere(eye + facing * 9f, 9f, ~0, QueryTriggerInteraction.Ignore))
            {
                Rigidbody rb = c != null ? c.attachedRigidbody : null;
                if (rb == null || rb == item || rb.isKinematic || rb.GetComponentInParent<RigManager>() != null)
                    continue;
                Vector3 p = rb.worldCenterOfMass;
                float far = Flat(p - eye).magnitude;
                if (far < 4f || far > 16f || Targets.NearPlayer(p, 2.5f) || Vector3.Angle(facing, Flat(p - eye)) > 75f)
                    continue;
                float score = UnityEngine.Random.value;
                bool clear = !Physics.Linecast(eye, p, out RaycastHit hit, ~0, QueryTriggerInteraction.Ignore) || hit.rigidbody == rb;
                if (clear && score < bestScore)
                {
                    bestScore = score;
                    best = p;
                }
            }
            if (bestScore == float.MaxValue)
            {
                Vector3 way = Quaternion.Euler(0f, bodyYaw + Random(-60f, 60f), 0f) * Vector3.forward;
                Vector3 p = eye + way * Random(5f, 13f);
                best = Physics.Raycast(p + Vector3.up * 2f, Vector3.down, out RaycastHit ground, 8f, ~0, QueryTriggerInteraction.Ignore) ? ground.point : p - Vector3.up * 1.5f;
            }
            throwAt = best;
            throwFar = Flat(best - eye).magnitude;
            return Flat(best - eye).normalized;
        }

        // The throw's speed: its way of throwing that far, tried this time a little differently.
        private Vector3 ThrowVelocity(Rigidbody thing)
        {
            SelfTaught.ThrowTry(throwFar, out float speed, out float loft);
            flying = thing;
            flyTarget = throwAt;
            flySpeed = speed;
            flyLoft = loft;
            flyFar = throwFar;
            flySince = Time.time;
            flyNearest = float.MaxValue;
            throwFar = 0f;
            float rad = loft * Mathf.Deg2Rad;
            Look(flyTarget, null, "its throw", 2.5f);
            return throwDir * (Mathf.Cos(rad) * speed) + Vector3.up * (Mathf.Sin(rad) * speed);
        }

        // Once a frame: its throw in the air, and how near it came.
        private void TrackThrow()
        {
            if (flying == null)
            {
                flySince = 0f;
                return;
            }
            float since = Time.time - flySince;
            flyNearest = Mathf.Min(flyNearest, (flying.worldCenterOfMass - flyTarget).magnitude);
            bool landed = since > 0.5f && flying.velocity.magnitude < 0.6f;
            if (!landed && since < 4f)
                return;
            string how = SelfTaught.ThrowResult(flyFar, flySpeed, flyLoft, flyNearest);
            Lab.Event("throw: at " + flyFar.ToString("0") + " m, " + flySpeed.ToString("0.0") + " m/s at " + flyLoft.ToString("0") + " deg: " + how);
            flying = null;
        }
    }
}
