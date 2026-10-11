using BoneLib;
using Il2CppSLZ.Marrow;
using Il2CppSLZ.Marrow.Pool;
using MelonLoader;
using UnityEngine;

namespace ThirdEyeBonelabMod.Bots
{
    /// <summary>
    /// Only the player's own body draws to the headset. When a bot changes avatar, the game makes
    /// a second player body of its own (a blank one, with a headset camera), and that camera took
    /// the player's view: "out of my body". Every frame, any camera drawing to the headset that
    /// is not on the player's rig is switched off (with its listener); a blank body that turns up
    /// just after a bot changed avatar is taken away again. Each is said in the MelonLoader log,
    /// with what spawned the body.
    /// </summary>
    public static class HeadsetGuard
    {
        /// <summary>When a bot last changed avatar (BotRig.UseAvatar).</summary>
        public static float SwappedAt = -100f;

        private const float AfterSwap = 8f;
        private static readonly HashSet<IntPtr> said = new HashSet<IntPtr>();

        public static void Update()
        {
            // Only just after a bot changed avatar: at other times (a level loading, the player's
            // own body made again) the player's new body may not be known yet, and its camera
            // must never be taken for a second one.
            if (Time.time - SwappedAt > AfterSwap)
                return;
            RigManager player = Player.RigManager;
            if (player == null || !player.gameObject.activeInHierarchy)
                return;
            try
            {
                foreach (Camera cam in Camera.allCameras)
                {
                    if (cam == null || !cam.enabled || cam.targetTexture != null || cam.stereoTargetEye == StereoTargetEyeMask.None)
                        continue;
                    RigManager owner = cam.GetComponentInParent<RigManager>();
                    if (owner == null || owner == player)
                        continue;
                    cam.enabled = false;
                    AudioListener ear = cam.GetComponent<AudioListener>();
                    if (ear != null)
                        ear.enabled = false;
                    if (said.Add(cam.Pointer))
                        MelonLogger.Warning("Bot Player: a second player body (" + owner.name + ") had a headset camera on: switched off");
                    if (!BotRig.IsBot(owner))
                        Remove(owner);
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning("Bot Player headset guard: " + e.Message);
            }
        }

        // A blank body the game made for a bot's change of avatar: back to its pool.
        private static void Remove(RigManager rig)
        {
            try
            {
                Poolee poolee = rig.GetComponentInParent<Poolee>();
                if (poolee != null)
                    poolee.Despawn();
                else
                    UnityEngine.Object.Destroy(rig.gameObject);
                MelonLogger.Msg("Bot Player: the second player body was taken away");
            }
            catch (Exception e)
            {
                MelonLogger.Warning("Bot Player: the blank body could not be taken away: " + e.Message);
            }
        }

        public static void ForgetLevel()
        {
            SwappedAt = -100f;
            said.Clear();
        }

        /// <summary>A whole player body spawned (the level's own player, or one that should not be): said in the log.</summary>
        public static void OnSpawn(Poolee poolee)
        {
            try
            {
                if (poolee == null || poolee.SpawnableCrate == null)
                    return;
                string barcode = poolee.SpawnableCrate.Barcode.ID;
                if (!Clone.IsBody(barcode, poolee.gameObject.name) && poolee.GetComponentInChildren<RigManager>(true) == null)
                    return;
                MelonLogger.Msg("Bot Player: a player body was spawned (" + barcode + ")");
            }
            catch (Exception)
            {
            }
        }
    }
}
