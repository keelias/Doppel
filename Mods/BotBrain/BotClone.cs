using BoneLib;
using Il2CppSLZ.Marrow;
using Il2CppSLZ.Marrow.Pool;
using Il2CppSLZ.Marrow.Warehouse;
using UnityEngine;

namespace ThirdEyeBonelabMod.Bots
{
    /// <summary>
    /// The bot playing back a clone recording (see Clone): it starts where the player started
    /// and does exactly what they did. Its body is pulled along their path (as the multiplayer
    /// mod moves another player's body), its head, hands, fingers, trigger and buttons are theirs
    /// frame by frame, and it spawns what they spawned, takes hold of what they took hold of
    /// (or its copy) and lets go when they did, so what they threw, it throws, and what they
    /// shot, it shoots. Its punches hurt the player.
    /// </summary>
    public partial class Bot
    {
        private Clone.Take cloneTake;
        private float cloneStart;
        private int cloneEvent;
        private bool cloneLoop;
        private int cloneFrame;
        private readonly Dictionary<int, Poolee> cloneSpawns = new Dictionary<int, Poolee>();
        private readonly List<(int id, string barcode, Vector3 at, float askedAt, HashSet<IntPtr> before)> clonePending = new List<(int, string, Vector3, float, HashSet<IntPtr>)>();
        // Grabs waiting on a thing that is still being spawned: (event, until when).
        private readonly List<(Clone.Event e, float until)> cloneGrabs = new List<(Clone.Event, float)>();
        private readonly Grip[] cloneHeld = new Grip[2];
        private float cloneFiredAt = -10f;
        private readonly float[] punchedAt = { -10f, -10f };

        public bool PlayingClone => mode == Mode.Clone && cloneTake != null;

        /// <summary>Plays a clone recording from its start (where the player stood when they began).</summary>
        public void PlayClone(Clone.Take take, bool loop)
        {
            if (take == null || take.frames.Count < 2)
            {
                Spawnables.Notify("No clone recorded yet");
                return;
            }
            StopPlaying();
            ReleaseCloneHands();
            SetMode(Mode.Clone);
            cloneTake = take;
            cloneLesson = null;
            cloneLoop = loop;
            // In the player's avatar of the time (a clone of a big body played on a small one
            // stretches it all wrong): put on first, then begun.
            string wearing = BotRig.AvatarOf(rig);
            bool sameScale = rig.avatar != null && (rig.avatar.transform.localScale - take.avatarScale).sqrMagnitude < 0.0001f;
            if (!string.IsNullOrEmpty(take.avatar) && (take.avatar != wearing || !sameScale))
            {
                cloneDressing = true;
                cloneDressedBy = Time.time + 6f;
                Clone.Take playing = take;
                BotRig.UseAvatar(rig, take.avatar, take.avatarScale, () =>
                {
                    if (cloneTake == playing && cloneDressing)
                    {
                        cloneDressing = false;
                        BeginClone();
                    }
                });
                Lab.Event("clone: putting on the recording's avatar");
                return;
            }
            BeginClone();
        }

        // Putting on the recording's avatar before it begins (and till when to wait for it).
        private bool cloneDressing;
        private float cloneDressedBy;

        private void BeginClone()
        {
            cloneStart = Time.time;
            cloneEvent = 0;
            cloneFrame = 0;
            cloneSpawns.Clear();
            clonePending.Clear();
            cloneGrabs.Clear();
            cloneStandIns.Clear();
            cloneDriven = -1;
            ReleaseCloneHands();
            try
            {
                bodyYaw = cloneTake.startYaw;
                rig.Teleport(cloneTake.startFeet, true);
            }
            catch (Exception e)
            {
                Lab.Event("clone: could not start where you did: " + e.Message);
            }
            Lab.Event("clone: playing " + cloneTake.Length.ToString("0.0") + " s");
        }

        private void ReleaseCloneHands()
        {
            LetGoOfPlayer("the clone stopped");
            Hand[] hands = { LeftHand, RightHand };
            for (int i = 0; i < 2; i++)
            {
                if (cloneHeld[i] != null && hands[i] != null)
                    LetGoOf(cloneHeld[i], hands[i]);
                // (A lesson's hands are all the lesson's: emptied whatever is left in them.)
                if (cloneLesson != null)
                    FreeHand(hands[i]);
                cloneHeld[i] = null;
            }
        }

        // The recording at time t (two frames either side, and how far between).
        private bool CloneAt(float t, out Clone.Frame a, out Clone.Frame b, out float k)
        {
            List<Clone.Frame> frames = cloneTake.frames;
            a = b = default;
            k = 0f;
            if (frames.Count < 2)
                return false;
            if (cloneFrame >= frames.Count || frames[cloneFrame].t > t)
                cloneFrame = 0;
            while (cloneFrame < frames.Count - 2 && frames[cloneFrame + 1].t <= t)
                cloneFrame++;
            a = frames[cloneFrame];
            b = frames[cloneFrame + 1];
            k = b.t > a.t ? Mathf.Clamp01((t - a.t) / (b.t - a.t)) : 0f;
            return true;
        }

        /// <summary>Once a frame while playing: the player's doings at their moments.</summary>
        private void CloneUpdate(float dt)
        {
            if (cloneTake == null)
            {
                SetMode(Mode.Stand);
                return;
            }
            walk = Vector2.zero;
            if (cloneDressing)
            {
                if (Time.time < cloneDressedBy)
                    return;
                cloneDressing = false;
                BeginClone();
            }
            LessonLoop();
            // (Driving somewhere: from where the player drove off, the driving is its own.)
            if (cloneLesson != null && driveMeant && DriveStep(dt))
                return;
            if (cloneTake == null || mode != Mode.Clone)
                return;
            float t = Time.time - cloneStart;
            if (cloneLesson != null)
            {
                if (seatWaiting != null)
                    LessonSeat(seatWaiting);
                SeatCheck();
                LessonKeepHold();
                LessonWatch();
                if (cloneTake == null || mode != Mode.Clone)
                    return;
            }

            List<Clone.Event> events = cloneTake.events;
            while (cloneEvent < events.Count && events[cloneEvent].t <= t)
            {
                Do(events[cloneEvent]);
                cloneEvent++;
            }
            FindCloneSpawns();
            // Grabs waiting on their thing.
            for (int i = cloneGrabs.Count - 1; i >= 0; i--)
            {
                bool took = TryCloneGrab(cloneGrabs[i].e);
                if (!took && Time.time > cloneGrabs[i].until && cloneLesson != null)
                    Lab.Event("lesson: could not take the " + Clone.ThingName(cloneGrabs[i].e) + ": " + grabWhy);
                if (took || Time.time > cloneGrabs[i].until)
                    cloneGrabs.RemoveAt(i);
            }
            if (cloneLesson == null && CloneAt(t, out Clone.Frame grabA, out Clone.Frame grabB, out float grabK))
                GrabPlayerUpdate(grabA, grabB, grabK);

            if (t > cloneTake.Length)
            {
                // (Up a wall: not done till it stands on the top. The player's own getting over
                // may leave it short of the edge, hanging in the air, to fall back down.)
                if (cloneLesson != null && lessonPurposeful && cloneLesson.purpose == "climb" && !LandedOnTop())
                    return;
                if (cloneLesson != null)
                    EndLesson("done with the " + cloneLesson.name);
                else if (cloneLoop)
                {
                    if (!string.IsNullOrEmpty(cloneTake.avatar) && BotRig.AvatarOf(rig) != cloneTake.avatar)
                        BotRig.UseAvatar(rig, cloneTake.avatar, cloneTake.avatarScale, null);
                    BeginClone();
                }
                else
                {
                    Lab.Event("clone: done");
                    ReleaseCloneHands();
                    cloneTake = null;
                    SetMode(Mode.Stand);
                }
            }
        }

        private void Do(Clone.Event e)
        {
            switch (e.kind)
            {
                case Clone.Kind.Spawn:
                    // (Not tools or pouch magazines, kept by recordings made before those were left out.)
                    string what = (e.name ?? "").ToLowerInvariant();
                    if (what.Contains("spawn_gun") || what.Contains("nimbus") || what.StartsWith("mag_") || what.StartsWith("vfx"))
                        break;
                    // (Never a whole player body: kept by recordings made before those were left out.)
                    if (Clone.IsBody(e.barcode, e.name))
                        break;
                    CloneSpawn(e.spawn, e.barcode, LessonPlace(e.at), LessonTurn(e.turn));
                    break;
                case Clone.Kind.Grab:
                    // (A hold of the wall by a bare hand: that hand is on its wall from here, where it comes to it.)
                    if (e.name != null && e.name.StartsWith("@wall", StringComparison.Ordinal))
                    {
                        wallHeld[e.hand] = true;
                        wallPinned[e.hand] = false;
                        break;
                    }
                    if (!TryCloneGrab(e))
                        cloneGrabs.Add((e, Time.time + 1.5f));
                    break;
                case Clone.Kind.Release:
                    wallHeld[e.hand] = false;
                    Hand hand = e.hand == 0 ? LeftHand : RightHand;
                    if (cloneHeld[e.hand] != null && hand != null)
                        LetGoOf(cloneHeld[e.hand], hand);
                    if (hand != null && hand.m_CurrentAttachedGO != null)
                        FreeHand(hand);
                    cloneHeld[e.hand] = null;
                    break;
                case Clone.Kind.Fire:
                    // Its trigger is the player's: if that did not fire the gun in its hand, it is fired.
                    if (cloneLesson == null || !LessonUnsafe(e.hand == 0 ? LeftHand : RightHand))
                        CloneFireCheck(e.hand);
                    break;
                case Clone.Kind.Seat:
                    LessonSeat(e);
                    break;
                case Clone.Kind.Avatar:
                    // The player changed avatar here: so does the clone (a lesson is done in its own body).
                    if (cloneLesson == null && BotRig.AvatarOf(rig) != e.barcode)
                    {
                        BotRig.UseAvatar(rig, e.barcode, e.at, null);
                        Lab.Event("clone: putting on " + e.barcode);
                    }
                    break;
            }
        }

        private float noSpawnSaidAt = -10f;

        // Whether a lesson has a thing in it that the bot would have to have (a tool, something to sit in).
        private static bool NeedsThings(Lessons.Lesson lesson)
        {
            try
            {
                foreach (Clone.Event e in lesson.take.events)
                {
                    if (e.barcode != null && e.barcode.Length > 0 && !Clone.IsBody(e.barcode, Clone.ThingName(e)))
                        return true;
                }
            }
            catch (Exception)
            {
            }
            return false;
        }

        private void CloneSpawn(int id, string barcode, Vector3 at, Quaternion turn)
        {
            // (Nothing brought into being for it: it makes do with what is on the map.)
            if (!BotManager.OwnSpawns)
            {
                if (Time.time - noSpawnSaidAt > 5f)
                {
                    noSpawnSaidAt = Time.time;
                    Lab.Event("clone: no " + barcode + " of its own (bots are not to spawn things)");
                }
                return;
            }
            try
            {
                AssetWarehouse warehouse = AssetWarehouse.Instance;
                if (warehouse == null || !warehouse.HasCrate(new Barcode(barcode)))
                {
                    Lab.Event("clone: cannot spawn " + barcode);
                    return;
                }
                HashSet<IntPtr> before = new HashSet<IntPtr>();
                foreach (Collider c in Physics.OverlapSphere(at, 1.5f, ~0, QueryTriggerInteraction.Ignore))
                {
                    Poolee p = c != null ? c.GetComponentInParent<Poolee>() : null;
                    if (p != null)
                        before.Add(p.Pointer);
                }
                // (A thing to sit in: every one there is already, wherever it stands, is not the one
                // asked for now. The player's own, parked by, had rolled in and been taken for it.)
                if (cloneLesson != null && cloneLesson.purpose == "drive")
                {
                    foreach (Seat seat in UnityEngine.Object.FindObjectsOfType<Seat>())
                    {
                        Poolee old = seat != null ? seat.GetComponentInParent<Poolee>() : null;
                        if (old != null)
                            before.Add(old.Pointer);
                    }
                }
                Clone.Suppress();
                HelperMethods.SpawnCrate(barcode, at, turn, Vector3.one, false, null, null);
                clonePending.Add((id, barcode, at, Time.time, before));
            }
            catch (Exception ex)
            {
                Lab.Event("clone: spawn failed: " + ex.Message);
            }
        }

        // Its spawns, found where they were asked for (the game does not always say when a spawn is done).
        private void FindCloneSpawns()
        {
            for (int i = clonePending.Count - 1; i >= 0; i--)
            {
                var pending = clonePending[i];
                if (Time.time - pending.askedAt < 0.05f)
                    continue;
                foreach (Collider c in Physics.OverlapSphere(pending.at, 1.5f, ~0, QueryTriggerInteraction.Ignore))
                {
                    Poolee p = c != null ? c.GetComponentInParent<Poolee>() : null;
                    if (p == null || pending.before.Contains(p.Pointer) || p.SpawnableCrate == null || p.SpawnableCrate.Barcode.ID != pending.barcode)
                        continue;
                    // (Not one already found for another grab: two alike, asked for together, are two.)
                    if (cloneSpawns.ContainsValue(p))
                        continue;
                    // (Nor, for a thing it is to sit in, one somebody is in or the player stands by: that is theirs.)
                    if (cloneLesson != null && cloneLesson.purpose == "drive" && Occupied(p))
                        continue;
                    cloneSpawns[pending.id] = p;
                    if (lessonBigAsked.Contains(pending.id))
                        lessonBig.Add(p.transform.GetInstanceID());
                    clonePending.RemoveAt(i);
                    break;
                }
                if (i < clonePending.Count && clonePending[i].id == pending.id && Time.time - pending.askedAt > 4f)
                    clonePending.RemoveAt(i);
            }
        }

        private static bool Occupied(Poolee p)
        {
            try
            {
                foreach (Seat s in p.GetComponentsInChildren<Seat>(true))
                {
                    if (s != null && s.rigManager != null)
                        return true;
                }
            }
            catch (Exception)
            {
            }
            return false;
        }

        // A thing with a seat that somebody sits in, or that the player stands by (theirs, left where they got out of it).
        private static bool NotItsToTake(Poolee p)
        {
            try
            {
                foreach (Seat s in p.GetComponentsInChildren<Seat>(true))
                {
                    if (s != null && s.rigManager != null)
                        return true;
                }
                Transform you = Player.Head;
                // (Bots left to what is on the map: one the player has put down by them is for the bot, unless they stand right at it.)
                return you != null && Flat(p.transform.position - you.position).magnitude < (BotManager.OwnSpawns ? Tuning.F("drive.yours", 5f) : Tuning.F("drive.yours.map", 1.6f));
            }
            catch (Exception)
            {
                return false;
            }
        }

        // Taking hold as the player did: of its own spawn of what they spawned, or of the same
        // kind of thing where theirs was (not one the player holds), by the same grip.
        private string grabWhy = "";

        private bool TryCloneGrab(Clone.Event e)
        {
            Hand hand = e.hand == 0 ? LeftHand : RightHand;
            if (hand == null)
                return true;
            // (A hold of a wall by the bare hand: its hand goes where theirs went on its own wall, and its body is taken up as theirs went.)
            if (e.name != null && e.name.StartsWith("@wall", StringComparison.Ordinal))
                return true;
            Transform thing = null;
            // (A spawn of the recording's, or one it asked for itself to stand in for a thing that
            // was not about: waited for, never asked for again.)
            // (Kept by the bot, not in the recording: a recording is played many times, by many bots.)
            int spawnId = e.spawn;
            if (spawnId == -1 && cloneStandIns.TryGetValue(e, out int standIn))
                spawnId = standIn;
            // (In a lesson the thing is its own, by its hand, not the player's where they had theirs.)
            Vector3 where = cloneLesson != null ? hand.transform.position : e.at;
            if (spawnId != -1)
            {
                if (!cloneSpawns.TryGetValue(spawnId, out Poolee p) || p == null)
                {
                    grabWhy = "its own one (" + spawnId + ") has not appeared";
                    return false;
                }
                thing = p.transform;
            }
            else if (cloneLesson != null && (thing = LessonOwn(e, hand)) != null)
            {
                // (Its own one: the thing already in its other hand, or the one it spawned for
                // this lesson. Taking hold of its gun's slide is not taking a second gun.)
            }
            else
            {
                float nearest = cloneLesson != null ? 1.2f : 2f;
                // (A magazine, in a lesson: always a fresh one of its own, as the player's comes
                // from their pouch. The one about is the one in its gun, which cannot be taken so.)
                bool freshMag = cloneLesson != null;
                foreach (Collider c in Physics.OverlapSphere(where, freshMag ? 0.001f : nearest, freshMag ? 0 : ~0, QueryTriggerInteraction.Ignore))
                {
                    Poolee p = c != null ? c.GetComponentInParent<Poolee>() : null;
                    Transform candidate = p != null ? p.transform : null;
                    if (candidate == null)
                        continue;
                    bool same = e.barcode.Length > 0 ? p.SpawnableCrate != null && p.SpawnableCrate.Barcode.ID == e.barcode : candidate.name == Clone.ThingName(e);
                    if (!same || HeldByPlayer(candidate) || candidate.GetComponentInChildren<RigManager>() != null)
                        continue;
                    // (Nor one it has in a hand itself: that one is another of the player's things.)
                    if (cloneLesson != null && (HeldByAnyone(candidate, rig) || HeldByAnyone(candidate, null) || lessonThings.ContainsValue(p)))
                        continue;
                    float d = (candidate.position - where).magnitude;
                    if (d < nearest)
                    {
                        nearest = d;
                        thing = candidate;
                    }
                }
                // None like it about: one spawned there (the grab waits for it). Never a whole
                // player body (a grab of the player's own body log, in older recordings).
                if (thing == null && e.barcode.Length > 0 && !Clone.IsBody(e.barcode, Clone.ThingName(e)))
                {
                    // (Its own one of that thing already asked for, by its other hand in the same moment: that one.)
                    string standKey = cloneLesson != null ? LessonThingKey(e, hand) : null;
                    if (standKey != null && lessonStandIns.TryGetValue(standKey, out int asked))
                    {
                        cloneStandIns[e] = asked;
                        return false;
                    }
                    int id = -1000 - cloneStandIns.Count;
                    Quaternion faces = cloneLesson != null ? hand.transform.rotation : Quaternion.identity;
                    if (cloneLesson != null)
                        PlaceBigThing(e, ref where, ref faces);
                    CloneSpawn(id, e.barcode, where, faces);
                    cloneStandIns[e] = id;
                    if (standKey != null)
                        lessonStandIns[standKey] = id;
                    if (cloneLesson != null)
                        Lab.Event("lesson: needs a " + Clone.ThingName(e) + " for its " + (e.hand == 0 ? "left" : "right") + " hand, spawning one");
                    return false;
                }
                if (thing == null)
                    return true;
            }
            Grip[] grips = Clone.GripsOf(thing);
            Grip grip = null;
            // By the grip's own name, where the recording has it.
            string gripName = Clone.GripName(e);
            if (gripName != null)
            {
                foreach (Grip g in grips)
                {
                    if (g != null && g.name == gripName && (grip == null || !g.HasAttachedHands()))
                        grip = g;
                }
            }
            // (An older lesson with a gun: the first thing done is taking it by its handle.)
            if (grip == null && cloneLesson != null && e.t < 0.3f)
            {
                Gun itsGun = thing.GetComponentInChildren<Gun>();
                if (itsGun != null && itsGun.triggerGrip != null)
                    grip = itsGun.triggerGrip;
            }
            if (grip == null)
                grip = e.grip >= 0 && e.grip < grips.Length ? grips[e.grip] : null;
            if (grip == null)
            {
                float best = float.MaxValue;
                foreach (Grip g in grips)
                {
                    float d = (g.transform.position - hand.transform.position).magnitude;
                    if (d < best)
                    {
                        best = d;
                        grip = g;
                    }
                }
            }
            if (grip == null)
                return true;
            if (hand.m_CurrentAttachedGO != null)
                FreeHand(hand);
            // A magazine taken from the player's pouch comes full.
            Magazine mag = thing.GetComponentInChildren<Magazine>();
            if (mag != null && mag.magazineState != null)
            {
                try
                {
                    mag.magazineState.Refill();
                }
                catch (Exception)
                {
                }
            }
            // (Already in that hand: taken.)
            if (grip.attachedHands != null && grip.attachedHands.Contains(hand))
            {
                cloneHeld[e.hand] = grip;
                return true;
            }
            // (A thing too big for a hand is taken hold of where it stands, when the hand has
            // come to it: taken from a way off, it and the body were wrenched together.)
            if (cloneLesson != null && lessonBig.Contains(thing.GetInstanceID()) && (grip.transform.position - hand.transform.position).magnitude > Tuning.F("lesson.reach", 0.4f))
            {
                grabWhy = "its hand is " + (grip.transform.position - hand.transform.position).magnitude.ToString("0.0") + " m from the " + grip.name;
                return false;
            }
            grabWhy = "the " + thing.name + "'s " + grip.name + " would not be taken" + (grip.HasAttachedHands() ? " (a hand is on it)" : "") + (hand.m_CurrentAttachedGO != null ? " (its hand is full)" : "");
            if (Grab(hand, grip))
            {
                cloneHeld[e.hand] = grip;
                if (cloneLesson != null)
                {
                    Poolee mine = thing.GetComponentInParent<Poolee>();
                    if (mine != null)
                        lessonThings[LessonThingKey(e, hand)] = mine;
                }
                if (cloneLesson != null)
                    Lab.Event("lesson: took the " + thing.name + " by its " + grip.name + " (" + (e.hand == 0 ? "left" : "right") + " hand)");
                return true;
            }
            return false;
        }

        // Held by some hand that is not this body's own.
        private static bool HeldByAnyone(Transform thing, RigManager but)
        {
            foreach (Grip g in thing.GetComponentsInChildren<Grip>())
            {
                if (g == null || g.attachedHands == null)
                    continue;
                foreach (Hand h in g.attachedHands)
                {
                    if (h != null && (but == null || h.GetComponentInParent<RigManager>() != but))
                        return true;
                }
            }
            return false;
        }

        // Which of the player's things a grab was of: its kind, and which one of that kind. (A
        // recording says; an older one does not, and there a hand taking a thing by the very
        // grip its other hand has a thing of that kind by is taking a second one: a cup in each hand.)
        private string LessonThingKey(Clone.Event e, Hand hand)
        {
            int number = Clone.ThingNumber(e);
            if (number >= 0)
                return e.barcode + "#" + number;
            Hand other = hand == LeftHand ? RightHand : LeftHand;
            GameObject held = other != null ? other.m_CurrentAttachedGO : null;
            Poolee theirs = held != null ? held.GetComponentInParent<Poolee>() : null;
            if (theirs != null && theirs.SpawnableCrate != null && theirs.SpawnableCrate.Barcode.ID == e.barcode)
            {
                Grip[] grips = Clone.GripsOf(theirs.transform);
                Grip wanted = e.grip >= 0 && e.grip < grips.Length ? grips[e.grip] : null;
                string gripName = Clone.GripName(e);
                Grip has = held.GetComponent<Grip>() ?? held.GetComponentInParent<Grip>();
                bool sameGrip = has != null && ((wanted != null && wanted == has) || (gripName != null && has.name == gripName));
                if (sameGrip)
                    return e.barcode + "#hand" + e.hand;
            }
            return e.barcode + "#0";
        }

        // In a lesson: the thing it already has for that one of the player's things; null if it has none yet.
        private Transform LessonOwn(Clone.Event e, Hand hand)
        {
            if (string.IsNullOrEmpty(e.barcode))
                return null;
            string key = LessonThingKey(e, hand);
            if (lessonThings.TryGetValue(key, out Poolee p) && p != null && p.gameObject.activeInHierarchy)
                return p.transform;
            // (The first of its kind, in an older recording: one already in its other hand is that one: its gun, taken by the slide.)
            if (key.EndsWith("#0", StringComparison.Ordinal) && Clone.ThingNumber(e) < 0)
            {
                foreach (Hand h in new[] { LeftHand, RightHand })
                {
                    GameObject held = h != null ? h.m_CurrentAttachedGO : null;
                    Poolee q = held != null ? held.GetComponentInParent<Poolee>() : null;
                    if (q != null && q.SpawnableCrate != null && q.SpawnableCrate.Barcode.ID == e.barcode)
                        return q.transform;
                }
            }
            return null;
        }

        private static bool HeldByPlayer(Transform thing)
        {
            foreach (Hand hand in new[] { Player.LeftHand, Player.RightHand })
            {
                GameObject held = hand != null ? hand.m_CurrentAttachedGO : null;
                if (held != null && held.transform.IsChildOf(thing))
                    return true;
            }
            return false;
        }

        private void CloneFireCheck(int which)
        {
            Hand hand = which == 0 ? LeftHand : RightHand;
            GameObject held = hand != null ? hand.m_CurrentAttachedGO : null;
            Gun g = held != null ? held.GetComponentInParent<Gun>() : null;
            if (g == null)
                return;
            // A moment for its own trigger to have fired it.
            if (Time.time - cloneFiredAt < 0.12f)
                return;
            try
            {
                if (g.cartridgeState != Gun.CartridgeStates.UNSPENT)
                    TryChargeGun(g);
                g.Fire();
            }
            catch (Exception)
            {
            }
        }

        private static void TryChargeGun(Gun g)
        {
            try
            {
                if (g.slideState == Gun.SlideStates.LOCKED)
                    g.SlideRelease();
                else
                    g.Charge();
            }
            catch (Exception)
            {
            }
        }

        // Something to stand on just under its feet.
        private bool FeetOnGround()
        {
            Vector3 feet = rig.physicsRig.centerOfPressure.position;
            foreach (RaycastHit hit in Physics.RaycastAll(feet + Vector3.up * 0.3f, Vector3.down, 0.55f, ~0, QueryTriggerInteraction.Ignore))
            {
                if (hit.collider != null && hit.collider.GetComponentInParent<RigManager>() == null)
                    return true;
            }
            return false;
        }

        /// <summary>A gun in its hands fired (any of them): its trigger works.</summary>
        public void OnCloneGunFired(Gun fired)
        {
            foreach (Hand hand in new[] { LeftHand, RightHand })
            {
                GameObject held = hand != null ? hand.m_CurrentAttachedGO : null;
                if (held != null && fired != null && held.GetComponentInParent<Gun>() == fired)
                    cloneFiredAt = Time.time;
            }
        }

        /// <summary>
        /// Puts the player's frame on its body: its head and hands where theirs were (in its
        /// play space), its fingers, trigger and buttons as theirs, its crouch as theirs.
        /// </summary>
        private void CloneDrive()
        {
            cloneDrives++;
            if (cloneLesson != null && driving)
            {
                DrivePose();
                return;
            }
            if (cloneTake == null || cloneDressing || !CloneAt(Time.time - cloneStart, out Clone.Frame a, out Clone.Frame b, out float k))
                return;
            Transform root = controls.vrRoot;
            rig.remapHeptaRig.inWeight = 1f;
            Quaternion space = Quaternion.Slerp(a.playspace, b.playspace, k);
            Bot.Pose head = Bot.Pose.Lerp(a.head, b.head, k);
            Bot.Pose left = Bot.Pose.Lerp(a.left, b.left, k);
            Bot.Pose right = Bot.Pose.Lerp(a.right, b.right, k);
            if (cloneLesson != null)
            {
                // Turned by something other than this since last frame (a portal it went
                // through): it goes on facing the way that left it.
                cloneYaw += PortalTurn(root);
                if (LessonPortalTurns())
                    space = b.playspace;
                space = Quaternion.Euler(0f, cloneYaw, 0f) * space;
                // In its own body: the player's head and hands, sized to it (about the point under their head).
                if (Mathf.Abs(cloneSize - 1f) > 0.02f)
                {
                    Vector3 under = new Vector3(head.position.x, 0f, head.position.z);
                    head.position = new Vector3(head.position.x, head.position.y * cloneSize, head.position.z);
                    left.position = under + (left.position - under) * cloneSize;
                    right.position = under + (right.position - under) * cloneSize;
                }
            }
            // (Over the join where a stretch is done again: eased from the pose it ended in.)
            if (cloneLesson != null)
            {
                float since = Time.time - lessonSeamAt;
                if (since < 0.3f)
                {
                    float u = since / 0.3f;
                    float eased = u * u * (3f - 2f * u);
                    head = Bot.Pose.Lerp(seamHead, head, eased);
                    left = Bot.Pose.Lerp(seamLeft, left, eased);
                    right = Bot.Pose.Lerp(seamRight, right, eased);
                }
                lastHeadPose = head;
                lastLeftPose = left;
                lastRightPose = right;
            }
            root.rotation = space;
            NotePortalTurn(root);
            lessonRootYaw = root.eulerAngles.y;
            lessonRootSet = true;
            controls.headset.SetLocalPositionAndRotation(head.position, head.rotation);
            KeepHeadDown(root, controls.headset);
            controls.leftController.transform.SetLocalPositionAndRotation(left.position, left.rotation);
            controls.rightController.transform.SetLocalPositionAndRotation(right.position, right.rotation);
            WallHands();
            Clone.HandInput leftNow = LessonInput(true, a, out bool leftTap);
            Clone.HandInput rightNow = LessonInput(false, a, out bool rightTap);
            cloneDriven = cloneFrame;
            // (A real gun pointing the player's way in a lesson: that pull of the trigger is left out.)
            bool leftSafe = cloneLesson == null || !LessonUnsafe(LeftHand);
            bool rightSafe = cloneLesson == null || !LessonUnsafe(RightHand);
            // (Going up a wall, it is carried: the thing in its hand is not set working, or it
            // would hold to the wall while the body it is held by is moved on, and be torn from the hand.)
            if (cloneLesson != null && lessonPurposeful && cloneLesson.purpose == "climb" && Tuning.F("tool.climb.press", 0f) < 0.5f)
            {
                leftSafe = rightSafe = false;
                leftNow.secondary = rightNow.secondary = false;
                leftNow.a = leftNow.b = rightNow.a = rightNow.b = false;
                leftNow.click = rightNow.click = leftNow.pad = rightNow.pad = false;
                leftTap = rightTap = false;
            }
            PutInput(controls.leftController, leftNow, b.leftIn, k, leftSafe);
            PutInput(controls.rightController, rightNow, b.rightIn, k, rightSafe);
            PutButtons(controls.leftController, leftNow, leftTap, cloneHeld[0] != null);
            PutButtons(controls.rightController, rightNow, rightTap, cloneHeld[1] != null);
            controls._primaryAButton = a.aPrimary;
            controls._secondaryAButton = a.aSecondary;
            rig.remapHeptaRig._crouchTarget = Mathf.Lerp(a.crouch, b.crouch, k);
            rig.remapHeptaRig._feetOffset = Mathf.Lerp(a.feet, b.feet, k);
            if (controls.leftController != null)
                controls.leftController._thumbstickAxis = Vector2.zero;
            controls._axisPrimary = Vector2.zero;
            // (Sat in something: its sticks are the player's, for driving it.)
            if (cloneLesson != null && rig.activeSeat != null)
            {
                Vector2 leftStick = Vector2.Lerp(a.leftIn.stick, b.leftIn.stick, k);
                Vector2 rightStick = Vector2.Lerp(a.rightIn.stick, b.rightIn.stick, k);
                if (controls.leftController != null)
                    controls.leftController._thumbstickAxis = leftStick;
                if (controls.rightController != null)
                    controls.rightController._thumbstickAxis = rightStick;
                controls._axisPrimary = leftStick;
                controls._axisPrimaryDirty = leftStick != Vector2.zero;
            }
        }

        private static void PutInput(BaseController c, Clone.HandInput a, Clone.HandInput b, float k, bool trigger)
        {
            if (c == null)
                return;
            if (!trigger)
            {
                a.trigger = b.trigger = 0f;
                a.primary = false;
            }
            c._processedIndex = Mathf.Lerp(a.index, b.index, k);
            c._processedMiddle = Mathf.Lerp(a.middle, b.middle, k);
            c._processedRing = Mathf.Lerp(a.ring, b.ring, k);
            c._processedPinky = Mathf.Lerp(a.pinky, b.pinky, k);
            c._processedThumb = Mathf.Lerp(a.thumb, b.thumb, k);
            // (Held just short of grabbing: what it holds, it takes hold of when the player did.)
            c._solvedGrip = Mathf.Min(Mathf.Lerp(a.grip, b.grip, k), OpenController.grabThreshold - 0.01f);
            bool was = c._primaryInteractionButton;
            c._primaryAxis = Mathf.Lerp(a.trigger, b.trigger, k);
            c._primaryInteractionButton = a.primary;
            c._primaryInteractionButtonDown = a.primary && !was;
            c._primaryInteractionButtonUp = !a.primary && was;
            c._secondaryInteractionButton = a.secondary;
        }

        /// <summary>Each physics step while playing: its body pulled along the player's path.</summary>
        public void CloneFixed(float dt)
        {
            GrabPlayerFixed();
            if (!PlayingClone || cloneDressing || !CloneAt(Time.time - cloneStart, out Clone.Frame a, out Clone.Frame b, out float k))
                return;
            Rigidbody pelvis = rig.physicsRig != null && rig.physicsRig.m_pelvis != null ? rig.physicsRig.m_pelvis.GetComponent<Rigidbody>() : null;
            if (pelvis == null)
                return;
            Vector3 want = Vector3.Lerp(a.pelvis, b.pelvis, k);
            Vector3 wantVelocity = Vector3.Lerp(a.pelvisVelocity, b.pelvisVelocity, k);
            if (cloneLesson != null)
            {
                // Carried off since the last step (through a portal), or the player was at this
                // moment: it goes on from where it is now, the same moves from there.
                bool carried = lessonPelvisHas && (pelvis.position - lessonPelvisWas).magnitude > 1.5f;
                // (Over the step in which they were carried off, it is already at the far side of it.)
                bool across = (b.pelvis - a.pelvis).magnitude > 1.5f;
                if (across)
                {
                    want = b.pelvis;
                    wantVelocity = b.pelvisVelocity;
                }
                bool theyWere = across && lessonJumpAt != cloneFrame;
                if (theyWere)
                    lessonJumpAt = cloneFrame;
                if (carried || theyWere)
                {
                    LessonFromHere(want, pelvis.position);
                    Lab.Event("lesson: " + (carried ? "carried off somewhere (a portal?)" : "you were carried off here") + ", going on from where it is");
                }
                lessonPelvisWas = pelvis.position;
                lessonPelvisHas = true;
                Vector3 theirs = want;
                want = LessonPlace(want);
                wantVelocity = Quaternion.Euler(0f, cloneYaw, 0f) * wantVelocity;
                // Far off its way (held up by something not there for the player): never put
                // back by force; it does the rest from where it has got to.
                if ((want - pelvis.position).magnitude > Tuning.F("clone.snap", 2.5f))
                {
                    LessonFromHere(theirs, pelvis.position);
                    return;
                }
            }
            // A climb: its body is taken up the way the player's went (its own stands where its
            // legs are: nothing it does with its hands lifts it), a little each step, and set
            // down on its own feet again once it is up and over.
            if (lessonLanding && rig.physicsRig.centerOfPressure != null)
            {
                // Set down on the top of its wall, a step at a time.
                Vector3 feetNow = rig.physicsRig.centerOfPressure.position;
                rig.Teleport(Vector3.MoveTowards(feetNow, lessonLandAt, Tuning.F("climb.glide.step", 0.03f)), true);
                lessonPelvisHas = false;
                CarriedFollow();
                return;
            }
            // (Sat in something: it is carried by that, not drawn along the player's way.)
            if (cloneLesson != null && rig.activeSeat != null)
            {
                lessonPelvisHas = false;
                return;
            }
            if (cloneLesson != null && lessonPurposeful && cloneLesson.purpose == "climb" && rig.physicsRig.centerOfPressure != null)
            {
                float up = want.y - pelvis.position.y;
                bool rising = Time.time - cloneStart >= cloneLesson.riseAt - 0.1f;
                if (rising && (up > 0.12f || (lessonGliding && up > -0.25f && !FeetOnGround())))
                {
                    if (!lessonGliding)
                        CarryThings(true);
                    lessonGliding = true;
                    glidedAt = Time.time;
                    Vector3 feet = rig.physicsRig.centerOfPressure.position;
                    Vector3 to = feet + (want - pelvis.position);
                    rig.Teleport(Vector3.MoveTowards(feet, to, Tuning.F("climb.glide.step", 0.03f)), true);
                    lessonPelvisHas = false;
                    CarriedFollow();
                    return;
                }
                lessonGliding = false;
                // (On its feet again a moment: its things back in its hands as things are held.)
                if (carried.Count > 0 && Time.time - glidedAt > 0.6f)
                    CarryThings(false);
            }
            CarriedFollow();
            Vector3 off = want - pelvis.position;
            // Far off its path (stuck, knocked away): put back on it.
            if (off.magnitude > Tuning.F("clone.snap", 2.5f))
            {
                try
                {
                    Vector3 feet = rig.physicsRig.centerOfPressure.position + off;
                    rig.Teleport(feet, false);
                }
                catch (Exception)
                {
                }
                return;
            }
            float gain = Tuning.F("clone.pull", 14f);
            // Along the player's path over the ground only: its own legs (and gravity) keep its
            // height (pulled to the player's hips' height, a body not quite theirs went up on its
            // toes); up and down only when they jumped or fell.
            Vector3 velocity = wantVelocity + new Vector3(off.x, 0f, off.z) * gain;
            Vector3 change = velocity - pelvis.velocity;
            if (Mathf.Abs(wantVelocity.y) < Tuning.F("clone.vertical", 1.2f))
                change.y = 0f;
            pelvis.AddForce(Vector3.ClampMagnitude(change * Tuning.F("clone.push", 18f), 120f), ForceMode.Acceleration);
            if (!rig.physicsRig.ballLocoEnabled)
            {
                Quaternion turn = LessonTurn(Quaternion.Slerp(a.pelvisTurn, b.pelvisTurn, k)) * Quaternion.Inverse(pelvis.rotation);
                turn.ToAngleAxis(out float angle, out Vector3 axis);
                if (angle > 180f)
                    angle -= 360f;
                Vector3 spin = Vector3.Lerp(a.pelvisSpin, b.pelvisSpin, k) + axis * (angle * Mathf.Deg2Rad * gain);
                if (!float.IsNaN(spin.x))
                    pelvis.AddTorque(Vector3.ClampMagnitude((spin - pelvis.angularVelocity) * Tuning.F("clone.push", 18f), 200f), ForceMode.Acceleration);
            }
        }

        // Its fists (and whatever it swings) hitting the player hurt them.
        private void HurtPlayer()
        {
            Transform you = Player.Head;
            Health health = Player.RigManager != null ? Player.RigManager.health : null;
            if (you == null || health == null)
                return;
            Hand[] hands = { LeftHand, RightHand };
            for (int i = 0; i < 2; i++)
            {
                Hand hand = hands[i];
                if (hand == null || hand.rb == null || Time.time - punchedAt[i] < 0.5f)
                    continue;
                Vector3 at = hand.transform.position;
                float toHead = (at - you.position).magnitude;
                float toChest = (at - (you.position - Vector3.up * 0.35f)).magnitude;
                float speed = hand.rb.velocity.magnitude;
                if ((toHead < 0.2f || toChest < 0.28f) && speed > Tuning.F("hurt.punch.speed", 2.5f))
                {
                    punchedAt[i] = Time.time;
                    float damage = health.max_Health * Tuning.F("hurt.punch", 0.06f) * Mathf.Clamp(speed / 3f, 0.5f, 2.5f);
                    try
                    {
                        health.TAKEDAMAGE(damage);
                    }
                    catch (Exception)
                    {
                    }
                    Lab.Event("clone: punched you (" + speed.ToString("0.0") + " m/s, " + damage.ToString("0.00") + ")");
                }
            }
        }
    }
}
