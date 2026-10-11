using BoneLib;
using Il2CppSLZ.Marrow;
using Il2CppSLZ.Marrow.Audio;
using UnityEngine;

namespace ThirdEyeBonelabMod.Bots
{
    /// <summary>
    /// Being with the player as someone is, not only near them. It looks where they point (and,
    /// left to itself, may go and see what is there); it looks back at them when they look at
    /// it for a moment; it steps out of the way of their gun whatever it is doing, not only in
    /// a fight; and when they have stood in one place a good while it comes over to see what
    /// they are at. And its avatar's own voice, where it has one: a call when it spots an
    /// enemy, another when the last one is down, an effort as it jumps or hauls itself up, a
    /// groan as it gets back on its feet.
    /// </summary>
    public partial class Bot
    {
        // The player pointing: since when, with which hand, and when it last looked where they pointed.
        private static float pointSince = -1f, pointedAt = -100f;
        private static Vector3 pointPlace;
        private static bool pointHas;
        private static int pointFrame = -1;
        private float pointSeen = -100f;
        // The player looking at it: since when; and when it last looked back.
        private float stareSince = -1f, staredAt = -100f;
        // How long the player has stood in one place.
        private static Vector3 youStoodAt;
        private static float youStoodSince;
        private static int youFrame = -1;

        public static float YouStoodFor => Time.time - youStoodSince;

        // Once a frame (for all the bots): is the player pointing at something, and where; and how long have they stood where they are.
        private static void WatchPlayer(Transform you)
        {
            if (pointFrame == Time.frameCount)
                return;
            pointFrame = Time.frameCount;
            youFrame = Time.frameCount;
            try
            {
                if (Flat(you.position - youStoodAt).magnitude > 2.5f)
                {
                    youStoodAt = you.position;
                    youStoodSince = Time.time;
                }
                OpenControllerRig controls = Player.ControllerRig;
                Hand[] hands = { Player.LeftHand, Player.RightHand };
                BaseController[] sticks = { controls != null ? controls.leftController : null, controls != null ? controls.rightController : null };
                bool pointing = false;
                Vector3 from = Vector3.zero, along = Vector3.zero;
                for (int i = 0; i < 2 && !pointing; i++)
                {
                    Hand hand = hands[i];
                    BaseController c = sticks[i];
                    if (hand == null || c == null || hand.m_CurrentAttachedGO != null || WallGrab.Holding(hand))
                        continue;
                    // A finger out, the rest of the hand shut, the arm out from the body.
                    bool finger = c._processedIndex < 0.3f && c._processedMiddle > 0.6f && c._processedRing > 0.6f;
                    Vector3 shoulder = you.position - Vector3.up * 0.22f;
                    Vector3 arm = hand.transform.position - shoulder;
                    if (!finger || arm.magnitude < Tuning.F("point.arm", 0.45f))
                        continue;
                    pointing = true;
                    from = hand.transform.position;
                    along = arm.normalized;
                }
                if (!pointing)
                {
                    pointSince = -1f;
                    return;
                }
                if (pointSince < 0f)
                    pointSince = Time.time;
                if (Time.time - pointSince < Tuning.F("point.hold", 0.6f) || Time.time - pointedAt < 2.5f)
                    return;
                pointHas = false;
                foreach (RaycastHit hit in Physics.RaycastAll(from + along * 0.3f, along, 45f, ~0, QueryTriggerInteraction.Ignore))
                {
                    if (hit.collider == null || hit.collider.GetComponentInParent<RigManager>() == Player.RigManager)
                        continue;
                    if (!pointHas || (hit.point - from).magnitude < (pointPlace - from).magnitude)
                    {
                        pointPlace = hit.point;
                        pointHas = true;
                    }
                }
                if (!pointHas)
                {
                    pointPlace = from + along * 20f;
                    pointHas = true;
                }
                pointedAt = Time.time;
                Lab.Event("you pointed (" + (pointPlace - you.position).magnitude.ToString("0") + " m off)");
                PointedThing(you);
            }
            catch (Exception)
            {
            }
        }

        // A thing where the player pointed (something a bot can take up and try): the bot nearest it, with nothing pressing on, goes and tries it.
        private static void PointedThing(Transform you)
        {
            try
            {
                Rigidbody best = null;
                string bestCode = "", bestName = "";
                float nearest = Tuning.F("point.thing", 0.9f);
                foreach (Collider c in Physics.OverlapSphere(pointPlace, nearest, ~0, QueryTriggerInteraction.Ignore))
                {
                    Rigidbody rb = c != null ? c.attachedRigidbody : null;
                    if (rb == null || rb.isKinematic || rb.GetComponentInParent<RigManager>() != null || rb.mass > MaxMass || NotForBots(rb))
                        continue;
                    if (!Tryable(rb, out string code, out string name) || GripOf(rb) == null)
                        continue;
                    float d = (c.ClosestPoint(pointPlace) - pointPlace).magnitude;
                    if (d < nearest)
                    {
                        nearest = d;
                        best = rb;
                        bestCode = code;
                        bestName = name;
                    }
                }
                if (best == null)
                    return;
                Bot who = null;
                float whoFar = Tuning.F("point.thing.far", 40f);
                foreach (Bot bot in BotManager.All)
                {
                    if (bot.dead || bot.enemy || bot.InLesson || bot.climbing || bot.Reloading || bot.nearestSeen.transform != null || bot.brainTarget.transform != null || bot.rig.activeSeat != null)
                        continue;
                    float d = (best.worldCenterOfMass - bot.Feet).magnitude;
                    if (d < whoFar)
                    {
                        whoFar = d;
                        who = bot;
                    }
                }
                if (who == null)
                    return;
                string was = Lab.Who;
                Lab.Who = who.Name;
                who.TryPointedAt(bestCode, bestName);
                who.pointSeen = pointedAt;
                Lab.Who = was;
            }
            catch (Exception)
            {
            }
        }

        /// <summary>Once a frame while it is its own master: what the player is doing that it should heed.</summary>
        private void Aware(Transform you, Transform head)
        {
            if (you == null || head == null || enemy)
                return;
            WatchPlayer(you);
            Vector3 to = you.position - head.position;
            float far = to.magnitude;
            bool calm = nearestSeen.transform == null && brainTarget.transform == null && !Reloading && !climbing && !InLesson && Time.time >= waveUntil;

            // Where they point: it looks there (if it can see them pointing); and, with nothing else on, goes to see.
            if (pointedAt > pointSeen && Time.time - pointedAt < 1f)
            {
                pointSeen = pointedAt;
                if (calm && far < Tuning.F("point.far", 14f) && Targets.InSight(head.position, you.position, Player.RigManager != null ? Player.RigManager.transform : null, rig))
                {
                    Look(pointPlace, null, "where you pointed", Random(2.2f, 3.5f));
                    Lab.Event("looks where you pointed");
                    bool free = (mode == Mode.Wander || (mode == Mode.Brain && (goal == Goal.Explore || goal == Goal.Idle || goal == Goal.Play))) && !Playing;
                    if (free && Ground(pointPlace + Vector3.up * 0.5f, out Vector3 there) && Flat(there - Feet).magnitude > 3f && UnityEngine.Random.value < Tuning.F("point.go", 0.7f))
                    {
                        // (A step short of it, to look at it; not on top of it.)
                        Vector3 stand = there - Flat(there - Feet).normalized * 1.2f;
                        planPlace = Ground(stand + Vector3.up * 0.5f, out Vector3 onGround) ? onGround : there;
                        planLook = pointPlace;
                        planSight = Sight.Open;
                        planWhy = "going to see what you pointed at";
                        planPush = MotionLibrary.WalkPush;
                        planRun = false;
                        viaLeft = false;
                        onDetour = false;
                        detours = 0;
                        thinking2 = true;
                        thinkUntil = Time.time + Random(1f, 1.8f);
                        act = Act.Go;
                        actUntil = Time.time + 60f;
                        planSince = Time.time;
                        pauseNext = Time.time + 30f;
                        nav.Clear();
                        Lab.Event("roam: " + planWhy + " (" + Flat(planPlace - Feet).magnitude.ToString("0") + " m)");
                    }
                }
            }

            // Looked at by them a moment: it looks back.
            bool stared = far < 9f && Vector3.Angle(you.forward, -to) < Tuning.F("stare.cone", 9f);
            stareSince = stared ? (stareSince < 0f ? Time.time : stareSince) : -1f;
            if (stared && calm && Time.time - stareSince > Tuning.F("stare.hold", 1.3f) && Time.time - staredAt > Tuning.F("stare.every", 14f) && HandsFree)
            {
                staredAt = Time.time;
                Look(you.position, you, "you, looking at it", Random(1.8f, 3.2f));
            }
        }

        // Out of the way of the player's gun, whatever it is doing on its feet (called after it has chosen where to walk).
        private void AsideForYou(float dt)
        {
            if (enemy || climbing || InLesson || down || Playing && play != Play.Shoot)
                return;
            Transform head = Head;
            if (head == null || !Tactics.InPlayersLine(head.position - Vector3.up * 0.4f, out Vector3 outOfIt))
                return;
            Vector3 local = Quaternion.Euler(0f, -bodyYaw, 0f) * outOfIt;
            walk = new Vector2(local.x, local.z) * MotionLibrary.WalkPush;
        }

        // ---------------------------------------------------------------- its voice

        private readonly Dictionary<string, float> calledAt = new Dictionary<string, float>();

        /// <summary>
        /// A sound of its avatar's own, for what it is doing: "spot" (an enemy seen), "clear"
        /// (the last one down), "effort" (a jump, a throw), "heave" (hauling itself up), "up"
        /// (back on its feet). Nothing, if its avatar has no such sound.
        /// </summary>
        private void Call(string what)
        {
            try
            {
                if (dead || Tuning.F("bot.calls", 1f) < 0.5f)
                    return;
                float gap = what == "spot" ? 9f : what == "clear" ? 12f : what == "effort" ? 2.5f : 4f;
                if (calledAt.TryGetValue(what, out float last) && Time.time - last < gap)
                    return;
                // (Not on top of a cry of pain.)
                if (Time.time - voiceAt < 0.8f)
                    return;
                Il2CppSLZ.VRMK.Avatar avatar = rig.avatar;
                Transform head = Head;
                if (avatar == null || head == null)
                    return;
                Il2CppSLZ.Data.AudioVarianceData data = what == "spot" ? avatar.agro : what == "clear" ? avatar.unAgro : what == "effort" ? avatar.smallEffort
                    : what == "heave" ? (Clips(avatar.bigEffort) > 0 ? avatar.bigEffort : avatar.smallEffort) : avatar.recovery;
                if (Clips(data) == 0)
                {
                    // (An avatar with no such sound of its own: the body's own, for an effort or getting up, if it has one.)
                    HeadSFX sfx = what == "spot" || what == "clear" ? null : rig.GetComponentInChildren<HeadSFX>(true);
                    if (sfx == null)
                        return;
                    calledAt[what] = Time.time;
                    if (what == "up")
                        sfx.RecoveryVocal();
                    else
                        sfx.JumpEffort();
                    return;
                }
                calledAt[what] = Time.time;
                voiceAt = Time.time;
                Audio3dManager.PlayAtPoint(data.audioClips, head.position, Audio3dManager.hardInteraction, Tuning.F("bot.voice", 0.9f), 1f,
                    new Il2CppSystem.Nullable<float>(0.06f), new Il2CppSystem.Nullable<float>(1f), new Il2CppSystem.Nullable<float>(1f));
            }
            catch (Exception)
            {
            }
        }

        /// <summary>For the lab: what its avatar has to say, and one of each.</summary>
        public void LabVoice(string what)
        {
            Il2CppSLZ.VRMK.Avatar avatar = rig.avatar;
            if (avatar == null)
                return;
            Lab.Event("voice: " + avatar.name + " has " + Clips(avatar.agro) + " for spotting, " + Clips(avatar.unAgro) + " for all clear, " + Clips(avatar.smallEffort) + " small effort, "
                      + Clips(avatar.bigEffort) + " big effort, " + Clips(avatar.recovery) + " recovery, " + Clips(avatar.spawn) + " spawn");
            if (what.Length > 0)
            {
                calledAt.Clear();
                voiceAt = -10f;
                Call(what);
            }
        }
    }
}
