using BoneLib;
using Il2CppSLZ.Marrow;
using UnityEngine;

namespace ThirdEyeBonelabMod.Bots
{
    /// <summary>
    /// Using what the player showed it for what it is for. Every lesson says what the player got
    /// done with the thing (see Lessons: climbed, got somewhere, fought, moved things...), and
    /// where the bot has that to do and cannot do it by itself, it takes the thing and does it
    /// as they did:
    /// <list type="bullet">
    /// <item>a wall too high for it to climb by its hands (higher than climb.max), with
    /// somewhere to stand on top: the thing they climbed with, at the foot of the wall, their
    /// climbing done over and over till it is up, and then their getting over the top;</item>
    /// <item>something to fight, close, and nothing in its hands: the thing they fought with;</item>
    /// <item>things lying about: the thing they moved things with, turned to face them.</item>
    /// </list>
    /// </summary>
    public partial class Bot
    {
        private float purposeAt;

        /// <summary>A wall ahead too high to climb by hand, and its top: its foot (where to stand), the way into it, how high.</summary>
        private bool FindTallWall(Vector3 feet, Vector3 dir, float within, out Vector3 foot, out Vector3 inward, out float height, out Vector3 top)
        {
            foot = inward = top = Vector3.zero;
            height = 0f;
            if (!Physics.Raycast(feet + Vector3.up * 0.8f, dir, out RaycastHit wall, within, ~0, QueryTriggerInteraction.Ignore))
                return false;
            if (wall.collider.GetComponentInParent<RigManager>() != null || wall.collider.attachedRigidbody != null || Mathf.Abs(wall.normal.y) > 0.35f)
                return false;
            inward = -Flat(wall.normal);
            if (inward.sqrMagnitude < 0.01f)
                return false;
            inward.Normalize();
            // Its top: down from high above, just inside its face.
            Vector3 over = wall.point + inward * 0.45f;
            if (!Physics.Raycast(new Vector3(over.x, feet.y + Tuning.F("tool.climb.most", 30f), over.z), Vector3.down, out RaycastHit onTop, Tuning.F("tool.climb.most", 30f), ~0, QueryTriggerInteraction.Ignore))
                return false;
            height = onTop.point.y - feet.y;
            if (onTop.normal.y < 0.7f || height <= Tuning.F("climb.max", 2.5f) || onTop.collider.attachedRigidbody != null)
                return false;
            // (Room to stand up there, and the wall's face going all the way up to it.)
            if (Physics.Raycast(onTop.point + Vector3.up * 0.05f, Vector3.up, 1.6f, ~0, QueryTriggerInteraction.Ignore))
                return false;
            if (!Physics.Raycast(new Vector3(wall.point.x, onTop.point.y - 0.4f, wall.point.z) - inward * 0.6f, inward, 1.2f, ~0, QueryTriggerInteraction.Ignore))
                return false;
            top = onTop.point;
            foot = new Vector3(wall.point.x, feet.y, wall.point.z) - inward * Tuning.F("tool.climb.stand", 0.45f);
            return true;
        }

        /// <summary>A high wall somewhere about that it could get up with what it has been shown; false if none (or it has been shown nothing to climb with).</summary>
        private bool TallWallAbout(Vector3 feet, out Vector3 foot, out Vector3 inward, out float height, out Vector3 top)
        {
            foot = inward = top = Vector3.zero;
            height = 0f;
            if (Lessons.For("climb") == null)
                return false;
            float start = UnityEngine.Random.Range(0f, 360f);
            for (int i = 0; i < 12; i++)
            {
                Vector3 dir = Quaternion.Euler(0f, start + i * 30f, 0f) * Vector3.forward;
                if (FindTallWall(feet, dir, 25f, out foot, out inward, out height, out top) && SelfTaught.ClimbFails(top) < 2)
                    return true;
            }
            return false;
        }

        /// <summary>At the foot of a high wall: up it with the thing the player climbed with. False if it has nothing for that.</summary>
        private bool ClimbWithTool(Vector3 foot, Vector3 inward, float height)
        {
            Lessons.Lesson lesson = Lessons.For("climb");
            if (lesson == null || InLesson || Time.time - purposeAt < 4f)
                return false;
            // (One at a time at one place.)
            if (SomeoneClimbingAt(foot, Tuning.F("climb.apart", 1.8f)))
                return false;
            purposeAt = Time.time;
            // How many more times than the player its climbing stretch has to be done to reach the top.
            float more = height + 0.3f - lesson.rise;
            int loops = more > 0f && lesson.loopRise > 0.2f ? Mathf.CeilToInt(more / lesson.loopRise) : 0;
            if (more > 0.5f && loops == 0)
            {
                Lab.Event("purpose: the wall is " + height.ToString("0.0") + " m and the " + lesson.name + " lesson only goes up " + lesson.rise.ToString("0.0") + " m, with no stretch to do again");
                return false;
            }
            bodyYaw = Recorder.Yaw(inward);
            wallTopY = foot.y + height;
            Lab.Event("purpose: a " + height.ToString("0.0") + " m wall, up it with the " + lesson.name + " (" + loops + " stretches more than you did)");
            return UseLesson(lesson, false, Mathf.Max(0f, lesson.riseAt - 1.2f), loops, (foot, inward));
        }

        /// <summary>Now and then, where there is something its lessons are for: that, with the thing for it. True if it has started one.</summary>
        private bool MaybePurpose(Transform head)
        {
            if (InLesson || enemy || climbing || Playing || head == null || Time.time - purposeAt < Tuning.F("tool.every", 14f) || Lessons.Count == 0)
                return false;
            // Something to fight, close, and nothing in its hands.
            if (nearestSeen.transform != null && nearestSeenDistance < 4f && gun == null)
            {
                Lessons.Lesson fight = Lessons.For("fight");
                if (fight != null)
                {
                    purposeAt = Time.time;
                    Face(nearestSeen.Point, 1f, 720f);
                    Lab.Event("purpose: something to fight, with the " + fight.name);
                    lessonPurposeful = true;
                    bool begun = UseLesson(fight, false);
                    lessonPurposeful = begun;
                    return begun;
                }
            }
            return false;
        }

        /// <summary>For the lab: what its lessons are for, and up the nearest high wall with what it has for that.</summary>
        public void LabPurpose()
        {
            Lab.Event("purpose: " + Lessons.Purposes());
            Vector3 feet = rig.physicsRig != null && rig.physicsRig.centerOfPressure != null ? rig.physicsRig.centerOfPressure.position : Feet;
            if (TallWallAbout(feet, out Vector3 foot, out Vector3 inward, out float height, out Vector3 top))
            {
                Lab.Event("purpose: a wall " + height.ToString("0.0") + " m high, " + Flat(foot - feet).magnitude.ToString("0.0") + " m off");
                if (mode != Mode.Wander)
                    SetMode(Mode.Wander);
                StopPlaying();
                planPlace = foot;
                planLook = top;
                planSight = Sight.Tall;
                planWhy = "going up that wall with what you showed it";
                planWall = (foot, inward, height);
                planPush = MotionLibrary.WalkPush;
                planRun = false;
                viaLeft = false;
                onDetour = false;
                detours = 0;
                thinking2 = false;
                act = Act.Go;
                actUntil = Time.time + 90f;
                nav.Clear();
                nav.SetGoal(foot);
            }
            else
            {
                Lab.Event("purpose: no high wall about that it has anything to climb with");
            }
        }

        private (Vector3 foot, Vector3 inward, float height) planWall;
    }
}
