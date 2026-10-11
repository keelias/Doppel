using BoneLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppSLZ.Bonelab;
using Il2CppSLZ.Marrow;
using Il2CppSLZ.Marrow.Interaction;
using Il2CppSLZ.Marrow.Pool;
using Il2CppSLZ.Marrow.Warehouse;
using MelonLoader;
using UnityEngine;

namespace ThirdEyeBonelabMod.Bots
{
    /// <summary>
    /// Makes the bot's body: a second copy of the game's own player rig (the very one the player
    /// is), with everything that belongs to the one real player taken off it, so that it sees
    /// nothing, hears nothing, reads none of the player's controllers, and sets off no level
    /// triggers, while keeping the whole physical body: hands, legs, avatar, health.
    /// </summary>
    public static class BotRig
    {
        /// <summary>What every bot's rig is called (how its own patches know it, even while it is being destroyed).</summary>
        public const string Name = "[RigManager (Bot)]";

        public static bool IsBot(RigManager rig)
        {
            return rig != null && rig.gameObject.name == Name;
        }

        /// <summary>Loads the player rig and makes a bot of it, standing at feet, facing along forward.</summary>
        public static void Create(Vector3 feet, Vector3 forward, Action<RigManager> made)
        {
            MarrowSettings settings = MarrowSettings.RuntimeInstance;
            SpawnableCrate crate = settings != null && settings.DefaultPlayerRig != null ? settings.DefaultPlayerRig.Crate : null;
            if (crate == null)
            {
                Spawnables.Notify("The game's player rig could not be found");
                return;
            }
            crate.MainGameObject.LoadAsset((Action<GameObject>)(asset =>
            {
                try
                {
                    made(Make(asset, feet, forward));
                }
                catch (Exception e)
                {
                    MelonLogger.Error("The bot's body could not be made: " + e);
                }
            }));
        }

        private static RigManager Make(GameObject asset, Vector3 feet, Vector3 forward)
        {
            RigManager source = asset != null ? asset.GetComponentInChildren<RigManager>(true) : null;
            if (source == null)
                throw new InvalidOperationException("the player rig has no RigManager");

            // Made under an inactive holder, so nothing of it wakes up before it is a bot.
            GameObject holder = new GameObject("ThirdEyeBotHolder");
            holder.SetActive(false);
            GameObject copy = UnityEngine.Object.Instantiate(source.gameObject, holder.transform);
            copy.name = Name;
            copy.SetActive(false);
            copy.transform.position = feet;
            copy.transform.rotation = Quaternion.LookRotation(forward, Vector3.up);
            RigManager rig = copy.GetComponent<RigManager>();
            Strip(rig);
            copy.transform.parent = null;
            UnityEngine.Object.Destroy(holder);
            copy.SetActive(true);
            Hear(rig);
            return rig;
        }

        // What makes a rig the player and not just a body.
        private static void Strip(RigManager rig)
        {
            OpenControllerRig controls = rig.ControllerRig.TryCast<OpenControllerRig>();
            PhysicsRig body = rig.physicsRig;

            // Not "the player" to the game: level triggers, checkpoints and the like ignore it.
            RemovePlayerTag(body.marrowEntity);

            // The controller rig is not an entity of its own (as on the player's rig, it would be
            // pooled and culled with the player's).
            foreach (MarrowBody part in controls.GetComponentsInChildren<MarrowBody>(true))
                UnityEngine.Object.DestroyImmediate(part);
            foreach (Tracker tracker in controls.GetComponentsInChildren<Tracker>(true))
                UnityEngine.Object.DestroyImmediate(tracker.gameObject);
            MarrowEntity controlsEntity = controls.GetComponent<MarrowEntity>();
            if (controlsEntity != null)
                UnityEngine.Object.DestroyImmediate(controlsEntity);

            // Its health: no reloading the level, no slow motion, no red vignette in the player's
            // eyes; and, for now, it cannot die.
            Player_Health health = rig.health != null ? rig.health.TryCast<Player_Health>() : null;
            if (health != null)
            {
                health.reloadLevelOnDeath = false;
                health.slowMoOnDeath = false;
                health.healthMode = Health.HealthMode.Invincible;
                if (health.Vignetter != null)
                {
                    GameObject vignette = UnityEngine.Object.Instantiate(health.Vignetter);
                    SkinnedMeshRenderer look = vignette.GetComponent<SkinnedMeshRenderer>();
                    if (look != null)
                        look.enabled = false;
                    vignette.name = "ThirdEyeBotVignetter";
                    vignette.SetActive(false);
                    health.Vignetter = vignette;
                }
            }

            // No menus, no slow-motion button, no double jump.
            controls.quickmenuEnabled = false;
            controls._timeInput = false;
            rig.remapHeptaRig.doubleJump = false;

            // Its head sees nothing (its camera would draw over the player's view).
            Transform headset = controls.headset;
            Blind(headset);
            controls.cameras = new Il2CppReferenceArray<Camera>(0);
            controls.onLastCameraUpdate = null;
            headset.tag = "Untagged";

            // Its own head is drawn (the player's art hides the head from the inside).
            PlayerAvatarArt art = rig.GetComponentInChildren<PlayerAvatarArt>(true);
            if (art != null)
                UnityEngine.Object.DestroyImmediate(art);

            // The ammo pouch is the player's own (ammo is shared by the one inventory), and the
            // holsters reach less far, so they do not snatch what the player holds near the bot.
            InventoryAmmoReceiver ammo = rig.GetComponentInChildren<InventoryAmmoReceiver>(true);
            if (ammo != null && ammo.GetComponent<Collider>() != null)
                ammo.GetComponent<Collider>().enabled = false;
            foreach (Hand hand in new[] { body.leftHand, body.rightHand })
            {
                PhysHand phys = hand != null ? hand.GetComponent<PhysHand>() : null;
                if (phys != null && phys.inventoryPlug != null)
                {
                    phys.inventoryPlug.enabled = false;
                    phys.inventoryPlug.gameObject.SetActive(false);
                }
            }
            foreach (InventorySlotReceiver slot in rig.GetComponentsInChildren<InventorySlotReceiver>(true))
            {
                foreach (BoxCollider reach in slot.GetComponentsInChildren<BoxCollider>(true))
                {
                    if (reach.isTrigger)
                        reach.size *= 0.4f;
                }
            }

            // Its hands read the bot's brain, not the player's controllers: plain controllers in
            // place of the ones that read the headset's, with no menus and no rumble.
            rig.ControllerRig.leftController = Detach(rig.ControllerRig, rig.ControllerRig.leftController, Handedness.LEFT);
            rig.ControllerRig.rightController = Detach(rig.ControllerRig, rig.ControllerRig.rightController, Handedness.RIGHT);

            // Pooled like any spawned thing (what asks a body for its Poolee finds one).
            if (body.GetComponent<Poolee>() == null)
                body.gameObject.AddComponent<Poolee>();
        }

        private static BaseController Detach(ControllerRig controls, BaseController original, Handedness side)
        {
            GameObject where = original.gameObject;
            Haptor haptor = original.haptor;
            UIControllerInput ui = where.GetComponent<UIControllerInput>();
            if (ui != null)
                UnityEngine.Object.DestroyImmediate(ui);
            BaseController plain = where.AddComponent<BaseController>();
            plain.contRig = controls;
            plain.handedness = side;
            if (haptor != null)
            {
                haptor.device_Controller = plain;
                haptor.hapticsAllowed = false;
                haptor.enabled = false;
            }
            return plain;
        }

        private static void RemovePlayerTag(MarrowEntity entity)
        {
            MarrowSettings settings = MarrowSettings.RuntimeInstance;
            if (entity == null || settings == null || settings.PlayerTag == null || settings.PlayerTag.Barcode == null)
                return;
            string player = settings.PlayerTag.Barcode.ID;
            TagList tags = entity.Tags;
            if (tags == null || tags.Tags == null)
                return;
            for (int i = tags.Tags.Count - 1; i >= 0; i--)
            {
                BoneTagReference tag = tags.Tags[i];
                if (tag != null && tag.Barcode != null && tag.Barcode.ID == player)
                    tags.Tags.RemoveAt(i);
            }
            tags.UpdateCache(false);
        }

        // Every camera and listener in a thing (an avatar's), and what hangs on them, taken out.
        private static int StripSenses(Transform root)
        {
            int count = 0;
            foreach (Camera camera in root.GetComponentsInChildren<Camera>(true))
            {
                foreach (Component part in camera.GetComponents<Component>())
                {
                    if (part == null || part is Transform || part is Camera)
                        continue;
                    UnityEngine.Object.DestroyImmediate(part);
                    count++;
                }
                try
                {
                    UnityEngine.Object.DestroyImmediate(camera);
                }
                catch (Exception)
                {
                    camera.enabled = false;
                }
                count++;
            }
            foreach (AudioListener ear in root.GetComponentsInChildren<AudioListener>(true))
            {
                UnityEngine.Object.DestroyImmediate(ear);
                count++;
            }
            return count;
        }

        // The head's camera and listener, and what hangs on them.
        private static void Blind(Transform headset)
        {
            foreach (Camera camera in headset.GetComponentsInChildren<Camera>(true))
            {
                foreach (Component part in camera.GetComponents<Component>())
                {
                    if (part == null || part is Transform || part is Camera)
                        continue;
                    UnityEngine.Object.DestroyImmediate(part);
                }
                try
                {
                    UnityEngine.Object.DestroyImmediate(camera);
                }
                catch (Exception)
                {
                    camera.enabled = false;
                }
            }
            foreach (AudioListener ear in headset.GetComponentsInChildren<AudioListener>(true))
                UnityEngine.Object.DestroyImmediate(ear);
        }

        // Its sounds come from it, not from inside the player's head.
        private static void Hear(RigManager rig)
        {
            foreach (AudioSource source in rig.GetComponentsInChildren<AudioSource>(true))
            {
                if (source != null)
                    source.spatialBlend = 1f;
            }
        }

        /// <summary>Called a moment after it is made too: some of its sounds are made late.</summary>
        public static void HearLate(RigManager rig)
        {
            if (rig != null)
                Hear(rig);
        }

        /// <summary>Gives the bot the player's own avatar (at the player's own scale).</summary>
        public static void CopyPlayerAvatar(RigManager rig, Action done)
        {
            RigManager player = Player.RigManager;
            if (rig == null || player == null || player.AvatarCrate == null || player.AvatarCrate.Barcode == null)
                return;
            Vector3 scale = player.avatar != null ? player.avatar.transform.localScale : Vector3.one;
            UseAvatar(rig, player.AvatarCrate.Barcode.ID, scale, done);
        }

        /// <summary>The avatar a rig wears (its barcode), or "" if it is not known.</summary>
        public static string AvatarOf(RigManager rig)
        {
            try
            {
                return rig != null && rig._avatarCrate != null && rig._avatarCrate.Barcode != null ? rig._avatarCrate.Barcode.ID : "";
            }
            catch (Exception)
            {
                return "";
            }
        }

        /// <summary>Puts an avatar (by barcode, at a scale) on the bot; done once it wears it.</summary>
        public static void UseAvatar(RigManager rig, string barcode, Vector3 scale, Action done)
        {
            AvatarCrate crate = null;
            AssetWarehouse warehouse = AssetWarehouse.Instance;
            if (rig == null || string.IsNullOrEmpty(barcode) || warehouse == null || !warehouse.TryGetCrate<AvatarCrate>(new Barcode(barcode), out crate) || crate == null)
            {
                done?.Invoke();
                return;
            }
            crate.MainGameObject.LoadAsset((Action<GameObject>)(asset =>
            {
                try
                {
                    if (rig == null || asset == null)
                        return;
                    // Made under a holder that is off, so nothing in it wakes before it is cleaned:
                    // a custom avatar may carry a camera (for a mirror, a face cam), and woken, it
                    // takes the headset's view from the player.
                    GameObject holder = new GameObject("ThirdEyeBotAvatarHolder");
                    holder.SetActive(false);
                    GameObject copy = UnityEngine.Object.Instantiate(asset, holder.transform);
                    copy.SetActive(false);
                    copy.name = asset.name;
                    int stripped = StripSenses(copy.transform);
                    if (stripped > 0)
                        MelonLogger.Msg("Bot Player: took " + stripped + " camera/listener parts out of the avatar " + asset.name);
                    copy.transform.localScale = scale;
                    copy.transform.parent = rig.transform;
                    UnityEngine.Object.Destroy(holder);
                    copy.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
                    Il2CppSLZ.VRMK.Avatar avatar = copy.GetComponentInParent<Il2CppSLZ.VRMK.Avatar>(true);
                    if (avatar == null)
                    {
                        UnityEngine.Object.Destroy(copy);
                        return;
                    }
                    HeadsetGuard.SwappedAt = Time.time;
                    rig.SwapAvatar(avatar);
                    rig._avatarCrate = new AvatarCrateReference(crate.Barcode);
                    Hear(rig);
                    done?.Invoke();
                }
                catch (Exception e)
                {
                    MelonLogger.Warning("The bot could not take the player's avatar: " + e.Message);
                }
            }));
        }
    }
}
