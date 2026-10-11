using BoneLib;
using Il2CppSLZ.Marrow;
using UnityEngine;
using UnityEngine.AI;

namespace ThirdEyeBonelabMod.Bots
{
    /// <summary>
    /// The thinking a fight takes: how dangerous each enemy is (near, and coming on fast), where
    /// to find cover from them, and where the player is aiming (so a bot stays out of the way).
    /// </summary>
    public static class Tactics
    {
        // Where each enemy was, to see how it moves.
        private static readonly Dictionary<IntPtr, (Vector3 at, float time, Vector3 velocity)> tracks = new Dictionary<IntPtr, (Vector3, float, Vector3)>();

        public static void ForgetLevel()
        {
            tracks.Clear();
        }

        /// <summary>How fast an enemy is moving (as seen over the last moments).</summary>
        public static Vector3 Velocity(Targets.Target t)
        {
            if (t.aim == null)
                return Vector3.zero;
            IntPtr key = t.aim.Pointer;
            Vector3 now = t.Point;
            if (tracks.TryGetValue(key, out var track))
            {
                float dt = Time.time - track.time;
                if (dt < 0.05f)
                    return track.velocity;
                Vector3 v = dt < 1.5f ? Vector3.Lerp(track.velocity, (now - track.at) / dt, 0.5f) : Vector3.zero;
                tracks[key] = (now, Time.time, v);
                return v;
            }
            tracks[key] = (now, Time.time, Vector3.zero);
            return Vector3.zero;
        }

        /// <summary>
        /// How dangerous an enemy is to whoever stands at from: much more so near, more so
        /// coming on, and more so if it is what the player is shooting at (so they fight together).
        /// </summary>
        public static float Danger(Targets.Target t, Vector3 from)
        {
            Vector3 to = t.Point - from;
            float far = to.magnitude;
            Vector3 v = Velocity(t);
            float closing = far > 0.01f ? Mathf.Max(0f, -Vector3.Dot(v, to / far)) : 0f;
            float danger = 10f / (far + 1f) + closing * 1.5f;
            if (Time.time - PlayerWatch.FiredAt < 2.5f && (PlayerWatch.ShotAt - t.Point).magnitude < 2.5f)
                danger += 3f;
            return danger;
        }

        /// <summary>
        /// Somewhere near at hand out of sight of all these enemies (something solid between
        /// them and it), on ground it can walk to; nearer is better. False if none.
        /// </summary>
        public static bool FindCover(Vector3 feet, List<Vector3> enemies, out Vector3 cover)
        {
            cover = feet;
            if (enemies.Count == 0)
                return false;
            float best = float.MaxValue;
            for (int i = 0; i < 24; i++)
            {
                float angle = i * 15f + UnityEngine.Random.Range(-6f, 6f);
                float reach = UnityEngine.Random.Range(2.5f, 10f);
                Vector3 p = feet + Quaternion.Euler(0f, angle, 0f) * Vector3.forward * reach;
                try
                {
                    if (!NavMesh.SamplePosition(p, out NavMeshHit hit, 1.5f, NavMesh.AllAreas))
                        continue;
                    p = hit.position;
                }
                catch (Exception)
                {
                    if (!Physics.Raycast(p + Vector3.up * 1.5f, Vector3.down, out RaycastHit ground, 3f, ~0, QueryTriggerInteraction.Ignore))
                        continue;
                    p = ground.point;
                }
                // Hidden (chest high) from every enemy, by something not far from it.
                Vector3 chest = p + Vector3.up * 1.2f;
                bool hidden = true;
                foreach (Vector3 enemy in enemies)
                {
                    // (Traced from the spot out, so the first thing met is what is beside it, not the enemy.)
                    if (!Physics.Linecast(chest, enemy, out RaycastHit block, ~0, QueryTriggerInteraction.Ignore)
                        || (block.point - chest).magnitude > 3f || (block.rigidbody != null && !block.rigidbody.isKinematic))
                    {
                        hidden = false;
                        break;
                    }
                }
                if (!hidden)
                    continue;
                // Nearer is better, and not past the enemies.
                float cost = (p - feet).magnitude;
                foreach (Vector3 enemy in enemies)
                    cost += Mathf.Max(0f, 6f - (enemy - p).magnitude) * 2f;
                if (cost < best)
                {
                    best = cost;
                    cover = p;
                }
            }
            return best < float.MaxValue;
        }

        /// <summary>
        /// Whether a point is in the player's line of fire (just ahead of the gun they hold), and
        /// which way, across the line, is out of it.
        /// </summary>
        public static bool InPlayersLine(Vector3 point, out Vector3 outOfIt)
        {
            outOfIt = Vector3.zero;
            Gun gun = Player.GetComponentInHand<Gun>(Player.RightHand);
            if (gun == null)
                gun = Player.GetComponentInHand<Gun>(Player.LeftHand);
            Transform muzzle = gun != null ? gun.firePointTransform : null;
            if (muzzle == null)
                return false;
            Vector3 to = point - muzzle.position;
            float along = Vector3.Dot(to, muzzle.forward);
            if (along < 0.3f || along > 40f)
                return false;
            Vector3 across = to - muzzle.forward * along;
            across.y = 0f;
            if (across.magnitude > 0.8f + along * 0.02f)
                return false;
            outOfIt = across.sqrMagnitude > 0.0001f ? across.normalized : Vector3.Cross(Vector3.up, muzzle.forward).normalized;
            return true;
        }

        /// <summary>Whether there is room to step back (nothing solid just behind).</summary>
        public static bool RoomBehind(Vector3 chest, Vector3 back)
        {
            return !Physics.SphereCast(chest, 0.2f, back, out RaycastHit _, 1.2f, ~0, QueryTriggerInteraction.Ignore);
        }
    }
}
