using BoneLib.BoneMenu;
using MelonLoader;
using ThirdEyeBonelabMod.Bots;
using UnityEngine;

[assembly: MelonInfo(typeof(BotPlayerMod.Mod), "Doppel", "1.0.0", "RUDYHILL")]
[assembly: MelonGame("Stress Level Zero", "BONELAB")]

namespace BotPlayerMod
{
    /// <summary>A bot player: a second real player body that walks about, follows you, copies your moves, picks things up and shoots, learning how to move from recordings of you.</summary>
    public class Mod : MelonMod
    {
        public override void OnInitializeMelon()
        {
            ThirdEyeBonelabMod.AssetFiles.Root = "Bot Player";

            BotLoader.Initialize(Page.Root);
            MelonLogger.Msg("Doppel ready (BoneMenu > Doppel)");
        }

        public override void OnUpdate()
        {
            BotLoader.Update();
        }

        public override void OnFixedUpdate()
        {
            BotLoader.FixedUpdate();
        }
    }
}
