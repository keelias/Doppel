using System.Globalization;
using System.IO;
using System.Text;
using MelonLoader;
using UnityEngine;

namespace ThirdEyeBonelabMod.Bots
{
    /// <summary>
    /// Numbers the bot's handling can be tuned by while the game runs, kept in
    /// UserData/Bot Player/lab/tuning.txt (one "name = value" a line): read again whenever the
    /// file changes, and written out with every name in use (and its value) when new ones come
    /// up, so what can be tuned is always there to see.
    /// </summary>
    public static class Tuning
    {
        private static readonly Dictionary<string, float> values = new Dictionary<string, float>();
        private static readonly SortedDictionary<string, float> known = new SortedDictionary<string, float>();
        private static DateTime readAt;
        private static float checkAt;
        private static bool unwritten;

        private static string File => Path.Combine(Lab.Folder, "tuning.txt");

        public static float F(string name, float otherwise)
        {
            if (!known.ContainsKey(name))
            {
                known[name] = otherwise;
                unwritten = true;
            }
            return values.TryGetValue(name, out float v) ? v : otherwise;
        }

        /// <summary>A position (name.x, name.y, name.z).</summary>
        public static Vector3 V(string name, Vector3 otherwise)
        {
            return new Vector3(F(name + ".x", otherwise.x), F(name + ".y", otherwise.y), F(name + ".z", otherwise.z));
        }

        /// <summary>Once a frame: reads the file again if it has changed, and adds names new to it.</summary>
        public static void Update()
        {
            if (Time.time < checkAt)
                return;
            checkAt = Time.time + 1f;
            try
            {
                if (System.IO.File.Exists(File))
                {
                    DateTime at = System.IO.File.GetLastWriteTimeUtc(File);
                    if (at != readAt)
                    {
                        readAt = at;
                        Read();
                    }
                }
                if (unwritten)
                    Write();
            }
            catch (Exception e)
            {
                MelonLogger.Warning("Bot Player: tuning file: " + e.Message);
            }
        }

        private static void Read()
        {
            values.Clear();
            foreach (string raw in System.IO.File.ReadAllLines(File))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                    continue;
                int eq = line.IndexOf('=');
                if (eq <= 0)
                    continue;
                string name = line.Substring(0, eq).Trim();
                if (float.TryParse(line.Substring(eq + 1).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
                {
                    values[name] = v;
                    if (!known.ContainsKey(name))
                        known[name] = v;
                }
            }
            Lab.Event("tuning read (" + values.Count + " values)");
        }

        private static void Write()
        {
            unwritten = false;
            Directory.CreateDirectory(Lab.Folder);
            StringBuilder s = new StringBuilder("# Bot Player tuning: change a value and save; the game reads it within a second.\n");
            foreach (KeyValuePair<string, float> pair in known)
            {
                float v = values.TryGetValue(pair.Key, out float set) ? set : pair.Value;
                s.Append(pair.Key).Append(" = ").Append(v.ToString("0.####", CultureInfo.InvariantCulture)).Append('\n');
            }
            System.IO.File.WriteAllText(File, s.ToString());
            readAt = System.IO.File.GetLastWriteTimeUtc(File);
        }
    }
}
