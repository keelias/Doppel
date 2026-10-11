using BoneLib.Notifications;
using MelonLoader;
using MelonLoader.Utils;

namespace ThirdEyeBonelabMod
{
    /// <summary>Where the mod keeps its own files: UserData/(mod name).</summary>
    public static class AssetFiles
    {
        /// <summary>The mod's name: what its own saved files are kept under in UserData.</summary>
        public static string Root = "Mod";

        /// <summary>A folder the mod may write its own files to: UserData/(mod name)/(name).</summary>
        public static string SaveFolder(string name)
        {
            return Path.Combine(MelonEnvironment.UserDataDirectory, Root, name);
        }
    }

    /// <summary>Telling the player something: a line in the log and a notice in the headset.</summary>
    public static class Spawnables
    {
        public static void Notify(string message)
        {
            MelonLogger.Msg(message);
            Notifier.Send(new Notification
            {
                Title = "Doppel",
                Message = message,
                Type = NotificationType.Information,
                PopupLength = 1.5f,
                ShowTitleOnPopup = true,
            });
        }
    }
}
