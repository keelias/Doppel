using BoneLib.BoneMenu;
using MelonLoader;
using ThirdEyeBonelabMod.Bots;
using UnityEngine;

[assembly: MelonInfo(typeof(DoppelMod.Mod), "Doppel", "1.0.0", "RUDYHILL")]
[assembly: MelonGame("Stress Level Zero", "BONELAB")]

namespace DoppelMod
{
    /// <summary>Doppel, by RUDYHILL: bots that are real player bodies, and learn how to move and what to do from recordings of the player.</summary>
    public class Mod : MelonMod
    {
        private static bool started;
        private static float errorSaidAt = -10f;

        public override void OnInitializeMelon()
        {
            ThirdEyeBonelabMod.AssetFiles.Root = "Bot Player";
            try
            {
                Entry.Start(Page.Root.CreatePage("Doppel", Color.cyan));
                started = true;
                MelonLogger.Msg("Doppel ready (BoneMenu > Doppel)");
            }
            catch (Exception e)
            {
                MelonLogger.Error("Doppel could not start: " + e);
            }
        }

        public override void OnUpdate()
        {
            Run(Entry.Update);
        }

        public override void OnFixedUpdate()
        {
            Run(Entry.FixedUpdate);
        }

        public override void OnDeinitializeMelon()
        {
            if (started)
                Run(Entry.Stop);
        }

        private static void Run(Action step)
        {
            if (!started)
                return;
            try
            {
                step();
            }
            catch (Exception e)
            {
                // (Said now and then, not every frame.)
                if (Time.realtimeSinceStartup - errorSaidAt > 5f)
                {
                    errorSaidAt = Time.realtimeSinceStartup;
                    MelonLogger.Error("Doppel: " + e);
                }
            }
        }
    }
}
