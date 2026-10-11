using UnityEngine;

namespace ThirdEyeBonelabMod.Bots
{
    /// <summary>
    /// The player's own chosen moves (see MoveSlots) on a bot, where they are not its walking
    /// and standing (those come through its gaits, MotionLibrary): its wave, the swing of its
    /// arms in a jump, a crouch now and then as it stands about, and a move shown once for the
    /// player to see what they recorded.
    /// </summary>
    public partial class Bot
    {
        // The avatar it has on (its barcode): its moves are that avatar's.
        private string avatarCode = "";

        private Frame[] waveClip;
        private Frame[] jumpClip;
        private Frame[] crouchClip;
        private float crouchStart;
        private bool crouchReset;
        private Frame[] previewClip;
        private bool previewing;
        private Mode previewBack = Mode.Stand;

        // A wave begun: the player's own, if they have recorded one (it lasts as long as theirs did).
        private void WaveOfTheirs()
        {
            waveClip = MoveSlots.Pick(MoveSlots.Kind.Wave, null, avatarCode);
            if (waveClip != null)
                waveUntil = waveStart + waveClip[waveClip.Length - 1].time;
        }

        // A move's head and hands over its pose (eased in and out at its ends), sized to its body.
        private void OverClip(Frame[] from, float t, float head)
        {
            float length = from[from.Length - 1].time;
            Frame f = MotionLibrary.Sample(from, t);
            float k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(Mathf.Min(t, length - t) / 0.25f));
            float size = f.height > 0.3f ? pose.height / f.height : 1f;
            pose.leftPos = Vector3.Lerp(pose.leftPos, f.leftPos * size, k);
            pose.rightPos = Vector3.Lerp(pose.rightPos, f.rightPos * size, k);
            pose.leftRot = Quaternion.Slerp(pose.leftRot, f.leftRot, k);
            pose.rightRot = Quaternion.Slerp(pose.rightRot, f.rightRot, k);
            pose.head = Quaternion.Slerp(pose.head, f.head, k * head);
            curl = -1f;
            pose.leftGrip = f.leftGrip;
            pose.rightGrip = f.rightGrip;
        }

        // In the air: its arms as the player's were in their jump (from the moment they left the ground).
        private void JumpOfTheirs()
        {
            if (jumpClip == null)
                return;
            float t = Time.time - jumpedAt + MoveSlots.JumpAt(jumpClip);
            if (t >= jumpClip[jumpClip.Length - 1].time || Time.time - jumpedAt > 3f)
            {
                jumpClip = null;
                return;
            }
            OverClip(jumpClip, t, 0.6f);
        }

        private void StartCrouchOfTheirs()
        {
            crouchClip = MoveSlots.Pick(MoveSlots.Kind.Crouch, null, avatarCode);
            if (crouchClip == null)
                return;
            crouchStart = Time.time;
            actUntil = Mathf.Max(actUntil, Time.time + crouchClip[crouchClip.Length - 1].time + 0.5f);
            Lab.Event("crouches as you do");
        }

        // Standing about: down, and up again, as the player did it (their legs' bend with it).
        private void CrouchOfTheirs()
        {
            if (crouchClip == null)
                return;
            float t = Time.time - crouchStart;
            bool moving = walk.sqrMagnitude > 0.01f;
            if (t >= crouchClip[crouchClip.Length - 1].time || moving || act != Act.Idle || Playing || climbing)
            {
                crouchClip = null;
                crouchReset = true;
                return;
            }
            Frame f = MotionLibrary.Sample(crouchClip, t);
            float keep = pose.height;
            OverClip(crouchClip, t, 0.5f);
            // (Its head goes down with theirs, in proportion to its own height.)
            float standing = crouchClip[0].height > 0.3f ? crouchClip[0].height : standingHeight;
            pose.height = keep * Mathf.Clamp(f.height / standing, 0.35f, 1.05f);
            crouching = true;
            crouch = f.crouch;
            feet = f.feet;
        }

        /// <summary>Does a move once, as it was recorded (walking where the player walked in it), then goes back to what it was at.</summary>
        public void Preview(Frame[] clip)
        {
            if (clip == null || clip.Length < 2 || dead || Down || climbing || InLesson)
                return;
            Mode back = mode == Mode.Replay || mode == Mode.Clone ? Mode.Stand : mode;
            previewClip = clip;
            SetMode(Mode.Replay);
            previewing = mode == Mode.Replay;
            previewBack = back;
            Lab.Event("moves: shown on a bot (" + clip[clip.Length - 1].time.ToString("0.0") + " s)");
        }
    }
}
