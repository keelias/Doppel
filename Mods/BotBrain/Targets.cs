using System.Reflection;
using BoneLib;
using Il2CppSLZ.Marrow;
using Il2CppSLZ.Marrow.AI;
using MelonLoader;
using UnityEngine;

namespace ThirdEyeBonelabMod.Bots
{
    /// <summary>
    /// What a bot may shoot at: the Zombies mod's zombies (asked of that mod, if it is there),
    /// the game's own NPCs, and, for practice when there are none, things lying about. Never
    /// the player, nor anything near them, nor another bot; except for an enemy bot, whose one
    /// target is the player.
    /// </summary>
    public static class Targets
    {
        public struct Target
        {
            /// <summary>The whole of it (what may be in the way of a shot at it is not "in the way").</summary>
            public Transform transform;
            /// <summary>What is aimed at (a zombie's head, an NPC's chest bone, a prop), and where on it.</summary>
            public Transform aim;
            public Vector3 offset;
            public bool hostile;
            public AIBrain brain;
            public Transform zombieHead;
            public Rigidbody prop;
            public Vector3 propWasAt;
            /// <summary>The player (an enemy bot's target).</summary>
            public bool player;
            /// <summary>A bot of the other side (an enemy bot to the player's bots, one of theirs to an enemy bot).</summary>
            public Bot bot;

            public Vector3 Point => aim != null ? aim.position + offset : transform != null ? transform.position : Vector3.zero;
        }

        // Props shot at lately, not to be picked again for a while.
        private static readonly Dictionary<IntPtr, float> shotProps = new Dictionary<IntPtr, float>();
        private const float PropRest = 25f;

        /// <summary>Whether it is still worth shooting: alive, or (a prop) not yet knocked away.</summary>
        public static bool Alive(Target t)
        {
            if (t.player)
                return Player.RigManager != null && Player.RigManager.gameObject.activeInHierarchy && t.aim != null;
            if (t.transform == null || t.aim == null || !t.transform.gameObject.activeInHierarchy)
                return false;
            if (t.bot != null)
                return t.bot.Alive && !t.bot.dead;
            if (t.zombieHead != null)
                return Zombies().Contains(t.zombieHead);
            if (t.brain != null)
                return !t.brain.isDead && t.brain.gameObject.activeInHierarchy;
            if (t.prop != null)
                return (t.prop.worldCenterOfMass - t.propWasAt).magnitude < 0.5f;
            return true;
        }

        /// <summary>A prop shot at: left alone for a while after.</summary>
        public static void Shot(Target t)
        {
            if (t.prop != null)
                shotProps[t.prop.Pointer] = Time.time;
        }

        /// <summary>Whether it practises on props when there is nothing hostile about.</summary>
        public static bool Practice = true;

        private static MethodInfo livingHeads;
        private static bool lookedForZombies;
        private static AIBrain[] brains = new AIBrain[0];
        private static float brainsAt = -10f;

        public static void ForgetLevel()
        {
            brains = new AIBrain[0];
            brainsAt = -10f;
            shotProps.Clear();
        }

        /// <summary>The player, as an enemy bot's target: their chest mostly, their head now and then.</summary>
        public static bool PlayerTarget(out Target t)
        {
            t = default;
            RigManager rig = Player.RigManager;
            Transform head = Player.Head;
            if (rig == null || head == null || !rig.gameObject.activeInHierarchy)
                return false;
            Vector3 offset = UnityEngine.Random.value < 0.3f ? Vector3.zero : -Vector3.up * 0.3f;
            t = new Target { transform = rig.transform, aim = head, offset = offset, hostile = true, player = true };
            return true;
        }

        /// <summary>The bots of the other side from this one's (living ones): they fight each other.</summary>
        public static List<Target> OtherSide(RigManager self)
        {
            List<Target> all = new List<Target>();
            bool mine = BotManager.IsEnemy(self);
            foreach (Bot other in BotManager.All)
            {
                if (other.rig == self || other.enemy == mine || other.dead || !other.Alive || other.Head == null)
                    continue;
                Vector3 offset = UnityEngine.Random.value < 0.3f ? Vector3.zero : -Vector3.up * 0.3f;
                all.Add(new Target { transform = other.rig.transform, aim = other.Head, offset = offset, hostile = true, bot = other });
            }
            return all;
        }

        /// <summary>Every hostile thing about (living zombies and NPCs), seen or not.</summary>
        public static List<Target> Hostiles()
        {
            List<Target> all = new List<Target>();
            foreach (Transform head in Zombies())
            {
                if (head != null)
                    all.Add(new Target { transform = head.root, aim = head, offset = -Vector3.up * 0.3f, hostile = true, zombieHead = head });
            }
            RefreshBrains();
            foreach (AIBrain brain in brains)
            {
                if (brain == null || brain.isDead || !brain.gameObject.activeInHierarchy)
                    continue;
                Transform chest = Chest(brain, out Vector3 offset);
                all.Add(new Target { transform = brain.transform, aim = chest, offset = offset, hostile = true, brain = brain });
            }
            return all;
        }

        private static void RefreshBrains()
        {
            if (Time.time - brainsAt <= 2f)
                return;
            brainsAt = Time.time;
            try
            {
                brains = UnityEngine.Object.FindObjectsOfType<AIBrain>();
            }
            catch (Exception)
            {
                brains = new AIBrain[0];
            }
        }

        /// <summary>The best thing in sight to shoot at from eye, within range; false if none.</summary>
        public static bool Find(Vector3 eye, float range, RigManager self, out Target found)
        {
            found = default;
            float best = float.MaxValue;
            // An enemy bot: the player and nothing else.
            if (BotManager.IsEnemy(self))
            {
                if (PlayerTarget(out Target you))
                    Consider(eye, range, self, you, ref best, ref found);
                foreach (Target theirs in OtherSide(self))
                    Consider(eye, range, self, theirs, ref best, ref found);
                return found.transform != null;
            }
            foreach (Target theirs in OtherSide(self))
                Consider(eye, range, self, theirs, ref best, ref found);
            foreach (Transform head in Zombies())
            {
                if (head == null)
                    continue;
                // The chest mostly, the head now and then.
                Vector3 offset = UnityEngine.Random.value < 0.4f ? Vector3.zero : -Vector3.up * 0.3f;
                Consider(eye, range, self, new Target { transform = head.root, aim = head, offset = offset, hostile = true, zombieHead = head }, ref best, ref found);
            }
            RefreshBrains();
            foreach (AIBrain brain in brains)
            {
                if (brain == null || brain.isDead || !brain.gameObject.activeInHierarchy)
                    continue;
                Transform chest = Chest(brain, out Vector3 offset);
                Consider(eye, range, self, new Target { transform = brain.transform, aim = chest, offset = offset, hostile = true, brain = brain }, ref best, ref found);
            }
            if (found.transform != null || !Practice)
                return found.transform != null;
            // (Nor is it target practice while there is something hostile about that it only cannot get a shot at.)
            foreach (Target hostile in Hostiles())
            {
                if ((hostile.Point - eye).magnitude < 35f)
                    return false;
            }

            // Nothing hostile: something lying about, not too near and not by the player.
            foreach (Collider c in Physics.OverlapSphere(eye, Mathf.Min(range, 22f), ~0, QueryTriggerInteraction.Ignore))
            {
                Rigidbody rb = c != null ? c.attachedRigidbody : null;
                if (rb == null || rb.isKinematic || rb.mass < 0.3f || rb.mass > 40f)
                    continue;
                if (rb.GetComponentInParent<RigManager>() != null || rb.GetComponentInParent<Gun>() != null)
                    continue;
                // (Not the litter of its own shooting: spent cases, loose rounds, dropped magazines.)
                string called = rb.name.ToLowerInvariant();
                if (called.Contains("cartridge") || called.Contains("case") || called.Contains("shell") || called.Contains("bullet") || called.StartsWith("mag"))
                    continue;
                // (Nor the bodies of the dead.)
                if (called.Contains("valvebiped") || rb.transform.root.name.ToLowerInvariant().Contains("zombie"))
                    continue;
                Vector3 point = rb.worldCenterOfMass;
                if ((point - eye).magnitude < 4f)
                    continue;
                if (shotProps.TryGetValue(rb.Pointer, out float shot) && Time.time - shot < PropRest)
                    continue;
                // (Among props, any one will do: a little chance, so it moves from one to the next.)
                float before = best;
                Consider(eye, range, self, new Target { transform = rb.transform, aim = rb.transform, offset = rb.transform.InverseTransformPoint(point), prop = rb, propWasAt = point }, ref best, ref found);
                if (best < before)
                    best += UnityEngine.Random.Range(0f, 8f);
            }
            return found.transform != null;
        }

        private static void Consider(Vector3 eye, float range, RigManager self, Target t, ref float best, ref Target found)
        {
            if (t.aim == null)
                return;
            // (A prop's offset is in its own frame: it turns with it.)
            if (t.prop != null)
                t.offset = t.aim.TransformPoint(t.offset) - t.aim.position;
            Vector3 point = t.Point;
            float far = (point - eye).magnitude;
            if (far > range || far < 1f)
                return;
            // (Something hostile right by the player, on them: shot at all the same from close to,
            // with the player well out of the line of the shot. Left alone, as it was, a bot stood
            // and shot at spent cases on the ground while the player was being eaten.)
            if (!t.player && NearPlayer(point, t.hostile ? 1.2f : 2.5f))
            {
                if (!t.hostile || !ClearOfPlayer(eye, point))
                {
                    // (Kept: something hostile is on the player, and whoever was looking has no shot at it.)
                    if (t.hostile && far < 20f && self != null)
                    {
                        OnPlayerFor = self.Pointer;
                        OnPlayerAt = Time.time;
                    }
                    return;
                }
            }
            if (!InSight(eye, point, t.transform, self))
                return;
            // Hostile things first, then the nearest.
            float score = far + (t.hostile ? 0f : 100f);
            if (score < best)
            {
                best = score;
                found = t;
            }
        }

        /// <summary>The body that last looked for something to shoot and found only what it could not shoot for the player being in the way, and when.</summary>
        public static IntPtr OnPlayerFor;
        public static float OnPlayerAt = -10f;

        /// <summary>Whether a shot from there at that point, by the player, is one that may be taken: from near, the thing a little apart from them, and they well clear of the line of it.</summary>
        public static bool ClearOfPlayer(Vector3 from, Vector3 point)
        {
            Vector3 to = point - from;
            if (to.magnitude > Tuning.F("fire.byplayer.far", 9f) || NearPlayer(point, Tuning.F("fire.byplayer.apart", 0.35f)))
                return false;
            return !PlayerInLine(from, to.normalized, Tuning.F("fire.byplayer.clear", 0.5f));
        }

        /// <summary>Whether the point is close to the player (to anywhere on them).</summary>
        public static bool NearPlayer(Vector3 point, float within)
        {
            Transform head = Player.Head;
            if (head == null)
                return false;
            Vector3 top = head.position;
            Vector3 bottom = top - Vector3.up * 1.6f;
            return Distance(point, bottom, top) < within;
        }

        /// <summary>Whether a shot from muzzle along dir would pass near the player.</summary>
        public static bool PlayerInLine(Vector3 muzzle, Vector3 dir, float within)
        {
            Transform head = Player.Head;
            if (head == null)
                return false;
            for (float drop = 0f; drop <= 1.6f; drop += 0.4f)
            {
                Vector3 p = head.position - Vector3.up * drop;
                Vector3 to = p - muzzle;
                float along = Vector3.Dot(to, dir);
                if (along > -0.3f && (to - dir * along).magnitude < within + along * 0.02f)
                    return true;
            }
            return false;
        }

        private static float Distance(Vector3 p, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / ab.sqrMagnitude);
            return (a + ab * t - p).magnitude;
        }

        // Nothing but the thing itself (or the bot's own body) between the eye and the point.
        public static bool InSight(Vector3 eye, Vector3 point, Transform what, RigManager self)
        {
            Vector3 to = point - eye;
            RaycastHit[] hits = Physics.RaycastAll(eye, to.normalized, to.magnitude, ~0, QueryTriggerInteraction.Ignore);
            foreach (RaycastHit hit in hits)
            {
                if (hit.collider == null)
                    continue;
                Transform t = hit.collider.transform;
                if (what != null && (t.IsChildOf(what) || (hit.collider.attachedRigidbody != null && hit.collider.attachedRigidbody.transform.IsChildOf(what))))
                    continue;
                if (self != null && t.IsChildOf(self.transform))
                    continue;
                // Held by the bot (its gun): not in the way.
                if (hit.collider.attachedRigidbody != null && self != null && Held(hit.collider.attachedRigidbody, self))
                    continue;
                if ((hit.point - point).magnitude < 0.25f)
                    continue;
                LastBlocker = hit.collider.name + " of " + hit.collider.transform.root.name + ", " + (hit.point - point).magnitude.ToString("0.0") + " m short of it";
                return false;
            }
            return true;
        }

        /// <summary>What was in the way, the last time something was found not to be in sight (for finding out why).</summary>
        public static string LastBlocker = "";

        private static bool Held(Rigidbody rb, RigManager self)
        {
            foreach (Hand hand in new[] { self.physicsRig.leftHand, self.physicsRig.rightHand })
            {
                GameObject held = hand != null ? hand.m_CurrentAttachedGO : null;
                if (held != null && held.transform.root == rb.transform.root)
                    return true;
            }
            return false;
        }

        // What to aim at on an NPC: its chest bone, or a little above where it stands.
        private static Transform Chest(AIBrain brain, out Vector3 offset)
        {
            offset = Vector3.zero;
            Animator animator = brain.GetComponentInChildren<Animator>();
            if (animator != null && animator.isHuman)
            {
                Transform chest = animator.GetBoneTransform(HumanBodyBones.Chest);
                if (chest != null)
                    return chest;
            }
            offset = Vector3.up * 1.1f;
            return brain.transform;
        }

        // The Zombies mod's zombies still standing, if that mod is there (asked of it by name).
        private static List<Transform> Zombies()
        {
            if (!lookedForZombies)
            {
                lookedForZombies = true;
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type manager = assembly.GetType("ThirdEyeBonelabMod.Zombies.ZombieManager", false);
                    MethodInfo method = manager != null ? manager.GetMethod("LivingHeads", BindingFlags.Public | BindingFlags.Static) : null;
                    if (method != null)
                    {
                        livingHeads = method;
                        MelonLogger.Msg("Bot Player: found the Zombies mod");
                        break;
                    }
                }
            }
            if (livingHeads == null)
                return new List<Transform>();
            try
            {
                return livingHeads.Invoke(null, null) as List<Transform> ?? new List<Transform>();
            }
            catch (Exception)
            {
                return new List<Transform>();
            }
        }
    }
}
