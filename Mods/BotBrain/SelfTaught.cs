using System.IO;
using System.Text.Json;
using MelonLoader;
using UnityEngine;

namespace ThirdEyeBonelabMod.Bots
{
    /// <summary>
    /// What the bots have found out for themselves, by trying (kept between games in
    /// UserData/Bot Player/selftaught.json, and shared by all of them: what one finds out, the
    /// others know):
    /// <list type="bullet">
    /// <item>for each thing they have picked up and tried, what each press, pull and shake of
    /// it did (how much happened, on average, and what);</item>
    /// <item>how hard and how high to throw to hit something so far off (tried, and kept when it
    /// landed nearer than usual);</item>
    /// <item>each level: where they got stuck, which walls they failed to climb, and the high
    /// places they got up onto.</item>
    /// </list>
    /// </summary>
    public static class SelfTaught
    {
        public class Trial
        {
            public int tries { get; set; }
            public float effect { get; set; }
            public string what { get; set; } = "";
        }

        public class Item
        {
            public string name { get; set; } = "";
            public int sessions { get; set; }
            public Dictionary<string, Trial> actions { get; set; } = new Dictionary<string, Trial>();
        }

        public class Throw
        {
            public float speed { get; set; } = 8f;
            public float loft { get; set; } = 25f;
            public float spread { get; set; } = 1f;
            public float miss { get; set; } = -1f;
            public float firstMiss { get; set; } = -1f;
            public int tries { get; set; }
        }

        public class Level
        {
            public Dictionary<string, int> bad { get; set; } = new Dictionary<string, int>();
            public Dictionary<string, int> climbFails { get; set; } = new Dictionary<string, int>();
            public List<float[]> tops { get; set; } = new List<float[]>();
        }

        public class Store
        {
            public Dictionary<string, Item> items { get; set; } = new Dictionary<string, Item>();
            /// <summary>Things the player has had in their hands (barcode to name): what there is to try.</summary>
            public Dictionary<string, string> seen { get; set; } = new Dictionary<string, string>();
            /// <summary>Things found not to be things to pick up and try (a notice board, a fixture): never spawned for that again.</summary>
            public List<string> never { get; set; } = new List<string>();
            public Dictionary<string, Throw> throws { get; set; } = new Dictionary<string, Throw>();
            public Dictionary<string, Level> levels { get; set; } = new Dictionary<string, Level>();
        }

        /// <summary>Everything it can try with a thing in its hand.</summary>
        public static readonly string[] Actions =
        {
            "trigger/ahead", "trigger/down", "hold trigger/ahead", "hold trigger/down", "A/ahead", "A/down", "B/ahead", "B/down",
            "menu/ahead", "click/ahead", "shake", "swing",
        };

        private static Store store;
        private static bool dirty;
        private static float savedAt;

        private static string File => Path.Combine(AssetFiles.SaveFolder(""), "selftaught.json");

        public static Store Data
        {
            get
            {
                if (store != null)
                    return store;
                store = new Store();
                try
                {
                    if (System.IO.File.Exists(File))
                        store = JsonSerializer.Deserialize<Store>(System.IO.File.ReadAllText(File)) ?? new Store();
                }
                catch (Exception e)
                {
                    MelonLogger.Warning("Bot Player: what it taught itself could not be read: " + e.Message);
                }
                return store;
            }
        }

        public static void Save()
        {
            if (store == null || !dirty)
                return;
            try
            {
                System.IO.File.WriteAllText(File, JsonSerializer.Serialize(store, new JsonSerializerOptions { WriteIndented = true }));
                dirty = false;
            }
            catch (Exception e)
            {
                MelonLogger.Warning("Bot Player: what it taught itself could not be saved: " + e.Message);
            }
        }

        /// <summary>Now and then: saved if there is anything new.</summary>
        public static void Tick()
        {
            if (dirty && Time.time - savedAt > 20f)
            {
                savedAt = Time.time;
                Save();
            }
        }

        public static void Forget()
        {
            store = new Store();
            dirty = true;
            Save();
        }

        // ---------------------------------------------------------------- things tried

        /// <summary>The player had this in their hand: something there is to try.</summary>
        public static void Seen(string barcode, string name)
        {
            if (string.IsNullOrEmpty(barcode) || Data.seen.ContainsKey(barcode) || (Data.never != null && Data.never.Contains(barcode)))
                return;
            Data.seen[barcode] = name;
            dirty = true;
        }

        /// <summary>Not a thing to pick up and try, it turned out: forgotten, and never spawned for that again.</summary>
        public static void Never(string barcode)
        {
            if (string.IsNullOrEmpty(barcode))
                return;
            Data.never ??= new List<string>();
            if (!Data.never.Contains(barcode))
                Data.never.Add(barcode);
            Data.seen.Remove(barcode);
            dirty = true;
        }

        public static bool IsNever(string barcode) => Data.never != null && Data.never.Contains(barcode);

        public static Item Of(string barcode, string name)
        {
            if (!Data.items.TryGetValue(barcode, out Item item))
            {
                item = new Item { name = name };
                Data.items[barcode] = item;
                dirty = true;
            }
            return item;
        }

        /// <summary>How well it is known: 0 never tried, 1 everything tried a few times.</summary>
        public static float Known(string barcode)
        {
            if (!Data.items.TryGetValue(barcode, out Item item))
                return 0f;
            int tries = 0;
            foreach (string action in Actions)
                tries += item.actions.TryGetValue(action, out Trial t) ? Mathf.Min(t.tries, 3) : 0;
            return tries / (3f * Actions.Length);
        }

        /// <summary>The best thing found to do with it, and how much it does (0 if nothing found).</summary>
        public static float Best(string barcode, out string action, out string what)
        {
            action = what = "";
            float best = 0f;
            if (!Data.items.TryGetValue(barcode, out Item item))
                return 0f;
            foreach (KeyValuePair<string, Trial> pair in item.actions)
            {
                if (pair.Value.tries > 0 && pair.Value.effect > best)
                {
                    best = pair.Value.effect;
                    action = pair.Key;
                    what = pair.Value.what;
                }
            }
            return best;
        }

        /// <summary>Tried everything, more than once, and nothing does anything: not worth picking up to try again.</summary>
        public static bool Dull(string barcode) => Known(barcode) > 0.6f && Best(barcode, out _, out _) < 0.15f;

        /// <summary>
        /// What to try next with it: something never tried, first; then mostly what has done
        /// most, with now and then something that has done little (it may have been chance).
        /// </summary>
        public static string Choose(string barcode, string name, string not)
        {
            Item item = Of(barcode, name);
            List<string> untried = new List<string>();
            int total = 1;
            foreach (string action in Actions)
            {
                if (!item.actions.TryGetValue(action, out Trial t) || t.tries == 0)
                    untried.Add(action);
                else
                    total += t.tries;
            }
            untried.Remove(not);
            if (untried.Count > 0)
                return untried[UnityEngine.Random.Range(0, untried.Count)];
            string best = Actions[0];
            float bestScore = float.MinValue;
            foreach (string action in Actions)
            {
                if (action == not)
                    continue;
                Trial t = item.actions[action];
                float score = t.effect + Tuning.F("curious.explore", 0.35f) * Mathf.Sqrt(Mathf.Log(total) / t.tries) + UnityEngine.Random.value * 0.1f;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = action;
                }
            }
            return best;
        }

        /// <summary>The thing last found out (which thing, by which bot's place, when): the others come to try it.</summary>
        public static string NewsItem = "";
        public static string NewsName = "";
        public static Vector3 NewsAt;
        public static float NewsTime = -100f;

        /// <summary>A try made: how much happened. True if it is a discovery (something that does something, not known before).</summary>
        public static bool Tried(string barcode, string name, string action, float effect, string what, Vector3 at)
        {
            Item item = Of(barcode, name);
            if (!item.actions.TryGetValue(action, out Trial t))
                item.actions[action] = t = new Trial();
            bool known = Best(barcode, out _, out _) >= 0.35f;
            bool found = effect >= 0.4f && (t.tries == 0 || t.effect < 0.2f);
            t.effect = t.tries == 0 ? effect : Mathf.Lerp(t.effect, effect, 1f / Mathf.Min(t.tries + 1, 6));
            t.tries++;
            if (effect >= 0.3f && what.Length > 0)
                t.what = what;
            dirty = true;
            if (found)
            {
                NewsItem = barcode;
                NewsName = name;
                NewsAt = at;
                NewsTime = Time.time;
            }
            return found && !known;
        }

        // ---------------------------------------------------------------- throwing

        private static string Band(float far) => far < 6f ? "5" : far < 9f ? "8" : far < 13f ? "11" : "15";

        /// <summary>How to throw at something so far off, this time: its usual way, varied a little.</summary>
        public static void ThrowTry(float far, out float speed, out float loft)
        {
            string band = Band(far);
            if (!Data.throws.TryGetValue(band, out Throw t))
            {
                Data.throws[band] = t = new Throw();
                dirty = true;
            }
            speed = Mathf.Clamp(t.speed + Gauss() * t.spread * 1.6f, 3f, 20f);
            loft = Mathf.Clamp(t.loft + Gauss() * t.spread * 7f, 3f, 65f);
        }

        /// <summary>How that throw went (how far it missed by): kept, if nearer than its throws at that range usually land.</summary>
        public static string ThrowResult(float far, float speed, float loft, float miss)
        {
            Throw t = Data.throws[Band(far)];
            float usual = t.miss < 0f ? miss * 1.2f : t.miss;
            if (miss < usual)
            {
                // Nearer than usual: its way of throwing moves toward this one, and it varies less.
                float k = Mathf.Clamp01(0.3f + 0.5f * (usual - miss) / Mathf.Max(usual, 0.5f));
                t.speed = Mathf.Lerp(t.speed, speed, k);
                t.loft = Mathf.Lerp(t.loft, loft, k);
                t.spread = Mathf.Max(0.12f, t.spread * 0.88f);
            }
            else
            {
                t.spread = Mathf.Min(1.2f, t.spread * 1.04f);
            }
            if (t.firstMiss < 0f)
                t.firstMiss = miss;
            t.miss = t.miss < 0f ? miss : Mathf.Lerp(t.miss, miss, 0.2f);
            t.tries++;
            dirty = true;
            return "missed by " + miss.ToString("0.0") + " m (usually " + usual.ToString("0.0") + "), throw " + t.tries + " at that range";
        }

        private static float Gauss()
        {
            float u = Mathf.Max(1e-5f, UnityEngine.Random.value);
            return Mathf.Sqrt(-2f * Mathf.Log(u)) * Mathf.Cos(2f * Mathf.PI * UnityEngine.Random.value);
        }

        // ---------------------------------------------------------------- the level

        private static Level Here
        {
            get
            {
                string level = Lessons.Level;
                if (!Data.levels.TryGetValue(level, out Level l))
                    Data.levels[level] = l = new Level();
                return l;
            }
        }

        private static string Cell(Vector3 p) => Mathf.RoundToInt(p.x / 2f) + "_" + Mathf.RoundToInt(p.y / 2f) + "_" + Mathf.RoundToInt(p.z / 2f);

        /// <summary>Stuck here: somewhere not to pick to go to.</summary>
        public static void Bad(Vector3 at)
        {
            Level l = Here;
            string cell = Cell(at);
            l.bad[cell] = Mathf.Min(6, (l.bad.TryGetValue(cell, out int n) ? n : 0) + 1);
            dirty = true;
        }

        public static int BadAt(Vector3 at) => Here.bad.TryGetValue(Cell(at), out int n) ? n : 0;

        public static void ClimbFailed(Vector3 edge)
        {
            Level l = Here;
            string cell = Cell(edge);
            l.climbFails[cell] = (l.climbFails.TryGetValue(cell, out int n) ? n : 0) + 1;
            dirty = true;
        }

        public static int ClimbFails(Vector3 edge) => Here.climbFails.TryGetValue(Cell(edge), out int n) ? n : 0;

        /// <summary>Got up here: a high place worth coming back to (and its wall is one that can be climbed).</summary>
        public static void Top(Vector3 at)
        {
            Level l = Here;
            l.climbFails.Remove(Cell(at));
            foreach (float[] t in l.tops)
            {
                if ((new Vector3(t[0], t[1], t[2]) - at).magnitude < 3f)
                    return;
            }
            l.tops.Add(new[] { at.x, at.y, at.z });
            if (l.tops.Count > 40)
                l.tops.RemoveAt(0);
            dirty = true;
        }

        /// <summary>A high place it has been up to before, within reach; false if none.</summary>
        public static bool KnownTop(Vector3 from, float within, out Vector3 top)
        {
            top = default;
            List<Vector3> near = new List<Vector3>();
            foreach (float[] t in Here.tops)
            {
                Vector3 p = new Vector3(t[0], t[1], t[2]);
                float d = (p - from).magnitude;
                if (d > 6f && d < within)
                    near.Add(p);
            }
            if (near.Count == 0)
                return false;
            top = near[UnityEngine.Random.Range(0, near.Count)];
            return true;
        }

        public static string Summary()
        {
            int found = 0, tried = 0;
            foreach (Item item in Data.items.Values)
            {
                tried++;
                foreach (Trial t in item.actions.Values)
                    found += t.tries > 0 && t.effect >= 0.35f ? 1 : 0;
            }
            float now = 0f, first = 0f;
            int n = 0;
            foreach (Throw t in Data.throws.Values)
            {
                if (t.tries < 2)
                    continue;
                now += t.miss;
                first += t.firstMiss;
                n++;
            }
            Level l = Here;
            return "by itself: tried " + tried + " things and found " + found + " that do something"
                + (n > 0 ? "; throws miss by " + (now / n).ToString("0.0") + " m (" + (first / n).ToString("0.0") + " at first)" : "")
                + "; here it knows " + l.bad.Count + " bad spots, " + l.tops.Count + " high places";
        }
    }
}
