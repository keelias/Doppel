using System.Globalization;
using System.IO;
using System.Text.Json;
using MelonLoader;
using UnityEngine;

namespace ThirdEyeBonelabMod.Bots
{
    /// <summary>
    /// What the bots have learned, kept between games (UserData/Bot Player/brain.json):
    /// <list type="bullet">
    /// <item>for each thing it may choose to do, how well it goes, as a weighing of what it sees
    /// (enemies near or far, armed or not, low on ammo, the player near or far...): learned from
    /// every stretch of doing it, so what it learns in one fight carries over to any like it;</item>
    /// <item>how well each gun has done for it (kills for the time held);</item>
    /// <item>the distance it fights best at;</item>
    /// <item>the player's own way of fighting, from watching them (see PlayerWatch): their
    /// shooting rhythm, the distance they keep, whether they give ground, when they reload.</item>
    /// </list>
    /// </summary>
    public static class Knowledge
    {
        /// <summary>What it sees, as the numbers its choices are weighed by (each 0 to 1).</summary>
        public static readonly string[] FeatureNames =
        {
            "always", "enemy seen", "enemy close", "enemies many", "armed", "ammo low", "player far", "enemy lost", "close call lately", "player fighting",
        };

        public static int Features => FeatureNames.Length;

        public class GunRecord
        {
            public float seconds { get; set; }
            public int kills { get; set; }
            public int times { get; set; }
        }

        public class PlayerStyle
        {
            public List<float> gaps { get; set; } = new List<float>();
            public List<float> bursts { get; set; } = new List<float>();
            public List<float> pauses { get; set; } = new List<float>();
            public List<float> fightDistance { get; set; } = new List<float>();
            public List<float> giveGround { get; set; } = new List<float>();
            public List<float> reloadAt { get; set; } = new List<float>();
            public List<float> burstRounds { get; set; } = new List<float>();
            public List<float> strafe { get; set; } = new List<float>();
        }

        public class Store
        {
            public int version { get; set; } = 2;
            public Dictionary<string, float[]> weights { get; set; } = new Dictionary<string, float[]>();
            public Dictionary<string, int> tries { get; set; } = new Dictionary<string, int>();
            public Dictionary<string, GunRecord> guns { get; set; } = new Dictionary<string, GunRecord>();
            public PlayerStyle player { get; set; } = new PlayerStyle();
            public float fightDistance { get; set; } = -1f;
            public int kills { get; set; }
            public int closeCalls { get; set; }
            public float minutes { get; set; }
        }

        private const int Kept = 300;

        private static Store store;
        private static bool dirty;
        private static float savedAt;

        private static string File => Path.Combine(AssetFiles.SaveFolder(""), "brain.json");

        public static Store Data
        {
            get
            {
                if (store == null)
                    Load();
                return store;
            }
        }

        private static void Load()
        {
            store = new Store();
            try
            {
                if (System.IO.File.Exists(File))
                {
                    Store read = JsonSerializer.Deserialize<Store>(System.IO.File.ReadAllText(File));
                    // (What the first brain learned was kept another way: its guns and distance are kept.)
                    if (read != null && read.version >= 2)
                        store = read;
                    else if (read != null)
                    {
                        store.guns = read.guns ?? store.guns;
                        store.fightDistance = read.fightDistance;
                        store.kills = read.kills;
                        store.closeCalls = read.closeCalls;
                        store.minutes = read.minutes;
                    }
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning("Bot Player: what it learned could not be read (starting afresh): " + e.Message);
                store = new Store();
            }
            store.player ??= new PlayerStyle();
            store.weights ??= new Dictionary<string, float[]>();
        }

        public static void Save()
        {
            if (store == null || !dirty)
                return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(File));
                System.IO.File.WriteAllText(File, JsonSerializer.Serialize(store, new JsonSerializerOptions { WriteIndented = true }));
                dirty = false;
                savedAt = Time.time;
            }
            catch (Exception e)
            {
                MelonLogger.Warning("Bot Player: what it learned could not be saved: " + e.Message);
            }
        }

        /// <summary>Once a frame: saves now and then.</summary>
        public static void Tick(float dt, bool anyBot)
        {
            if (anyBot && store != null)
            {
                store.minutes += dt / 60f;
                if (Time.time - savedAt > 30f)
                {
                    dirty = true;
                    Save();
                }
            }
        }

        public static void Forget()
        {
            store = new Store();
            dirty = true;
            Save();
        }

        // ---------------------------------------------------------------- choices, weighed by what it sees

        private static float[] Weights(string choice)
        {
            if (!Data.weights.TryGetValue(choice, out float[] w) || w == null || w.Length != Features)
                Data.weights[choice] = w = new float[Features];
            return w;
        }

        /// <summary>How well a choice is expected to go, given what it sees: -1 to 1.</summary>
        public static float Worth(string choice, float[] seen)
        {
            float[] w = Weights(choice);
            float sum = 0f;
            for (int i = 0; i < Features; i++)
                sum += w[i] * seen[i];
            return Mathf.Clamp(sum, -1f, 1f);
        }

        public static int Tries(string choice)
        {
            return Data.tries.TryGetValue(choice, out int n) ? n : 0;
        }

        /// <summary>
        /// How a stretch of a choice went (reward for each ten seconds of it), given what it saw:
        /// each weight moved by its part in the miss (a quick learner at first, steadier later).
        /// </summary>
        public static void Learn(string choice, float[] seen, float outcome)
        {
            float[] w = Weights(choice);
            float error = Mathf.Clamp(outcome, -1f, 1f) - Worth(choice, seen);
            float norm = 0f;
            for (int i = 0; i < Features; i++)
                norm += seen[i] * seen[i];
            Data.tries.TryGetValue(choice, out int n);
            Data.tries[choice] = n + 1;
            float rate = Mathf.Lerp(0.5f, 0.15f, Mathf.Clamp01(n / 60f)) / Mathf.Max(norm, 1f);
            for (int i = 0; i < Features; i++)
                w[i] = Mathf.Clamp(w[i] + rate * error * seen[i], -2f, 2f);
            dirty = true;
        }

        // ---------------------------------------------------------------- guns

        public static void HeldGun(string barcode, float seconds)
        {
            if (string.IsNullOrEmpty(barcode))
                return;
            Gun(barcode).seconds += seconds;
            dirty = true;
        }

        public static void KilledWith(string barcode, float distance)
        {
            Data.kills++;
            if (!string.IsNullOrEmpty(barcode))
                Gun(barcode).kills++;
            float d = FightDistance;
            Data.fightDistance = Mathf.Clamp(d + 0.1f * (distance - d), 3f, 25f);
            dirty = true;
        }

        public static void CloseCall()
        {
            Data.closeCalls++;
            // Too close for comfort: it keeps a little further off from now on.
            Data.fightDistance = Mathf.Clamp(FightDistance + 0.3f, 3f, 25f);
            dirty = true;
        }

        private static GunRecord Gun(string barcode)
        {
            if (!Data.guns.TryGetValue(barcode, out GunRecord r))
                Data.guns[barcode] = r = new GunRecord();
            return r;
        }

        /// <summary>
        /// Which of these guns to take: the one that has done best for it, now and then one it
        /// knows less (so it keeps finding out).
        /// </summary>
        public static string PickGun(List<string> have)
        {
            if (have.Count == 0)
                return null;
            int total = 1;
            foreach (string id in have)
                total += Data.guns.TryGetValue(id, out GunRecord r) ? r.times : 0;
            string best = have[0];
            float bestScore = float.MinValue;
            foreach (string id in have)
            {
                Data.guns.TryGetValue(id, out GunRecord r);
                float kills = r != null ? r.kills : 0f;
                float minutes = r != null ? r.seconds / 60f : 0f;
                int times = r != null ? r.times : 0;
                float rate = (kills + 1f) / (minutes + 1f);
                float curiosity = 1.2f * Mathf.Sqrt(Mathf.Log(total + 1f) / (times + 1f));
                float score = rate + curiosity + UnityEngine.Random.value * 0.3f;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = id;
                }
            }
            Gun(best).times++;
            dirty = true;
            return best;
        }

        // ---------------------------------------------------------------- distance

        /// <summary>
        /// The distance it fights at: the player's own (watched, or from their recordings), moved
        /// by its own kills and close calls.
        /// </summary>
        public static float FightDistance
        {
            get
            {
                if (Data.fightDistance > 0f)
                    return Data.fightDistance;
                float watched = Style.Median(Style.fightDistance, -1f);
                if (watched > 0f)
                    return Mathf.Clamp(watched, 3f, 25f);
                return MotionLibrary.PlayerFightDistance > 0f ? MotionLibrary.PlayerFightDistance : 9f;
            }
        }

        // ---------------------------------------------------------------- the player's style

        public static class Style
        {
            public static List<float> gaps => Data.player.gaps;
            public static List<float> bursts => Data.player.bursts;
            public static List<float> pauses => Data.player.pauses;
            public static List<float> fightDistance => Data.player.fightDistance;
            public static List<float> giveGround => Data.player.giveGround;
            public static List<float> reloadAt => Data.player.reloadAt;
            public static List<float> burstRounds => Data.player.burstRounds ??= new List<float>();
            public static List<float> strafe => Data.player.strafe ??= new List<float>();

            public static void Add(List<float> list, float value)
            {
                list.Add(value);
                if (list.Count > Kept)
                    list.RemoveAt(0);
                dirty = true;
            }

            /// <summary>One of what was seen, at random (and a little varied), or otherwise.</summary>
            public static float Sample(List<float> list, float otherwise)
            {
                if (list.Count < 8)
                    return otherwise;
                return list[UnityEngine.Random.Range(0, list.Count)] * UnityEngine.Random.Range(0.88f, 1.12f);
            }

            public static float Median(List<float> list, float otherwise)
            {
                if (list.Count < 8)
                    return otherwise;
                List<float> sorted = new List<float>(list);
                sorted.Sort();
                return sorted[sorted.Count / 2];
            }

            public static float Mean(List<float> list, float otherwise)
            {
                if (list.Count < 8)
                    return otherwise;
                float sum = 0f;
                foreach (float v in list)
                    sum += v;
                return sum / list.Count;
            }
        }

        /// <summary>A line on what it has learned, for the menu.</summary>
        public static string Summary()
        {
            Store d = Data;
            string bestGun = "none yet";
            float bestRate = -1f;
            foreach (KeyValuePair<string, GunRecord> pair in d.guns)
            {
                if (pair.Value.kills == 0)
                    continue;
                float rate = pair.Value.kills / Mathf.Max(pair.Value.seconds / 60f, 0.5f);
                if (rate > bestRate)
                {
                    bestRate = rate;
                    int dot = pair.Key.LastIndexOf('.');
                    bestGun = dot >= 0 ? pair.Key.Substring(dot + 1) : pair.Key;
                }
            }
            int watched = d.player.gaps.Count + d.player.bursts.Count;
            return d.kills + " kills, " + d.closeCalls + " close calls, " + d.minutes.ToString("0", CultureInfo.InvariantCulture)
                + " min; fights at " + FightDistance.ToString("0", CultureInfo.InvariantCulture) + " m; best gun " + bestGun
                + "; watched " + watched + " of your shots";
        }
    }
}
