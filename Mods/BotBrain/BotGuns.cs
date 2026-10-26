using BoneLib;
using Il2CppSLZ.Marrow;
using Il2CppSLZ.Marrow.Interaction;
using Il2CppSLZ.Marrow.Warehouse;
using UnityEngine;

namespace ThirdEyeBonelabMod.Bots
{
    /// <summary>
    /// The bot with a gun: it takes one lying about (or spawns one into its hand), holds it by
    /// its handle with its other hand on the front of a long gun, finds something to shoot,
    /// turns to it, aims down the barrel and pulls the trigger (the game's own, so the gun fires
    /// as any gun does, and whatever counts shots, the Zombies mod say, counts its own), and
    /// reloads when it runs dry. It holds fire whenever the player is anywhere near the line.
    /// </summary>
    public partial class Bot
    {
        /// <summary>Guns it may spawn for itself (ones that fire on the trigger alone, without working a pump or bolt).</summary>
        private static readonly string[] SpawnGuns =
        {
            CommonBarcodes.Guns.M1911, CommonBarcodes.Guns.P350, CommonBarcodes.Guns.Eder22, CommonBarcodes.Guns.MP5,
            CommonBarcodes.Guns.UZI, CommonBarcodes.Guns.Vector, CommonBarcodes.Guns.UMP, CommonBarcodes.Guns.AKM,
            CommonBarcodes.Guns.M16IronSights, CommonBarcodes.Guns.MK18Holosight,
        };

        private const float Range = 35f;

        private Gun gun;
        private Grip support;
        private bool supportHeld;
        private float supportTriedAt = -10f;
        private int supportTries;
        private float shootUntil;
        private bool spawningGun;
        private float spawnAskedAt;
        // Its own gun: gone back for if dropped, and no other spawned while it is about.
        private Gun ownGun;
        private float lastSpawnAt = -100f;
        private Vector3 spawnAt;
        private readonly HashSet<IntPtr> gunsBefore = new HashSet<IntPtr>();

        private Targets.Target target;
        private float targetCheckAt;
        private float targetSince;
        private float lostTargetAt;
        // After a target goes down: a moment to see it is down, gun easing off, before the next.
        private float doneWithTargetAt = -10f;
        private int shotsAtTarget;
        // Moving while it fights: a step aside now and then, backing off from what comes close.
        private Vector2 sidestep;
        private float sidestepUntil;

        // The trigger, and whether pulling it fires the gun (if not, it is fired as a pull would).
        private float trigger;
        private float triggerSince;
        private bool triggerWorks;
        private float firedAt = -10f;
        private Vector3 lastAimPoint;
        private float lastTargetAt = -10f;
        private float noMagSince;
        private int burstRoundsLeft;
        private float firedByHandAt = -10f;
        private float nextPullAt;
        private float burstUntil;
        // Pulls of the trigger that fired nothing (a gun that has run dry), judged a moment after
        // the pull (a gun may fire a little after the trigger goes).
        private int dryPulls;
        private float pullToJudge = -1f;
        private float judgeAt;

        // How the physical hand sits on its controller (measured, so the barrel can be aimed true).
        private Pose rightOnController = Pose.identity;
        // How the right hand sits on its controller with nothing in it: its own natural offset
        // (held with a gun alone, the weight sags it, and that sag measured in pushed it out of
        // place once the other hand took half the weight).
        private Pose freeRight = Pose.identity;
        private bool freeRightSet;
        // The nudges each hand's target carries (see Correct).
        private Pose rightFix = Pose.identity;
        private Pose leftFix = Pose.identity;
        private bool handMeasured;
        // Where the gun is to be this frame, and how each hand sits on it: taken once, as each
        // hand settles on the gun, and held to (taken again and again, every tug of one hand
        // moved the other).
        private Pose gunWant;
        private bool gunWantSet;
        private Pose rightInGun;
        private bool rightInGunSet;
        private Pose leftInGun;
        private bool leftInGunSet;
        private float twoHandSince;
        // How soon it fires on a new target (a person's reaction), and how long a single-shot tap is.
        private float reaction = 0.45f;
        private float tapLength = 0.08f;
        // Wanting to fire, and nothing coming out of the gun: since when.
        private float wantedFireSince = -1f;
        private Vector2 wobble;

        /// <summary>Called (by BotManager) whenever any gun fires.</summary>
        public void OnAnyGunFired(Gun fired)
        {
            if (gun == null || fired == null || fired.Pointer != gun.Pointer)
                return;
            NoteShot();
            ZombieShot();
            firedAt = Time.time;
            shotsAtTarget++;
            burstRoundsLeft--;
            LabShots++;
            // A shot not of its own making came from pulling the trigger: the trigger works.
            if (Time.time - firedByHandAt > 0.05f)
                triggerWorks = true;
        }

        // ---------------------------------------------------------------- getting a gun

        /// <summary>The nearest gun lying about that it could take, and how far off; null if none.</summary>
        private Gun NearestGun(out float far)
        {
            far = Tuning.F("gun.seek", 45f);
            Gun best = null;
            Vector3 feet = Feet;
            HashSet<IntPtr> looked = new HashSet<IntPtr>();
            foreach (Collider c in Physics.OverlapSphere(feet + Vector3.up * 0.5f, far, ~0, QueryTriggerInteraction.Ignore))
            {
                Rigidbody rb = c != null ? c.attachedRigidbody : null;
                if (rb == null || !looked.Add(rb.Pointer) || rb.GetComponentInParent<RigManager>() != null)
                    continue;
                Gun g = rb.GetComponentInParent<Gun>();
                if (g == null)
                {
                    MarrowEntity entity = rb.GetComponentInParent<MarrowEntity>();
                    g = entity != null ? entity.GetComponentInChildren<Gun>() : null;
                }
                if (!Usable(g))
                    continue;
                float up = rb.worldCenterOfMass.y - feet.y;
                float d = (rb.worldCenterOfMass - feet).magnitude;
                if (up < -0.5f || up > 1.6f || d >= far)
                    continue;
                far = d;
                best = g;
            }
            return best;
        }

        /// <summary>Goes for a gun nearby, or spawns one into its hand. False if neither can be done.</summary>
        private bool GetAGun()
        {
            // The gun asked for, looked for where it was spawned, and taken in hand.
            if (spawningGun && Time.time - spawnAskedAt > 0.2f && TakeSpawnedGun())
                return true;
            // (A spawn that never comes is given up on.)
            if (spawningGun && Time.time - spawnAskedAt > 4f)
            {
                spawningGun = false;
                Spawnables.Notify("The gun the bot asked for never came");
            }
            if (spawningGun)
                return true;
            // (One put away on its body: that one, out again.)
            if (HasStowed && stowedGun != null)
                return Draw();
            Vector3 feet = Feet;
            // Its own gun, wherever it lies (if someone has it in hand, it waits for it).
            if (ownGun != null && ownGun.triggerGrip != null && ownGun.gameObject.activeInHierarchy)
            {
                Rigidbody body = ownGun.triggerGrip.GetComponentInParent<Rigidbody>();
                // (Its own gun in someone else's hands, or gone off somewhere: it looks for another.)
                if (!Usable(ownGun) || OwnGunTaken())
                {
                    ownGun = null;
                }
                else if (body != null && (body.worldCenterOfMass - feet).magnitude < 60f)
                {
                    Take(ownGun);
                    play = Play.GoTo;
                    playAt = Time.time;
                    nav.SetGoal(item.worldCenterOfMass);
                    return true;
                }
            }
            Gun best = null;
            float nearest = Tuning.F("gun.seek", 30f);
            HashSet<IntPtr> seen = new HashSet<IntPtr>();
            foreach (Collider c in Physics.OverlapSphere(feet + Vector3.up * 0.5f, nearest, ~0, QueryTriggerInteraction.Ignore))
            {
                Rigidbody rb = c != null ? c.attachedRigidbody : null;
                if (rb == null || !seen.Add(rb.Pointer) || rb.GetComponentInParent<RigManager>() != null)
                    continue;
                Gun g = rb.GetComponentInParent<Gun>();
                if (g == null)
                {
                    MarrowEntity entity = rb.GetComponentInParent<MarrowEntity>();
                    g = entity != null ? entity.GetComponentInChildren<Gun>() : null;
                }
                if (!Usable(g))
                    continue;
                // (Not one that would not be loaded, a while ago.)
                if (wontLoad.TryGetValue(g.Pointer, out float gaveUp) && Time.time - gaveUp < 120f)
                    continue;
                float up = rb.worldCenterOfMass.y - feet.y;
                float d = (rb.worldCenterOfMass - feet).magnitude;
                if (up < -0.5f || up > 1.6f || d >= nearest)
                    continue;
                nearest = d;
                best = g;
            }
            if (best != null)
            {
                Take(best);
                play = Play.GoTo;
                playAt = Time.time;
                nav.SetGoal(item.worldCenterOfMass);
                return true;
            }
            // (And a gun in another hand already is not one to go for.)
            // A gun of its own spawned only when it has none, and not again and again.
            if (!BotManager.OwnSpawns || Time.time - lastSpawnAt < 20f)
                return false;
            lastSpawnAt = Time.time;
            return SpawnGun();
        }

        /// <summary>A gun already in its hand (taken over from an earlier build): its own, and shot with.</summary>
        public void AdoptHeldGun(Gun g)
        {
            Take(g);
            if (Holding())
                StartShooting();
            else
                EndPlay();
        }

        public Gun OwnGun => ownGun;

        /// <summary>Its gun from an earlier build: shot with if in its hand, or gone back for.</summary>
        public void AdoptGun(Gun g, bool inHand)
        {
            if (inHand)
                AdoptHeldGun(g);
            else
                ownGun = g;
        }

        private static bool Usable(Gun g)
        {
            if (g == null || g.triggerGrip == null || g.triggerGrip.HasAttachedHands() || g.fireMode == Gun.FireMode.MANUAL)
                return false;
            // (Not one that would not be loaded, a while ago.)
            if (wontLoad.TryGetValue(g.Pointer, out float gaveUp) && Time.time - gaveUp < 120f)
                return false;
            // (Not the spawn gun, nor any such tool that is a "gun" to the game.)
            string name = g.name.ToUpperInvariant();
            if (name.Contains("SPAWN") || name.Contains("NIMBUS") || name.Contains("TOOL"))
                return false;
            Rigidbody rb = g.triggerGrip.GetComponentInParent<Rigidbody>();
            return rb != null && !rb.isKinematic;
        }

        // Its own gun held by a hand that is not its own.
        private bool OwnGunTaken()
        {
            try
            {
                MarrowEntity entity = ownGun.GetComponentInParent<MarrowEntity>();
                foreach (Grip g in (entity != null ? (Component)entity : ownGun).GetComponentsInChildren<Grip>(false))
                {
                    if (g == null || g.attachedHands == null)
                        continue;
                    foreach (Hand h in g.attachedHands)
                    {
                        if (h != null && h != LeftHand && h != RightHand)
                            return true;
                    }
                }
            }
            catch (Exception)
            {
            }
            return false;
        }

        private void Take(Gun g)
        {
            loadSettleUntil = 0f;
            loadFails = 0;
            directAsked = false;
            ownGun = g;
            step = Step.OneHand;
            stepAt = Time.time;
            aimFix = Vector2.zero;
            gun = g;
            grip = g.triggerGrip;
            item = grip.GetComponentInParent<Rigidbody>();
            support = SupportGrip(g);
            // A pistol the player holds with both hands: the other hand on it as well (on
            // whichever of its grips their other hand is found at, see HoldSupportLearned).
            if (support == null)
            {
                GunMoves.Hold learned = GunMoves.Held(GunMoves.KeyOf(g, false), false, true);
                if (learned != null && learned.left != null)
                    support = AnySecondGrip(g);
            }
            // A gun the player has shown it how to hold, in one hand: held in one hand (its
            // other hand not put on whatever grip is to be found: on this gun's cocking handle, say).
            // (This gun, or one of its kind.)
            GunMoves.Hold shown = GunMoves.Held(GunMoves.KeyOf(g, false), false, true);
            if (shown != null && shown.left == null && support != null)
            {
                Lab.Event("holding " + g.name + " in one hand, as you showed it (a " + GunMoves.KindOf(g) + ")");
                support = null;
            }
            supportHeld = false;
            supportTries = 0;
            supportTriedAt = -10f;
            triggerWorks = false;
            handMeasured = false;
            dryPulls = 0;
            gunWantSet = false;
            rightFix = Pose.identity;
            leftFix = Pose.identity;
            rightInGunSet = false;
            leftInGunSet = false;
            leftMeasured = false;
            wantedFireSince = -1f;
        }

        // Any grip of the gun but its handle (and not the slide, bolt or magazine), for a second hand.
        private static Grip AnySecondGrip(Gun g)
        {
            Il2CppSLZ.Marrow.Interaction.MarrowEntity entity = g.GetComponentInParent<Il2CppSLZ.Marrow.Interaction.MarrowEntity>();
            foreach (Grip other in (entity != null ? (Component)entity : g).GetComponentsInChildren<Grip>(false))
            {
                if (other == null || other == g.triggerGrip || other.HasAttachedHands() || other.GetComponentInParent<Magazine>() != null)
                    continue;
                string name = other.name.ToLowerInvariant();
                if (name.Contains("slide") || name.Contains("charg") || name.Contains("bolt") || name.Contains("mag") || name.Contains("capsule"))
                    continue;
                return other;
            }
            return null;
        }

        // A grip on the front of a long gun, for the other hand (not the slide, bolt or magazine):
        // among the gun's own other grips, or any grip of the gun ahead of its handle.
        private static Grip SupportGrip(Gun g)
        {
            if (g.firePointTransform == null || g.triggerGrip == null)
                return null;
            Vector3 forward = g.firePointTransform.forward;
            Vector3 handle = g.triggerGrip.transform.position;
            List<Grip> grips = new List<Grip>();
            if (g.otherGrips != null)
            {
                foreach (Grip other in g.otherGrips)
                    grips.Add(other);
            }
            MarrowEntity entity = g.GetComponentInParent<MarrowEntity>();
            foreach (Grip other in (entity != null ? (Component)entity : g).GetComponentsInChildren<Grip>(false))
            {
                if (!grips.Contains(other))
                    grips.Add(other);
            }
            // The handguard: about a forearm ahead of the handle (not out at the muzzle, nor the
            // sights, slide, bolt or magazine).
            Grip best = null;
            float bestMiss = float.MaxValue;
            List<string> names = new List<string>();
            foreach (Grip other in grips)
            {
                if (other == null || other == g.triggerGrip || !other.gameObject.activeInHierarchy)
                    continue;
                names.Add(other.name + " (" + Vector3.Dot(other.transform.position - handle, forward).ToString("0.00") + " m ahead)");
                string name = other.name.ToLowerInvariant();
                if (name.Contains("slide") || name.Contains("charg") || name.Contains("bolt") || name.Contains("mag") || name.Contains("pump")
                    || name.Contains("sight") || name.Contains("attachment") || name.Contains("scope") || name.Contains("stock"))
                    continue;
                float ahead = Vector3.Dot(other.transform.position - handle, forward);
                if (ahead < 0.15f || ahead > 0.5f)
                    continue;
                float miss = Mathf.Abs(ahead - 0.3f) - (name.Contains("fore") || name.Contains("hand") ? 0.1f : 0f);
                if (miss < bestMiss)
                {
                    bestMiss = miss;
                    best = other;
                }
            }
            MelonLoader.MelonLogger.Msg("Bot Player: " + g.name + " grips: " + (names.Count > 0 ? string.Join(", ", names) : "none but the handle")
                + "; front hand on " + (best != null ? best.name : "nothing (one-handed)"));
            return best;
        }

        private bool SpawnGun(string forced = null)
        {
            Hand hand = RightHand;
            Transform head = Head;
            if (hand == null || head == null)
                return false;
            // Only guns this copy of the game has.
            List<string> have = new List<string>();
            AssetWarehouse warehouse = AssetWarehouse.Instance;
            foreach (string id in SpawnGuns)
            {
                if (warehouse != null && warehouse.HasCrate(new Barcode(id)))
                    have.Add(id);
            }
            if (forced != null)
            {
                if (warehouse == null || !warehouse.HasCrate(new Barcode(forced)))
                    return false;
                have.Clear();
                have.Add(forced);
            }
            if (have.Count == 0)
            {
                Spawnables.Notify("None of the guns the bot knows are in this game");
                return false;
            }
            spawningGun = true;
            spawnAskedAt = Time.time;
            spawnAt = hand.transform.position;
            gunsBefore.Clear();
            foreach (Collider c in Physics.OverlapSphere(spawnAt, 1.5f, ~0, QueryTriggerInteraction.Ignore))
            {
                Gun near = c != null ? c.GetComponentInParent<Gun>() : null;
                if (near != null)
                    gunsBefore.Add(near.Pointer);
            }
            // The gun that has done best for it (now and then one it knows less).
            string barcode = forced ?? Knowledge.PickGun(have);
            MelonLoader.MelonLogger.Msg("Bot Player: spawning a gun (" + barcode + ")");
            Lab.Event("spawning gun " + barcode);
            Vector3 at = hand.transform.position + Quaternion.Euler(0f, bodyYaw, 0f) * Vector3.forward * 0.1f;
            try
            {
                Clone.Suppress();
                HelperMethods.SpawnCrate(barcode, at, Quaternion.Euler(0f, bodyYaw, 0f), Vector3.one, false, spawned =>
                {
                    if (!spawningGun)
                        return;
                    spawningGun = false;
                    Gun g = spawned != null ? spawned.GetComponentInChildren<Gun>() : null;
                    if (g == null || !Alive || RightHand == null)
                    {
                        MelonLoader.MelonLogger.Warning("Bot Player: the spawned gun could not be used (" + (spawned != null ? spawned.name : "nothing came") + ")");
                        return;
                    }
                    Take(g);
                    if (item != null && Grab(RightHand, grip))
                        AfterSpawnedGun();
                    else
                        EndPlay();
                }, null);
            }
            catch (Exception e)
            {
                spawningGun = false;
                Spawnables.Notify("The bot could not spawn a gun: " + e.Message);
                return false;
            }
            return true;
        }

        // A new gun near where it asked for one: in its hand at once.
        private bool TakeSpawnedGun()
        {
            Hand hand = RightHand;
            if (hand == null)
                return false;
            foreach (Collider c in Physics.OverlapSphere(spawnAt, 1.5f, ~0, QueryTriggerInteraction.Ignore))
            {
                Gun g = c != null ? c.GetComponentInParent<Gun>() : null;
                if (g == null || gunsBefore.Contains(g.Pointer) || !Usable(g))
                    continue;
                spawningGun = false;
                Take(g);
                if (item != null && Grab(hand, grip))
                    AfterSpawnedGun();
                else
                    EndPlay();
                return true;
            }
            return false;
        }

        // A gun just spawned into its hand: to shoot with, or, if it was asked to, to toss to the player.
        private void AfterSpawnedGun()
        {
            if (!tossNext)
            {
                StartShooting();
                return;
            }
            tossNext = false;
            gun = null;
            support = null;
            holdFor = 0.8f;
            forceToss = true;
            Next(Play.Hold);
            Lab.Event("tossing " + item.name + " to the player");
        }

        private void StartShooting()
        {
            play = Play.Shoot;
            playAt = Time.time;
            target = default;
            targetCheckAt = 0f;
            shootUntil = mode == Mode.Gunplay || (mode == Mode.Brain && hostiles.Count > 0) ? float.MaxValue : Time.time + UnityEngine.Random.Range(10f, 20f);
            Load();
            // (Time for the magazine to come, before it is taken for empty and reloaded.)
            loadSettleUntil = Time.time + Tuning.F("gun.settle", 1.5f);
            Lab.Event("holding " + gun.name + (support != null ? ", front grip " + support.name : ", one-handed"));
            NoPushOnItself(gun.transform);
        }

        /// <summary>
        /// The game loads a gun out of its holder's ammunition, and there is only one store of that:
        /// the player's. A bot's gun was loaded out of the player's rounds, and when they had none
        /// of that kind left (or never had any) it could not be loaded at all, and went round and
        /// round reloading. Before a bot loads a gun, what it is about to take is put into that
        /// store first, so the player's own count stays as it was and the gun always loads.
        /// </summary>
        private static void FeedPouch(Gun g)
        {
            try
            {
                AmmoInventory pouch = AmmoInventory.Instance;
                if (g == null || pouch == null || g.defaultCartridge == null)
                    return;
                int rounds = g.defaultMagazine != null ? Mathf.Max(1, g.defaultMagazine.rounds) : 30;
                pouch.AddCartridge(g.defaultCartridge, rounds);
            }
            catch (Exception)
            {
            }
        }

        private float unsafeSince = -1f, assistUntil = -10f, assistBegan = -10f, assistSawAt = -10f;

        /// <summary>Gone in with its hands to get something off the player (its gun put away meanwhile).</summary>
        private bool Assisting => Time.time < assistUntil;

        private bool directAsked;
        private float forcedAt;
        private int forcedDry;
        private float forcedSeen;
        // Guns the game has no magazine to give for: fed a round at a time, straight into the chamber.
        private static readonly HashSet<IntPtr> magless = new HashSet<IntPtr>();

        private void Chamber()
        {
            try
            {
                if (gun.cartridgeState == Gun.CartridgeStates.UNSPENT || Time.time - firedAt < 1f / Mathf.Max(1f, gun.roundsPerSecond))
                    return;
                gun.chamberedCartridge = gun.defaultCartridge;
                gun.cartridgeState = Gun.CartridgeStates.UNSPENT;
                gun.isCharged = true;
                if (gun.slideState == Gun.SlideStates.LOCKED)
                    gun.SlideRelease();
            }
            catch (Exception)
            {
            }
        }

        private void DirectLoad()
        {
            try
            {
                if (gun == null || gun.ammoSocket == null || gun.defaultMagazine == null)
                    return;
                FeedPouch(gun);
                directAsked = true;
                Transform well = gun.ammoSocket.transform;
                SpawnMag(well.position - well.up * 0.1f, well.rotation);
            }
            catch (Exception)
            {
                directAsked = false;
            }
        }

        private float loadSettleUntil;
        private int loadFails;
        private static readonly Dictionary<IntPtr, float> wontLoad = new Dictionary<IntPtr, float>();

        // After a load outright: the magazine in by now, filled, a round chambered. True once it can fire.
        private bool SettleLoad()
        {
            try
            {
                if (gun == null)
                    return true;
                // (A magazine asked for, to go straight in: in, once it has come.)
                if (directAsked && !gun.HasMagazine() && gun.ammoSocket != null && Time.time - magAskedAt > 0.15f)
                {
                    Magazine came = SpawnedMag();
                    if (came != null && came.magazinePlug != null)
                    {
                        directAsked = false;
                        came.transform.SetPositionAndRotation(gun.ammoSocket.transform.position, gun.ammoSocket.transform.rotation);
                        try
                        {
                            came.magazinePlug.InsertPlug(gun.ammoSocket);
                        }
                        catch (Exception)
                        {
                        }
                        if (!gun.HasMagazine())
                            came.magazinePlug.ForceInSocket(gun.ammoSocket);
                        if (!gun.HasMagazine())
                            dropped.Add((came, Time.time));
                    }
                }
                if (gun.HasMagazine())
                {
                    if (gun.MagazineState != null && gun.MagazineState.AmmoCount <= 0)
                        gun.MagazineState.Refill();
                    if (gun.slideState == Gun.SlideStates.LOCKED)
                        gun.SlideRelease();
                    if (gun.cartridgeState != Gun.CartridgeStates.UNSPENT)
                        gun.Charge();
                }
                return gun.cartridgeState == Gun.CartridgeStates.UNSPENT || (gun.HasMagazine() && gun.AmmoCount() > 0);
            }
            catch (Exception)
            {
                return true;
            }
        }

        // Loaded and ready: a magazine in (the game's own quick load), full, and a round chambered.
        private void Load(bool reload = false)
        {
            FeedPouch(gun);
            try
            {
                if (!gun.HasMagazine())
                    gun.InstantLoadAsync();
                else if (gun.MagazineState != null && (reload || gun.MagazineState.AmmoCount <= 0))
                    gun.MagazineState.Refill();
                // (A round chambered: "charged" can be so with the chamber empty, as a gun comes.)
                if (reload || !gun.isCharged || gun.cartridgeState != Gun.CartridgeStates.UNSPENT)
                    gun.Charge();
                if (reload)
                    MelonLoader.MelonLogger.Msg("Bot Player: reloaded " + gun.name + " (" + gun.AmmoCount() + " rounds)");
            }
            catch (Exception e)
            {
                MelonLoader.MelonLogger.Warning("Bot Player: could not load " + gun.name + ": " + e.Message);
            }
        }

        // A round in the magazine but none in the chamber (as a gun comes): the slide wants racking.
        private bool NeedsRacking()
        {
            try
            {
                float since = Time.time - firedAt;
                bool emptyChamber = gun.cartridgeState == Gun.CartridgeStates.NONE && since > 0.6f;
                bool stuckCase = gun.cartridgeState == Gun.CartridgeStates.SPENT && since > 1.5f;
                return (emptyChamber || stuckCase) && gun.HasMagazine() && gun.AmmoCount() > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private bool Empty()
        {
            try
            {
                // (Fed straight into its chamber: never out.)
                if (magless.Contains(gun.Pointer))
                    return false;
                bool chambered = gun.cartridgeState == Gun.CartridgeStates.UNSPENT;
                // (A full magazine in, with the slide or bolt locked open, is not empty: it wants
                // the slide let go, not another magazine.)
                return !chambered && gun.AmmoCount() <= 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ---------------------------------------------------------------- shooting

        /// <summary>Once a frame while it holds a gun to shoot. False once it is done with it.</summary>
        private bool Shoot(Transform you, Transform head, float dt)
        {
            if (gun == null || !Holding() || head == null)
                return EndPlay();
            if (Time.time > shootUntil)
            {
                Next(Play.Drop);
                return true;
            }
            Transform muzzle = gun.firePointTransform;
            if (muzzle == null)
                return EndPlay();
            bool thinking = mode == Mode.Brain;

            // Out of rounds (or, with a lull in the fighting, low): a reload, by hand.
            bool calm = goal == Goal.Explore || goal == Goal.Follow || goal == Goal.Idle || goal == Goal.Investigate || goal == Goal.Guard;
            bool inCover = goal == Goal.TakeCover && coverReachedAt > 0f;
            bool lull = thinking && ((calm && nearestSeen.transform == null && AmmoFraction() < ReloadBelow) || (inCover && AmmoFraction() < 0.6f));
            bool jammed = wantedFireSince > 0f && Time.time - wantedFireSince > 2f && Time.time - firedAt > 2f;
            if (magless.Contains(gun.Pointer))
                jammed = lull = false;
            // (Just reloaded: not again for a lull or a jam straight away.)
            if (Time.time - reloadedAt < Tuning.F("reload.rest", 6f))
                jammed = lull = false;
            // (And with no magazine in it at all, a round in the chamber or not: it wants one.)
            bool noMag = false;
            try
            {
                noMag = !gun.HasMagazine();
            }
            catch (Exception)
            {
            }
            if (!noMag)
                noMagSince = Time.time;
            // Just loaded outright: the game takes a moment to put the magazine in. Starting
            // another reload meanwhile threw that magazine out as it came, over and over.
            if (!Reloading && Time.time < loadSettleUntil)
            {
                if (SettleLoad())
                {
                    loadSettleUntil = 0f;
                    loadFails = 0;
                }
                else
                {
                    Pull(false);
                    noMagSince = Time.time;
                    dryPulls = 0;
                    wantedFireSince = -1f;
                    return true;
                }
            }
            else if (!Reloading && loadSettleUntil > 0f)
            {
                // Still not loaded after waiting: once more; then it is a gun that cannot be loaded, put down for another.
                loadSettleUntil = 0f;
                if (!SettleLoad())
                {
                    loadFails++;
                    if (loadFails >= 2)
                    {
                        // No magazine to be had for it at all (the game has none to give for some
                        // guns): its rounds are put straight into its chamber from here on.
                        loadFails = 0;
                        magless.Add(gun.Pointer);
                        Lab.Event("gun: no magazine to be had for the " + gun.name + ": its rounds go straight into its chamber");
                    }
                    else
                    {
                    // (The game's own quick load does nothing for some guns: a magazine of its own, put straight in.)
                    Lab.Event("gun: the " + gun.name + " is still empty after loading: a magazine put straight into it");
                    DirectLoad();
                    loadSettleUntil = Time.time + Tuning.F("gun.settle", 1.5f) + 0.5f;
                    return true;
                    }
                }
                else
                {
                    loadFails = 0;
                }
            }
            if (!Reloading && magless.Contains(gun.Pointer))
            {
                // (Still not firing, so fed: a gun that cannot be used. Put down for another.)
                // Its trigger pulled and nothing come of it (fed so, a gun's own works do not always
                // go off): it is fired outright, as fast as it fires.
                float gapShots = 1f / Mathf.Max(1f, gun.roundsPerSecond);
                if (trigger > 0.5f && Time.time - triggerSince > 0.1f && Time.time - firedAt > gapShots && Time.time - forcedAt > Mathf.Max(0.12f, gapShots)
                    && (firedAt < triggerSince || gun.fireMode == Gun.FireMode.AUTOMATIC))
                {
                    forcedAt = Time.time;
                    FeedPouch(gun);
                    try
                    {
                        gun.chamberedCartridge = gun.defaultCartridge;
                        gun.cartridgeState = Gun.CartridgeStates.UNSPENT;
                        gun.isCharged = true;
                        gun.Fire();
                    }
                    catch (Exception)
                    {
                    }
                    forcedDry++;
                }
                // (Any shot at all since: it works.)
                if (firedAt > forcedSeen)
                {
                    forcedSeen = firedAt;
                    forcedDry = 0;
                }
                dryPulls = 0;
                if (forcedDry >= 8)
                {
                    forcedDry = 0;
                    magless.Remove(gun.Pointer);
                    wontLoad[gun.Pointer] = Time.time;
                    Lab.Event("gun: the " + gun.name + " will not fire, putting it down for another");
                    Next(Play.Drop);
                    return true;
                }
                Chamber();
                noMagSince = Time.time;
            }
            if (!Reloading && (Empty() || dryPulls >= 3 || lull || jammed || Time.time - noMagSince > 1.2f))
                StartReload();
            else if (!Reloading && NeedsRacking())
                StartReload(rackOnly: true);
            if (Reloading)
            {
                Pull(false);
                ReloadStep(head);
                // Backing off as it reloads, if something is coming.
                if (thinking && goal == Goal.Retreat && hasGoalPlace)
                {
                    if (!nav.HasGoal)
                        nav.SetGoal(goalPlace);
                    if (Travel(MotionLibrary.RunPush, 1f, dt))
                        hasGoalPlace = false;
                }
                else
                {
                    walk = Vector2.zero;
                }
                return true;
            }
            HoldSupport();
            if (step != Step.Ready)
            {
                // Getting the gun properly in hand first: held low and steady, the other hand onto
                // it, a moment to settle; then up.
                Pull(false);
                walk = Vector2.zero;
                // (With the player's two-handed hold learned, the gun comes up into it as the other
                // hand goes to the grip: that hold is what puts that hand there.)
                bool raising = (step == Step.Reach || step == Step.Settle) && LearnedHold(true) != null;
                Vector3 ahead = raising ? head.position + Quaternion.Euler(0f, bodyYaw, 0f) * new Vector3(0f, -0.3f, 5f)
                                        : head.position + Quaternion.Euler(0f, bodyYaw, 0f) * new Vector3(0f, -1.3f, 2.5f);
                Aim(ahead, head, raising ? Hold.Aimed : Hold.Low);
                labAimError = -1f;
                return true;
            }

            // Its target down (dead, or a prop knocked away), or shot at enough: done with it.
            if (target.transform != null && (!Targets.Alive(target) || (target.prop != null && shotsAtTarget >= 4)))
            {
                Targets.Shot(target);
                target = default;
                doneWithTargetAt = Time.time;
                Pull(false);
            }
            if (Time.time - doneWithTargetAt < 0.6f)
            {
                walk = Vector2.zero;
                Pull(false);
                // (Its gun still up where it was firing: left to nothing for this moment, its
                // hands went to its standing-about pose and back, in the middle of shooting.)
                Aim(lastAimPoint != Vector3.zero ? lastAimPoint : Lookout(head), head, Hold.Aimed);
                return true;
            }

            if (thinking && goal != Goal.Practice)
                FollowBrainsTarget();
            else
                PickOwnTarget(head);
            // (Its gun put away meanwhile, to go in with its hands.)
            if (play != Play.Shoot || gun == null)
                return false;

            bool movingOff = thinking && (goal == Goal.Retreat || (goal == Goal.TakeCover && coverReachedAt < 0f)) && hasGoalPlace;
            if (target.transform == null || movingOff)
            {
                // Nothing to shoot (or on its way to cover): the gun held low, looking about,
                // going where it means to go.
                Pull(false);
                // (Up and ready a while yet after its last target, looking for the next: down
                // only once there has been nothing to shoot at for a few seconds.)
                bool stillUp = Time.time - lastTargetAt < Tuning.F("fire.ready", 3f) && lastAimPoint != Vector3.zero && !movingOff;
                Aim(stillUp ? lastAimPoint : Lookout(head), head, stillUp ? Hold.Aimed : Hold.Low);
                labAimError = -1f;
                if (thinking)
                {
                    if (goal == Goal.Follow)
                    {
                        Follow(you, head, dt);
                    }
                    else if (hasGoalPlace)
                    {
                        if (!nav.HasGoal)
                            nav.SetGoal(goalPlace);
                        running = goal == Goal.Retreat || goal == Goal.TakeCover;
                        if (Travel(running ? MotionLibrary.RunPush : MotionLibrary.WalkPush * 0.85f, 1f, dt) || nav.StuckCount >= 3)
                            Arrived();
                    }
                    else
                    {
                        walk = Vector2.zero;
                        // (Turning round only to gunfire it heard, or, guarding, away from where the
                        // player is shooting: its scanning about is its head and gun, not its feet.)
                        if (Time.time < lookUntil || (goal == Goal.Guard && PlayerWatch.Fighting))
                            Face(Lookout(head), dt, 120f);
                    }
                    return true;
                }
                if (!nav.HasGoal && Navigator.RandomPlace(Feet, 3f, 10f, out Vector3 place))
                    nav.SetGoal(place);
                if (nav.HasGoal && (Travel(MotionLibrary.WalkPush * 0.8f, 0.8f, dt) || nav.StuckCount >= 3))
                    nav.Clear();
                return true;
            }

            // A target: faces it and aims, moving as it fights (keeping its distance, stepping
            // aside now and then, strafing rather than turning away), and fires a moment after
            // it has it in its sights, never with the player near the line.
            nav.Clear();
            Vector3 point = TargetPoint();
            float far = Flat(point - head.position).magnitude;
            float keep = (thinking ? Knowledge.FightDistance : 8f) * personality.distance;
            Vector2 move = Vector2.zero;
            if (target.hostile)
            {
                // Coming on fast, it gives ground sooner (as the player does, if they do).
                float giving = Knowledge.Style.Mean(Knowledge.Style.giveGround, 0.6f);
                Vector3 closing = Tactics.Velocity(target);
                bool rushing = Vector3.Dot(closing, (head.position - point).normalized) > 1f;
                if (far < Mathf.Max(3f, keep - 2.5f) || (rushing && far < keep && giving > 0.4f))
                {
                    move.y = -Mathf.Clamp01((keep - far) / 3f + 0.3f) * MotionLibrary.WalkPush;
                    // A wall at its back: it steps aside instead.
                    Vector3 back = Quaternion.Euler(0f, bodyYaw, 0f) * Vector3.back;
                    if (!Tactics.RoomBehind(head.position - Vector3.up * 0.5f, back))
                    {
                        move.y = 0f;
                        if (Time.time > sidestepUntil)
                        {
                            sidestep = new Vector2(UnityEngine.Random.value < 0.5f ? -1f : 1f, 0f) * MotionLibrary.WalkPush;
                            sidestepUntil = Time.time + 0.8f;
                        }
                    }
                }
                else if (far > keep + 6f && !(Partnered && YouFar() > PartnerLeash))
                {
                    // (Closing on a far-off target; not a partner already out of the player's reach: it shoots from where it is.)
                    move.y = MotionLibrary.WalkPush * 0.8f;
                }
            }
            // (Stepping aside as often as the player strafes in a fight.)
            float strafing = Mathf.Lerp(0.25f, 1.6f, Knowledge.Style.Mean(Knowledge.Style.strafe, 0.25f));
            if (Time.time > sidestepUntil + 1.5f && UnityEngine.Random.value < dt * strafing)
            {
                sidestep = new Vector2(UnityEngine.Random.value < 0.5f ? -1f : 1f, 0f) * MotionLibrary.WalkPush * 0.7f;
                sidestepUntil = Time.time + UnityEngine.Random.Range(0.4f, 0.9f);
            }
            if (Time.time < sidestepUntil)
                move += sidestep;
            walk = Vector2.ClampMagnitude(move, 1f);
            Face(point, dt, 260f);
            wobble = new Vector2(Mathf.PerlinNoise(Time.time * 0.7f, 3.1f) - 0.5f, Mathf.PerlinNoise(5.7f, Time.time * 0.7f) - 0.5f) * 1.6f;
            lastAimPoint = point;
            lastTargetAt = Time.time;
            Aim(point, head, Hold.Aimed);
            Vector3 dir = (point - muzzle.position).normalized;
            float off = Vector3.Angle(muzzle.forward, dir);
            labAimError = off;
            // (Not near the player, unless it is the player it is shooting at.)
            // (By the player: only from close to, with them well out of the line of it; see Targets.ClearOfPlayer.)
            bool byThem = Targets.NearPlayer(point, 1.2f);
            bool safe = target.player || (byThem ? target.hostile && Targets.ClearOfPlayer(muzzle.position, point) && !Targets.PlayerInLine(muzzle.position, muzzle.forward, 0.5f)
                : !Targets.PlayerInLine(muzzle.position, muzzle.forward, 1f));
            // (On it: within so many degrees, and no wider than the thing is at that range.)
            float span = Mathf.Atan2(Skill[ShootSkill.Size], Mathf.Max(1f, (point - muzzle.position).magnitude)) * Mathf.Rad2Deg;
            bool ready = Time.time - targetSince > reaction
                         && off < Mathf.Max(0.8f, Mathf.Min(Skill[ShootSkill.Within], span));
            shotRange = (point - muzzle.position).magnitude;
            Fire(ready && safe);
            // Something on the player that it cannot get a safe shot at: its gun put away, and in
            // with its fists to get the thing off them (a bot with a gun stood and watched).
            // (Or the player standing in the line of the shot behind it; or the thing right on top of it,
            // too close for it to bring its gun to bear: a bot with a gun stood with one at arm's length and its gun pointing at the floor.)
            bool onTop = target.hostile && (point - head.position).magnitude < Tuning.F("assist.close", 2.3f) && off > Tuning.F("assist.close.off", 20f);
            if (target.hostile && (!safe || onTop))
            {
                // (Further off: a step to one side first, for a line that is clear of them.)
                if (!byThem && (point - head.position).magnitude >= Tuning.F("assist.within", 14f) && Time.time > sidestepUntil + 0.5f)
                {
                    sidestep = new Vector2(UnityEngine.Random.value < 0.5f ? -1f : 1f, 0f) * MotionLibrary.WalkPush;
                    sidestepUntil = Time.time + 1.2f;
                }
                if (unsafeSince < 0f)
                    unsafeSince = Time.time;
                if (Time.time - unsafeSince > Tuning.F("assist.after", 1.5f) && (point - head.position).magnitude < Tuning.F("assist.within", 14f))
                {
                    unsafeSince = -1f;
                    assistUntil = Time.time + Tuning.F("assist.for", 7f);
                    assistBegan = assistSawAt = Time.time;
                    Lab.Event("assist: no safe shot at what is on you, in with its hands");
                    Stow();
                    return false;
                }
            }
            else
            {
                unsafeSince = -1f;
            }
            if (ready && safe && wantedFireSince < 0f)
                wantedFireSince = Time.time;
            else if (!(ready && safe) || Time.time - firedAt < 0.2f)
                wantedFireSince = ready && safe ? Time.time : -1f;
            if (ready && safe)
            {
                engaged = target;
                engagedAt = Time.time;
            }
            return true;
        }

        // The brain's choice of target (its nearest enemy in sight), when it is fighting.
        private void FollowBrainsTarget()
        {
            bool shoots = goal == Goal.Fight || goal == Goal.Guard || (goal == Goal.TakeCover && coverReachedAt > 0f);
            Targets.Target want = shoots ? brainTarget : default;
            if (want.transform == target.transform)
                return;
            target = want;
            targetSince = Time.time;
            shotsAtTarget = 0;
            reaction = UnityEngine.Random.Range(0.3f, 0.7f) * personality.reaction;
        }

        // Its own choice: looked for now and then, dropped once out of sight a while.
        private void PickOwnTarget(Transform head)
        {
            // Nothing to shoot but what is on the player, where no shot is safe: its gun away, and in with its hands.
            if (target.transform == null && Targets.OnPlayerFor == rig.Pointer && Time.time - Targets.OnPlayerAt < 0.6f && !Reloading)
            {
                if (unsafeSince < 0f)
                    unsafeSince = Time.time;
                if (Time.time - unsafeSince > Tuning.F("assist.after", 1.5f))
                {
                    unsafeSince = -1f;
                    assistUntil = Time.time + Tuning.F("assist.for", 7f);
                    assistBegan = assistSawAt = Time.time;
                    Lab.Event("assist: no safe shot at what is on you, in with its hands");
                    Stow();
                    return;
                }
            }
            else if (target.transform != null || Time.time - Targets.OnPlayerAt > 1.5f)
            {
                unsafeSince = -1f;
            }
            if (Time.time < targetCheckAt)
                return;
            targetCheckAt = Time.time + 0.3f;
            bool keep = target.transform != null
                        && Targets.InSight(head.position, TargetPoint(), target.transform, rig)
                        && (target.player || !Targets.NearPlayer(TargetPoint(), target.hostile ? 1.2f : 2.5f));
            if (keep)
            {
                lostTargetAt = -1f;
                return;
            }
            if (lostTargetAt < 0f)
                lostTargetAt = Time.time;
            if (target.transform != null && Time.time - lostTargetAt <= 1f)
                return;
            if (Targets.Find(head.position, Range, rig, out Targets.Target next))
            {
                bool same = target.transform != null && next.transform == target.transform;
                target = next;
                if (!same)
                {
                    targetSince = Time.time;
                    shotsAtTarget = 0;
                    reaction = UnityEngine.Random.Range(0.3f, 0.7f) * personality.reaction;
                }
                lostTargetAt = -1f;
            }
            else
            {
                target = default;
            }
        }

        // The Zombies mod works out for itself what each shot hits: the first thing along the barrel
        // that is not the gun or the player. For a bot's gun that first thing is often the bot (its
        // own hand out in front, its arm), and the shot was counted as hitting nothing: a bot could
        // fire at a zombie all day. Where that is so, the hit is worked out here, past its own
        // body, and given to the mod.
        private static System.Reflection.MethodInfo zombieHit;
        private static bool zombieLooked;
        private static float zombieDamage = 25f;
        private float zombieSaidAt = -10f;

        private void ZombieShot()
        {
            try
            {
                if (!zombieLooked)
                {
                    zombieLooked = true;
                    foreach (System.Reflection.Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        Type manager = assembly.GetType("ThirdEyeBonelabMod.Zombies.ZombieManager", false);
                        if (manager == null)
                            continue;
                        zombieHit = manager.GetMethod("ArrowHit", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                        System.Reflection.FieldInfo damage = manager.GetField("BulletDamage", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                        if (damage != null)
                            zombieDamage = Convert.ToSingle(damage.GetValue(null));
                    }
                }
                if (zombieHit == null || gun == null || gun.firePointTransform == null)
                    return;
                Transform muzzle = gun.firePointTransform;
                Transform player = BoneLib.Player.RigManager != null ? BoneLib.Player.RigManager.transform : null;
                // What the mod will take the shot to have hit, and what it really hit past this bot's own body.
                float theirs = float.MaxValue, mine = float.MaxValue;
                bool theirsIsMe = false;
                RaycastHit struck = default;
                foreach (RaycastHit hit in Physics.RaycastAll(muzzle.position, muzzle.forward, 150f, ~0, QueryTriggerInteraction.Ignore))
                {
                    Collider c = hit.collider;
                    if (c == null)
                        continue;
                    Transform t = c.transform;
                    if (t.IsChildOf(gun.transform) || (player != null && t.IsChildOf(player)))
                        continue;
                    bool me = t.IsChildOf(rig.transform);
                    if (hit.distance < theirs)
                    {
                        theirs = hit.distance;
                        theirsIsMe = me;
                    }
                    if (!me && hit.distance < mine)
                    {
                        mine = hit.distance;
                        struck = hit;
                    }
                }
                if (Time.time - zombieSaidAt > 2f)
                {
                    zombieSaidAt = Time.time;
                    Lab.Event("shot: along the barrel first " + (theirs < float.MaxValue ? theirs.ToString("0.0") + " m" + (theirsIsMe ? " (its own body)" : "") : "nothing") + "; past itself: "
                              + (struck.collider != null ? struck.collider.name + " of " + struck.collider.transform.root.name + " at " + mine.ToString("0.0") + " m" : "nothing")
                              + "; aiming at " + (target.transform != null ? target.transform.name + " " + (target.Point - muzzle.position).magnitude.ToString("0.0") + " m off, " + Vector3.Angle(muzzle.forward, target.Point - muzzle.position).ToString("0.0") + " deg off the barrel" : "nothing"));
                }
                if (!theirsIsMe || struck.collider == null)
                    return;
                zombieHit.Invoke(null, new object[] { struck.collider, struck.point, muzzle.forward, zombieDamage });
            }
            catch (Exception)
            {
            }
        }

        // ---- getting better at it (see ShootSkill): its settings for these shots, and how they go.
        private float[] shootSkill;
        private bool shootTrying;
        private int skillShots, skillHits;
        private float skillShooting, skillLastShot = -10f;

        private float[] Skill
        {
            get
            {
                if (shootSkill == null)
                    shootSkill = ShootSkill.Begin(rig.Pointer, out shootTrying);
                return shootSkill;
            }
        }

        // A shot of its own, at something: on it or not (by where its gun pointed as it went off).
        private void NoteShot()
        {
            try
            {
                // (Only shots at something that fights back: scored on things lying about, it learned to
                // wait for a perfect aim, and then took a minute to shoot what was coming at it.)
                if (target.transform == null || gun == null || gun.firePointTransform == null || target.player || target.prop != null || !target.hostile)
                    return;
                float[] now = Skill;
                Transform muzzle = gun.firePointTransform;
                bool hit = false;
                if (Physics.Raycast(muzzle.position, muzzle.forward, out RaycastHit struck, 120f, ~0, QueryTriggerInteraction.Ignore) && struck.collider != null)
                {
                    Transform what = struck.collider.transform;
                    hit = what == target.transform || what.IsChildOf(target.transform) || (target.prop != null && struck.rigidbody == target.prop)
                          || (target.aim != null && (struck.point - target.Point).magnitude < 0.45f);
                }
                skillShots++;
                if (hit)
                    skillHits++;
                // (The time it was at it: between its shots, not the waits between one target and the next.)
                skillShooting += Mathf.Min(Time.time - skillLastShot, 2f);
                skillLastShot = Time.time;
                if (skillShots >= ShootSkill.Window)
                {
                    ShootSkill.End(rig.Pointer, shootTrying, skillShots, skillHits, skillShooting);
                    skillShots = skillHits = 0;
                    skillShooting = 0f;
                    shootSkill = null;
                }
            }
            catch (Exception)
            {
            }
        }

        // Where on its target it aims, now (it moves with it).
        private Vector3 TargetPoint()
        {
            return target.prop != null ? target.prop.worldCenterOfMass : target.Point;
        }

        // Pulls the trigger in time with the gun: taps for single shots, bursts for an automatic.
        private void Fire(bool want)
        {
            if (!want)
            {
                Pull(false);
                return;
            }
            bool auto = gun.fireMode == Gun.FireMode.AUTOMATIC;
            if (trigger > 0.5f)
            {
                // Let go after a tap, or at the end of a burst.
                if ((!auto && Time.time - triggerSince > tapLength) || (auto && (burstRoundsLeft <= 0 || Time.time > burstUntil)))
                {
                    Pull(false);
                    nextPullAt = Time.time + (auto ? NextPause() : PacedGap());
                }
            }
            else if (Time.time >= nextPullAt)
            {
                Pull(true);
                tapLength = UnityEngine.Random.Range(0.06f, 0.11f);
                burstUntil = Time.time + (auto ? Tuning.F("fire.burst.most", 1.2f) : 0f);
                burstRoundsLeft = auto ? Mathf.Clamp(Mathf.RoundToInt(NextBurstRounds() * Skill[ShootSkill.Burst] * (shotRange > 18f ? 0.6f : 1f)), 1, 12) : 1;
            }

            // If its trigger does not fire this gun, it is fired as a pull would fire it.
            // (Once a pull for a single-shot gun; at an automatic's pace for one.)
            bool due = auto ? Time.time - firedAt > 0.1f : firedAt < triggerSince;
            if (trigger > 0.5f && !triggerWorks && Time.time - triggerSince > 0.05f && due && !Empty())
            {
                try
                {
                    firedByHandAt = Time.time;
                    if (!gun.isCharged)
                        gun.Charge();
                    gun.Fire();
                }
                catch (Exception)
                {
                }
            }
        }

        // The player's own rhythm, if it has watched them shoot enough; otherwise a natural mix:
        // mostly steady shots, now and then a quick pair, now and then a pause to settle.
        private float shotRange = 8f;

        // Between single shots: the player's own pace, but never faster than suits the gun and
        // the range (time for the gun to come back on its target; longer from far off, and
        // with few rounds to spend: a seven-round pistol was emptied in two seconds).
        private float PacedGap()
        {
            float least = 0.16f + Mathf.Clamp(shotRange, 0f, 25f) * 0.018f;
            try
            {
                int rounds = gun.defaultMagazine != null ? gun.defaultMagazine.rounds : 15;
                if (rounds <= 10)
                    least *= 1.25f;
                else if (rounds <= 17)
                    least *= 1.15f;
            }
            catch (Exception)
            {
            }
            least *= Skill[ShootSkill.Gap] * UnityEngine.Random.Range(0.85f, 1.25f);
            return Mathf.Max(NextGap(), least);
        }

        private static float NextGap()
        {
            float gap = Knowledge.Style.Sample(Knowledge.Style.gaps, -1f);
            if (gap > 0f)
                return Mathf.Max(0.1f, gap);
            float r = UnityEngine.Random.value;
            return r < 0.22f ? UnityEngine.Random.Range(0.14f, 0.22f) : r < 0.85f ? UnityEngine.Random.Range(0.32f, 0.7f) : UnityEngine.Random.Range(0.9f, 1.6f);
        }

        // An automatic's burst: mostly short (two to five rounds), sometimes a single round, now
        // and then a longer one, more so up close.
        // How many rounds in the next burst: as many as the player fires in theirs (or a natural
        // mix: mostly one to three, now and then more, more often up close).
        private int NextBurstRounds()
        {
            float learned = Knowledge.Style.Sample(Knowledge.Style.burstRounds, -1f);
            if (learned > 0f)
                return Mathf.Clamp(Mathf.RoundToInt(learned * personality.burst), 1, 12);
            Transform eyes = Head;
            bool close = target.transform != null && eyes != null && (TargetPoint() - eyes.position).magnitude < 5f;
            float r = UnityEngine.Random.value;
            return r < 0.3f ? 1 : r < 0.65f ? 2 : r < (close ? 0.8f : 0.9f) ? 3 : UnityEngine.Random.Range(4, 7);
        }

        private float NextBurst()
        {
            float burst = Knowledge.Style.Sample(Knowledge.Style.bursts, -1f);
            if (burst > 0f)
                return Mathf.Clamp(burst, 0.05f, 1.5f);
            Transform eyes = Head;
            bool close = target.transform != null && eyes != null && (TargetPoint() - eyes.position).magnitude < 5f;
            float r = UnityEngine.Random.value;
            return r < 0.15f ? 0.05f : r < (close ? 0.6f : 0.85f) ? UnityEngine.Random.Range(0.12f, 0.35f) : UnityEngine.Random.Range(0.45f, 0.9f);
        }

        private static float NextPause()
        {
            float pause = Knowledge.Style.Sample(Knowledge.Style.pauses, -1f);
            if (pause > 0f)
                return Mathf.Clamp(pause, 0.12f, 2.5f);
            return UnityEngine.Random.value < 0.15f ? UnityEngine.Random.Range(1f, 1.8f) : UnityEngine.Random.Range(0.3f, 0.9f);
        }

        private void Pull(bool on)
        {
            if (on && trigger <= 0.5f)
                triggerSince = Time.time;
            // Letting go of a pull: whether it fired is judged a moment later.
            if (!on && trigger > 0.5f && gun != null)
            {
                pullToJudge = triggerSince;
                judgeAt = Time.time + 0.35f;
            }
            if (pullToJudge > 0f && Time.time >= judgeAt)
            {
                if (firedAt >= pullToJudge)
                    dryPulls = 0;
                else
                    dryPulls++;
                pullToJudge = -1f;
            }
            trigger = on ? 1f : 0f;
        }

        /// <summary>How it holds the gun: up and aimed, low and ready, or in close to reload.</summary>
        private enum Hold
        {
            Aimed,
            Low,
            Reload,
        }

        // Where the gun is to be (barrel on the point, held as the hold says), and the right hand
        // where it holds that gun: the gun is placed first and the hands on it after, so both
        // hands always agree on one gun (each working out its own, they pulled it two ways).
        private void Aim(Vector3 point, Transform head, Hold hold)
        {
            Hand hand = RightHand;
            Transform muzzle = gun.firePointTransform;
            BaseController controller = controls.rightController;
            if (hand == null || muzzle == null || controller == null || item == null)
                return;
            // The player's own hold, if it has watched them hold this gun (or one like it).
            if (hold != Hold.Reload && HoldLearned(point, head, hold == Hold.Aimed))
                return;
            Transform root = controls.vrRoot;
            Quaternion body = Quaternion.Euler(0f, bodyYaw, 0f);
            Transform gunBody = item.transform;

            // How the hand sits on its controller: measured while it holds the gun alone (two
            // hands on it, the other hand's pull would be taken for it), smoothed.
            if (!supportHeld)
            {
                Pose measured = Pose.Of(controller.transform).Inverse() * Pose.Of(hand.transform);
                rightOnController = handMeasured ? Pose.Lerp(rightOnController, measured, 0.08f) : measured;
                handMeasured = true;
            }
            // How the hand sits on the gun: as it is, while it holds it alone (the grab brings the
            // hand onto the handle over a moment, so a single early look would be off for good);
            // held to once the other hand is on too (then each hand's pull would move it).
            if (!supportHeld || !rightInGunSet)
            {
                rightInGun = Pose.Of(gunBody).Inverse() * Pose.Of(hand.transform);
                rightInGunSet = true;
            }
            Pose handInGun = rightInGun;
            Pose muzzleInGun = Pose.Of(gunBody).Inverse() * Pose.Of(muzzle);

            Vector3 dir = point - muzzle.position;
            if (dir.sqrMagnitude < 0.0001f)
                return;
            // The gun's own forward (along the barrel) and up (from its handle up to the barrel:
            // the handle hangs below on any gun; the fire point's own "up" may be any way round,
            // and held to it, a rifle came out upside down), in the gun's frame.
            Vector3 forwardInGun = muzzleInGun.rotation * Vector3.forward;
            // (The magazine well, straight under the barrel, says best which way is up; the
            // handle's middle may sit off to one side.)
            Transform below = gun.ammoSocket != null ? gun.ammoSocket.transform : gun.triggerGrip.transform;
            Vector3 belowInGun = Quaternion.Inverse(gunBody.rotation) * (below.position - gunBody.position);
            Vector3 upInGun = Vector3.ProjectOnPlane(muzzleInGun.position - belowInGun, forwardInGun);
            if (upInGun.sqrMagnitude < 1e-6f)
                upInGun = Vector3.ProjectOnPlane(muzzleInGun.rotation * Vector3.up, forwardInGun);
            Quaternion gunFrame = Quaternion.LookRotation(forwardInGun, upInGun.normalized);
            labGunUp = gunBody.rotation * upInGun.normalized;
            // The gun turned so its barrel points at the point and its top is up.
            Quaternion barrel = Quaternion.LookRotation(dir.normalized, Vector3.up) * Quaternion.Euler(wobble.y, wobble.x, 0f);
            // (A turn about the barrel, tunable live, for a gun whose "up" still comes out wrong.)
            Quaternion gunTurn = barrel * Quaternion.Euler(0f, 0f, Tuning.F(support != null ? "hold.rifle.roll" : "hold.pistol.roll", 0f)) * Quaternion.Inverse(gunFrame);
            labWantUp = gunTurn * upInGun.normalized;

            // Where its gun hand is, by the hold: a long gun in to the shoulder, a pistol out at
            // arm's length; low and ready; or in at the chest, elbows bent, to reload.
            bool longGun = support != null;
            float h = standingHeight;
            Vector3 at;
            // (Heights here are from its eyes: the lab can tune them, see Tuning.)
            switch (hold)
            {
                case Hold.Low:
                    at = longGun ? Tuning.V("hold.rifle.low", new Vector3(0.14f, -0.55f, 0.22f)) : Tuning.V("hold.pistol.low", new Vector3(0.15f, -0.6f, 0.28f));
                    break;
                case Hold.Reload:
                    at = Tuning.V("hold.reload", new Vector3(0.07f, -0.42f, 0.24f));
                    break;
                default:
                    at = longGun ? Tuning.V("hold.rifle.aim", new Vector3(0.11f, -0.05f, 0.2f)) : Tuning.V("hold.pistol.aim", new Vector3(0.08f, -0.22f, 0.44f));
                    break;
            }
            at.y += h;
            Vector3 handWorld = root.TransformPoint(at);
            Pose want = new Pose(handWorld - gunTurn * handInGun.position, gunTurn);
            // (Moved smoothly towards it, as arms move a gun, not snapped there.)
            float ease = Mathf.Clamp01(Time.deltaTime * (step == Step.Ready ? Tuning.F("hold.ease", 6f) : Tuning.F("hold.ease.getting", 3f)));
            gunWant = gunWantSet ? Pose.Lerp(gunWant, want, ease) : want;
            gunWantSet = true;

            // Each hand's target nudged, frame by frame, by how far the hand still is from where
            // it should be (a hand on a grip sits where the grip puts it, not where its controller
            // says: the nudge closes what the measured offsets cannot).
            Pose handWant = gunWant * handInGun;
            // (Held alone, the measured offset already keeps up with the hand: no nudge on top.)
            if (supportHeld)
            {
                // (By the gun's own pose: it can turn in the hand, which the hand's pose does not
                // show. Learned only while the gun is steady: a shot's kick, or a big swing over to
                // a new target, is not a lasting error, and taken for one it was overdone, and the
                // gun flailed.)
                bool steady = GunSteady() && Time.time - firedAt > Tuning.F("hold.fix.afterShot", 0.4f)
                              && Quaternion.Angle(gunWant.rotation, gunBody.rotation) < Tuning.F("hold.fix.within", 35f);
                if (steady)
                    Correct(ref rightFix, gunWant, gunBody);
            }
            else
                rightFix = Pose.identity;
            Pose onController = supportHeld && freeRightSet ? freeRight : rightOnController;
            Pose controllerWant = new Pose(handWant.position + rightFix.position, rightFix.rotation * handWant.rotation) * onController.Inverse();
            handOverride = true;
            handAt = root.InverseTransformPoint(controllerWant.position);
            handTurn = Quaternion.Inverse(body) * controllerWant.rotation;
            playHeight = h * 0.985f;

            // Its head looks where it aims (to reload, down at the gun).
            Vector3 look = Quaternion.Inverse(body) * (point - head.position);
            if (hold == Hold.Reload)
            {
                playPitch = 28f;
                playYaw = 6f;
            }
            else
            {
                playPitch = Mathf.Clamp(-Mathf.Atan2(look.y, Mathf.Max(new Vector2(look.x, look.z).magnitude, 0.1f)) * Mathf.Rad2Deg, -50f, 60f);
                playYaw = Mathf.Clamp(Mathf.Atan2(look.x, look.z) * Mathf.Rad2Deg, -50f, 50f);
            }
        }

        /// <summary>Once a frame: how each empty hand sits on its controller (its natural offset).</summary>
        private void MeasureFreeHands()
        {
            Hand right = RightHand;
            BaseController controller = controls.rightController;
            MeasureFreeLeft();
            if (right == null || controller == null || right.m_CurrentAttachedGO != null)
                return;
            Pose measured = Pose.Of(controller.transform).Inverse() * Pose.Of(right.transform);
            freeRight = freeRightSet ? Pose.Lerp(freeRight, measured, 0.05f) : measured;
            freeRightSet = true;
        }

        // How its other hand sits on its controller, measured while that hand is empty.
        private void MeasureFreeLeft()
        {
            Hand left = LeftHand;
            BaseController other = controls.leftController;
            if (left == null || other == null || left.m_CurrentAttachedGO != null)
                return;
            Pose leftMeasuredNow = Pose.Of(other.transform).Inverse() * Pose.Of(left.transform);
            freeLeft = freeLeftSet ? Pose.Lerp(freeLeft, leftMeasuredNow, 0.05f) : leftMeasuredNow;
            freeLeftSet = true;
        }

        private Pose freeLeft = Pose.identity;
        private bool freeLeftSet;

        // The correction a hand's target carries: moved a little towards closing the gap between
        // where the hand is and where it should be, each frame (held within reason).
        private static void Correct(ref Pose fix, Pose want, Transform hand)
        {
            float k = Mathf.Clamp01(Time.deltaTime * Tuning.F("hold.fix.rate", 1.5f));
            float most = Tuning.F("hold.fix.most", 0.5f);
            fix.position = Vector3.ClampMagnitude(fix.position + (want.position - hand.position) * k, most);
            Quaternion off = want.rotation * Quaternion.Inverse(hand.rotation);
            fix.rotation = Quaternion.Slerp(Quaternion.identity, off, k) * fix.rotation;
            // (Not past a quarter turn either way.)
            float angle = Quaternion.Angle(Quaternion.identity, fix.rotation);
            float maxAngle = Tuning.F("hold.fix.degrees", 120f);
            if (angle > maxAngle)
                fix.rotation = Quaternion.Slerp(Quaternion.identity, fix.rotation, maxAngle / angle);
        }

        /// <summary>Getting a gun properly in hand, a step at a time (as a person does).</summary>
        private enum Step
        {
            /// <summary>In the gun hand alone, held low, till it hangs still.</summary>
            OneHand,
            /// <summary>The other hand going over to the front grip, smoothly, and taking hold once there.</summary>
            Reach,
            /// <summary>Both hands on: a moment to settle.</summary>
            Settle,
            /// <summary>Up and aiming.</summary>
            Ready,
        }

        private Step step = Step.Ready;
        private float stepAt;
        private Pose leftStart;

        private void NextStep(Step next)
        {
            Lab.Event("hold: " + step + " -> " + next + " after " + (Time.time - stepAt).ToString("0.00") + " s");
            step = next;
            stepAt = Time.time;
            reachFix = Vector3.zero;
            if (next == Step.Reach && controls.leftController != null)
                leftStart = Pose.Of(controls.leftController.transform);
        }

        // Where the game would put this hand holding this grip (or, failing that, the grip itself).
        private static Pose HandOnGrip(Grip g, Hand hand)
        {
            try
            {
                Il2CppSLZ.Marrow.Utilities.SimpleTransform solved = g.SolveHandTarget(hand);
                if (solved.rotation.x != 0f || solved.rotation.y != 0f || solved.rotation.z != 0f || solved.rotation.w != 0f)
                    return new Pose(solved.position, solved.rotation);
            }
            catch (Exception)
            {
            }
            return Pose.Of(g.transform);
        }

        // The gun hanging still in the hand (not swinging from being picked up).
        private bool GunSteady()
        {
            return item == null || (item.angularVelocity.magnitude < Tuning.F("hold.steady.spin", 1.5f) && item.velocity.magnitude < Tuning.F("hold.steady.speed", 0.8f));
        }

        // The other hand: kept out of the way while the gun settles in the gun hand, then reaching
        // over to the front grip (to where it is, not where it will be) and taking hold only once
        // it is there; then holding on, supporting the gun where it is and drawn only a little
        // towards where it is to be (the gun hand aims it: two hands each steering it fought).
        private void HoldSupport()
        {
            // With the player's two-handed hold learned, that hold places the other hand.
            GunMoves.Hold learned = support != null ? LearnedHold(true) : null;
            if (learned != null && learned.left != null)
            {
                HoldSupportLearned();
                return;
            }
            Hand left = rig.physicsRig != null ? rig.physicsRig.leftHand : null;
            float since = Time.time - stepAt;
            if (support == null || left == null || item == null)
            {
                supportHeld = false;
                if (step != Step.Ready && since > 0.7f && GunSteady())
                    NextStep(Step.Ready);
                return;
            }
            supportHeld = left.m_CurrentAttachedGO != null && support.attachedHands != null && support.attachedHands.Contains(left);
            Transform root = controls.vrRoot;
            Transform gunBody = item.transform;
            BaseController controller = controls.leftController;
            Quaternion body = Quaternion.Euler(0f, bodyYaw, 0f);

            // How the free hand sits on its controller (measured while it is free, not once it holds).
            if (!supportHeld && controller != null)
            {
                Pose measured = Pose.Of(controller.transform).Inverse() * Pose.Of(left.transform);
                leftOnController = leftMeasured ? Pose.Lerp(leftOnController, measured, 0.08f) : measured;
                leftMeasured = true;
            }

            switch (step)
            {
                case Step.OneHand:
                    leftOverride = false;
                    if (since > Tuning.F("hold.onehand.time", 0.7f) && GunSteady())
                        NextStep(Step.Reach);
                    return;

                case Step.Reach:
                    if (supportHeld)
                    {
                        NextStep(Step.Settle);
                        break;
                    }
                    // Over to the grip, eased in and out (and kept to where the grip is now).
                    leftOverride = true;
                    float k = Mathf.SmoothStep(0f, 1f, since / Tuning.F("hold.reach.time", 0.7f));
                    // (To the pose the game itself puts a hand in on this grip: the grip's own
                    // middle is inside the gun, and its turn is not a hand's.)
                    Pose handOnGrip = HandOnGrip(support, left);
                    Pose onGrip = handOnGrip * leftOnController.Inverse();
                    Pose reaching = Pose.Lerp(leftStart, onGrip, k);
                    leftAt = root.InverseTransformPoint(reaching.position);
                    leftTurn = Quaternion.Inverse(body) * reaching.rotation;
                    // Taking hold once the hand is at the grip (no grabbing from a distance: the game
                    // would fling the hand across onto it).
                    float gap = (left.transform.position - handOnGrip.position).magnitude;
                    if (k >= 1f && (gap < Tuning.F("hold.reach.grab", 0.08f) || (since > 1.4f && gap < 0.15f)))
                    {
                        supportHeld = Grab(left, support);
                        Lab.Event((supportHeld ? "front hand on " : "front hand missed ") + support.name + " (gap " + gap.ToString("0.00") + " m)");
                        if (supportHeld)
                        {
                            twoHandSince = Time.time;
                            leftInGunSet = false;
                            NextStep(Step.Settle);
                        }
                    }
                    if (!supportHeld && since > 2.5f)
                    {
                        // Out of reach: one-handed then.
                        Lab.Event("front hand gave up on " + support.name + " (gap " + gap.ToString("0.00") + " m)");
                        support = null;
                        leftOverride = false;
                        NextStep(Step.Ready);
                    }
                    return;
            }

            // Settling, or ready: holding on.
            if (!supportHeld)
            {
                // Lost its hold: back over to it.
                NextStep(Step.Reach);
                return;
            }
            if (step == Step.Settle && since > Tuning.F("hold.settle.time", 0.45f))
                NextStep(Step.Ready);
            leftOverride = true;

            // How the left hand sits on the gun: as it is while the grab is settling it on, then held to.
            if (Time.time - twoHandSince < 0.6f || !leftInGunSet)
            {
                leftInGun = Pose.Of(gunBody).Inverse() * Pose.Of(left.transform);
                leftInGunSet = Time.time - twoHandSince >= 0.6f;
            }
            Pose now = Pose.Of(gunBody) * leftInGun;
            Pose wantHand = gunWantSet ? Pose.Lerp(now, gunWant * leftInGun, Tuning.F("hold.left.pull", 0.35f)) : now;
            leftFix = Pose.identity;
            Pose wantController = wantHand * leftOnController.Inverse();
            leftAt = root.InverseTransformPoint(wantController.position);
            leftTurn = Quaternion.Inverse(body) * wantController.rotation;
        }

        private void LetGoOfSupport()
        {
            Hand left = rig.physicsRig != null ? rig.physicsRig.leftHand : null;
            try
            {
                if (left != null && support != null && support.attachedHands != null && support.attachedHands.Contains(left))
                    support.ForceDetach(left);
            }
            catch (Exception)
            {
            }
            supportHeld = false;
            leftInGunSet = false;
            leftOverride = false;
        }
    }
}
