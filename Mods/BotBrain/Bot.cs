using BoneLib;
using Il2CppSLZ.Marrow;
using UnityEngine;

namespace ThirdEyeBonelabMod.Bots
{
    /// <summary>
    /// One bot: a real player body (see BotRig) and what drives it. Its brain decides where its
    /// head and hands go, how its fingers curl and which way it walks; the game's own body
    /// does the rest (balance, legs, hands reaching and holding), as it does for the player.
    /// </summary>
    public partial class Bot
    {
        public enum Mode
        {
            /// <summary>Stands about and watches the player.</summary>
            Stand,
            /// <summary>Walks after the player (finding its way round things) and stops near them.</summary>
            Follow,
            /// <summary>Walks about the level on its own, stopping now and then to look around.</summary>
            Wander,
            /// <summary>Gets a gun and shoots at zombies, NPCs, or (for practice) things lying about.</summary>
            Gunplay,
            /// <summary>Plays on its own: its brain decides what to do, and learns from how it goes.</summary>
            Brain,
            /// <summary>Fists only: gun down, after the nearest enemy, punching (as the player punches).</summary>
            Fists,
            /// <summary>Doing again exactly what the player did in a clone recording (see Clone).</summary>
            Clone,
            /// <summary>Copies the player's head and hands, live, facing them.</summary>
            Copy,
            /// <summary>Plays back a recording of the player.</summary>
            Replay,
        }

        public readonly RigManager rig;

        /// <summary>What makes it not every other bot (rolled when it was spawned).</summary>
        public readonly Personality personality;
        private readonly OpenControllerRig controls;

        public Mode mode = Mode.Stand;

        // What the brain wants, in the body's own frame (see Frame); put on the body each frame
        // by Drive, just before the game works the body out from it.
        private Frame pose;
        private float bodyYaw;
        private Vector2 walk;
        // The stick as it is pushed (eased towards what the brain wants, as a thumb moves it).
        private Vector2 stick;
        private bool jump, otherA;
        // Its sticks clicked in (copied, or to run, if the player clicks to run).
        private bool primaryClick, secondaryClick;
        private bool running;
        // When it last started running (it keeps on a while once it has).
        private float runningSince;
        // Copying the player's crouch (their legs' bend), or leaving the body to stand by itself.
        private bool crouching;
        private float crouch, feet;
        // A jump it is to make: if its own buttons have not made it jump by then, it is made to.
        private float jumpDue = -1f;
        private int jumpsCleared;
        private float jumpClearSaidAt = -10f;
        private float jumpAsked;
        private bool wasJumping;
        private float jumpedAt = -10f;
        private float curl = 0.25f;

        private List<Frame> replay;
        private int replayAt;
        private int lastJumpFrame = -1;
        private float replayStart;
        private float replayYaw;

        // Finding its way about.
        private readonly Navigator nav = new Navigator();
        private float idleUntil;
        private float ledgeJumpAt = -10f;
        private float stride;
        private float headYaw;
        private bool following;

        // The player's own moves (learned from their recordings) playing on its head and hands.
        private Frame[] clip, fromClip;
        private float clipT, fromT, clipLeft, fade = 1f;
        // Each stretch is played once, at its own pace, either way round, and not again soon.
        private bool clipMirror, fromMirror;
        private float clipRate = 1f, fromRate = 1f;
        private readonly List<Frame[]> lately = new List<Frame[]>();
        private MotionLibrary.Gait gait;
        private MotionLibrary.Gait wantGait;
        private float wantSince;

        private readonly float born;
        private bool heard;
        // How high its head is when it stands: its own avatar's eye height. (It was the
        // player's height at the moment the bot was made, or a new build took it over: with
        // the player sat down or crouched just then, it did everything with its hands at a squat.)
        private float standingHeight
        {
            get
            {
                float eyes = StandingEyes();
                return eyes > 0.3f ? eyes * 0.985f : metHeight;
            }
        }

        private float metHeight = 1.65f;

        /// <summary>An enemy bot: the player is what it fights (BotManager's "Spawn enemy bot").</summary>
        public bool enemy;

        /// <summary>A child of an enemy bot's rig that says so (to a new build too).</summary>
        public const string EnemyMark = "BotEnemyMark";

        public Bot(RigManager rig, float yaw)
        {
            this.rig = rig;
            enemy = rig.transform.Find(EnemyMark) != null;
            personality = Personality.Of(rig);
            controls = rig.ControllerRig.TryCast<OpenControllerRig>();
            bodyYaw = yaw;
            born = Time.time;
            if (Recorder.Capture(out Frame you))
                metHeight = Mathf.Clamp(you.height, 1.2f, 2.1f);
            MotionLibrary.Refresh();
            pose = Standing(0f, 0f, 0f, 0f);
        }

        public bool Alive => rig != null && controls != null && controls.vrRoot != null;

        public Transform Head => rig.physicsRig != null ? rig.physicsRig.m_head : null;

        // Where its feet are: under its head, by its standing height.
        private Vector3 Feet => Head != null ? Head.position - Vector3.up * pose.height : rig.transform.position;

        /// <summary>The way the stick is pushed (forward is the way its head faces).</summary>
        public Vector2 Walk => stick.sqrMagnitude < 0.0001f ? Vector2.zero : gameStick;

        // Its stick as the game is to read it. The game walks a body the way its HEAD faces with
        // the stick forward, not the way its body faces: with its stick worked out for its body,
        // every glance to one side as it walked took it off that way, and back again after: it
        // went along in a zigzag. So its stick is turned by as much as its head is turned from its body.
        private Vector2 gameStick;

        private Vector2 ForItsHead(Vector2 forBody)
        {
            try
            {
                Transform head = Head;
                if (head == null || forBody.sqrMagnitude < 0.0001f || Tuning.F("move.byhead", 1f) < 0.5f)
                    return forBody;
                Vector3 ahead = Flat(head.forward);
                // (Looking straight up or down: no telling which way it faces; as it was.)
                if (ahead.sqrMagnitude < 0.04f)
                    return forBody;
                float off = Mathf.DeltaAngle(Recorder.Yaw(ahead), bodyYaw) * Mathf.Deg2Rad;
                float c = Mathf.Cos(off), s = Mathf.Sin(off);
                return new Vector2(forBody.x * c + forBody.y * s, -forBody.x * s + forBody.y * c);
            }
            catch (Exception)
            {
                return forBody;
            }
        }


        private float seatOutAt = -10f;
        private int driveCalls, driveStops, cloneDrives;
        private bool gameSprints;

        /// <summary>The frame its head, hands and controls were last put on its body.</summary>
        public int LastDriveFrame;

        private static int named;
        private string nameIs;

        /// <summary>Its name in the lab's log: B1, B2... (E1, E2... for an enemy), in the order they were first seen.</summary>
        public string Name => nameIs ??= (enemy ? "E" : "B") + (++named);

        public void SetMode(Mode next)
        {
            // (Told to do something else: its weapon is put away, not dropped where it stands.)
            if (WeaponInHand && next != Mode.Clone && next != Mode.Copy && next != Mode.Replay)
                Stow();
            StopPlaying();
            if (mode == Mode.Clone && next != Mode.Clone)
                LeaveClone();
            MotionLibrary.Refresh();
            if (mode == Mode.Brain && next != Mode.Brain)
                SwitchGoal(Goal.Idle);
            HideThought();
            mode = next;
            walk = Vector2.zero;
            primaryClick = secondaryClick = running = false;
            jump = otherA = false;
            crouching = false;
            jumpDue = -1f;
            nav.Clear();
            following = false;
            idleUntil = 0f;
            if (next == Mode.Copy)
                jumpAsked = Recorder.PlayerJumpedAt;
            if (next == Mode.Replay && !StartReplay())
                mode = Mode.Stand;
        }

        private bool StartReplay()
        {
            // (A move being shown: that, not the last recording.)
            if (previewClip != null)
            {
                replay = new List<Frame>(previewClip);
                previewClip = null;
                replayAt = 0;
                lastJumpFrame = -1;
                replayStart = Time.time;
                replayYaw = bodyYaw - replay[0].yaw;
                return true;
            }
            string file = Recorder.Latest();
            if (file == null)
            {
                Spawnables.Notify("Nothing recorded yet (turn on Record my play)");
                return false;
            }
            try
            {
                replay = Recorder.Load(file);
            }
            catch (Exception e)
            {
                Spawnables.Notify("That recording could not be read: " + e.Message);
                return false;
            }
            if (replay.Count < 2)
            {
                Spawnables.Notify("That recording is too short");
                return false;
            }
            replayAt = 0;
            lastJumpFrame = -1;
            replayStart = Time.time;
            replayYaw = bodyYaw - replay[0].yaw;
            Spawnables.Notify("Replaying " + replay[replay.Count - 1].time.ToString("0") + " seconds of you");
            return true;
        }

        /// <summary>Once a frame: what the brain wants now.</summary>
        public void Update(float dt)
        {
            if (Dead())
                return;
            KeepAwake();
            StowFollow();
            // (Still sat in something with its drive or its lesson over: out of it.)
            try
            {
                if (mode != Mode.Clone && rig.activeSeat != null && Time.time - seatOutAt > 1.5f)
                {
                    seatOutAt = Time.time;
                    rig.activeSeat.EgressRig(false);
                }
            }
            catch (Exception)
            {
            }
            OnItsFeet();
            TheirWayStep();
            Flesh();
            Mend(dt);
            if (DownStep(dt))
                return;
            // Its hands hit as a player's do, whatever it is doing.
            HurtPlayer();
            if (!heard && Time.time - born > 2f)
            {
                heard = true;
                BotRig.HearLate(rig);
            }

            Transform head = Head;
            Transform you = Player.Head;
            float t = Time.time - born;
            if (spawningGun && Time.time - spawnAskedAt > 0.2f && !TakeSpawnedGun() && Time.time - spawnAskedAt > 4f)
            {
                spawningGun = false;
                tossNext = false;
            }
            jump = otherA = false;
            pressA = pressB = pressMenu = pressClick = false;
            primaryClick = secondaryClick = false;
            crouching = false;
            NoteJumping();
            TrackThrow();
            MeasureFreeHands();
            bool itsOwn = mode == Mode.Stand || mode == Mode.Follow || mode == Mode.Wander || mode == Mode.Gunplay || mode == Mode.Brain || mode == Mode.Fists;
            if (itsOwn)
            {
                Notice(you, head);
                Aware(you, head);
                HearNews();
            }
            // (Getting up onto an edge from where it hangs: that first, whatever it was doing.)
            bool overEdge = climbing && climbHang;
            if (overEdge)
                ClimbStep(dt);
            switch (overEdge ? (Mode)(-1) : mode)
            {
                case Mode.Stand:
                    Watch(you, head, dt, Time.time < waveUntil ? 200f : 60f);
                    walk = Vector2.zero;
                    if (Playing)
                        PlayStep(you, head, dt);
                    break;
                case Mode.Follow:
                    Follow(you, head, dt);
                    break;
                case Mode.Wander:
                    Wander(you, head, dt);
                    break;
                case Mode.Gunplay:
                    Gunplay(you, head, dt);
                    break;
                case Mode.Clone:
                    CloneUpdate(dt);
                    break;
                case Mode.Fists:
                    FistsOnly(you, head, dt);
                    break;
                case Mode.Brain:
                    BrainStep(you, head, dt);
                    break;
                case Mode.Copy:
                    Watch(you, head, dt, 150f);
                    if (Recorder.Capture(out Frame live))
                    {
                        pose = live;
                        Body(live);
                    }
                    if (Recorder.PlayerJumpedAt > jumpAsked)
                    {
                        jumpAsked = Recorder.PlayerJumpedAt;
                        AskJump();
                    }
                    walk = Vector2.zero;
                    curl = -1f;
                    break;
                case Mode.Replay:
                    Replay();
                    break;
            }
            // (Out of the way of the player's gun, following them or going about: not only in a fight.)
            if (!overEdge && (mode == Mode.Follow || mode == Mode.Wander))
                AsideForYou(dt);
            if (itsOwn)
            {
                curl = 0.25f;
                float speed = walk.magnitude;
                stride += dt * (2f + 5f * speed);
                bool moving = speed > 0.05f;
                primaryClick = SprintClick();
                Frame made = Standing(t, moving ? 8f : LookPitch(you, head), moving ? 0f : headYaw, moving ? speed : 0f);
                // (Sprinting: its sprinting moves; moving otherwise: its walking ones, however hard
                // it pushes the stick, as the player pushes it all the way and still only jogs.)
                MotionLibrary.Gait gaitNow = !moving ? MotionLibrary.Gait.Idle : running ? MotionLibrary.Gait.Run : MotionLibrary.Gait.Walk;
                if (Moves(gaitNow, dt, out Frame learned))
                {
                    // Standing about, it still looks where its brain looks, with a little of the
                    // player's own way of holding their head.
                    if (!moving)
                        learned.head = Quaternion.Euler(LookPitch(you, head), headYaw, 0f) * Quaternion.Slerp(Quaternion.identity, learned.head, 0.35f);
                    made = learned;
                }
                pose = made;
                CrouchOfTheirs();
                JumpOfTheirs();
                OverHands();
                Wave(t);
                Fidget(you, moving, dt);
                Gaze(you, moving, dt);
                React(dt);
                ClimbPose();
                Smooth(dt);
                if (!MaybePurpose(head))
                    MaybeLesson();
            }
        }

        // ---------------------------------------------------------------- sprinting

        private float walkMost;
        private float sprintClickAt = -10f;
        private float sprintSaidAt = -10f;

        /// <summary>
        /// The click of the stick that starts a sprint, as a thumb gives it: a short press, once
        /// the stick is pushed well forward (pressed before that, as it set off, the game took
        /// no notice, and a click held down is never a second one), and again if it is meant to
        /// be sprinting and is still only jogging.
        /// </summary>
        private bool SprintClick()
        {
            // Its legs' top speed: a sprinter's while it runs (the click alone does not make a
            // bot's body sprint: its top speed stayed a jog's), its own again when it does not.
            RemapRig its = rig.remapHeptaRig;
            if (its != null)
            {
                if (walkMost <= 0f)
                {
                    walkMost = its.maxVelocity;
                    // (Taken over from an older build while it ran: its top speed was left a
                    // sprinter's, and was taken for its walk, and doubled again.)
                    while (walkMost > Tuning.F("walk.most", 2.6f))
                        walkMost /= Tuning.F("sprint.times", 2.1f);
                }
                // (Where the game does take its sprint click, its legs go at twice their top speed by
                // themselves: its top speed is left alone then, for as long as that run lasts. Raised as
                // well, it ran at eight metres a second.)
                float sprint = walkMost * Tuning.F("sprint.times", 2.1f);
                if (!running)
                    gameSprints = false;
                else if (its._currentVelocity.magnitude > sprint * 1.12f)
                    gameSprints = true;
                float most = running && !gameSprints ? sprint : walkMost;
                its.maxVelocity = Mathf.MoveTowards(its.maxVelocity, most, Time.deltaTime * (gameSprints ? 30f : 6f));
            }
            // (And told they are walking again, once it is: the game does not take them out of a run it did not put them in.)
            try
            {
                Rigidbody hipsNow = Pelvis();
                if (its != null && its.travState == RemapRig.TraversalState.Run && (!running || hipsNow == null || Flat(hipsNow.velocity).magnitude < Tuning.F("sprint.gait", 2.6f) - 0.6f))
                    its.travState = RemapRig.TraversalState.Walk;
            }
            catch (Exception)
            {
            }
            if (!running || stick.magnitude < 0.85f || stick.y < 0.6f)
                return false;
            Rigidbody hips = Pelvis();
            float going = hips != null ? Flat(hips.velocity).magnitude : 0f;
            if (going < Tuning.F("sprint.speed", 2.9f) && Time.time - sprintClickAt > Tuning.F("sprint.again", 0.7f))
                sprintClickAt = Time.time;
            if (Time.time - sprintSaidAt > 3f)
            {
                sprintSaidAt = Time.time;
                RemapRig legs = rig.remapHeptaRig;
                Lab.Event("run: going " + going.ToString("0.0") + " m/s, stick " + stick.magnitude.ToString("0.00") + " (" + stick.x.ToString("0.0") + ", " + stick.y.ToString("0.0") + ")"
                    + (legs != null ? ", " + legs.travState + ", most " + legs.currentMaxVelocity.ToString("0.0") + " of " + legs.maxVelocity.ToString("0.0") + ", its own " + legs._currentVelocity.magnitude.ToString("0.0") : ""));
            }
            // (Its legs told they are running, while it goes at a run: left at "walk", as the game
            // leaves a bot's, they took a walk's steps at a sprint's speed.)
            try
            {
                if (going > Tuning.F("sprint.gait", 2.6f) && its != null && its.travState == RemapRig.TraversalState.Walk || (its != null && its.travState == RemapRig.TraversalState.Jog && going > Tuning.F("sprint.gait", 2.6f)))
                    its.travState = RemapRig.TraversalState.Run;
            }
            catch (Exception)
            {
            }
            // (The click as well: with it the game runs its legs as a sprint is run. Without it they
            // went at a sprint's speed with a walk's steps, and it looked to float along.)
            return Time.time - sprintClickAt < 0.15f;
        }

        /// <summary>For the lab: a body's legs, as the game has them (how fast, what they are doing, its jump).</summary>
        public static string LegsOf(RigManager who)
        {
            try
            {
                RemapRig legs = who != null ? who.remapHeptaRig : null;
                if (legs == null)
                    return "none";
                OpenControllerRig c = who.ControllerRig.TryCast<OpenControllerRig>();
                return legs.travState + ", top speed " + legs.maxVelocity.ToString("0.00") + " (now " + legs.currentMaxVelocity.ToString("0.00") + "), accel " + legs.maxAcceleration.ToString("0.0")
                    + ", jump " + legs.jumpVelocity.ToString("0.00") + ", jumping " + legs._jumping + ", crouch " + legs._crouchTarget.ToString("0.00") + ", feet " + legs._feetOffset.ToString("0.00")
                    + ", in weight " + legs.inWeight.ToString("0.00") + ", enabled " + legs.enabled + ", ball loco " + (who.physicsRig != null && who.physicsRig.ballLocoEnabled)
                    + (c != null ? ", A " + c._primaryAButton + "/" + c._secondaryAButton + ", stick click " + c._primaryStick + "/" + c._secondaryStick : "")
                    + BodiesOf(who);
            }
            catch (Exception e)
            {
                return "failed: " + e.Message;
            }
        }

        // For the lab: the body's own parts, as the physics has them (any held still, asleep, or switched off).
        private static string BodiesOf(RigManager who)
        {
            try
            {
                int all = 0, kinematic = 0, asleep = 0, off = 0, joints = 0;
                string which = "";
                foreach (Rigidbody b in who.physicsRig.GetComponentsInChildren<Rigidbody>(true))
                {
                    all++;
                    bool k = b.isKinematic, o = !b.gameObject.activeInHierarchy;
                    if (k)
                        kinematic++;
                    if (o)
                        off++;
                    if (!k && !o && b.IsSleeping())
                        asleep++;
                    if ((k || o) && which.Length < 160)
                        which += " " + b.name + (o ? "(off)" : "");
                    foreach (Joint j in b.GetComponents<Joint>())
                    {
                        if (j.connectedBody == null || j.connectedBody.GetComponentInParent<RigManager>() != who)
                        {
                            joints++;
                            if (which.Length < 220)
                                which += " " + b.name + "->" + (j.connectedBody != null ? j.connectedBody.name : "world");
                        }
                    }
                }
                return ", seat " + (who.activeSeat != null ? who.activeSeat.name : "none") + ", bodies " + all + " (" + kinematic + " held still, " + asleep + " asleep, " + off + " off, " + joints + " tied to something else:" + which + ")"
                    + ", physics rig on " + (who.physicsRig.enabled && who.physicsRig.gameObject.activeInHierarchy) + PartsOf(who);
            }
            catch (Exception e)
            {
                return ", bodies failed: " + e.Message;
            }
        }

        private float seatedLooseSince = -1f;
        private int seatedTries;

        /// <summary>
        /// Out of a seat, its body is to be a walking one again. Its seat gone from under it
        /// (the kart taken away, or it got out as the kart was), the game left its body "sat":
        /// with no seat, its legs not worked at all. It stood where it was for good, pushing
        /// its stick. Put back on its feet here, a step harder each time it has not taken.
        /// </summary>
        private void OnItsFeet()
        {
            try
            {
                // (Not while it is getting into something: sitting down, it is "sat" a moment before its seat is its own.)
                if (rig.activeSeat != null || rig.bodyState != RigManager.BodyState.Seated || InLesson)
                {
                    seatedLooseSince = -1f;
                    seatedTries = 0;
                    return;
                }
                if (seatedLooseSince < 0f)
                    seatedLooseSince = Time.time;
                if (Time.time - seatedLooseSince < (seatedTries == 0 ? 2.5f : 0.8f))
                    return;
                seatedLooseSince = Time.time;
                seatedTries++;
                string how;
                if (seatedTries == 1)
                {
                    rig.SwitchBodyState(RigManager.BodyState.OnFoot);
                    how = "asked for";
                }
                else if (seatedTries == 2)
                {
                    rig.bodyState = RigManager.BodyState.OnFoot;
                    how = "set";
                }
                else
                {
                    rig.physicsRig.SetBodyState(RigManager.BodyState.OnFoot, RigManager.BodyState.Seated);
                    rig._bodyState = RigManager.BodyState.OnFoot;
                    rig._bodyStateOnDeck = RigManager.BodyState.OnFoot;
                    rig._bodyStateDirty = false;
                    if (!rig.physicsRig.ballLocoEnabled)
                        rig.physicsRig.EnableBallLoco();
                    how = "forced";
                }
                if (seatedTries <= 4 || seatedTries % 20 == 0)
                    Lab.Event("seat: its body was still a sat one with no seat under it, back on its feet (" + how + ", try " + seatedTries + ")");
            }
            catch (Exception e)
            {
                if (seatedTries <= 4)
                    Lab.Event("seat: could not put it back on its feet: " + e.Message);
            }
        }

        private static string PartsOf(RigManager who)
        {
            try
            {
                PhysicsRig p = who.physicsRig;
                Vector3 root = who.transform.position;
                string said = ", rig root " + root.ToString("0.00");
                Rigidbody hips = p.m_pelvis != null ? p.m_pelvis.GetComponent<Rigidbody>() : null;
                if (hips != null)
                    said += ", hips at " + hips.position.ToString("0.00") + " going " + hips.velocity.ToString("0.00") + " drag " + hips.drag.ToString("0.0") + " constraints " + hips.constraints;
                foreach (Rigidbody b in p.GetComponentsInChildren<Rigidbody>(true))
                {
                    string n = b.name.ToLowerInvariant();
                    if (!b.isKinematic && (n.Contains("knee") || n.Contains("feet") || n.Contains("foot") || n.Contains("loco")))
                        said += ", " + b.name + " y " + b.position.y.ToString("0.00") + " going " + b.velocity.magnitude.ToString("0.00") + " spin " + b.angularVelocity.magnitude.ToString("0.0") + (b.detectCollisions ? "" : " NO COLLISIONS") + " layer " + b.gameObject.layer;
                }
                foreach (Rig r in who.GetComponentsInChildren<Rig>(true))
                    said += ", " + r.GetIl2CppType().Name + (r.enabled ? "" : " OFF") + (r.gameObject.activeInHierarchy ? "" : " (object off)");
                said += ", manager " + (who.enabled ? "on" : "OFF") + ", body state " + who.bodyState + " (next " + who._bodyStateOnDeck + (who._bodyStateDirty ? ", due" : "") + ")" + (who._leashed ? ", LEASHED" : "") + ", phys input " + (who.GetPhysInputRig() != null ? who.GetPhysInputRig().GetIl2CppType().Name : "none");
                return said;
            }
            catch (Exception e)
            {
                return ", parts failed: " + e.Message;
            }
        }

        /// <summary>For the lab: sprints off somewhere now.</summary>
        public void LabSprint()
        {
            if (mode != Mode.Wander)
                SetMode(Mode.Wander);
            StopPlaying();
            Vector3 feet = Feet;
            if (Begin(Act.Sprint, Player.Head, feet))
                Lab.Event("run: sent sprinting " + Flat(nav.Goal - feet).magnitude.ToString("0") + " m");
        }

        // ---------------------------------------------------------------- waving

        private float waveUntil;
        private float waveStart;

        /// <summary>Waves at the player, so many times (a wave: the hand up by its head, side to side).</summary>
        public void LabWave(int times)
        {
            StopPlaying();
            if (mode != Mode.Stand)
                SetMode(Mode.Stand);
            waveStart = Time.time;
            waveUntil = Time.time + 0.35f + times * 0.7f;
            waveLeft = false;
            WaveOfTheirs();
        }

        private void Wave(float t)
        {
            if (Time.time >= waveUntil)
                return;
            if (waveClip != null)
            {
                OverClip(waveClip, Time.time - waveStart, 1f);
                return;
            }
            float left = waveUntil - Time.time;
            // Up, side to side once each 0.7 s, and down at the end.
            float up = Mathf.Clamp01(Mathf.Min((Time.time - waveStart) * 4f, left * 4f));
            float swing = Mathf.Sin(t * Mathf.PI * 2f / 0.7f);
            float size = pose.height / 1.7f;
            Vector3 raised = new Vector3((0.32f + swing * 0.12f) * size, pose.height + 0.08f * size, 0.12f * size);
            Quaternion turn = Quaternion.Euler(-75f, 0f, swing * 25f);
            // (With the hand it favours: the other way round for a left-handed one.)
            if (waveLeft)
            {
                pose.leftPos = Vector3.Lerp(pose.leftPos, new Vector3(-raised.x, raised.y, raised.z), Mathf.SmoothStep(0f, 1f, up));
                pose.leftRot = Quaternion.Slerp(pose.leftRot, turn, up);
            }
            else
            {
                pose.rightPos = Vector3.Lerp(pose.rightPos, raised, Mathf.SmoothStep(0f, 1f, up));
                pose.rightRot = Quaternion.Slerp(pose.rightRot, turn, up);
            }
            curl = 0.1f;
        }

        /// <summary>A gun spawned into its hand and tossed to the player.</summary>
        public void LabToss(string barcode)
        {
            StopPlaying();
            if (mode != Mode.Stand)
                SetMode(Mode.Stand);
            tossNext = true;
            if (!SpawnGun(barcode))
            {
                tossNext = false;
                Lab.Event("could not spawn a gun to toss");
            }
        }

        // The player's own head and hand moves for a gait, out of their recordings, one stretch
        // after another, faded from one into the next. False if nothing of theirs is recorded.
        private bool Moves(MotionLibrary.Gait want, float dt, out Frame f)
        {
            f = default;
            // (A gait has to be wanted a moment before it changes, so it does not flicker.)
            if (want != wantGait)
            {
                wantGait = want;
                wantSince = Time.time;
            }
            bool change = clip == null || (want != gait && Time.time - wantSince > 0.25f) || clipLeft <= 0f;
            if (change)
            {
                // (With enemies about, or one seen not long ago: the player's moves from such times.)
                bool tense = (mode == Mode.Brain || mode == Mode.Fists || mode == Mode.Gunplay) && (nearestSeen.transform != null || Time.time - lastEnemyTime < 8f);
                Frame[] next = MotionLibrary.Pick(clip == null ? want : wantGait, lately, personality, tense, walk.magnitude < 0.6f, avatarCode);
                if (next == null)
                {
                    clip = null;
                    return false;
                }
                if (clip != null)
                {
                    fromClip = clip;
                    fromT = clipT;
                    fromMirror = clipMirror;
                    fromRate = clipRate;
                    fade = 0f;
                }
                clip = next;
                gait = wantGait;
                lately.Add(clip);
                while (lately.Count > Mathf.Max(1, MotionLibrary.Count(gait) * 2 / 3))
                    lately.RemoveAt(0);
                clipMirror = UnityEngine.Random.value < personality.mirror;
                clipRate = UnityEngine.Random.Range(0.92f, 1.08f) * personality.tempo;
                // From somewhere in it to its end (a long one only in part), played once through.
                float length = clip[clip.Length - 1].time;
                float play = Mathf.Min(length, UnityEngine.Random.Range(2.5f, 6f));
                clipT = UnityEngine.Random.Range(0f, length - play);
                // (A move of the player's own choosing: the whole of it, from its start.)
                if (MoveSlots.IsSlot(clip))
                {
                    play = length;
                    clipT = 0f;
                }
                clipLeft = play / clipRate - 0.35f;
            }
            // A walk or a run of the player's own choosing is played at the pace the bot is
            // really going (they went so fast in it; the bot goes so fast now).
            float paced = MoveSlots.SpeedOf(clip);
            if (paced > 0.3f && gait != MotionLibrary.Gait.Idle)
            {
                Rigidbody hips = Pelvis();
                float going = hips != null ? Flat(hips.velocity).magnitude : paced;
                clipRate = Mathf.Lerp(clipRate, Mathf.Clamp(going / paced, 0.7f, 1.4f), Mathf.Clamp01(dt * 4f));
            }
            clipT += dt * clipRate;
            fromT += dt * fromRate;
            clipLeft -= dt;
            f = Sample(clip, clipT, clipMirror);
            if (fade < 1f && fromClip != null)
            {
                fade = Mathf.Min(1f, fade + dt / 0.35f);
                f = Blend(Sample(fromClip, fromT, fromMirror), f, Mathf.SmoothStep(0f, 1f, fade));
            }
            // (Hands empty: arms relaxed, not held up at the chest as the player holds controllers.)
            // (Not a move of the player's own choosing: that is played just as they did it, only
            // sized to this body where it was recorded in another.)
            if (!MoveSlots.IsSlot(clip))
            {
                f = MotionLibrary.Relax(f, Tuning.F("relax.arms", 0.85f));
            }
            else
            {
                float size = MoveSlots.Size(clip, StandingEyes());
                if (Mathf.Abs(size - 1f) > 0.03f)
                {
                    f.height *= size;
                    f.leftPos *= size;
                    f.rightPos *= size;
                }
            }
            return true;
        }

        private static Frame Sample(Frame[] from, float t, bool mirror)
        {
            Frame f = MotionLibrary.Sample(from, t);
            return mirror ? MotionLibrary.Mirror(f) : f;
        }

        // ---------------------------------------------------------------- finding its way

        private void Follow(Transform you, Transform head, float dt)
        {
            headYaw = 0f;
            if (you == null || head == null)
            {
                walk = Vector2.zero;
                return;
            }
            Vector3 feet = Feet;
            Vector3 yourFeet = you.position - Vector3.up * 1.4f;
            // Through a portal, if the player went through one or it is much the shorter way.
            if (PortalStep(yourFeet, true, dt))
            {
                following = true;
                return;
            }
            float far = Flat(yourFeet - feet).magnitude;
            // The player driving off: after them, in its own.
            if (DriveAfterYou(you, far))
                return;
            if (ToAVehicle(you, far, dt))
                return;
            // The player up on high ground (over it, however near along the ground): not with
            // them yet. It goes on to the wall between them, and climbs it.
            // (Its own feet where they really are: its pose's height can be off just after a climb.)
            PhysicsRig legs = rig.physicsRig;
            float ownFeet = legs != null && legs.centerOfPressure != null ? legs.centerOfPressure.position.y : feet.y;
            bool above = yourFeet.y - ownFeet > Tuning.F("follow.above", 1.0f);
            // (The player down below it, however near along the ground: not with them yet either.)
            bool below = ownFeet - yourFeet.y > Tuning.F("follow.below", 1.0f);
            // Sets off when the player is a little further than it stops at, so it does not keep
            // stepping to and fro at the edge.
            if (!following && (far > BotManager.FollowDistance + 0.6f || above || below))
                following = true;
            if (!following)
            {
                Watch(you, head, dt, 150f);
                walk = Vector2.zero;
                return;
            }
            // (Each to its own place by the player: several making for the very same spot ran into
            // one another, were pushed apart, and came together again, all the way there.)
            Vector3 mySpot = yourFeet + FollowSide(yourFeet, feet);
            if (!nav.HasGoal || Flat(nav.Goal - mySpot).magnitude > Tuning.F("follow.regoal", 1.5f))
                nav.SetGoal(mySpot);
            // Runs to catch up when far behind, walks when close (at the player's own pace).
            // (Once running, on for a while and till it is near: not flicking to walking as it closes in.)
            bool wasRunning = running;
            // (And at once when the player themselves is running, without first falling far behind.)
            bool youRun = false;
            try
            {
                Rigidbody yours = Player.PhysicsRig != null && Player.PhysicsRig.m_pelvis != null ? Player.PhysicsRig.m_pelvis.GetComponent<Rigidbody>() : null;
                youRun = yours != null && Flat(yours.velocity).magnitude > Tuning.F("follow.yourun", 2.8f);
            }
            catch (Exception)
            {
            }
            running = far > 6f || (youRun && far > BotManager.FollowDistance + 0.8f) || (running && (far > BotManager.FollowDistance + 1.5f || Time.time - runningSince < 2.5f));
            if (running && !wasRunning)
                runningSince = Time.time;
            float speed = running ? MotionLibrary.RunPush : Mathf.Clamp(MotionLibrary.WalkPush * (0.75f + (far - BotManager.FollowDistance) * 0.15f), 0.3f, MotionLibrary.WalkPush);
            if (above && !climbing && far > 0.2f)
                TryStartClimb(Flat(yourFeet - feet).normalized);
            if (above && !climbing && far < 1.2f)
            {
                // At the foot of their wall with nothing it can climb: it waits there, looking up at them.
                Watch(you, head, dt, 150f);
                walk = Vector2.zero;
                return;
            }
            if (Travel(speed, above || below ? 0.2f : BotManager.FollowDistance, dt) && !above && !below)
            {
                following = false;
                running = false;
                nav.Clear();
            }
        }

        // Its own place beside the player (to one side of the line it comes along; none when it is the only one).
        private Vector3 FollowSide(Vector3 yourFeet, Vector3 feet)
        {
            int mine = 0, all = 0;
            foreach (Bot other in BotManager.All)
            {
                if (other.dead || other.enemy != enemy || other.mode != Mode.Follow)
                    continue;
                if (other == this)
                    mine = all;
                all++;
            }
            if (all < 2)
                return Vector3.zero;
            Vector3 line = Flat(yourFeet - feet);
            if (line.sqrMagnitude < 4f)
                return followSideWas;
            Vector3 side = Vector3.Cross(Vector3.up, line.normalized);
            float slot = (mine % 2 == 0 ? -1f : 1f) * (0.9f + 0.9f * (mine / 2));
            followSideWas = Vector3.Lerp(followSideWas, side * slot * Tuning.F("follow.side", 1f), 0.05f);
            return followSideWas;
        }

        private Vector3 followSideWas;

        private void Gunplay(Transform you, Transform head, float dt)
        {
            running = false;
            if (Assisting && !Playing)
            {
                FistsOnly(you, head, dt);
                // (Nothing left on them, for a moment or two: back to its gun.)
                if (nearestSeen.transform != null)
                    assistSawAt = Time.time;
                else if (Time.time - assistSawAt > 2.5f && Time.time - assistBegan > 2.5f)
                    assistUntil = 0f;
                // (Still at it: it stays at it.)
                if (nearestSeen.transform != null && nearestSeenDistance < 3f)
                    assistUntil = Mathf.Max(assistUntil, Time.time + 3f);
                return;
            }
            if (Playing)
            {
                PlayStep(you, head, dt);
                return;
            }
            walk = Vector2.zero;
            if (Time.time < idleUntil)
            {
                Watch(you, head, dt, 90f);
                return;
            }
            if (!GetAGun())
            {
                // (Said once in a while, and only when it is so: it looks again every few
                // seconds, and while a gun of its own is on its way it has nothing to say.)
                if (!BotManager.OwnSpawns && Time.time - noGunSaidAt > 60f)
                {
                    noGunSaidAt = Time.time;
                    Spawnables.Notify("No gun near the bot (turn on Spawn guns for bots)");
                }
                idleUntil = Time.time + 3f;
            }
        }

        // Left to itself: one thing after another, as a player passes the time (BotLife).
        private void Wander(Transform you, Transform head, float dt) => Life(you, head, dt);

        // Walks on along its way. True once it is there (or can go no further).
        private float dropSaidAt = -10f;
        private string travelEnded = "";
        private float noGunSaidAt = -100f;

        private bool Travel(float speed, float arrive, float dt)
        {
            if (climbing)
            {
                ClimbStep(dt);
                return false;
            }
            Vector3 feet = Feet;
            if (nav.Remaining(feet) < arrive)
            {
                walk = Vector2.zero;
                travelEnded = "there (" + nav.Remaining(feet).ToString("0.0") + " m left)";
                return true;
            }
            Transform head = Head;
            Vector3 chest = head != null ? head.position - Vector3.up * 0.45f : feet + Vector3.up * 1.2f;
            Vector3 dir = nav.Steer(feet, chest, Quaternion.Euler(0f, bodyYaw, 0f) * Vector3.forward, out float rise, out bool drop);
            // At an edge: over it and down, when where it is going is down there and the drop is
            // one a player would take (it falls as they do); otherwise it stops short.
            if (drop && dir != Vector3.zero && nav.HasGoal && nav.Goal.y < feet.y - 0.5f && nav.DropDepth <= Tuning.F("drop.max", 5f))
            {
                if (Time.time - dropSaidAt > 3f)
                {
                    dropSaidAt = Time.time;
                    Lab.Event("drop: down a " + nav.DropDepth.ToString("0.0") + " m edge");
                }
                Go(dir, speed, dt);
                return false;
            }
            if (dir == Vector3.zero || drop)
            {
                walk = Vector2.zero;
                travelEnded = (drop ? "an edge ahead, " + nav.DropDepth.ToString("0.0") + " m down" : "no way to turn") + (nav.OnMesh ? ", on the level's ways" : ", by feel") + ", feet at " + feet.y.ToString("0.00");
                return true;
            }
            // A wall too high to jump between it and where it is going: climbed, as the player climbs.
            if (TryStartClimb(dir))
                return false;
            Go(dir, speed, dt);
            // Up onto a ledge just ahead, or over whatever it is stuck against.
            if (rise > 0.4f && rise < 1.3f && Time.time - ledgeJumpAt > 1.5f)
            {
                ledgeJumpAt = Time.time;
                AskJump();
            }
            if (nav.CheckStuck(feet, walk.magnitude, dt))
            {
                AskJump();
                // (Stuck here more than once: remembered, as somewhere not to pick to go to.)
                if (nav.StuckCount == 2)
                    SelfTaught.Bad(nav.HasGoal ? nav.Goal : feet);
            }
            return false;
        }

        // Turns towards dir as it walks (slowing for sharp turns), pushing its stick that way.
        private Vector3 steer;

        // For the lab: what turned its body as it walked (its way, another bot in its way, or something else between two steps).
        private float goYawWas, goAt = -10f, goSaidAt = -10f, goByOthers, goByWay, goByApart;
        private Vector3 goDirWas;

        private void Go(Vector3 dir, float speed, float dt)
        {
            Vector3 asked = dir;
            if (Time.time - goAt < 0.2f)
            {
                goByOthers += Mathf.Abs(Mathf.DeltaAngle(goYawWas, bodyYaw));
                goByWay += goDirWas.sqrMagnitude > 0.01f ? Vector3.Angle(goDirWas, asked) : 0f;
            }
            else
            {
                goByOthers = goByWay = goByApart = 0f;
                goSaidAt = Time.time;
            }
            goDirWas = asked;
            dir = KeepApart(dir, ref speed);
            goByApart += Vector3.Angle(asked, dir) * dt * 5f;
            if (Time.time - goSaidAt > 3f)
            {
                if (goByOthers > 120f || goByWay > 240f || goByApart > 60f)
                    Lab.Event("walk: in 3 s its body was turned " + goByOthers.ToString("0") + " deg by something other than its walking, its way swung " + goByWay.ToString("0") + " deg in all, kept off others by " + goByApart.ToString("0")
                              + "; " + act + "/" + play + (Time.time < gazeUntil ? ", looking at something" : "") + (thinking2 ? ", thinking" : ""));
                goByOthers = goByWay = goByApart = 0f;
                goSaidAt = Time.time;
            }
            // The way on, eased (so a corner is taken in a curve, not a snap), and the body turned
            // to it the slower the faster it goes (a sprinter cannot turn on the spot).
            steer = steer.sqrMagnitude < 0.01f ? dir : Vector3.Slerp(steer, dir, 1f - Mathf.Exp(-dt * Tuning.F("move.curve", 2.6f)));
            dir = Flat(steer).sqrMagnitude > 0.001f ? Flat(steer).normalized : dir;
            float want = Recorder.Yaw(dir);
            float turnRate = Mathf.Lerp(Tuning.F("move.turn.slow", 200f), Tuning.F("move.turn.fast", 110f), Mathf.Clamp01(stick.magnitude));
            bodyYaw = Mathf.MoveTowardsAngle(bodyYaw, want, turnRate * dt);
            float off = Mathf.Abs(Mathf.DeltaAngle(bodyYaw, want));
            Vector3 local = Quaternion.Euler(0f, -bodyYaw, 0f) * dir;
            walk = new Vector2(local.x, local.z) * (speed * Mathf.Clamp01(1.3f - off / 90f));
            goYawWas = bodyYaw;
            goAt = Time.time;
        }

        /// <summary>Walks about on its own, starting by picking something up.</summary>
        public void PickSomethingUp()
        {
            SetMode(Mode.Wander);
            if (!FindSomethingToPlayWith())
                Spawnables.Notify("Nothing it can pick up nearby");
        }

        /// <summary>Puts it on its feet at feet, facing along forward.</summary>
        public void BringTo(Vector3 feet, Vector3 forward)
        {
            try
            {
                bodyYaw = Recorder.Yaw(forward);
                StopPlaying();
                nav.Clear();
                droveHas = false;
                droveHeadHas = false;
                portalHeadHas = false;
                portalGoing = false;
                SmoothReset();
                rig.Teleport(feet, true);
            }
            catch (Exception e)
            {
                Spawnables.Notify("The bot could not be brought over: " + e.Message);
            }
        }

        // Turns the body to face the player (at up to `speed` degrees a second, once it is more
        // than a little off), as a person would. How far off it still is, in degrees.
        private float Watch(Transform you, Transform head, float dt, float speed)
        {
            if (you == null || head == null)
                return 0f;
            Vector3 to = Flat(you.position - head.position);
            if (to.sqrMagnitude < 0.01f)
                return 0f;
            float want = Recorder.Yaw(to);
            float off = Mathf.DeltaAngle(bodyYaw, want);
            if (Mathf.Abs(off) > 15f || mode != Mode.Stand)
                bodyYaw = Mathf.MoveTowardsAngle(bodyYaw, want, speed * dt);
            return Mathf.DeltaAngle(bodyYaw, want);
        }

        private float LookPitch(Transform you, Transform head)
        {
            if (you == null || head == null)
                return 0f;
            Vector3 to = you.position - head.position;
            float flat = Flat(to).magnitude;
            return Mathf.Clamp(-Mathf.Atan2(to.y, Mathf.Max(flat, 0.1f)) * Mathf.Rad2Deg, -40f, 40f);
        }

        // Standing about, arms down at its sides, breathing, looking about; or walking, its arms
        // swinging with its stride (swing: how fast it walks, 0 to 1).
        private Frame Standing(float t, float pitch, float lookYaw, float swing)
        {
            float breath = Mathf.Sin(t * 1.7f);
            float sway = Mathf.Sin(t * 0.6f);
            float arm = Mathf.Sin(stride) * 0.13f * swing;
            float lift = Mathf.Abs(Mathf.Cos(stride)) * 0.02f * swing;
            float h = standingHeight + breath * 0.004f;
            Frame f = new Frame();
            f.height = h;
            f.head = Quaternion.Euler(pitch, lookYaw + sway * 4f, 0f);
            f.leftPos = new Vector3(-0.2f, h - 0.78f + breath * 0.006f + lift, 0.06f + sway * 0.01f + arm);
            f.rightPos = new Vector3(0.2f, h - 0.78f + breath * 0.006f + lift, 0.06f - sway * 0.01f - arm);
            f.leftRot = Quaternion.Euler(65f, 10f, 0f);
            f.rightRot = Quaternion.Euler(65f, -10f, 0f);
            return f;
        }

        private void Replay()
        {
            float now = Time.time - replayStart;
            while (replayAt < replay.Count - 1 && replay[replayAt + 1].time <= now)
            {
                replayAt++;
                if (replay[replayAt].jumped && replayAt != lastJumpFrame)
                {
                    lastJumpFrame = replayAt;
                    AskJump();
                }
            }
            if (replayAt >= replay.Count - 1)
            {
                if (previewing)
                {
                    previewing = false;
                    crouchReset = true;
                    SetMode(previewBack);
                    return;
                }
                Spawnables.Notify("Replay finished");
                SetMode(Mode.Stand);
                return;
            }
            Frame a = replay[replayAt];
            Frame b = replay[replayAt + 1];
            float k = Mathf.InverseLerp(a.time, b.time, now);
            pose = Blend(a, b, k);
            bodyYaw = replayYaw + Mathf.LerpAngle(a.yaw, b.yaw, k);
            walk = Vector2.Lerp(a.stick, b.stick, k);
            Body(a);
            crouch = Mathf.Lerp(a.crouch, b.crouch, k);
            feet = Mathf.Lerp(a.feet, b.feet, k);
            curl = -1f;
        }

        // The body's own state copied from a frame: its crouch and its "A" buttons.
        private void Body(Frame f)
        {
            jump = f.jump;
            otherA = f.otherA;
            primaryClick = f.primaryStick;
            secondaryClick = f.secondaryStick;
            crouching = true;
            crouch = f.crouch;
            feet = f.feet;
        }

        // Its body starting a jump (from the buttons, or from being made to).
        private void NoteJumping()
        {
            RemapRig remap = rig.remapHeptaRig;
            bool jumping = remap != null && remap._jumping;
            if (jumping && !wasJumping)
            {
                jumpedAt = Time.time;
                jumpClip = MoveSlots.Pick(MoveSlots.Kind.Jump, null, avatarCode);
            }
            wasJumping = jumping;
            // A jump that never ended: its legs still "jumping" long after, with its feet on the
            // ground and going nowhere. In that state they do not walk: it stood where it was,
            // pushing its stick, for good. Its legs are told the jump is over.
            try
            {
                if (jumping && remap != null && Time.time - jumpedAt > Tuning.F("jump.longest", 1.6f) && !climbing && !InLesson)
                {
                    Rigidbody hips = Pelvis();
                    bool resting = hips != null && Mathf.Abs(hips.velocity.y) < 0.6f && rig.physicsRig.centerOfPressure != null && FeetOnGround();
                    if (resting)
                    {
                        remap._jumping = false;
                        remap._jumpCycle = 0f;
                        remap._timeOfFlight = 0f;
                        if (remap.travState != RemapRig.TraversalState.Walk)
                            remap.travState = RemapRig.TraversalState.Walk;
                        wasJumping = false;
                        jumpsCleared++;
                        if (Time.time - jumpClearSaidAt > 5f)
                        {
                            jumpClearSaidAt = Time.time;
                            Lab.Event("legs: a jump that never ended (" + (Time.time - jumpedAt).ToString("0.0") + " s, feet on the ground): ended, " + jumpsCleared + " so far");
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
            // The buttons copied should have made it jump by now; if not, it is made to.
            if (jumpDue > 0f && Time.time >= jumpDue)
            {
                jumpDue = -1f;
                // (Not on top of one it is still in.)
                if (Time.time - jumpedAt > 0.3f && remap != null && !remap._jumping)
                    remap.Jump();
            }
        }

        private void AskJump()
        {
            Call("effort");
            if (jumpDue < 0f)
                jumpDue = Time.time + 0.12f;
        }

        internal static Frame Blend(Frame a, Frame b, float k)
        {
            Frame f = a;
            f.height = Mathf.Lerp(a.height, b.height, k);
            f.head = Quaternion.Slerp(a.head, b.head, k);
            f.leftPos = Vector3.Lerp(a.leftPos, b.leftPos, k);
            f.rightPos = Vector3.Lerp(a.rightPos, b.rightPos, k);
            f.leftRot = Quaternion.Slerp(a.leftRot, b.leftRot, k);
            f.rightRot = Quaternion.Slerp(a.rightRot, b.rightRot, k);
            f.leftGrip = Mathf.Lerp(a.leftGrip, b.leftGrip, k);
            f.rightGrip = Mathf.Lerp(a.rightGrip, b.rightGrip, k);
            return f;
        }

        /// <summary>
        /// Puts what the brain wants on the body: called by the game's own controller rig, just
        /// before it works out the body from the head and hands (where the headset's and
        /// controllers' tracking would come in for the player).
        /// </summary>
        public void Drive()
        {
            driveCalls++;
            LastDriveFrame = Time.frameCount;
            // (Dead, or knocked down: a ragdoll, nothing put on it.)
            if (dead || down)
            {
                driveStops++;
                return;
            }
            Transform root = controls.vrRoot;
            Transform headset = controls.headset;
            if (root == null || headset == null)
                return;
            // A pose gone bad (not numbers: from a body thrown about too hard) is never put on
            // it: one would break the whole body for good. It stands as it is meant to instead.
            if (Bad(pose.height) || Bad(pose.leftPos) || Bad(pose.rightPos) || Bad(pose.head) || Bad(pose.leftRot) || Bad(pose.rightRot))
            {
                if (Time.time - badSaidAt > 5f)
                {
                    badSaidAt = Time.time;
                    Lab.Event("bad pose (not numbers) in " + mode + (climbing ? ", climbing" : "") + ": stood up again");
                }
                if (climbing)
                    StopClimb("its pose went bad");
                pose = Standing(Time.time, 0f, 0f, 0f);
            }
            // A clone: the player's own head, hands and buttons, as they were.
            if (mode == Mode.Clone)
            {
                CloneDrive();
                return;
            }
            rig.remapHeptaRig.inWeight = 1f;

            // Its stick, eased towards where it wants it, and put where the game reads a stick
            // (its legs step by it, not only its body moving).
            Vector2 push = Vector2.ClampMagnitude(running ? walk : walk * personality.pace, 1f);
            if (mode == Mode.Copy || mode == Mode.Replay)
            {
                stick = Vector2.MoveTowards(stick, push, 30f * Time.deltaTime);
            }
            else
            {
                // Getting going and stopping as long as the player takes to (learned), its way
                // swinging round gradually, not snapping from one heading to the next.
                float now = stick.magnitude, want = push.magnitude;
                float rate = want > now ? 1f / Mathf.Max(0.1f, MotionLibrary.StartTime) : 1f / Mathf.Max(0.1f, MotionLibrary.StopTime);
                float size = Mathf.MoveTowards(now, want, rate * Time.deltaTime);
                Vector2 way = want > 0.01f ? push / want : now > 0.01f ? stick / now : Vector2.up;
                Vector2 from = now > 0.01f ? stick / now : way;
                float angle = Vector2.SignedAngle(from, way);
                float swing = Mathf.Clamp(angle, -Tuning.F("move.swing", 360f) * Time.deltaTime, Tuning.F("move.swing", 360f) * Time.deltaTime);
                Vector2 heading = (Vector2)(Quaternion.Euler(0f, 0f, swing) * from);
                stick = heading.normalized * size;
            }
            if (stick.sqrMagnitude < 0.0004f && walk == Vector2.zero)
                stick = Vector2.zero;
            gameStick = mode == Mode.Copy || mode == Mode.Replay ? stick : ForItsHead(stick);
            if (controls.leftController != null)
                controls.leftController._thumbstickAxis = gameStick;
            controls._axisPrimary = gameStick;
            controls._axisPrimaryDirty = stick != Vector2.zero;

            KeepPortalTurn(root);
            Quaternion body = Quaternion.Euler(0f, bodyYaw, 0f);
            root.rotation = body;
            NotePortalTurn(root);
            headset.SetPositionAndRotation(root.TransformPoint(new Vector3(0f, pose.height + personality.posture, 0f)), body * Norm(pose.head));
            KeepHeadDown(root, headset);
            Place(controls.leftController, root, body, pose.leftPos, pose.leftRot, pose.leftGrip);
            Place(controls.rightController, root, body, pose.rightPos, pose.rightRot, pose.rightGrip);

            // Its "A" buttons, as the game reads them (one of them is the jump), and its crouch.
            bool was = controls._secondaryAButton;
            controls._secondaryAButtonDown = jump && !was;
            controls._secondaryAButtonUp = !jump && was;
            controls._secondaryAButton = jump;
            was = controls._primaryAButton;
            controls._primaryAButtonDown = otherA && !was;
            controls._primaryAButtonUp = !otherA && was;
            controls._primaryAButton = otherA;
            was = controls._primaryStick;
            controls._primaryStickDown = primaryClick && !was;
            controls._primaryStickUp = !primaryClick && was;
            controls._primaryStick = primaryClick;
            // (The sprint is read from the left controller's own stick click, as the player's
            // comes in: the player sprints, at about twice their running speed, after clicking it.)
            BaseController leftStick = controls.leftController;
            if (leftStick != null)
            {
                bool held = leftStick._thumbstick;
                leftStick._thumbstickDown = primaryClick && !held;
                leftStick._thumbstickUp = !primaryClick && held;
                leftStick._thumbstick = primaryClick;
            }
            was = controls._secondaryStick;
            controls._secondaryStickDown = secondaryClick && !was;
            controls._secondaryStickUp = !secondaryClick && was;
            controls._secondaryStick = secondaryClick;
            if (crouching)
            {
                rig.remapHeptaRig._crouchTarget = crouch;
                rig.remapHeptaRig._feetOffset = feet;
            }
            else if (crouchReset)
            {
                // (Up again after a crouch of the player's was played on it.)
                crouchReset = false;
                rig.remapHeptaRig._crouchTarget = 0f;
                rig.remapHeptaRig._feetOffset = 0f;
            }
        }

        private void Place(BaseController controller, Transform root, Quaternion body, Vector3 at, Quaternion turn, float grip)
        {
            if (controller == null)
                return;
            controller.transform.SetPositionAndRotation(root.TransformPoint(at), body * Norm(turn));

            // Its fingers: relaxed, or the recorded or live grip. Kept just short of grabbing
            // (the bot's hands hold nothing yet).
            float c = curl >= 0f ? curl : Mathf.Clamp01(grip);
            controller._processedIndex = c;
            controller._processedMiddle = c;
            controller._processedRing = c;
            controller._processedPinky = c;
            controller._processedThumb = c;
            controller._solvedGrip = Mathf.Min(c, OpenController.grabThreshold - 0.01f);
            // Its trigger finger (the right hand's, on a gun).
            float pull = controller == controls.rightController ? trigger : 0f;
            bool was = controller._primaryInteractionButton;
            bool on = pull > 0.5f;
            controller._primaryAxis = pull;
            controller._primaryInteractionButtonDown = on && !was;
            controller._primaryInteractionButtonUp = !on && was;
            controller._primaryInteractionButton = on;
            if (controller == controls.rightController)
                PutPresses(controller);
        }

        // How high its eyes can be with its feet flat: its avatar's eye height, standing.
        private float StandingEyes()
        {
            Il2CppSLZ.VRMK.Avatar avatar = rig.avatar;
            if (avatar == null)
                return 0f;
            float eyes = avatar.eyeHeight * avatar.transform.lossyScale.y;
            if (avatar.Pointer != eyesOf)
            {
                eyesOf = avatar.Pointer;
                Lab.Event("head limit: " + avatar.name + " eye height " + avatar.eyeHeight.ToString("0.000") + " x scale "
                    + avatar.transform.lossyScale.y.ToString("0.000") + " = " + eyes.ToString("0.000") + " m");
            }
            return eyes;
        }

        private IntPtr eyesOf;
        private float headCutSaidAt = -10f;

        /// <summary>
        /// No higher than its eyes can reach standing. The head comes from the player's recordings,
        /// and where the player stood straighter or stretched (or was taller than their avatar),
        /// the game lifted the body to put the head there: the bot stood on its toes.
        /// </summary>
        private void KeepHeadDown(Transform root, Transform headset)
        {
            float eyes = StandingEyes() * Tuning.F("head.max", 0.99f);
            if (eyes <= 0.3f)
                return;
            Vector3 at = headset.position;
            float above = at.y - root.position.y;
            if (above <= eyes)
                return;
            headset.position = new Vector3(at.x, root.position.y + eyes, at.z);
            if (Time.time - headCutSaidAt > 2f)
            {
                headCutSaidAt = Time.time;
                Lab.Event("head limit: head was " + above.ToString("0.00") + " m up, kept at " + eyes.ToString("0.00") + " m (" + mode + ")");
            }
        }

        private float badSaidAt = -10f;

        private static bool Bad(float v) => float.IsNaN(v) || float.IsInfinity(v);
        private static bool Bad(Vector3 v) => Bad(v.x) || Bad(v.y) || Bad(v.z);
        private static bool Bad(Quaternion q) => Bad(q.x) || Bad(q.y) || Bad(q.z) || Bad(q.w) || q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w < 0.5f;

        private static Quaternion Norm(Quaternion q)
        {
            float m = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            return m < 1e-4f ? Quaternion.identity : new Quaternion(q.x / m, q.y / m, q.z / m, q.w / m);
        }

        private static Vector3 Flat(Vector3 v)
        {
            v.y = 0f;
            return v;
        }

        /// <summary>Let go of, for a newer build to take over: puts down what it holds, takes its label away (the body stays).</summary>
        public void Detach()
        {
            DropStowed();
            // (Its legs' own top speed again, for whatever has it next.)
            try
            {
                if (walkMost > 0f && rig != null && rig.remapHeptaRig != null)
                    rig.remapHeptaRig.maxVelocity = walkMost;
            }
            catch (Exception)
            {
            }
            if (InLesson)
                ReleaseCloneHands();
            if (gun != null && Holding())
            {
                if (Reloading)
                    DropNewMag();
                LetGoOfSupport();
                Pull(false);
            }
            else
            {
                StopPlaying();
            }
            HideThought();
        }

        public void Remove()
        {
            if (rig == null)
                return;
            StopPlaying();
            HideThought();
            // Let go of anyone holding it first (a hand left holding a grip that is gone sticks).
            foreach (Grip grip in rig.GetComponentsInChildren<Grip>(true))
            {
                try
                {
                    if (grip != null)
                        grip.ForceDetach();
                }
                catch (Exception)
                {
                }
            }
            UnityEngine.Object.Destroy(rig.gameObject);
        }
    }
}
