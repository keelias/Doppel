using UnityEngine;

namespace ThirdEyeBonelabMod.Bots
{
    /// <summary>
    /// No snap from one move to the next. Its pose each frame comes from whatever it is doing
    /// (a move of the player's, a gun held low, the same gun brought up to fire, a wave, a
    /// reload), and where one of those takes over from another, its hands and head would be
    /// somewhere else from one frame to the next. Here, last of all, any such jump is taken up:
    /// the pose carries on from where it was and is eased over onto the new one in a third of a
    /// second (slow away, slow in), each hand, the head and its height by itself. Moves that
    /// are only fast (a punch, a throw) are not jumps, and are left just as they are.
    /// </summary>
    public partial class Bot
    {
        private struct Eased
        {
            public Vector3 from;
            public Quaternion turnFrom;
            public float at, turnAt;
            public bool turning, moving;
        }

        private Frame smoothOut;
        private bool smoothHas;
        private Eased easeLeft, easeRight, easeHead;
        private float easeHeightFrom, easeHeightAt = -10f;
        private float smoothSkipUntil;
        private int smoothDoing;
        // Whether its gun is up to fire (as against held low) this frame.
        private bool holdAimedNow;
        private int smoothFrame;

        /// <summary>Its pose is about to be somewhere else on purpose (put somewhere, got up): nothing to ease from.</summary>
        private void SmoothReset()
        {
            smoothHas = false;
        }

        // 1 at the start of an easing, 0 at its end (slow away from where it was, slow in to where it is going).
        private static float Left(float since, float time)
        {
            float u = Mathf.Clamp01(since / Mathf.Max(0.01f, time));
            return 1f - u * u * u * (u * (u * 6f - 15f) + 10f);
        }

        // Over its pose, last of all, each frame.
        private void Smooth(float dt)
        {
            float time = Tuning.F("smooth.time", 0.32f);
            // (Nothing to carry on from after a stretch of something else: on the ground, or
            // doing exactly what a recording says.)
            if (Time.frameCount - smoothFrame > 2)
                smoothHas = false;
            smoothFrame = Time.frameCount;
            if (time <= 0f || dt <= 0f || !smoothHas || Time.time < smoothSkipUntil)
            {
                smoothOut = pose;
                smoothHas = true;
                easeLeft.moving = easeLeft.turning = easeRight.moving = easeRight.turning = easeHead.turning = false;
                easeHeightAt = -10f;
                return;
            }
            // Eased when what it is doing changes (its gun brought up or let down, a reload
            // begun or done, a wave, a crouch, its hands taken up with something or let go):
            // those are the moments one pose takes over from another. (Told so, not guessed
            // from how far a hand went in a frame: guessed, a quick move of the player's own
            // was now and then taken for a jump and dragged.)
            int doing = (handOverride ? 1 : 0) | (Reloading ? 2 : 0) | (Time.time < waveUntil ? 4 : 0) | (crouchClip != null ? 8 : 0)
                        | (holdAimedNow ? 16 : 0) | (leftOverride ? 32 : 0) | (jumpClip != null ? 64 : 0) | ((int)play << 8);
            bool changed = doing != smoothDoing;
            smoothDoing = doing;
            bool auto = Tuning.F("smooth.auto", 0f) > 0.5f;
            float far = changed ? 0.02f : auto ? Mathf.Max(Tuning.F("smooth.jump", 0.07f), Tuning.F("smooth.speed", 6.5f) * dt) : float.MaxValue;
            float round = changed ? 3f : auto ? Mathf.Max(Tuning.F("smooth.turn", 22f), 900f * dt) : float.MaxValue;
            pose.leftPos = EasePlace(ref easeLeft, pose.leftPos, smoothOut.leftPos, far, time);
            pose.rightPos = EasePlace(ref easeRight, pose.rightPos, smoothOut.rightPos, far, time);
            pose.leftRot = EaseTurn(ref easeLeft, pose.leftRot, smoothOut.leftRot, round, time);
            pose.rightRot = EaseTurn(ref easeRight, pose.rightRot, smoothOut.rightRot, round, time);
            pose.head = EaseTurn(ref easeHead, pose.head, smoothOut.head, round, time);
            // Its height (a crouch begun or ended at a stroke).
            float tall = pose.height;
            if (Mathf.Abs(tall - smoothOut.height) > (changed ? 0.02f : auto ? Mathf.Max(0.05f, 2.5f * dt) : float.MaxValue))
            {
                easeHeightFrom = smoothOut.height - tall;
                easeHeightAt = Time.time;
            }
            float still = Left(Time.time - easeHeightAt, time);
            if (still > 0f)
                pose.height = tall + easeHeightFrom * still;
            smoothOut = pose;
        }

        // A hand's place: carried on from where it was when what it is asked for jumps, and eased onto it.
        private static Vector3 EasePlace(ref Eased e, Vector3 asked, Vector3 was, float far, float time)
        {
            Vector3 now = asked;
            if (e.moving)
            {
                float k = Left(Time.time - e.at, time);
                if (k <= 0f)
                    e.moving = false;
                else
                    now = asked + e.from * k;
            }
            if ((now - was).magnitude > far)
            {
                // (From where it was, whatever easing was still going on.)
                e.from = was - asked;
                e.at = Time.time;
                e.moving = true;
                now = was;
            }
            return now;
        }

        private static Quaternion EaseTurn(ref Eased e, Quaternion asked, Quaternion was, float round, float time)
        {
            Quaternion now = asked;
            if (e.turning)
            {
                float k = Left(Time.time - e.turnAt, time);
                if (k <= 0f)
                    e.turning = false;
                else
                    now = Quaternion.Slerp(Quaternion.identity, e.turnFrom, k) * asked;
            }
            if (Quaternion.Angle(now, was) > round)
            {
                e.turnFrom = was * Quaternion.Inverse(asked);
                e.turnAt = Time.time;
                e.turning = true;
                now = was;
            }
            return now;
        }
    }
}
