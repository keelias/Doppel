using Il2CppSLZ.Marrow;
using UnityEngine;

namespace ThirdEyeBonelabMod.Bots
{
    /// <summary>
    /// Putting its weapon away, and getting it out again. When it has nothing to fight (it has
    /// lost whoever it was after, or lost interest), and whenever it needs its hands (to climb,
    /// to drive), the gun or the thing it hits with goes away on its body: a short gun on its
    /// hip, a long gun or a blade or a bat across its back. It stays there, carried with it,
    /// till there is something to fight again, when it is drawn and used as before.
    /// </summary>
    public partial class Bot
    {
        private Rigidbody stowed;
        private Gun stowedGun;
        private Grip stowedGrip;
        private Transform stowOn;
        private Vector3 stowAt;
        private Quaternion stowTurn;
        private bool stowedKinematic;
        private readonly List<Collider> stowedOff = new List<Collider>();
        private float stowedSince, drawnAt = -10f;

        /// <summary>A weapon put away on its body.</summary>
        public bool HasStowed => stowed != null && stowed.gameObject.activeInHierarchy;

        /// <summary>A weapon in its hand, of either kind.</summary>
        private bool WeaponInHand => Holding() && ((gun != null && play == Play.Shoot) || play == Play.Melee);

        /// <summary>Puts away the weapon in its hand. False if it has none (or one put away already).</summary>
        private bool Stow()
        {
            try
            {
                if (!WeaponInHand || HasStowed || item == null || grip == null || rig.physicsRig == null)
                    return false;
                Rigidbody body = item;
                Gun wasGun = play == Play.Shoot ? gun : null;
                Grip wasGrip = grip;
                // Which way it points (a gun its barrel, anything else from its handle to its heavy end), and how long it is.
                Vector3 points = wasGun != null && wasGun.firePointTransform != null ? wasGun.firePointTransform.forward : (body.worldCenterOfMass - wasGrip.transform.position).normalized;
                float length = 0.3f;
                foreach (Collider c in body.GetComponentsInChildren<Collider>())
                {
                    if (c != null && !c.isTrigger)
                        length = Mathf.Max(length, c.bounds.size.magnitude);
                }
                // (A pistol on its hip; anything longer, and anything it hits with, across its back.)
                bool onHip = wasGun != null && GunMoves.KindOf(wasGun) == "pistol";
                Transform on = onHip ? rig.physicsRig.m_pelvis : rig.physicsRig.m_chest;
                if (on == null)
                    on = rig.physicsRig.m_pelvis;
                if (on == null)
                    return false;
                // Let go of it (as a play ends, but the thing is kept).
                LetGoOfSupport();
                Pull(false);
                if (Reloading)
                {
                    DropNewMag();
                    reload = Reload.None;
                    learnedReload = null;
                }
                Release();
                foreach (Hand hand in new[] { LeftHand, RightHand })
                {
                    if (hand != null && hand.m_CurrentAttachedGO != null && hand.m_CurrentAttachedGO.transform.IsChildOf(body.transform.root) && body.transform.root != rig.transform)
                        FreeHand(hand);
                }
                // On its hip, muzzle down; or across its back, pointing down to one side.
                Vector3 down = onHip ? -on.up : (-on.up + on.right * 0.45f).normalized;
                Quaternion turn = Quaternion.FromToRotation(points, down) * body.rotation;
                Vector3 centre = body.worldCenterOfMass - body.position;
                Vector3 place = onHip ? on.TransformPoint(new Vector3(0.21f, -0.02f, 0.0f)) : on.TransformPoint(new Vector3(0.0f, 0.02f, -0.19f));
                // (Its middle there, not its origin.)
                Vector3 at = place - (Quaternion.FromToRotation(points, down) * centre);
                stowedOff.Clear();
                foreach (Collider c in body.GetComponentsInChildren<Collider>())
                {
                    if (c != null && c.enabled && !c.isTrigger)
                    {
                        c.enabled = false;
                        stowedOff.Add(c);
                    }
                }
                stowedKinematic = body.isKinematic;
                body.isKinematic = true;
                stowOn = on;
                stowAt = on.InverseTransformPoint(at);
                stowTurn = Quaternion.Inverse(on.rotation) * turn;
                stowed = body;
                stowedGun = wasGun;
                stowedGrip = wasGrip;
                stowedSince = Time.time;
                gun = null;
                support = null;
                item = null;
                grip = null;
                play = Play.None;
                handOverride = false;
                leftOverride = false;
                hitClip = null;
                Lab.Event("holster: the " + body.name + " put away " + (onHip ? "on its hip" : "across its back"));
                StowFollow();
                return true;
            }
            catch (Exception e)
            {
                Lab.Event("holster: could not put it away: " + e.Message);
                return false;
            }
        }

        /// <summary>Each frame and each physics step: what it has put away, carried where it was put.</summary>
        public void StowFollow()
        {
            if (stowed == null)
                return;
            try
            {
                if (!stowed.gameObject.activeInHierarchy || stowOn == null)
                {
                    stowed = null;
                    return;
                }
                Vector3 at = stowOn.TransformPoint(stowAt);
                Quaternion turn = stowOn.rotation * stowTurn;
                if (Bad(at) || Bad(turn))
                    return;
                stowed.position = at;
                stowed.rotation = turn;
                stowed.transform.SetPositionAndRotation(at, turn);
            }
            catch (Exception)
            {
                stowed = null;
            }
        }

        // What it had put away, a thing again (its colliders on, falling as things fall); where it is, unless it is to be in a hand.
        private Rigidbody Unstow()
        {
            Rigidbody body = stowed;
            stowed = null;
            if (body == null)
                return null;
            try
            {
                foreach (Collider c in stowedOff)
                {
                    if (c != null)
                        c.enabled = true;
                }
                stowedOff.Clear();
                body.isKinematic = stowedKinematic;
                body.velocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
            }
            catch (Exception)
            {
            }
            return body;
        }

        /// <summary>Lets what it has put away fall where it is (dying, being taken apart, a new build taking over).</summary>
        public void DropStowed()
        {
            if (stowed != null)
            {
                Unstow();
                stowedGun = null;
                stowedGrip = null;
            }
        }

        /// <summary>For a new build taking over: what it has put away, handed on as it is (still on its body).</summary>
        public object[] KeepStowed()
        {
            if (!HasStowed)
                return null;
            object[] kept = { stowed, stowedGun, stowedGrip, stowOn, stowAt, stowTurn, stowedKinematic, stowedOff.ToArray() };
            stowed = null;
            stowedGun = null;
            stowedGrip = null;
            return kept;
        }

        /// <summary>What the build before had put away on this body: its own now.</summary>
        public void TakeStowed(object[] kept)
        {
            try
            {
                if (kept == null || kept.Length < 8 || kept[0] == null)
                    return;
                stowed = kept[0] as Rigidbody;
                stowedGun = kept[1] as Gun;
                stowedGrip = kept[2] as Grip;
                stowOn = kept[3] as Transform;
                stowAt = (Vector3)kept[4];
                stowTurn = (Quaternion)kept[5];
                stowedKinematic = (bool)kept[6];
                stowedOff.Clear();
                if (kept[7] is Collider[] off)
                    stowedOff.AddRange(off);
                stowedSince = Time.time;
                if (stowed == null || stowOn == null || stowedGrip == null)
                    stowed = null;
            }
            catch (Exception)
            {
                stowed = null;
            }
        }

        /// <summary>Gets its weapon out and into its hand, to use. False if it has none put away (or its hand is full).</summary>
        private bool Draw()
        {
            try
            {
                Hand hand = RightHand;
                if (!HasStowed || hand == null || stowedGrip == null)
                    return false;
                if (hand.m_CurrentAttachedGO != null)
                    FreeHand(hand);
                Gun wasGun = stowedGun;
                Grip wasGrip = stowedGrip;
                Rigidbody body = Unstow();
                stowedGun = null;
                stowedGrip = null;
                if (body == null)
                    return false;
                // Into its hand, by the grip it had it by.
                Vector3 by = wasGrip.transform.position - body.position;
                body.position = hand.transform.position - by;
                body.transform.position = hand.transform.position - by;
                drawnAt = Time.time;
                if (wasGun != null)
                {
                    Take(wasGun);
                    if (!Grab(hand, grip))
                    {
                        Lab.Event("holster: could not take the " + body.name + " in hand again");
                        EndPlay();
                        return false;
                    }
                    StartShooting();
                }
                else
                {
                    item = body;
                    grip = wasGrip;
                    gun = null;
                    support = null;
                    Quaternion? turned = null;
                    Vector3 way = body.worldCenterOfMass - wasGrip.transform.position;
                    if (way.sqrMagnitude > 0.0004f)
                        turned = Quaternion.FromToRotation(hand.transform.rotation * Vector3.back, way.normalized) * hand.transform.rotation;
                    if (!Grab(hand, grip, turned))
                    {
                        Lab.Event("holster: could not take the " + body.name + " in hand again");
                        item = null;
                        grip = null;
                        return false;
                    }
                    MeasureMelee();
                    Next(Play.Melee);
                }
                Lab.Event("holster: the " + body.name + " drawn");
                return true;
            }
            catch (Exception e)
            {
                Lab.Event("holster: could not draw: " + e.Message);
                return false;
            }
        }

        // ---------------------------------------------------------------- kept awake

        private Il2CppSLZ.Marrow.Interaction.MarrowEntity[] ownEntities;
        private float awakeAt;
        private int wakings;

        /// <summary>
        /// A level is in parts, and the game puts to sleep whatever is in a part the player is not
        /// in or next to: a bot there stopped dead (its body no longer worked by the game, though
        /// its mind went on and pushed its stick), or sat in its kart with its hands unmoving.
        /// A bot's body is not to be put to sleep so, and is woken if it has been.
        /// </summary>
        private void KeepAwake()
        {
            if (Time.time < awakeAt)
                return;
            awakeAt = Time.time + 1f;
            try
            {
                if (ownEntities == null)
                    ownEntities = rig.GetComponentsInChildren<Il2CppSLZ.Marrow.Interaction.MarrowEntity>(true);
                foreach (Il2CppSLZ.Marrow.Interaction.MarrowEntity e in ownEntities)
                {
                    if (e == null)
                        continue;
                    e.PreventDisableOnCull(true);
                    if (e.IsCulled || e.IsHibernating)
                    {
                        wakings++;
                        if (wakings <= 3 || wakings % 20 == 0)
                            Lab.Event("awake: " + e.name + " had been put to sleep by the level (" + (e.IsCulled ? "culled" : "hibernating") + "), woken (" + wakings + ")");
                        try
                        {
                            // (Its being culled taken back, as the level itself would on the player coming near.)
                            if (e.IsCulled && e._inactiveStatus != null)
                                e._inactiveStatus.Cull(false);
                            e.Hibernate(false, (Il2CppSLZ.Marrow.Interaction.MarrowEntity.HibernationSources)(-1));
                        }
                        catch (Exception)
                        {
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        private string AwakeSaid()
        {
            try
            {
                if (ownEntities == null)
                    return "entities ?";
                int culled = 0, asleep = 0;
                string which = "";
                foreach (Il2CppSLZ.Marrow.Interaction.MarrowEntity e in ownEntities)
                {
                    if (e == null)
                        continue;
                    if (e.IsCulled)
                    {
                        culled++;
                        which += " " + e.name;
                    }
                    if (e.IsHibernating)
                        asleep++;
                }
                return ownEntities.Length + " part(s), " + culled + " culled" + (which.Length > 0 ? " (" + which.Trim() + ")" : "") + ", " + asleep + " asleep";
            }
            catch (Exception)
            {
                return "entities ?";
            }
        }

        /// <summary>For the lab: what it is at and what it makes of what is about it, in a line.</summary>
        public string LabStatus()
        {
            Transform head = Head;
            float nearest = 999f;
            bool sight = false;
            string nearestIs = "";
            try
            {
                foreach (Targets.Target t in Targets.Hostiles())
                {
                    float d = head != null ? (t.Point - head.position).magnitude : 999f;
                    if (d < nearest)
                    {
                        nearest = d;
                        sight = head != null && Targets.InSight(head.position, t.Point, t.transform, rig);
                        nearestIs = (t.transform != null ? t.transform.name : "?") + (t.brain != null ? " (an NPC" + (t.brain.isDead ? ", dead" : "") + ")" : t.zombieHead != null ? " (a zombie)" : "")
                                    + ", its point " + (t.Point.y - Feet.y).ToString("0.0") + " m above its feet's level";
                    }
                }
            }
            catch (Exception)
            {
            }
            return Name + " " + mode + "/" + goal + "/" + play + (act != Act.Idle ? "/" + act : "") + ", health " + (hp < 0f ? "full" : hp.ToString("0.0"))
                   + ", nearest hostile " + (nearest > 900f ? "none" : nearest.ToString("0.0") + " m" + (sight ? " in sight" : " NOT in sight (behind " + Targets.LastBlocker + ")")) + " [" + nearestIs + "]" + ", sees " + (nearestSeen.transform != null ? nearestSeenDistance.ToString("0.0") + " m" : "none")
                   + ", " + AwakeSaid() + (WeaponInHand ? ", weapon in hand" : HasStowed ? ", weapon put away" : ", unarmed") + (climbing ? ", climbing" : "") + (InLesson ? ", in a lesson" : "") + (down ? ", down" : "");
        }

        /// <summary>For the lab: puts its weapon away, or gets it out.</summary>
        public void LabHolster()
        {
            if (HasStowed)
                Draw();
            else if (!Stow())
                Lab.Event("holster: nothing in its hand to put away");
        }

        // ---------------------------------------------------------------- not on top of one another

        /// <summary>
        /// The way it means to walk, turned off a little from the other bots close by, and how
        /// much slower to go for one just ahead going the same way: bots after the same thing
        /// took the same line, walked into one another and stuck there.
        /// </summary>
        private Vector3 KeepApart(Vector3 dir, ref float speed)
        {
            try
            {
                Transform head = Head;
                if (head == null || dir.sqrMagnitude < 0.0001f)
                    return dir;
                float room = Tuning.F("apart.room", 1.4f);
                Vector3 push = Vector3.zero;
                float slow = 1f;
                foreach (Bot other in BotManager.All)
                {
                    if (other == this || other.dead || other.Head == null)
                        continue;
                    Vector3 from = Flat(head.position - other.Head.position);
                    float d = from.magnitude;
                    if (d > room || Mathf.Abs(head.position.y - other.Head.position.y) > 1.5f)
                        continue;
                    Vector3 away = d > 0.05f ? from / d : Quaternion.Euler(0f, (rig.Pointer.ToInt64() % 360), 0f) * Vector3.forward;
                    push += away * (1f - d / room);
                    // (One right in front, on its way: behind it, not through it. The one behind gives way: the one with the lower name.)
                    if (Vector3.Dot(-away, dir) > 0.6f && d < room * 0.8f && rig.Pointer.ToInt64() < other.rig.Pointer.ToInt64())
                        slow = Mathf.Min(slow, Mathf.Clamp01(d / room - 0.2f));
                }
                if (push.sqrMagnitude < 0.0001f)
                    return dir;
                speed *= Mathf.Max(0.15f, slow);
                Vector3 turned = dir + push * Tuning.F("apart.push", 0.8f);
                return turned.sqrMagnitude > 0.0001f ? Flat(turned).normalized : dir;
            }
            catch (Exception)
            {
                return dir;
            }
        }

        // ---------------------------------------------------------------- never thrown about

        private float fastSaidAt = -10f;

        /// <summary>
        /// Each physics step: on its feet and going faster than anything on its feet goes (thrown
        /// by something: two bodies in one place, a thing appearing inside it), it is held back
        /// to a speed a body can have, and what it was doing is written down to find out why.
        /// </summary>
        private readonly HashSet<IntPtr> noPushDone = new HashSet<IntPtr>();

        /// <summary>
        /// A thing in its hands does not push on its own body. Held in two hands and aimed
        /// steeply, its rifle was pressed into its own chest by its arms, and its body, pushing
        /// back on what it was itself holding, went up into the air: sixteen metres in three seconds.
        /// </summary>
        private void NoPushOnItself(Transform thing)
        {
            try
            {
                Il2CppSLZ.Marrow.Interaction.MarrowEntity entity = thing.GetComponentInParent<Il2CppSLZ.Marrow.Interaction.MarrowEntity>();
                Transform whole = entity != null ? entity.transform : thing;
                if (!noPushDone.Add(whole.Pointer))
                    return;
                Collider[] its = whole.GetComponentsInChildren<Collider>(true);
                Collider[] mine = rig.physicsRig.GetComponentsInChildren<Collider>(true);
                foreach (Collider a in its)
                {
                    if (a == null || a.isTrigger)
                        continue;
                    foreach (Collider c in mine)
                    {
                        if (c != null && !c.isTrigger)
                            Physics.IgnoreCollision(a, c, true);
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        private float risingFor;

        // Going up and up with nothing to take it up (no jump just made, no climb): stopped.
        private void RiseGuard(Rigidbody hips)
        {
            Vector3 v = hips.velocity;
            bool lifted = v.y > Tuning.F("guard.rise", 3.2f) && !climbing && !InLesson && !lessonGliding && Time.time - jumpedAt > 0.9f;
            risingFor = lifted ? risingFor + Time.fixedDeltaTime : 0f;
            if (risingFor < Tuning.F("guard.rise.for", 0.3f))
                return;
            if (Time.time - fastSaidAt > 1f)
            {
                fastSaidAt = Time.time;
                Lab.Event("guard: going up at " + v.y.ToString("0.0") + " m/s with nothing taking it up, in " + mode + "/" + goal + "/" + play + (WeaponInHand ? ", weapon in hand" : "") + ": stopped");
            }
            foreach (Rigidbody b in rig.physicsRig.GetComponentsInChildren<Rigidbody>())
            {
                if (b == null || b.isKinematic)
                    continue;
                Vector3 bv = b.velocity;
                b.velocity = new Vector3(bv.x, Mathf.Min(bv.y, 0f), bv.z);
            }
            // (And its front hand off what it holds: most often it is that, pressed against itself.)
            if (risingFor > 0.6f)
            {
                FreeHand(LeftHand);
                risingFor = 0f;
            }
        }

        public void SpeedGuard()
        {
            try
            {
                if (dead || down || rig.physicsRig == null || rig.activeSeat != null)
                    return;
                Rigidbody hips = Pelvis();
                if (hips == null)
                    return;
                RiseGuard(hips);
                Vector3 v = hips.velocity;
                float flat = Flat(v).magnitude;
                float most = Tuning.F("guard.speed", 9.5f);
                if (flat <= most && v.y <= Tuning.F("guard.up", 9f))
                    return;
                if (Time.time - fastSaidAt > 1f)
                {
                    fastSaidAt = Time.time;
                    Lab.Event("guard: going " + flat.ToString("0.0") + " m/s (up " + v.y.ToString("0.0") + ") in " + mode + "/" + goal + "/" + play + (climbing ? ", climbing" : "") + (climbHang ? ", hanging" : "")
                              + (InLesson ? ", in a lesson" : "") + (lessonGliding ? ", carried up" : "") + (HasStowed ? ", weapon put away" : "") + (running ? ", running" : "")
                              + ", legs' most " + (rig.remapHeptaRig != null ? rig.remapHeptaRig.maxVelocity.ToString("0.0") : "?") + ": held back");
                }
                float keep = flat > most ? most / flat : 1f;
                foreach (Rigidbody b in rig.physicsRig.GetComponentsInChildren<Rigidbody>())
                {
                    if (b == null || b.isKinematic)
                        continue;
                    Vector3 bv = b.velocity;
                    b.velocity = new Vector3(bv.x * keep, Mathf.Min(bv.y, Tuning.F("guard.up", 9f)), bv.z * keep);
                }
            }
            catch (Exception)
            {
            }
        }

        // Its own place about whatever it is after (so several after the same thing come at it from round about, not in a line).
        private Vector3 OwnSide(float far)
        {
            float angle = (Mathf.Abs(rig.Pointer.ToInt64() / 16) % 8) * 45f;
            return Quaternion.Euler(0f, angle, 0f) * Vector3.forward * far;
        }
    }
}
