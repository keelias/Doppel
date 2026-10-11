using BoneLib;
using Il2CppSLZ.Marrow;
using MelonLoader;
using UnityEngine;

namespace ThirdEyeBonelabMod.Bots
{
    /// <summary>
    /// A reload done as a player does it: the empty magazine dropped out, a fresh one taken from
    /// its belt (spawned into its hand, as the game's own ammo pouch gives one), brought up to
    /// the gun and pushed into the magazine well (the gun's own socket takes it in), and the
    /// slide racked if the gun needs it. If any of that goes wrong it still ends up loaded.
    /// </summary>
    public partial class Bot
    {
        private enum Reload
        {
            None,
            Eject,
            Fetch,
            Insert,
            Rack,
        }

        private Reload reload;
        private float reloadAt;
        private Magazine newMag;
        private bool magAsked;
        private float magAskedAt;
        private Vector3 magAt;
        private readonly HashSet<IntPtr> magsBefore = new HashSet<IntPtr>();

        // Magazines it has dropped, cleared away after a while (as the game clears its own).
        private static readonly List<(Magazine mag, float at)> dropped = new List<(Magazine, float)>();
        private const float DroppedFor = 20f;
        private bool ejected;
        private Grip slide;
        private bool slideHeld;
        private Vector3 slideFrom;

        // How the left hand sits on its controller (as for the right, when aiming).
        private Pose leftOnController = Pose.identity;
        private bool leftMeasured;

        public bool Reloading => reload != Reload.None;

        private Hand LeftHand => rig.physicsRig != null ? rig.physicsRig.leftHand : null;

        private void StartReload(bool rackOnly = false)
        {
            Lab.Event((rackOnly ? "racking: " : "reload starts: ") + (gun != null ? SafeState() : "?") + ", dry pulls " + dryPulls);
            if (!rackOnly)
                FeedPouch(gun);
            LetGoOfSupport();
            reload = rackOnly ? Reload.Rack : Reload.Eject;
            learnedReload = null;
            // The player's own reload, if it has watched them reload this gun (or one like it).
            if (!rackOnly)
                StartLearnedReload();
            reloadAt = Time.time;
            newMag = null;
            magAsked = false;
            ejected = false;
            slide = null;
            slideHeld = false;
            dryPulls = 0;
        }

        private void NextReload(Reload stage)
        {
            Lab.Event("reload " + reload + " -> " + stage + " after " + (Time.time - reloadAt).ToString("0.00") + " s");
            reload = stage;
            reloadAt = Time.time;
        }

        // Once a frame while reloading.
        private void ReloadStep(Transform head)
        {
            if (learnedReload != null)
            {
                LearnedReloadStep(head);
                return;
            }
            float since = Time.time - reloadAt;
            AmmoSocket socket = gun.ammoSocket;
            // The gun held in at the chest, elbows bent, muzzle out ahead and a little down, its
            // magazine well towards the other hand.
            Aim(head.position + Quaternion.Euler(0f, bodyYaw - 15f, 0f) * new Vector3(0f, -0.9f, 2.5f), head, Hold.Reload);
            leftOverride = true;
            Vector3 belt = new Vector3(-0.17f, standingHeight - 0.88f, 0.04f);

            // A gun with no magazine of its own to swap (a fixed one): loaded the quick way.
            if (socket == null || gun.defaultMagazine == null)
            {
                leftAt = Vector3.Lerp(belt, new Vector3(0.05f, standingHeight - 0.55f, 0.32f), Mathf.PingPong(since * 2f, 1f));
                leftTurn = Quaternion.Euler(60f, 20f, 0f);
                if (since > 1f)
                    FinishReload(quick: true);
                return;
            }
            // Never stuck on it: after a while it is loaded anyway.
            if (since > 3.5f && reload != Reload.Rack)
            {
                MelonLogger.Msg("Bot Player: the reload of " + gun.name + " did not go in by hand (" + reload + "); loading it");
                DropNewMag();
                FinishReload(quick: true);
                return;
            }

            switch (reload)
            {
                case Reload.Eject:
                    // The other hand to the magazine well, and the empty one out.
                    leftAt = controls.vrRoot.InverseTransformPoint(socket.transform.position) + new Vector3(-0.05f, -0.06f, 0f);
                    leftTurn = Quaternion.Euler(70f, 30f, 0f);
                    if (!ejected && since > 0.25f)
                    {
                        ejected = true;
                        try
                        {
                            if (gun.HasMagazine())
                            {
                                Magazine old = socket._magazinePlug != null ? socket._magazinePlug.magazine : null;
                                socket.EjectMagazine();
                                if (old != null)
                                    dropped.Add((old, Time.time));
                            }
                        }
                        catch (Exception e)
                        {
                            MelonLogger.Warning("Bot Player: could not drop the magazine: " + e.Message);
                        }
                    }
                    if (since > 0.4f)
                        NextReload(Reload.Fetch);
                    break;

                case Reload.Fetch:
                    // Down to its belt for a fresh one.
                    leftAt = belt;
                    leftTurn = Quaternion.Euler(80f, 0f, 0f);
                    Hand left = LeftHand;
                    if (!magAsked && since > 0.3f && left != null)
                    {
                        magAsked = true;
                        SpawnMag(left.transform.position, left.transform.rotation);
                    }
                    if (newMag == null && magAsked && Time.time - magAskedAt > 0.15f)
                        newMag = SpawnedMag();
                    if (newMag != null && newMag.grip != null && left != null)
                    {
                        if (left.m_CurrentAttachedGO == null && !Grab(left, newMag.grip))
                        {
                            DropNewMag();
                            FinishReload(quick: true);
                            return;
                        }
                        leftMeasured = false;
                        NextReload(Reload.Insert);
                    }
                    break;

                case Reload.Insert:
                    Insert(socket, since);
                    break;

                case Reload.Rack:
                    RackSlide(since);
                    break;
            }
        }

        private void SpawnMag(Vector3 at, Quaternion turn)
        {
            try
            {
                var crate = gun.defaultMagazine.spawnable != null ? gun.defaultMagazine.spawnable.crateRef : null;
                if (crate == null)
                {
                    FinishReload(quick: true);
                    return;
                }
                // (The game does not always say when a spawn is done: the new magazine is looked
                // for where it was asked for, among those that were not there before.)
                magAt = at;
                magAskedAt = Time.time;
                magsBefore.Clear();
                foreach (Collider c in Physics.OverlapSphere(at, 1f, ~0, QueryTriggerInteraction.Ignore))
                {
                    Magazine near = c != null ? c.GetComponentInParent<Magazine>() : null;
                    if (near != null)
                        magsBefore.Add(near.Pointer);
                }
                Clone.Suppress();
                HelperMethods.SpawnCrate(crate, at, turn, Vector3.one, false, null, null);
            }
            catch (Exception e)
            {
                MelonLogger.Warning("Bot Player: no magazine for " + gun.name + ": " + e.Message);
                FinishReload(quick: true);
            }
        }

        // The magazine just spawned at its hand: full, and not one that was lying there already.
        private Magazine SpawnedMag()
        {
            foreach (Collider c in Physics.OverlapSphere(magAt, 1f, ~0, QueryTriggerInteraction.Ignore))
            {
                Magazine mag = c != null ? c.GetComponentInParent<Magazine>() : null;
                if (mag == null || magsBefore.Contains(mag.Pointer) || mag.isMagazineInserted || (mag.grip != null && mag.grip.HasAttachedHands()))
                    continue;
                try
                {
                    mag.Initialize(gun.defaultCartridge, gun.defaultMagazine.rounds);
                }
                catch (Exception)
                {
                }
                return mag;
            }
            return null;
        }

        /// <summary>Clears away the magazines bots dropped a while ago (unless someone has picked one up).</summary>
        public static void ClearDropped()
        {
            for (int i = dropped.Count - 1; i >= 0; i--)
            {
                (Magazine mag, float at) = dropped[i];
                if (mag == null)
                {
                    dropped.RemoveAt(i);
                    continue;
                }
                if (Time.time - at < DroppedFor)
                    continue;
                dropped.RemoveAt(i);
                try
                {
                    if (mag.isMagazineInserted || (mag.grip != null && mag.grip.HasAttachedHands()))
                        continue;
                    Il2CppSLZ.Marrow.Pool.Poolee poolee = mag.GetComponentInParent<Il2CppSLZ.Marrow.Pool.Poolee>();
                    if (poolee != null)
                        poolee.Despawn();
                    else
                        UnityEngine.Object.Destroy(mag.gameObject);
                }
                catch (Exception)
                {
                }
            }
        }

        public static void ForgetDropped()
        {
            dropped.Clear();
        }

        // Brings the new magazine's plug up under the well and pushes it in.
        private void Insert(AmmoSocket socket, float since)
        {
            Hand left = LeftHand;
            if (newMag == null || left == null || newMag.magazinePlug == null)
            {
                FinishReload(quick: true);
                return;
            }
            if (gun.HasMagazine())
            {
                // In: the hand lets go (the gun has it now).
                LetGoOf(newMag.grip, left);
                newMag = null;
                NextReload(Reload.Rack);
                return;
            }
            Transform plug = newMag.magazinePlug.transform;
            Transform well = socket.transform;
            // In along the way the socket takes it (from its far end back out past its mouth).
            Vector3 into = socket.endTransform != null && (socket.endTransform.position - well.position).sqrMagnitude > 1e-6f
                ? (socket.endTransform.position - well.position).normalized
                : -well.up;
            float k = Mathf.SmoothStep(0f, 1f, since / 0.7f);
            Pose wantPlug = new Pose(well.position - into * Mathf.Lerp(0.12f, -0.01f, k), well.rotation);

            // Where the hand must be for the plug to be there: through the magazine and the grip.
            Transform host = newMag.transform;
            Pose plugInHost = Pose.Of(host).Inverse() * Pose.Of(plug);
            Pose hostInHand = Pose.Of(left.transform).Inverse() * Pose.Of(host);
            Pose wantHand = wantPlug * plugInHost.Inverse() * hostInHand.Inverse();
            BaseController controller = controls.leftController;
            Pose measured = Pose.Of(controller.transform).Inverse() * Pose.Of(left.transform);
            leftOnController = leftMeasured ? Pose.Lerp(leftOnController, measured, 0.1f) : measured;
            leftMeasured = true;
            Pose wantController = wantHand * leftOnController.Inverse();
            leftAt = controls.vrRoot.InverseTransformPoint(wantController.position);
            leftTurn = Quaternion.Inverse(Quaternion.Euler(0f, bodyYaw, 0f)) * wantController.rotation;

            // Close to the well: the socket is let take it (as it does when it is pushed home).
            float gap = (plug.position - well.position).magnitude;
            if (gap < 0.06f && since > 0.4f)
            {
                try
                {
                    newMag.magazinePlug.InsertPlug(socket);
                }
                catch (Exception)
                {
                }
            }
            if (since > 2.2f && !gun.HasMagazine())
            {
                try
                {
                    newMag.magazinePlug.ForceInSocket(socket);
                }
                catch (Exception)
                {
                }
            }
        }

        // Works the slide back and lets it go, if there is no round in the chamber.
        private void RackSlide(float since)
        {
            Hand left = LeftHand;
            if (gun.cartridgeState == Gun.CartridgeStates.UNSPENT && !slideHeld)
            {
                FinishReload(quick: false);
                return;
            }
            if (slide == null)
                slide = SlideGrip(gun);
            if (slide == null || left == null)
            {
                if (since > 0.3f)
                {
                    TryCharge();
                    FinishReload(quick: false);
                }
                return;
            }
            Vector3 back = gun.firePointTransform != null ? -gun.firePointTransform.forward : Quaternion.Euler(0f, bodyYaw, 0f) * Vector3.back;
            if (!slideHeld)
            {
                leftAt = controls.vrRoot.InverseTransformPoint(slide.transform.position);
                leftTurn = Quaternion.Inverse(Quaternion.Euler(0f, bodyYaw, 0f)) * slide.transform.rotation;
                if (since > 0.3f)
                {
                    slideHeld = Grab(left, slide);
                    slideFrom = slide.transform.position;
                    reloadAt = Time.time;
                    if (!slideHeld)
                    {
                        TryCharge();
                        FinishReload(quick: false);
                    }
                }
                return;
            }
            // Pulled back sharply, then let go.
            leftAt = controls.vrRoot.InverseTransformPoint(slideFrom + back * Mathf.Clamp01(since / 0.15f) * 0.1f);
            if (since > 0.22f)
            {
                LetGoOf(slide, left);
                slideHeld = false;
                if (gun.cartridgeState != Gun.CartridgeStates.UNSPENT)
                    TryCharge();
                FinishReload(quick: false);
            }
        }

        private static Grip SlideGrip(Gun g)
        {
            if (g.otherGrips == null)
                return null;
            foreach (Grip other in g.otherGrips)
            {
                if (other == null || !other.gameObject.activeInHierarchy)
                    continue;
                string name = other.name.ToLowerInvariant();
                if (name.Contains("slide") || name.Contains("charg") || name.Contains("bolt"))
                    return other;
            }
            return null;
        }

        private void TryCharge()
        {
            try
            {
                // Locked open: let go, as the slide (or bolt) release does, chambering a round.
                if (gun.slideState == Gun.SlideStates.LOCKED)
                    gun.SlideRelease();
                if (gun.cartridgeState != Gun.CartridgeStates.UNSPENT)
                    gun.Charge();
            }
            catch (Exception)
            {
            }
        }

        private float reloadedAt = -10f;

        private void FinishReload(bool quick)
        {
            LabReloads++;
            // A reload always ends with a gun that can fire: if its hands did not get rounds
            // into it (a magazine that would not go in, a gun that loads some other way), it
            // is loaded outright. (Left empty, it started the reload again, over and over.)
            if (!quick && gun != null)
            {
                try
                {
                    if (!gun.HasMagazine() || (gun.AmmoCount() <= 0 && gun.cartridgeState != Gun.CartridgeStates.UNSPENT))
                        quick = true;
                }
                catch (Exception)
                {
                }
            }
            // A reload ends with a full magazine, whatever its hands managed: one that went through
            // the motions and left the same part-used magazine in was begun again at once, over and over.
            reloadedAt = Time.time;
            if (!quick && gun != null)
            {
                try
                {
                    int full = gun.defaultMagazine != null ? gun.defaultMagazine.rounds : 0;
                    if (gun.HasMagazine() && gun.MagazineState != null && full > 0 && gun.MagazineState.AmmoCount < full - 1)
                    {
                        Lab.Event("reload: the magazine in it had " + gun.MagazineState.AmmoCount + " of " + full + ": filled");
                        gun.MagazineState.Refill();
                    }
                }
                catch (Exception)
                {
                }
            }
            Lab.Event("chamber after: " + SafeState());
            Lab.Event("reload done " + (quick ? "QUICK (fallback)" : "by hand") + " at stage " + reload);
            if (quick && gun != null)
            {
                Load(reload: true);
                loadSettleUntil = Time.time + Tuning.F("gun.settle", 1.5f);
            }
            else if (gun != null)
                MelonLogger.Msg("Bot Player: reloaded " + gun.name + " by hand (" + SafeAmmo() + " rounds)");
            Hand left = LeftHand;
            if (slideHeld && slide != null && left != null)
                LetGoOf(slide, left);
            slideHeld = false;
            reload = Reload.None;
            leftOverride = false;
            supportTries = 0;
            supportTriedAt = -10f;
            if (gun != null)
            {
                step = support != null ? Step.Reach : Step.Ready;
                stepAt = Time.time;
                if (controls.leftController != null)
                    leftStart = Pose.Of(controls.leftController.transform);
            }
            dryPulls = 0;
            wantedFireSince = -1f;
            Feel("reloaded");
        }

        private int SafeAmmo()
        {
            try
            {
                return gun.AmmoCount();
            }
            catch (Exception)
            {
                return -1;
            }
        }

        // A magazine taken out of the pouch and not got in: dropped.
        private void DropNewMag()
        {
            Hand left = LeftHand;
            if (newMag != null && newMag.grip != null && left != null)
                LetGoOf(newMag.grip, left);
            if (newMag != null)
                dropped.Add((newMag, Time.time));
            newMag = null;
        }

        /// <summary>
        /// The hand emptied, whatever it holds and however it came to hold it: by every grip of
        /// the thing that has the hand on it, by the grip it thinks it holds, and (a grab
        /// that went wrong half way leaves the hand "full" of a thing no grip knows it has)
        /// by the hand's own letting go.
        /// </summary>
        private static void FreeHand(Hand hand)
        {
            try
            {
                GameObject held = hand != null ? hand.m_CurrentAttachedGO : null;
                if (held == null)
                    return;
                Il2CppSLZ.Marrow.Interaction.MarrowEntity entity = held.GetComponentInParent<Il2CppSLZ.Marrow.Interaction.MarrowEntity>();
                Transform root = entity != null ? entity.transform : held.transform.root;
                if (root.GetComponent<RigManager>() == null)
                {
                    foreach (Grip g in root.GetComponentsInChildren<Grip>(true))
                        LetGoOf(g, hand);
                }
                if (hand.m_CurrentAttachedGO == null)
                    return;
                Grip own = held.GetComponent<Grip>() ?? held.GetComponentInParent<Grip>();
                if (own != null)
                    own.ForceDetach(hand);
                if (hand.m_CurrentAttachedGO != null)
                    hand.DetachObject();
            }
            catch (Exception e)
            {
                Lab.Event("hand could not be emptied: " + e.Message);
            }
        }

        private static void LetGoOf(Grip g, Hand hand)
        {
            try
            {
                if (g != null && hand != null && g.attachedHands != null && g.attachedHands.Contains(hand))
                    g.ForceDetach(hand);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>A position and a turn, put together as transforms are.</summary>
        public struct Pose
        {
            public Vector3 position;
            public Quaternion rotation;

            public static readonly Pose identity = new Pose(Vector3.zero, Quaternion.identity);

            public Pose(Vector3 position, Quaternion rotation)
            {
                this.position = position;
                this.rotation = rotation;
            }

            public static Pose Of(Transform t) => new Pose(t.position, t.rotation);

            public static Pose operator *(Pose a, Pose b) => new Pose(a.position + a.rotation * b.position, a.rotation * b.rotation);

            public Pose Inverse()
            {
                Quaternion inv = Quaternion.Inverse(rotation);
                return new Pose(inv * (-position), inv);
            }

            public static Pose Lerp(Pose a, Pose b, float k) => new Pose(Vector3.Lerp(a.position, b.position, k), Quaternion.Slerp(a.rotation, b.rotation, k));
        }
    }
}
