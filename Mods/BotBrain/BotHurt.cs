using BoneLib;
using Il2CppSLZ.Marrow;
using Il2CppSLZ.Marrow.Audio;
using UnityEngine;

namespace ThirdEyeBonelabMod.Bots
{
    /// <summary>
    /// A bot hurt, as a living thing is. Its body is given its avatar's own surface (what every
    /// avatar is made of: flesh and its blood, or whatever that avatar has), on every part
    /// that takes damage: the game then does with it what it does with any creature, so a shot
    /// or a cut bleeds as that avatar bleeds, and a blade driven into it goes in and stays
    /// there (the game's own stabbing: held by a joint till it is pulled out). Hit, it cries
    /// out in its avatar's own voice (its small and big pain, its death), and starts.
    /// </summary>
    public partial class Bot
    {
        private IntPtr fleshOf;
        private float hurtAt = -100f;
        private float voiceAt = -10f;

        // Its body made of what its avatar is made of (again whenever it puts on another).
        private void Flesh()
        {
            Il2CppSLZ.VRMK.Avatar avatar = rig.avatar;
            if (avatar == null || avatar.Pointer == fleshOf || rig.physicsRig == null)
                return;
            fleshOf = avatar.Pointer;
            avatarCode = BotRig.AvatarOf(rig);
            // (Another body: its moves are another avatar's, from the next one on.)
            clip = null;
            lately.Clear();
            try
            {
                Il2CppSLZ.Data.SurfaceData data = avatar.surfaceData;
                // (An avatar with none of its own: the player's avatar's, else it is left as it is.)
                if (data == null && Player.RigManager != null && Player.RigManager.avatar != null)
                    data = Player.RigManager.avatar.surfaceData;
                // Blood: the game's own blood surface (what its creatures are made of), when it
                // is to be had; the avatar's own otherwise (the stock avatars' is "hydraulic").
                if (Tuning.F("flesh.blood", 1f) > 0f)
                {
                    AskForBlood();
                    if (bloodData != null)
                        data = bloodData;
                }
                string was = data != null ? data.name + ", resists a blade " + data.PenetrationResistance.ToString("0.00") + ", " + data.megaPascal.ToString("0.0") + " MPa" : "none";
                // (Its own copy, soft enough for a blade to go in: the stock avatars' surface
                // is as hard as metal to a knife, though it bleeds as theirs does.)
                if (data != null)
                {
                    float soft = Tuning.F("flesh.resist", 0.6f);
                    if (data.PenetrationResistance > soft)
                    {
                        Il2CppSLZ.Data.SurfaceData own = UnityEngine.Object.Instantiate(data);
                        own.name = data.name;
                        own.megaPascal = data.megaPascal * Mathf.Clamp01(soft / Mathf.Max(0.01f, data.PenetrationResistance)) * Tuning.F("flesh.mpa", 1f);
                        own.PenetrationResistance = soft;
                        data = own;
                    }
                }
                bool blood = false;
                try
                {
                    blood = Il2CppSLZ.Marrow.Warehouse.AssetWarehouse.Instance.HasDataCard(new Il2CppSLZ.Marrow.Warehouse.Barcode("SLZ.Backlot.SurfaceDataCard.Blood"));
                }
                catch (Exception)
                {
                }
                Lab.Event("flesh: its avatar's surface was " + was + "; a blood card in the game: " + blood);
                int parts = 0;
                foreach (PlayerDamageReceiver part in rig.GetComponentsInChildren<PlayerDamageReceiver>(true))
                {
                    if (part == null || part.GetComponent<Collider>() == null)
                        continue;
                    ImpactProperties ip = part.GetComponent<ImpactProperties>();
                    if (ip == null)
                    {
                        if (data == null)
                            continue;
                        ip = part.gameObject.AddComponent<ImpactProperties>();
                    }
                    // (Its card too: the game's impact sounds look a surface up by it.)
                    try
                    {
                        if (bloodData != null && data == bloodData)
                            ip.SurfaceDataCard = new Il2CppSLZ.Marrow.Warehouse.DataCardReference<Il2CppSLZ.Marrow.Warehouse.SurfaceDataCard>(new Il2CppSLZ.Marrow.Warehouse.Barcode("SLZ.Backlot.SurfaceDataCard.Blood"));
                    }
                    catch (Exception)
                    {
                    }
                    if (data != null)
                    {
                        ip.surfaceData = data;
                        ip._cachedSurfaceData = data;
                        ip._isSurfaceDataLoaded = true;
                    }
                    ip.decalType = ImpactProperties.DecalType.None;
                    parts++;
                }
                Lab.Event("flesh: " + avatar.name + " is made of " + (data != null ? data.name + " (resists a blade " + data.PenetrationResistance.ToString("0.00") + ")" : "nothing it says")
                    + ", on " + parts + " parts of its body; voice: " + Clips(avatar.smallPain) + " small pain, " + Clips(avatar.bigPain) + " big pain, " + Clips(avatar.dying) + " dying, " + Clips(avatar.dead) + " dead");
            }
            catch (Exception e)
            {
                Lab.Event("flesh: could not be given to its body: " + e.Message);
            }
        }

        private static Il2CppSLZ.Data.SurfaceData bloodData;
        private static bool bloodAsked;

        /// <summary>Its body is made again of what it should be (the blood surface has come).</summary>
        public void FleshAgain() => fleshOf = IntPtr.Zero;

        // The game's blood surface, asked for once (it comes a moment later, and the bots' bodies are then made of it).
        private static void AskForBlood()
        {
            if (bloodAsked)
                return;
            bloodAsked = true;
            try
            {
                var warehouse = Il2CppSLZ.Marrow.Warehouse.AssetWarehouse.Instance;
                if (!warehouse.TryGetDataCard<Il2CppSLZ.Marrow.Warehouse.SurfaceDataCard>(new Il2CppSLZ.Marrow.Warehouse.Barcode("SLZ.Backlot.SurfaceDataCard.Blood"), out var card) || card == null)
                {
                    Lab.Event("flesh: no blood card to be had");
                    return;
                }
                card.SurfaceData.LoadAsset((Action<Il2CppSLZ.Data.SurfaceData>)(loaded =>
                {
                    if (loaded == null)
                        return;
                    bloodData = loaded;
                    string effect = "none";
                    try
                    {
                        effect = loaded.impactEffectSpawnable != null && loaded.impactEffectSpawnable.crateRef != null ? loaded.impactEffectSpawnable.crateRef.Barcode.ID : "none";
                    }
                    catch (Exception)
                    {
                    }
                    Lab.Event("flesh: the game's blood is " + loaded.name + ", resists a blade " + loaded.PenetrationResistance.ToString("0.00") + ", " + loaded.megaPascal.ToString("0.0") + " MPa, effect " + effect);
                    foreach (Bot bot in BotManager.All)
                        bot.FleshAgain();
                }));
            }
            catch (Exception e)
            {
                Lab.Event("flesh: the blood could not be asked for: " + e.Message);
            }
        }

        private static int Clips(Il2CppSLZ.Data.AudioVarianceData data) => data != null && data.audioClips != null ? data.audioClips.Length : 0;

        // Hurt (and still alive): it cries out, starts, and looks to where it came from.
        private void Hurt(float damage, Il2CppSLZ.Marrow.Combat.Attack attack)
        {
            hurtAt = Time.time;
            bool big = damage > MaxHp * Tuning.F("bot.bigpain", 0.2f);
            Voice(big, false);
            Transform head = Head;
            if (head == null)
                return;
            // A start, away from the blow.
            Vector3 from = attack != null && attack.direction.sqrMagnitude > 0.01f ? -attack.direction : head.forward;
            Vector3 local = Quaternion.Euler(0f, -bodyYaw, 0f) * Flat(from).normalized;
            flinchSide = Mathf.Clamp(local.x, -1f, 1f);
            flinchSize = Mathf.Clamp(0.6f + damage / MaxHp, 0.6f, 1.4f);
            flinchAt = Time.time;
            // And a look that way (where the shot or the blow came from).
            if (attack != null && attack.direction.sqrMagnitude > 0.01f)
            {
                heardAt = head.position - attack.direction.normalized * 8f;
                heardTime = Time.time;
                gazeUntil = 0f;
            }
        }

        // Its avatar's own voice: pain (small or big), or its death.
        private void Voice(bool big, bool death)
        {
            if (!death && Time.time - voiceAt < Tuning.F("bot.voice.gap", 0.5f))
                return;
            voiceAt = Time.time;
            try
            {
                Il2CppSLZ.VRMK.Avatar avatar = rig.avatar;
                Transform head = Head;
                if (avatar == null || head == null)
                    return;
                Il2CppSLZ.Data.AudioVarianceData data = death ? (Clips(avatar.dead) > 0 ? avatar.dead : avatar.dying) : big ? avatar.bigPain : avatar.smallPain;
                if (Clips(data) == 0)
                    data = Clips(avatar.bigPain) > 0 ? avatar.bigPain : avatar.smallPain;
                if (Clips(data) == 0)
                {
                    // (An avatar with no voice of its own: the body's, if it has one.)
                    HeadSFX sfx = rig.GetComponentInChildren<HeadSFX>(true);
                    if (sfx != null)
                    {
                        if (death)
                            sfx.DeathVocal();
                        else if (big)
                            sfx.BigDamageVocal();
                        else
                            sfx.SmallDamageVocal(1f);
                    }
                    return;
                }
                Audio3dManager.PlayAtPoint(data.audioClips, head.position, Audio3dManager.hardInteraction, Tuning.F("bot.voice", 0.9f), 1f,
                    new Il2CppSystem.Nullable<float>(0.06f), new Il2CppSystem.Nullable<float>(1f), new Il2CppSystem.Nullable<float>(1f));
            }
            catch (Exception e)
            {
                Lab.Event("voice failed: " + e.Message);
            }
        }

        /// <summary>For the lab: hit as if by a blow from in front (so much of its health).</summary>
        public void LabHurt(float share)
        {
            TakeHit(MaxHp * share);
        }
    }
}
