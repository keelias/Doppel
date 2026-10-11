using Il2CppSLZ.Marrow;
using UnityEngine;

namespace ThirdEyeBonelabMod.Bots
{
    /// <summary>
    /// What makes one bot not another: rolled when it is spawned (from a number kept on its
    /// body, so a new build knows it again). All of them learned from the same player, but each
    /// has moves of theirs it likes best, a hand it favours, its own pace, height and tempo,
    /// how quick it is to fire, how close it likes to fight, and how long its bursts run.
    /// </summary>
    public class Personality
    {
        /// <summary>A child of the bot's rig named so, carrying the number it is rolled from.</summary>
        public const string Mark = "BotSeed_";

        public readonly int seed;
        /// <summary>The share of the player's moves it likes best (played far more often than the rest).</summary>
        public readonly float favourites;
        /// <summary>How often it does a move the other way round (left-handed bots, mostly).</summary>
        public readonly float mirror;
        /// <summary>How fast it plays the moves.</summary>
        public readonly float tempo;
        /// <summary>How hard it pushes on when it walks or runs.</summary>
        public readonly float pace;
        /// <summary>Metres off its head height: a little hunched, or upright.</summary>
        public readonly float posture;
        /// <summary>How long it takes to fire once it has a target (a factor).</summary>
        public readonly float reaction;
        /// <summary>How close it likes to fight (a factor).</summary>
        public readonly float distance;
        /// <summary>How long its bursts run (a factor).</summary>
        public readonly float burst;

        public Personality(int seed)
        {
            this.seed = seed;
            System.Random r = new System.Random(seed);
            float R(float a, float b) => a + (float)r.NextDouble() * (b - a);
            favourites = R(0.3f, 0.6f);
            // About one in four favours the left hand.
            mirror = r.NextDouble() < 0.25 ? R(0.7f, 0.9f) : R(0.1f, 0.4f);
            tempo = R(0.9f, 1.1f);
            pace = R(0.85f, 1.12f);
            posture = R(-0.06f, 0f);
            reaction = R(0.7f, 1.4f);
            distance = R(0.75f, 1.3f);
            burst = R(0.6f, 1.5f);
            // (Rolled after the others, so bots spawned before these keep what they had.)
            energy = R(0.4f, 1.6f);
            curiosity = R(0.3f, 1.6f);
            shooter = R(0.2f, 1.6f);
            calm = R(0.5f, 1.8f);
        }

        /// <summary>How much it jogs and sprints about, left to itself.</summary>
        public readonly float energy;
        /// <summary>How much it goes exploring: climbing up high, picking things up.</summary>
        public readonly float curiosity;
        /// <summary>How much it gets a gun and shoots at things for practice.</summary>
        public readonly float shooter;
        /// <summary>How much (and how long) it stands about.</summary>
        public readonly float calm;

        /// <summary>The bot's own (kept on its body), or a new one rolled for it.</summary>
        public static Personality Of(RigManager rig)
        {
            Transform t = rig.transform;
            for (int i = 0; i < t.childCount; i++)
            {
                string name = t.GetChild(i).name;
                if (name.StartsWith(Mark, StringComparison.Ordinal) && int.TryParse(name.Substring(Mark.Length), out int kept))
                    return new Personality(kept);
            }
            int seed = UnityEngine.Random.Range(1, int.MaxValue);
            new GameObject(Mark + seed).transform.SetParent(t, false);
            return new Personality(seed);
        }

        /// <summary>Seconds over which its favourites move on to others (gradually, all the time).</summary>
        public const float Drift = 150f;

        /// <summary>
        /// How much it likes a move of the player's (named by where it was recorded): its
        /// favourites a lot, the rest a little. Its taste drifts: over every couple of minutes it
        /// goes over, smoothly, from one set of favourites to the next, so it keeps changing
        /// how it moves instead of settling on one way.
        /// </summary>
        public float Likes(string move)
        {
            float age = Time.time / Drift + (seed % 1000) / 1000f;
            int era = Mathf.FloorToInt(age);
            float k = Mathf.SmoothStep(0f, 1f, age - era);
            return Mathf.Lerp(Taste(move, era), Taste(move, era + 1), k);
        }

        private float Taste(string move, int era)
        {
            // (A hash of its own, the same every time the game runs.)
            uint h = 2166136261;
            foreach (char c in move)
                h = (h ^ c) * 16777619;
            h = (h ^ (uint)seed) * 16777619;
            h = (h ^ (uint)era) * 16777619;
            h ^= h >> 15;
            h *= 2246822519;
            h ^= h >> 13;
            return h / (float)uint.MaxValue < favourites ? 3f : 0.4f;
        }

        public string Describe()
        {
            return (mirror > 0.5f ? "left-handed" : "right-handed")
                + (pace > 1.04f ? ", quick on its feet" : pace < 0.93f ? ", unhurried" : "")
                + (reaction < 0.9f ? ", fast trigger" : reaction > 1.2f ? ", careful shot" : "")
                + (distance < 0.9f ? ", fights up close" : distance > 1.15f ? ", keeps its distance" : "")
                + (burst > 1.2f ? ", long bursts" : burst < 0.8f ? ", short bursts" : "")
                + (energy > 1.2f ? ", restless" : calm > 1.4f ? ", laid-back" : "")
                + (curiosity > 1.2f ? ", a climber" : "")
                + (shooter > 1.2f ? ", trigger-happy" : "");
        }
    }
}
