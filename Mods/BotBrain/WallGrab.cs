using BoneLib;
using Il2CppSLZ.Marrow;
using UnityEngine;

namespace ThirdEyeBonelabMod.Bots
{
    /// <summary>
    /// Holding on to any surface. A hand that closes with nothing in it, its palm against
    /// something that does not move (a wall, the ground, a roof), has hold of it there: it
    /// stays where it took hold for as long as it is kept shut, and the body is drawn after
    /// it as the arm pulls, as on a ledge. So any wall is climbed hand over hand, with no
    /// tool. The player's hands, and the bots' (a bot closes its hand on a wall by asking,
    /// see Hold).
    /// </summary>
    public static class WallGrab
    {
        private class Held
        {
            public Hand hand;
            public ConfigurableJoint joint;
            public bool players;
        }

        private static readonly Dictionary<IntPtr, Held> held = new Dictionary<IntPtr, Held>();
        // When each of the player's hands closed (to give the game's own grab of a thing there the first go).
        private static readonly float[] closedAt = { -1f, -1f };

        public static bool Holding(Hand hand) => hand != null && held.ContainsKey(hand.Pointer);

        /// <summary>Once a frame: the player's hands taking hold of what they close on, and letting go.</summary>
        public static void Update()
        {
            if (Tuning.F("wallgrab", 1f) < 0.5f)
            {
                if (held.Count > 0)
                    DropAll();
                return;
            }
            try
            {
                Hand[] hands = { Player.LeftHand, Player.RightHand };
                OpenControllerRig controls = Player.ControllerRig;
                BaseController[] sticks = { controls != null ? controls.leftController : null, controls != null ? controls.rightController : null };
                for (int i = 0; i < 2; i++)
                {
                    Hand hand = hands[i];
                    if (hand == null || sticks[i] == null)
                        continue;
                    bool shut = sticks[i]._solvedGrip >= OpenController.grabThreshold;
                    if (!shut)
                    {
                        closedAt[i] = -1f;
                        Release(hand);
                        continue;
                    }
                    if (closedAt[i] < 0f)
                        closedAt[i] = Time.time;
                    // (A thing in the hand after all: that, not the wall.)
                    if (hand.m_CurrentAttachedGO != null)
                    {
                        Release(hand);
                        continue;
                    }
                    if (!Holding(hand) && Time.time - closedAt[i] > Tuning.F("wallgrab.wait", 0.08f) && Time.time - closedAt[i] < 0.6f)
                        Take(hand, true);
                }
                // (Bots' holds whose hand is gone.)
                List<IntPtr> gone = null;
                foreach (KeyValuePair<IntPtr, Held> h in held)
                {
                    if (h.Value.hand == null || h.Value.joint == null)
                        (gone ??= new List<IntPtr>()).Add(h.Key);
                }
                if (gone != null)
                {
                    foreach (IntPtr key in gone)
                        held.Remove(key);
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>A bot's hand: shut on the surface under its palm (true once it has hold), or let go.</summary>
        public static bool Hold(Hand hand, bool on)
        {
            if (hand == null)
                return false;
            if (!on)
            {
                Release(hand);
                return false;
            }
            return Holding(hand) || Take(hand, false);
        }

        // Something that does not move, under the palm.
        private static bool Take(Hand hand, bool players)
        {
            Rigidbody body = hand.rb;
            if (body == null)
                return false;
            Vector3 palm = hand.palmPositionTransform != null ? hand.palmPositionTransform.position : hand.transform.position;
            float reach = Tuning.F("wallgrab.reach", 0.1f);
            Vector3 at = palm;
            bool found = false;
            float nearest = float.MaxValue;
            foreach (Collider c in Physics.OverlapSphere(palm, reach, ~0, QueryTriggerInteraction.Ignore))
            {
                if (c == null || c.GetComponentInParent<RigManager>() != null)
                    continue;
                Rigidbody other = c.attachedRigidbody;
                if (other != null && !other.isKinematic)
                    continue;
                // (Not what has a grip of its own to be taken by: a ledge, a handle.)
                Vector3 p = palm;
                try
                {
                    MeshCollider mesh = c.TryCast<MeshCollider>();
                    if (mesh == null || mesh.convex)
                        p = c.ClosestPoint(palm);
                }
                catch (Exception)
                {
                }
                float d = (p - palm).magnitude;
                if (d < nearest)
                {
                    nearest = d;
                    at = p;
                    found = true;
                }
            }
            if (!found)
                return false;
            ConfigurableJoint joint = body.gameObject.AddComponent<ConfigurableJoint>();
            joint.autoConfigureConnectedAnchor = false;
            joint.anchor = body.transform.InverseTransformPoint(palm);
            joint.connectedAnchor = palm;
            joint.xMotion = joint.yMotion = joint.zMotion = ConfigurableJointMotion.Locked;
            joint.angularXMotion = joint.angularYMotion = joint.angularZMotion = ConfigurableJointMotion.Free;
            joint.breakForce = float.PositiveInfinity;
            joint.breakTorque = float.PositiveInfinity;
            held[hand.Pointer] = new Held { hand = hand, joint = joint, players = players };
            if (players)
                Lessons.OnWall(hand == Player.LeftHand ? 0 : 1, true);
            if (players && Time.time - saidAt > 5f)
            {
                saidAt = Time.time;
                Lab.Event("wall grab: you have hold of a surface");
            }
            return true;
        }

        private static float saidAt = -10f;

        private static void Release(Hand hand)
        {
            if (hand == null || !held.TryGetValue(hand.Pointer, out Held h))
                return;
            held.Remove(hand.Pointer);
            if (h.joint != null)
                UnityEngine.Object.Destroy(h.joint);
            if (h.players)
                Lessons.OnWall(hand == Player.LeftHand ? 0 : 1, false);
        }

        public static void DropAll()
        {
            foreach (Held h in held.Values)
            {
                if (h.joint != null)
                    UnityEngine.Object.Destroy(h.joint);
            }
            held.Clear();
        }
    }
}
