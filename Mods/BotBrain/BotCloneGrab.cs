using BoneLib;
using Il2CppSLZ.Marrow;
using UnityEngine;

namespace ThirdEyeBonelabMod.Bots
{
    /// <summary>
    /// A clone taking hold of the player. Where the recording closes a hand on nothing (the
    /// player grabbing the air), and the player now stands where that hand closes, the clone
    /// has them: their body is drawn along with its hand for as long as the recording keeps the
    /// hand shut, and when it opens they are thrown off at the speed the hand was going. Neither
    /// hurts: only the clone's punches do (HurtPlayer).
    /// </summary>
    public partial class Bot
    {
        // The hand holding the player (-1: none), where they are from it, and since when.
        private int holdingPlayer = -1;
        private Vector3 heldOffset;
        private float playerHeldSince;
        private readonly List<Rigidbody> heldBodies = new List<Rigidbody>();
        private readonly float[] cloneGrip = new float[2];

        /// <summary>Per frame while it plays a clone: its hands closing on the player, and opening.</summary>
        private void GrabPlayerUpdate(Clone.Frame a, Clone.Frame b, float k)
        {
            Hand[] hands = { LeftHand, RightHand };
            float[] grip = { Mathf.Lerp(a.leftIn.grip, b.leftIn.grip, k), Mathf.Lerp(a.rightIn.grip, b.rightIn.grip, k) };
            for (int i = 0; i < 2; i++)
            {
                float was = cloneGrip[i];
                cloneGrip[i] = grip[i];
                Hand hand = hands[i];
                if (hand == null || hand.rb == null)
                    continue;
                if (holdingPlayer == i)
                {
                    if (grip[i] < Tuning.F("grab.open", 0.35f))
                        ThrowPlayer();
                    else if (Time.time - playerHeldSince > Tuning.F("grab.longest", 12f) || !PlayerStillNear(hand))
                        LetGoOfPlayer("slipped free");
                    continue;
                }
                float shut = Tuning.F("grab.shut", 0.6f);
                if (holdingPlayer < 0 && grip[i] >= shut && was < shut && hand.m_CurrentAttachedGO == null && PlayerWithin(hand))
                    TakePlayer(i, hand);
            }
        }

        // Some part of the player's body in reach of the hand.
        private static bool PlayerWithin(Hand hand)
        {
            RigManager player = Player.RigManager;
            if (player == null)
                return false;
            float reach = Tuning.F("grab.reach", 0.25f);
            foreach (Collider c in Physics.OverlapSphere(hand.transform.position, reach, ~0, QueryTriggerInteraction.Ignore))
            {
                if (c != null && c.GetComponentInParent<RigManager>() == player)
                    return true;
            }
            return false;
        }

        private static Rigidbody PlayerPelvis()
        {
            PhysicsRig body = Player.PhysicsRig;
            return body != null && body.m_pelvis != null ? body.m_pelvis.GetComponent<Rigidbody>() : null;
        }

        private bool PlayerStillNear(Hand hand)
        {
            Rigidbody pelvis = PlayerPelvis();
            if (pelvis == null)
                return false;
            Vector3 target = hand.transform.position + hand.transform.rotation * heldOffset;
            return (pelvis.position - target).magnitude < Tuning.F("grab.slip", 1.5f);
        }

        private void TakePlayer(int which, Hand hand)
        {
            Rigidbody pelvis = PlayerPelvis();
            if (pelvis == null || Player.PhysicsRig == null)
                return;
            holdingPlayer = which;
            playerHeldSince = Time.time;
            heldOffset = Vector3.ClampMagnitude(Quaternion.Inverse(hand.transform.rotation) * (pelvis.position - hand.transform.position), Tuning.F("grab.offset", 0.6f));
            heldBodies.Clear();
            foreach (Rigidbody body in Player.PhysicsRig.GetComponentsInChildren<Rigidbody>())
            {
                if (body != null && !body.isKinematic)
                    heldBodies.Add(body);
            }
            Lab.Event("clone: grabbed you (" + (which == 0 ? "left" : "right") + " hand)");
        }

        /// <summary>Each physics step: the player drawn along with the hand holding them.</summary>
        private void GrabPlayerFixed()
        {
            if (holdingPlayer < 0)
                return;
            Hand hand = holdingPlayer == 0 ? LeftHand : RightHand;
            Rigidbody pelvis = PlayerPelvis();
            if (hand == null || hand.rb == null || pelvis == null)
            {
                LetGoOfPlayer("lost hold");
                return;
            }
            Vector3 target = hand.transform.position + hand.transform.rotation * heldOffset;
            Vector3 want = hand.rb.velocity + (target - pelvis.position) * Tuning.F("grab.pull", 12f);
            Vector3 change = Vector3.ClampMagnitude(want - pelvis.velocity, Tuning.F("grab.step", 2.5f));
            // (The whole body together, so it is carried, not pulled apart.)
            foreach (Rigidbody body in heldBodies)
            {
                if (body != null)
                    body.AddForce(change, ForceMode.VelocityChange);
            }
        }

        // The hand opened: thrown off at the speed it was going.
        private void ThrowPlayer()
        {
            Hand hand = holdingPlayer == 0 ? LeftHand : RightHand;
            Vector3 throwAt = hand != null && hand.rb != null ? hand.rb.velocity * Tuning.F("grab.throw", 1.4f) : Vector3.zero;
            Rigidbody pelvis = PlayerPelvis();
            if (pelvis != null)
            {
                Vector3 change = throwAt - pelvis.velocity;
                foreach (Rigidbody body in heldBodies)
                {
                    if (body != null)
                        body.AddForce(change, ForceMode.VelocityChange);
                }
            }
            LetGoOfPlayer("threw you at " + throwAt.magnitude.ToString("0.0") + " m/s");
        }

        private void LetGoOfPlayer(string why)
        {
            if (holdingPlayer < 0)
                return;
            holdingPlayer = -1;
            heldBodies.Clear();
            Lab.Event("clone: let go of you (" + why + ")");
        }
    }
}
