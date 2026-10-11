using System.Globalization;
using System.IO;
using UnityEngine;

namespace ThirdEyeBonelabMod.Bots
{
    /// <summary>
    /// Getting better at something by trying. A skill comes down to a handful of settings; each
    /// go at it is scored; most goes try the settings a little changed (one bot at a time), and
    /// a change that scores better than what there was, twice running, is kept: the next
    /// changes start from there. Kept between games in a file of its own in
    /// UserData/Bot Player, and shared by all the bots. (Driving: see DriveSkill. Shooting: see
    /// ShootSkill.)
    /// </summary>
    public class Practice
    {
        private readonly string label, fileName;
        private readonly string[] names;
        private readonly float[] first, least, most;
        private readonly float better, muchWorse;

        private float[] best;
        private float bestScore = -1f;
        private int runs, kept;
        private float[] trial;
        private IntPtr trialBy;
        private float trialSum;
        private int trialRuns, sinceBase;
        private string trialWhat = "";

        public Practice(string label, string fileName, string[] names, float[] first, float[] least, float[] most, float better, float muchWorse)
        {
            this.label = label;
            this.fileName = fileName;
            this.names = names;
            this.first = first;
            this.least = least;
            this.most = most;
            this.better = better;
            this.muchWorse = muchWorse;
        }

        private string File => Path.Combine(AssetFiles.SaveFolder(""), fileName);

        private void Load()
        {
            if (best != null)
                return;
            best = (float[])first.Clone();
            try
            {
                if (!System.IO.File.Exists(File))
                    return;
                foreach (string line in System.IO.File.ReadAllLines(File))
                {
                    string[] p = line.Split('=');
                    if (p.Length != 2 || !float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
                        continue;
                    int i = Array.IndexOf(names, p[0]);
                    if (i >= 0)
                        best[i] = Mathf.Clamp(v, least[i], most[i]);
                    else if (p[0] == "score")
                        bestScore = v;
                    else if (p[0] == "runs")
                        runs = (int)v;
                    else if (p[0] == "kept")
                        kept = (int)v;
                }
            }
            catch (Exception)
            {
            }
        }

        private void Save()
        {
            try
            {
                List<string> lines = new List<string>();
                for (int i = 0; i < names.Length; i++)
                    lines.Add(names[i] + "=" + best[i].ToString("0.###", CultureInfo.InvariantCulture));
                lines.Add("score=" + bestScore.ToString("0.###", CultureInfo.InvariantCulture));
                lines.Add("runs=" + runs);
                lines.Add("kept=" + kept);
                System.IO.File.WriteAllLines(File, lines);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>Back to how it was before anything was learned.</summary>
        public void Reset()
        {
            best = (float[])first.Clone();
            bestScore = -1f;
            runs = kept = 0;
            trial = null;
            Save();
        }

        public string Said()
        {
            Load();
            List<string> said = new List<string>();
            for (int i = 0; i < names.Length; i++)
                said.Add(names[i] + " " + best[i].ToString("0.00") + (Mathf.Abs(best[i] - first[i]) > 0.005f ? " (was " + first[i].ToString("0.00") + ")" : ""));
            return runs + " goes scored, " + kept + " changes kept, usual score " + (bestScore < 0f ? "none yet" : bestScore.ToString("0.00")) + ": " + string.Join(", ", said)
                   + (trial != null ? "; trying " + trialWhat + " (" + trialRuns + " of 2)" : "");
        }

        /// <summary>The settings for a go about to begin: the best found, or (most times, one bot at a time) that a little changed, to try.</summary>
        public float[] Begin(IntPtr who, out bool trying)
        {
            Load();
            trying = false;
            // (Before anything is tried, and every third go after: as it is, to know how good that is.)
            if (bestScore < 0f || sinceBase >= 2 || (trial != null && trialBy != who))
                return (float[])best.Clone();
            if (trial == null)
            {
                trial = (float[])best.Clone();
                List<string> what = new List<string>();
                int changes = UnityEngine.Random.value < 0.5f ? 1 : 2;
                for (int c = 0; c < changes; c++)
                {
                    int i = UnityEngine.Random.Range(0, names.Length);
                    float by = UnityEngine.Random.Range(0.08f, 0.22f) * (UnityEngine.Random.value < 0.5f ? -1f : 1f);
                    float was = trial[i];
                    trial[i] = Mathf.Clamp(trial[i] * (1f + by), least[i], most[i]);
                    if (Mathf.Abs(trial[i] - was) > 0.001f)
                        what.Add(names[i] + " " + was.ToString("0.00") + " -> " + trial[i].ToString("0.00"));
                }
                if (what.Count == 0)
                {
                    trial = null;
                    return (float[])best.Clone();
                }
                trialWhat = string.Join(", ", what);
                trialBy = who;
                trialSum = 0f;
                trialRuns = 0;
            }
            trying = true;
            return (float[])trial.Clone();
        }

        /// <summary>A go over: its score, and how it went in words.</summary>
        public void End(IntPtr who, bool trying, float score, string how)
        {
            Load();
            runs++;
            if (!trying || trial == null || trialBy != who)
            {
                sinceBase = 0;
                bestScore = bestScore < 0f ? score : Mathf.Lerp(bestScore, score, 0.3f);
                Lab.Event(label + ": as it is now, " + how + " (its usual " + bestScore.ToString("0.00") + ")");
                Save();
                return;
            }
            sinceBase++;
            trialSum += score;
            trialRuns++;
            // (Much worse at once: dropped without a second go.)
            if (trialRuns == 1 && score > bestScore - muchWorse)
            {
                Lab.Event(label + ": trying " + trialWhat + ", " + how + " (its usual " + bestScore.ToString("0.00") + "), once more");
                Save();
                return;
            }
            float mean = trialSum / trialRuns;
            if (trialRuns >= 2 && mean > bestScore + better)
            {
                best = trial;
                bestScore = Mathf.Lerp(bestScore, mean, 0.5f);
                kept++;
                Lab.Event(label + ": KEPT " + trialWhat + " (" + mean.ToString("0.00") + " over two goes; now its usual " + bestScore.ToString("0.00") + ")");
            }
            else
            {
                Lab.Event(label + ": " + trialWhat + " was no better (" + how + "; mean " + mean.ToString("0.00") + " against " + bestScore.ToString("0.00") + "), dropped");
            }
            trial = null;
            Save();
        }
    }

    /// <summary>
    /// The bots getting better at driving by themselves (see Practice). Every drive of their
    /// own is scored: how fast it got them along, all told, with time added for each time it
    /// was stopped by something and had to back out, each time it had to back round, and a
    /// lot for giving up or turning over.
    /// </summary>
    public static class DriveSkill
    {
        public const int Pace = 0, Side = 1, Brake = 2, Steer = 3, Look = 4, Grip = 5, Early = 6;

        /// <summary>Whether bots left to themselves drive one drive after another, to get better at it.</summary>
        public static bool Practice;

        private static readonly Practice core = new Practice("drive skill", "driveskill.txt",
            new[] { "pace", "side", "brake", "steer", "look", "grip", "early" },
            new[] { 0.8f, 0.8f, 3f, 1.3f, 0.7f, 0.9f, 1f },
            new[] { 0.45f, 0.4f, 1.5f, 0.7f, 0.35f, 0.5f, 0.4f },
            new[] { 1.05f, 1.6f, 7f, 2.4f, 1.4f, 1.6f, 2.2f }, 0.12f, 0.8f);

        public static void Reset() => core.Reset();

        public static string Said() => core.Said() + (Practice ? "; practising" : "");

        public static float[] Begin(IntPtr who, out bool trying) => core.Begin(who, out trying);

        public static void End(IntPtr who, bool trying, float covered, float seconds, int backs, int abouts, bool failed)
        {
            if (seconds < 3f)
                return;
            float score = covered / (seconds + 4f * backs + 2f * abouts + (failed ? 15f : 0f));
            core.End(who, trying, score, covered.ToString("0") + " m in " + seconds.ToString("0") + " s, backed out " + backs + ", backed round " + abouts + (failed ? ", gave up" : "") + ": " + score.ToString("0.00") + " m/s");
        }
    }

    /// <summary>
    /// The bots getting better at shooting by themselves (see Practice). Every fifteen shots
    /// at something are scored: hits a second while it was shooting, less for the rounds
    /// wasted. Its settings: how nearly on its target its gun must be before it fires (at
    /// most so many degrees, and no wider than its target is at that range), how long it
    /// leaves between single shots, how long its bursts are.
    /// </summary>
    public static class ShootSkill
    {
        public const int Within = 0, Size = 1, Gap = 2, Burst = 3;

        private static readonly Practice core = new Practice("shoot skill", "shootskill.txt",
            new[] { "within", "size", "gap", "burst" },
            new[] { 4f, 0.3f, 1f, 1f },
            new[] { 2.5f, 0.22f, 0.6f, 0.5f },
            new[] { 7f, 0.6f, 2.5f, 2f }, 0.05f, 0.35f);

        public const int Window = 15;

        public static void Reset() => core.Reset();

        public static string Said() => core.Said();

        public static float[] Begin(IntPtr who, out bool trying) => core.Begin(who, out trying);

        public static void End(IntPtr who, bool trying, int shots, int hits, float seconds)
        {
            if (shots < 5 || seconds < 0.5f)
                return;
            float accuracy = hits / (float)shots;
            float score = hits / seconds * Mathf.Sqrt(accuracy);
            core.End(who, trying, score, hits + " of " + shots + " shots on it in " + seconds.ToString("0.0") + " s of shooting: " + score.ToString("0.00"));
        }
    }
}
