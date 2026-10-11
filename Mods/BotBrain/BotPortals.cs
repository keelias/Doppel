using System.Runtime.CompilerServices;
using BoneLib;
using Il2CppSLZ.Marrow;
using UnityEngine;

namespace ThirdEyeBonelabMod.Bots
{
    /// <summary>
    /// The portals about (the Portals mod's), as ways from one place to another: where each
    /// is, which way it opens, and where it comes out. Looked for once a second. Also where
    /// the player last went through one. (Without the Portals mod there are none, and all
    /// this does nothing.)
    /// </summary>
    public static class PortalWays
    {
        public struct Way
        {
            /// <summary>The portal to go into: its middle, and the way it opens (out of its wall or floor).</summary>
            public Vector3 inAt, inOpen;
            /// <summary>Where it comes out, and the way that one opens.</summary>
            public Vector3 outAt, outOpen;
            public IntPtr id;
            /// <summary>The two portals themselves (the Portals mod's own).</summary>
            public Component portal, other;
        }

        private static readonly List<Way> ways = new List<Way>();
        private static float lookedAt = -10f;
        private static bool missing;

        /// <summary>The way the player last took, and when.</summary>
        public static Way PlayerTook;
        public static float PlayerTookAt = -100f;
        private static Vector3 playerWas;
        private static bool playerHas;

        public static List<Way> All
        {
            get
            {
                if (!missing && Time.time - lookedAt > 1f)
                {
                    lookedAt = Time.time;
                    ways.Clear();
                    try
                    {
                        Look();
                    }
                    catch (Exception e) when (e is TypeLoadException || e is System.IO.FileNotFoundException || e is MissingMethodException)
                    {
                        missing = true;
                    }
                    catch (Exception e)
                    {
                        Lab.Event("portals: could not be looked for: " + e.Message);
                    }
                }
                return ways;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Look()
        {
            foreach (Portals.MonoBehaviours.Portal portal in UnityEngine.Object.FindObjectsOfType<Portals.MonoBehaviours.Portal>())
            {
                if (portal == null || !portal.gameObject.activeInHierarchy)
                    continue;
                Portals.MonoBehaviours.Portal other = portal.OtherPortal;
                if (other == null || !other.gameObject.activeInHierarchy)
                    continue;
                ways.Add(new Way
                {
                    inAt = portal.transform.position,
                    inOpen = Open(portal.transform),
                    outAt = other.transform.position,
                    outOpen = Open(other.transform),
                    id = portal.Pointer,
                    portal = portal,
                    other = other,
                });
            }
        }

        // The way a portal opens: along its own forward, to the side that is not its wall.
        private static Vector3 Open(Transform portal)
        {
            // (The Portals mod's portals are gone into from the side their forward points to.)
            return portal.forward;
        }

        private static bool Blocked(Vector3 at, Vector3 dir)
        {
            foreach (RaycastHit hit in Physics.RaycastAll(at + dir * 0.02f, dir, 0.5f, ~0, QueryTriggerInteraction.Ignore))
            {
                if (hit.collider != null && hit.collider.attachedRigidbody == null)
                    return true;
            }
            return false;
        }

        /// <summary>Once a frame: the player going through one (there one moment, far off the next, a portal where they were).</summary>
        public static void Update()
        {
            Transform head = Player.Head;
            if (head == null)
            {
                playerHas = false;
                return;
            }
            Vector3 at = head.position;
            if (playerHas && (at - playerWas).magnitude > 2f)
            {
                foreach (Way way in All)
                {
                    if ((way.inAt - playerWas).magnitude < 2.5f && (way.outAt - at).magnitude < 3.5f)
                    {
                        PlayerTook = way;
                        PlayerTookAt = Time.time;
                        Lab.Event("portals: you went through one");
                        break;
                    }
                }
            }
            playerWas = at;
            playerHas = true;
        }

        /// <summary>A portal that makes the way from one place to another a good deal shorter; false if none does.</summary>
        public static bool Shorter(Vector3 from, Vector3 to, out Way best)
        {
            best = default;
            float straight = (to - from).magnitude;
            float least = straight * Tuning.F("portal.worth", 0.7f) - 2f;
            bool found = false;
            foreach (Way way in All)
            {
                // (Not one in a ceiling: it cannot get up into it.)
                if (way.inOpen.y < -0.5f || way.inAt.y - from.y > 2.2f)
                    continue;
                float through = (way.inAt - from).magnitude + (to - way.outAt).magnitude;
                if (through < least)
                {
                    least = through;
                    best = way;
                    found = true;
                }
            }
            return found;
        }
    }

    /// <summary>
    /// A bot taking a portal: when the player it follows has gone through one, or when one
    /// makes its way a good deal shorter, it goes up to the portal and walks into it, as a
    /// player does, and goes on from where it comes out (facing the way the portal left it).
    /// </summary>
    public partial class Bot
    {
        private bool portalGoing;
        private PortalWays.Way portalWay;
        private float portalSince;
        private int portalPhase;
        private float portalPhaseAt;
        private float portalNotBefore;
        private float portalTookSeen;
        private Vector3 portalHeadWas;
        private bool portalHeadHas;
        private float portalThroughAt = -100f;

        // The way its play space was turned when last it was set (to see a portal turning it).
        private float droveYaw;
        private bool droveHas;

        /// <summary>
        /// Before its body is turned to face the way it wants: if something other than the
        /// bot has turned it since last frame (a portal it went through), it faces that way now.
        /// </summary>
        private void KeepPortalTurn(Transform root)
        {
            float turn = PortalTurn(root);
            if (turn == 0f)
                return;
            bodyYaw += turn;
            steer = Quaternion.Euler(0f, turn, 0f) * steer;
        }

        // How far a portal has just turned it (0 if none has).
        private float PortalTurn(Transform root)
        {
            // (Only a turn that comes with being carried off somewhere: nothing else is a portal's.)
            // (Its play space is carried off in the same stroke as it is turned: seen at once,
            // where its head, a body, is seen to have gone a moment later.)
            if (droveHeadHas && (root.position - droveHead).magnitude > 1f)
                droveJumpAt = Time.time;
            droveHead = root.position;
            droveHeadHas = true;
            if (climbing)
                droveHas = false;
            if (droveHas)
            {
                float turned = Mathf.DeltaAngle(droveYaw, root.eulerAngles.y);
                if (Mathf.Abs(turned) > 3f)
                {
                    droveTurn = turned;
                    droveTurnAt = Time.time;
                }
            }
            if (Time.time - droveTurnAt < 0.5f && Time.time - droveJumpAt < 0.5f)
            {
                Lab.Event("portals: turned " + droveTurn.ToString("0") + " deg by one");
                droveTurnAt = -10f;
                return droveTurn;
            }
            return 0f;
        }

        private Vector3 droveHead;
        private bool droveHeadHas;
        private float droveJumpAt = -10f, droveTurn, droveTurnAt = -10f;

        private void NotePortalTurn(Transform root)
        {
            droveYaw = root.eulerAngles.y;
            droveHas = true;
            droveHead = root.position;
        }

        // Its head far from where it was a frame ago (and not put there by the bot): it has gone through.
        private bool PortalCarried()
        {
            Transform head = Head;
            if (head == null)
                return false;
            Vector3 at = head.position;
            bool carried = portalHeadHas && (at - portalHeadWas).magnitude > 1.5f;
            portalHeadWas = at;
            portalHeadHas = true;
            return carried;
        }

        /// <summary>
        /// On its way to a place: takes a portal if that is the way. True while it is doing so
        /// (its walking is then this, not the way it would have gone).
        /// </summary>
        private bool PortalStep(Vector3 goal, bool afterPlayer, float dt)
        {
            bool carried = PortalCarried();
            if (climbing || Time.time < portalNotBefore)
            {
                portalGoing = false;
                return false;
            }
            Vector3 feet = Feet;
            if (!portalGoing)
            {
                bool take = false;
                PortalWays.Way way = default;
                // The player it is after went through one just now, near where it is: the same way.
                if (afterPlayer && PortalWays.PlayerTookAt > portalTookSeen && Time.time - PortalWays.PlayerTookAt < 12f)
                {
                    portalTookSeen = PortalWays.PlayerTookAt;
                    way = PortalWays.PlayerTook;
                    take = (way.inAt - feet).magnitude < Tuning.F("portal.follow", 30f) && (way.inAt - feet).magnitude < (goal - feet).magnitude + 2f;
                }
                if (!take && Time.time - portalThroughAt > 4f)
                    take = PortalWays.Shorter(feet, goal, out way);
                if (!take)
                    return false;
                portalGoing = true;
                portalWay = way;
                portalSince = Time.time;
                portalPhase = 0;
                portalPhaseAt = Time.time;
                nav.Clear();
                Lab.Event("portals: going through one, " + (way.inAt - feet).magnitude.ToString("0") + " m off" + (afterPlayer ? " (after you)" : ""));
            }
            if (carried)
            {
                portalGoing = false;
                portalThroughAt = Time.time;
                nav.Clear();
                Lab.Event("portals: through");
                return false;
            }
            Vector3 open = portalWay.inOpen;
            bool inFloor = open.y > 0.7f;
            // In front of it (on the ground there), or on it if it is in the floor.
            Vector3 before = inFloor ? portalWay.inAt : portalWay.inAt + Flat(open).normalized * 1.1f;
            if (!inFloor && Physics.Raycast(before, Vector3.down, out RaycastHit ground, 4f, ~0, QueryTriggerInteraction.Ignore))
                before = ground.point;
            if (portalPhase == 0)
            {
                if (!nav.HasGoal || (nav.Goal - before).magnitude > 0.5f)
                    nav.SetGoal(before);
                running = (before - feet).magnitude > 6f;
                bool there = Travel(running ? MotionLibrary.RunPush : MotionLibrary.WalkPush, 0.45f, dt) || Flat(before - feet).magnitude < 0.6f;
                if (there || inFloor && Flat(before - feet).magnitude < 0.4f)
                {
                    portalPhase = 1;
                    portalPhaseAt = Time.time;
                }
                else if (Time.time - portalSince > 20f || nav.StuckCount >= 4)
                {
                    GiveUpPortal("could not get to it");
                }
                return portalGoing;
            }
            // Into it: straight at its middle (nothing found for it to walk round: the wall there is the way).
            running = false;
            // (Square on to it first, if it has come at it from the side: its opening is narrow.)
            Vector3 off = Flat(feet - portalWay.inAt);
            Vector3 along = Flat(open).sqrMagnitude > 0.01f ? Flat(open).normalized : Vector3.zero;
            float aside = (off - along * Vector3.Dot(off, along)).magnitude;
            Vector3 into = Flat((aside > 0.2f && !inFloor ? portalWay.inAt + along * 0.8f : portalWay.inAt - open * 0.6f) - feet);
            if (into.sqrMagnitude < 0.01f)
                into = -Flat(open);
            Go(into.normalized, MotionLibrary.WalkPush, dt);
            // The portal takes it as it crosses (seen above, next frame). If it has crossed and
            // the portal has not taken it, or it stands against the wall there and cannot
            // cross, it is put through.
            string state = "";
            bool through = false;
            try
            {
                through = PortalPush(Time.time - portalPhaseAt, out state);
            }
            catch (Exception e)
            {
                state = e.Message;
            }
            if (through)
            {
                portalGoing = false;
                portalThroughAt = Time.time;
                portalHeadHas = false;
                nav.Clear();
                Lab.Event("portals: through (" + state + ")");
                return false;
            }
            if (Time.time - portalPhaseAt > 7f)
                GiveUpPortal("walked at it and did not go through: " + state);
            return portalGoing;
        }

        // Where its head is to the portal it is going into, and through it if the portal has not done it.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private bool PortalPush(float tried, out string state)
        {
            state = "";
            Transform head = Head;
            Portals.MonoBehaviours.Portal portal = portalWay.portal != null ? portalWay.portal.TryCast<Portals.MonoBehaviours.Portal>() : null;
            Portals.MonoBehaviours.Portal other = portalWay.other != null ? portalWay.other.TryCast<Portals.MonoBehaviours.Portal>() : null;
            if (head == null || portal == null || other == null)
            {
                state = "the portal is gone";
                return false;
            }
            Vector3 local = portal.transform.InverseTransformPoint(head.position);
            Vector2 half = portal.Size * 0.5f;
            bool inside = Mathf.Abs(local.x) <= half.x && Mathf.Abs(local.y) <= half.y;
            Portals.MonoBehaviours.TeleportableRigManager mine = rig.GetComponent<Portals.MonoBehaviours.TeleportableRigManager>();
            state = "head " + local.ToString("0.00") + " to it, its half size " + half.ToString("0.00")
                + (mine == null ? ", its body is not one the portals know" : ", bodies " + mine.Bodies.Count + (mine.HasPortals ? ", in a portal" : ", in none"));
            // Crossed, and not taken: the portal's own carrying, asked for outright.
            if (mine != null && inside && local.z < -0.02f)
            {
                mine.Teleport(portal, other);
                state += "; carried by the portal on being asked";
                return true;
            }
            // Up against it and no further (its wall is solid to it): put out of the other one.
            Vector2 loose = half + new Vector2(0.35f, 0.9f);
            bool about = Mathf.Abs(local.x) <= loose.x && Mathf.Abs(local.y) <= loose.y;
            if (tried > Tuning.F("portal.patience", 3.5f) && about && local.z < Tuning.F("portal.reach", 0.9f))
            {
                Vector3 open = portalWay.outOpen;
                Vector3 feet;
                if (open.y > 0.7f)
                    feet = portalWay.outAt + Vector3.up * 0.1f;
                else if (open.y < -0.7f)
                    feet = portalWay.outAt + open * 2f;
                else
                {
                    Vector3 before = portalWay.outAt + Flat(open).normalized * 0.9f;
                    feet = Physics.Raycast(before, Vector3.down, out RaycastHit ground, 5f, ~0, QueryTriggerInteraction.Ignore) ? ground.point : before - Vector3.up;
                }
                Vector3 facing = Flat(open).sqrMagnitude > 0.01f ? Flat(open).normalized : Quaternion.Euler(0f, bodyYaw, 0f) * Vector3.forward;
                bodyYaw = Recorder.Yaw(facing);
                steer = facing;
                droveHas = false;
                droveHeadHas = false;
                rig.Teleport(feet, true);
                state += "; put out of the other one";
                return true;
            }
            return false;
        }

        private void GiveUpPortal(string why)
        {
            portalGoing = false;
            portalNotBefore = Time.time + 20f;
            nav.Clear();
            Lab.Event("portals: gave up (" + why + ")");
        }

        /// <summary>For the lab: through the nearest portal now.</summary>
        public void LabPortal()
        {
            float best = float.MaxValue;
            foreach (PortalWays.Way way in PortalWays.All)
            {
                float d = (way.inAt - Feet).magnitude;
                if (d < best && way.inOpen.y > -0.5f)
                {
                    best = d;
                    portalWay = way;
                }
            }
            Lab.Event("portals: " + PortalWays.All.Count + " ways about" + (best < float.MaxValue ? ", nearest " + best.ToString("0.0") + " m, opens " + portalWay.inOpen.ToString("0.00") : ""));
            if (best == float.MaxValue)
                return;
            portalGoing = true;
            portalSince = Time.time;
            portalPhase = 0;
            portalNotBefore = 0f;
            labPortal = true;
            if (mode != Mode.Follow)
                SetMode(Mode.Follow);
        }

        private bool labPortal;
    }
}
