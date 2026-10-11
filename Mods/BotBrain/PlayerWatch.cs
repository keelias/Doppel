using BoneLib;
using Il2CppSLZ.Marrow;
using UnityEngine;

namespace ThirdEyeBonelabMod.Bots
{
    /// <summary>
    /// Watches the player play, live, and learns how they fight (kept in Knowledge):
    /// <list type="bullet">
    /// <item>the rhythm of their shooting: for single-shot guns the time between shots; for
    /// automatics how long they hold a burst and how long they wait between bursts;</item>
    /// <item>how far from enemies they fight, and whether they back off from one coming close;</item>
    /// <item>how much is left in the magazine when they change it.</item>
    /// </list>
    /// Also what they are shooting at just now (so a bot can shoot at it too).
    /// </summary>
    public static class PlayerWatch
    {
        private const float BurstGap = 0.16f;

        private static float lastShot = -10f;
        private static float burstStart = -10f;
        private static bool lastAuto;
        private static int burstRounds;

        // The gun in their hands and its ammo a moment ago (to see them change magazines).
        private static Gun watchedGun;
        private static bool hadMagazine;
        private static float lastAmmoFraction = 1f;

        private static float sampleAt;
        private static float lastNearDistance = -1f;

        /// <summary>When the player last fired, and what they were shooting at (where their shot went).</summary>
        public static float FiredAt { get; private set; } = -10f;
        public static Vector3 ShotAt { get; private set; }
        public static Vector3 AimFrom { get; private set; }
        public static Vector3 AimAlong { get; private set; }

        /// <summary>Whether the player is in a fight (fired lately).</summary>
        public static bool Fighting => Time.time - FiredAt < 6f;

        /// <summary>Called (by BotManager) whenever any gun fires.</summary>
        public static void OnGunFired(Gun gun)
        {
            if (gun == null || !HeldByPlayer(gun))
                return;
            ShotsSeen++;
            float now = Time.time;
            bool auto = gun.fireMode == Gun.FireMode.AUTOMATIC;
            Transform muzzle = gun.firePointTransform;
            if (muzzle != null)
            {
                AimFrom = muzzle.position;
                AimAlong = muzzle.forward;
                ShotAt = Physics.Raycast(muzzle.position, muzzle.forward, out RaycastHit hit, 80f, ~0, QueryTriggerInteraction.Ignore)
                    ? hit.point : muzzle.position + muzzle.forward * 30f;
            }
            float gap = now - lastShot;
            if (auto)
            {
                // A burst ends at a longer gap: its length, and the wait after it, are learned.
                if (gap > BurstGap || !lastAuto)
                {
                    // (A burst's length learned in rounds as well as time: a gun firing faster
                    // fits more rounds in the same time, and it is the rounds that show.)
                    if (lastAuto && gap < 3f && burstRounds > 0)
                    {
                        Knowledge.Style.Add(Knowledge.Style.bursts, lastShot - burstStart + 0.08f);
                        Knowledge.Style.Add(Knowledge.Style.burstRounds, burstRounds);
                        Knowledge.Style.Add(Knowledge.Style.pauses, gap);
                    }
                    burstStart = now;
                    burstRounds = 0;
                }
                burstRounds++;
            }
            else if (!lastAuto && gap > 0.05f && gap < 2.5f)
            {
                Knowledge.Style.Add(Knowledge.Style.gaps, gap);
            }
            lastAuto = auto;
            lastShot = now;
            FiredAt = now;
        }

        // ---------------------------------------------------------------- what the player chooses to do

        /// <summary>How many of the player's choices it has learned from.</summary>
        public static int Imitated { get; private set; }

        /// <summary>How many shots of the player's have been seen (this game).</summary>
        public static float ShotsSeen { get; private set; }


        private static float chooseAt;
        private static float seenAt = -100f;
        private static float nearestBefore = -1f;
        private static float nearestBeforeAt;
        private static Vector3 placeBefore;
        private static float placeBeforeAt;
        private static float armedSince = -100f;
        private static bool wasArmed;
        private static float closeCallAt = -100f;
        private static readonly float[] seenByPlayer = new float[Knowledge.Features];

        // Every half second while teaching: what the player is doing (fighting, hiding, backing
        // off, punching, arming, looking, exploring, standing about), in what situation (as the
        // bot weighs its own), learned as what to do in times like these.
        private static void Choose(Gun gun)
        {
            if (Time.time < chooseAt)
                return;
            float dt = Time.time - (chooseAt - 0.5f);
            chooseAt = Time.time + 0.5f;
            Transform head = Player.Head;
            if (head == null)
                return;
            Vector3 eye = head.position;
            bool armed = gun != null;
            if (armed && !wasArmed)
                armedSince = Time.time;
            wasArmed = armed;

            int seenCount = 0, known = 0;
            float nearestSeen = 999f, nearestAny = 999f;
            foreach (Targets.Target t in Targets.Hostiles())
            {
                float d = (t.Point - eye).magnitude;
                if (d > 45f)
                    continue;
                known++;
                nearestAny = Mathf.Min(nearestAny, d);
                if (Targets.InSight(eye, t.Point, t.transform, Player.RigManager))
                {
                    seenCount++;
                    nearestSeen = Mathf.Min(nearestSeen, d);
                }
            }
            bool seen = seenCount > 0;
            if (seen)
                seenAt = Time.time;
            if (nearestAny < 1.6f)
                closeCallAt = Time.time;
            float speed = Time.time - placeBeforeAt < 1.5f ? Flat(eye - placeBefore).magnitude / Mathf.Max(Time.time - placeBeforeAt, 0.1f) : 0f;
            float opening = nearestBefore > 0f && seen && Time.time - nearestBeforeAt < 1.5f ? (nearestSeen - nearestBefore) / Mathf.Max(Time.time - nearestBeforeAt, 0.1f) : 0f;
            placeBefore = eye;
            placeBeforeAt = Time.time;
            nearestBefore = seen ? nearestSeen : -1f;
            nearestBeforeAt = Time.time;

            seenByPlayer[0] = 1f;
            seenByPlayer[1] = seen ? 1f : 0f;
            seenByPlayer[2] = nearestSeen < 5f ? 1f : 0f;
            seenByPlayer[3] = Mathf.Clamp01(seenCount / 4f);
            seenByPlayer[4] = armed ? 1f : 0f;
            seenByPlayer[5] = armed && AmmoFraction(gun) < 0.3f ? 1f : 0f;
            seenByPlayer[6] = 0f;
            seenByPlayer[7] = !seen && Time.time - seenAt < 25f ? 1f : 0f;
            seenByPlayer[8] = Time.time - closeCallAt < 5f ? 1f : 0f;
            seenByPlayer[9] = Fighting ? 1f : 0f;

            string choice = null;
            if (Time.time - GunMoves.PlayerPunchedAt < 1f && !armed)
                choice = "Brawl";
            else if (seen && armed && Time.time - FiredAt < 1.5f)
                choice = "Fight";
            else if (!seen && known > 0 && Time.time - seenAt < 4f && Time.time - seenAt > 0.4f)
                choice = "TakeCover";
            else if (seen && nearestSeen < 7f && opening > 0.6f)
                choice = "Retreat";
            else if (armed && Time.time - armedSince < 2f)
                choice = "ArmUp";
            else if (!seen && Time.time - seenAt < 25f && speed > 0.8f)
                choice = "Investigate";
            else if (known == 0 && speed > 0.8f)
                choice = "Explore";
            else if (!seen && speed < 0.3f)
                choice = "Idle";
            if (choice == null)
                return;
            Knowledge.Learn(choice, seenByPlayer, Tuning.F("teach.imitate", 0.6f));
            Imitated++;
            if (Imitated % 20 == 0)
                Lab.Event("teach: learned " + Imitated + " of your choices (last: " + choice + ")");
        }

        // ---------------------------------------------------------------- how they move in a fight

        private static Vector3 movedFrom;
        private static float movedFromAt;

        // In a fight: how much of their moving is sideways to where they aim (strafing).
        private static void Strafing()
        {
            Transform head = Player.Head;
            if (head == null || Time.time - FiredAt > 2f)
            {
                movedFromAt = 0f;
                return;
            }
            if (movedFromAt <= 0f || Time.time - movedFromAt < 0.5f)
            {
                if (movedFromAt <= 0f)
                {
                    movedFrom = head.position;
                    movedFromAt = Time.time;
                }
                return;
            }
            Vector3 v = Flat(head.position - movedFrom) / (Time.time - movedFromAt);
            movedFrom = head.position;
            movedFromAt = Time.time;
            if (v.magnitude < 0.5f)
                return;
            Vector3 aim = Flat(AimAlong).sqrMagnitude > 0.01f ? Flat(AimAlong).normalized : Flat(head.forward).normalized;
            float sideways = Mathf.Abs(Vector3.Dot(v.normalized, Vector3.Cross(Vector3.up, aim)));
            Knowledge.Style.Add(Knowledge.Style.strafe, sideways > 0.6f ? 1f : 0f);
        }

        private static Vector3 Flat(Vector3 v)
        {
            v.y = 0f;
            return v;
        }

        /// <summary>Once a frame.</summary>
        public static void Update()
        {
            if (GunMoves.Teaching)
                Choose(HeldGun());
            Strafing();
            Gun gun = HeldGun();

            // Changing magazines: what was left when the old one came out.
            if (gun != watchedGun)
            {
                watchedGun = gun;
                hadMagazine = gun != null && SafeHasMagazine(gun);
                lastAmmoFraction = gun != null ? AmmoFraction(gun) : 1f;
            }
            else if (gun != null)
            {
                bool has = SafeHasMagazine(gun);
                if (hadMagazine && !has && lastAmmoFraction < 0.95f)
                    Knowledge.Style.Add(Knowledge.Style.reloadAt, lastAmmoFraction);
                hadMagazine = has;
                if (has)
                    lastAmmoFraction = AmmoFraction(gun);
            }

            // In a fight: how far from the nearest enemy, and whether they back off from it.
            if (Time.time < sampleAt)
                return;
            sampleAt = Time.time + 0.5f;
            Transform head = Player.Head;
            if (head == null || gun == null || !Fighting)
            {
                lastNearDistance = -1f;
                return;
            }
            float nearest = float.MaxValue;
            foreach (Targets.Target t in Targets.Hostiles())
                nearest = Mathf.Min(nearest, (t.Point - head.position).magnitude);
            if (nearest > 60f)
            {
                lastNearDistance = -1f;
                return;
            }
            Knowledge.Style.Add(Knowledge.Style.fightDistance, nearest);
            if (lastNearDistance > 0f && nearest < 6f)
            {
                // Closing on them: did they give ground (or stand, or press in)?
                float change = nearest - lastNearDistance;
                Knowledge.Style.Add(Knowledge.Style.giveGround, change > 0.1f ? 1f : 0f);
            }
            lastNearDistance = nearest;
        }

        private static Gun HeldGun()
        {
            Gun gun = Player.GetComponentInHand<Gun>(Player.RightHand);
            return gun != null ? gun : Player.GetComponentInHand<Gun>(Player.LeftHand);
        }

        private static bool HeldByPlayer(Gun gun)
        {
            Grip grip = gun.triggerGrip;
            if (grip == null || grip.attachedHands == null)
                return false;
            foreach (Hand hand in new[] { Player.LeftHand, Player.RightHand })
            {
                if (hand != null && grip.attachedHands.Contains(hand))
                    return true;
            }
            return false;
        }

        private static bool SafeHasMagazine(Gun gun)
        {
            try
            {
                return gun.HasMagazine();
            }
            catch (Exception)
            {
                return true;
            }
        }

        private static float AmmoFraction(Gun gun)
        {
            try
            {
                int full = gun.defaultMagazine != null ? Mathf.Max(1, gun.defaultMagazine.rounds) : 15;
                return Mathf.Clamp01(gun.AmmoCount() / (float)full);
            }
            catch (Exception)
            {
                return 1f;
            }
        }
    }
}
