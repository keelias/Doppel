using Il2CppSLZ.Marrow;
using UnityEngine;

namespace ThirdEyeBonelabMod.Bots
{
    /// <summary>
    /// A bot's health (a friend's as much as an enemy's). Its body is a player's, whose own health is kept "invincible" (a
    /// player body dying would do what a player's death does: the level reloading and the like),
    /// so what hits it (bullets, blows: Player_Health.OnReceivedDamage, and TAKEDAMAGE) is
    /// counted here instead. At nothing it drops as a ragdoll, lets go of what it holds, and is
    /// gone a few seconds later. Left alone a while, it mends. (How it shows it is hurt: BotHurt.)
    /// </summary>
    public partial class Bot
    {
        private float hp = -1f;
        public bool dead;
        private float diedAt;

        /// <summary>A hit on its body (damage as the game counts it for a player).</summary>
        public void TakeHit(float damage, Il2CppSLZ.Marrow.Combat.Attack attack = null, PlayerDamageReceiver.BodyPart part = PlayerDamageReceiver.BodyPart.Chest)
        {
            if (dead || damage <= 0f)
                return;
            string kind = "";
            if (attack != null)
            {
                // Not what it holds knocking against its own body, and no blow at all (a
                // "blunt" one: the game's for anything hitting it, the ground included) while
                // it lies on the ground or is just up, or has just taken or spawned a thing.
                // And a blow never takes more than a part of its health at once.
                kind = attack.attackType.ToString();
                bool blunt = kind.IndexOf("Blunt", StringComparison.OrdinalIgnoreCase) >= 0;
                if (OwnThingAt(attack.origin))
                    return;
                // (Nor any blow the game itself puts down to this very body: the blade in its own
                // hand, counted as stabbing it from wherever. A bot that took up a sword fell dead.)
                try
                {
                    if (attack.proxy != null && attack.proxy.GetComponentInParent<RigManager>() == rig)
                        return;
                }
                catch (Exception)
                {
                }
                // (Nor a thing lying about that it walked onto or into: a blade on the ground cut it as it stepped over it.)
                if (LyingThingAt(attack.origin))
                    return;
                if (blunt)
                {
                    // (Nor its own foot knocking on something as it walks, which the game counts a blow
                    // to it from nobody: now and then one walking along fell flat for no reason to be seen.)
                    try
                    {
                        Collider on = attack.collider;
                        Rigidbody hips = Pelvis();
                        if (attack.proxy == null && on != null && (on.name == "Feet" || on.name == "Knee") && on.GetComponentInParent<RigManager>() == rig
                            && (hips == null || hips.velocity.y > -Tuning.F("foot.knock.fall", 3f)))
                            return;
                    }
                    catch (Exception)
                    {
                    }
                    if (Down || Time.time - upAt < 1.5f || Time.time < Clone.SuppressUntil + 1f || Time.time - spawnAskedAt < 2f || Time.time - bluntAt < 0.35f)
                        return;
                    bluntAt = Time.time;
                    damage = Mathf.Min(damage, MaxHp * Tuning.F("bot.knock.most", 0.3f));
                }
            }
            // (A bullet: eleven of them to put a bot down was too many. The head, more again.)
            if (kind.IndexOf("Pierc", StringComparison.OrdinalIgnoreCase) >= 0)
                damage *= Tuning.F("bullet.hurt", 2.5f) * (part == PlayerDamageReceiver.BodyPart.Head ? Tuning.F("bullet.head", 2f) : 1f);
            Lessons.NoteHarm();
            if (hp < 0f)
                hp = MaxHp;
            hp -= damage * (enemy ? Tuning.F("enemy.hurt", 1f) : Tuning.F("bot.hurt", 1f));
            Lab.Event("[" + Name + "] " + (enemy ? "enemy bot" : "bot") + " hit" + (kind.Length > 0 ? " (" + kind + ")" : "") + ": " + damage.ToString("0.00") + ", " + Mathf.Max(hp, 0f).ToString("0.00") + " left" + HitBy(attack));
            if (hp <= 0f)
                Die();
            else
            {
                Hurt(damage, attack);
                TripByHit(damage, part, attack);
            }
        }

        /// <summary>
        /// Harm from being knocked into (the game's own, for a body hitting things): never from
        /// a thing a bot has just spawned (it comes into being in its hand, against its body),
        /// nor while it lies on the ground, and never the whole of its health at one knock.
        /// </summary>
        public void TakeKnock(float damage)
        {
            if (Time.time < Clone.SuppressUntil + 1f || Time.time - spawnAskedAt < 2f || Down)
                return;
            TakeHit(Mathf.Min(damage, MaxHp * Tuning.F("bot.knock.most", 0.3f)));
        }

        private float bluntAt = -10f;

        // For the lab: what a blow came from, as far as the game says.
        private string HitBy(Il2CppSLZ.Marrow.Combat.Attack attack)
        {
            try
            {
                if (attack == null)
                    return "";
                string said = "";
                Collider c = attack.collider;
                if (c != null)
                {
                    Il2CppSLZ.Marrow.Interaction.MarrowEntity e = c.GetComponentInParent<Il2CppSLZ.Marrow.Interaction.MarrowEntity>();
                    RigManager whose = c.GetComponentInParent<RigManager>();
                    said += "; collider " + c.name + " of " + (e != null ? e.name : c.transform.root.name) + (whose == rig ? " (its own body)" : whose != null ? " (another body)" : "");
                }
                if (attack.proxy != null)
                    said += "; from " + attack.proxy.transform.root.name + (attack.proxy.GetComponentInParent<RigManager>() == rig ? " (itself)" : "");
                float nearest = 99f;
                string nearestIs = "";
                foreach (Collider near in Physics.OverlapSphere(attack.origin, 1.2f, ~0, QueryTriggerInteraction.Ignore))
                {
                    Rigidbody rb = near != null ? near.attachedRigidbody : null;
                    if (rb == null || rb.GetComponentInParent<RigManager>() != null)
                        continue;
                    float d = (near.bounds.ClosestPoint(attack.origin) - attack.origin).magnitude;
                    if (d < nearest)
                    {
                        nearest = d;
                        Il2CppSLZ.Marrow.Interaction.MarrowEntity e = rb.GetComponentInParent<Il2CppSLZ.Marrow.Interaction.MarrowEntity>();
                        Transform whole = e != null ? e.transform : rb.transform;
                        nearestIs = whole.name + (HeldByAnyone(whole, null) ? " (held)" : " (loose, " + rb.velocity.magnitude.ToString("0.0") + " m/s)");
                    }
                }
                Transform head = Head;
                said += "; " + (head != null ? (attack.origin - head.position).magnitude.ToString("0.0") : "?") + " m from its head; nearest thing " + (nearestIs.Length > 0 ? nearestIs + " " + nearest.ToString("0.00") + " m" : "none")
                        + "; holding " + (RightHand != null && RightHand.m_CurrentAttachedGO != null ? RightHand.m_CurrentAttachedGO.transform.root.name : "nothing") + (item != null ? ", busy with " + item.name : "");
                return said;
            }
            catch (Exception e)
            {
                return "; from ? (" + e.Message + ")";
            }
        }

        // Whether what is at that place is a loose thing nobody holds and that is hardly moving (it came to the thing, not the thing to it).
        private bool LyingThingAt(Vector3 at)
        {
            try
            {
                foreach (Collider c in Physics.OverlapSphere(at, 0.2f, ~0, QueryTriggerInteraction.Ignore))
                {
                    Rigidbody rb = c != null ? c.attachedRigidbody : null;
                    if (rb == null || rb.isKinematic || rb.GetComponentInParent<RigManager>() != null)
                        continue;
                    if (rb.velocity.magnitude > Tuning.F("lying.speed", 1.5f))
                        return false;
                    Il2CppSLZ.Marrow.Interaction.MarrowEntity entity = rb.GetComponentInParent<Il2CppSLZ.Marrow.Interaction.MarrowEntity>();
                    Transform whole = entity != null ? entity.transform : rb.transform;
                    if (!HeldByAnyone(whole, null))
                        return true;
                }
            }
            catch (Exception)
            {
            }
            return false;
        }

        // Whether something in its own hands is at that place (what hit it there is its own).
        private bool OwnThingAt(Vector3 at)
        {
            // (Or the thing it is taking up, or has just put down: a bar it reached for cut it as it took hold of it.)
            try
            {
                if (item != null && (Playing || Time.time - playAt < 2f))
                {
                    Il2CppSLZ.Marrow.Interaction.MarrowEntity its = item.GetComponentInParent<Il2CppSLZ.Marrow.Interaction.MarrowEntity>();
                    Transform whole = its != null ? its.transform : item.transform;
                    foreach (Collider c in whole.GetComponentsInChildren<Collider>())
                    {
                        if (c != null && !c.isTrigger && (c.ClosestPoint(at) - at).magnitude < 0.4f)
                            return true;
                    }
                }
            }
            catch (Exception)
            {
            }
            foreach (Hand hand in new[] { LeftHand, RightHand })
            {
                GameObject held = hand != null ? hand.m_CurrentAttachedGO : null;
                if (held == null)
                    continue;
                Il2CppSLZ.Marrow.Interaction.MarrowEntity entity = held.GetComponentInParent<Il2CppSLZ.Marrow.Interaction.MarrowEntity>();
                Transform root = entity != null ? entity.transform : held.transform.root;
                if (root == rig.transform || root.GetComponent<RigManager>() != null)
                    continue;
                foreach (Collider c in root.GetComponentsInChildren<Collider>())
                {
                    if (c == null || c.isTrigger)
                        continue;
                    // (By the box about each part of it, with room to spare: a long blade's cut is
                    // counted from along its edge, and a bot trying out a sword stabbed itself dead.)
                    Bounds box = c.bounds;
                    box.Expand(Tuning.F("own.thing.room", 0.5f));
                    if (box.Contains(at) || (c.ClosestPoint(at) - at).magnitude < 0.3f)
                        return true;
                }
            }
            return false;
        }

        private float MaxHp => rig.health != null && rig.health.max_Health > 0f ? rig.health.max_Health : 10f;

        // Left alone a while, it mends (as the player does).
        private void Mend(float dt)
        {
            if (hp < 0f || hp >= MaxHp || Time.time - hurtAt < (enemy ? Tuning.F("enemy.mend.after", 30f) : Tuning.F("bot.mend.after", 8f)))
                return;
            hp = Mathf.Min(MaxHp, hp + MaxHp * Tuning.F("bot.mend", 0.1f) * dt);
        }

        private void Die()
        {
            dead = true;
            diedAt = Time.time;
            DropStowed();
            Voice(false, true);
            try
            {
                StopPlaying();
                ReleaseCloneHands();
                foreach (Hand hand in new[] { LeftHand, RightHand })
                {
                    if (hand != null && hand.m_CurrentAttachedGO != null)
                    {
                        Grip g = hand.m_CurrentAttachedGO.GetComponent<Grip>();
                        if (g != null)
                            LetGoOf(g, hand);
                    }
                }
                rig.physicsRig.RagdollRig();
            }
            catch (Exception e)
            {
                MelonLoader.MelonLogger.Warning("Bot Player: the enemy bot did not fall cleanly: " + e.Message);
            }
            Spawnables.Notify(enemy ? "Enemy bot down" : "Bot down");
            Lab.Event((enemy ? "enemy bot" : "bot") + " died");
        }

        /// <summary>While dead: nothing more, and gone after a while. True while it is dead.</summary>
        private bool Dead()
        {
            if (!dead)
                return false;
            if (Time.time - diedAt > Tuning.F("enemy.gone", 6f) * (enemy ? 1f : 2f))
                Remove();
            return true;
        }
    }
}
