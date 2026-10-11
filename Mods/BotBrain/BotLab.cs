using System.Globalization;
using System.Reflection;
using System.Text;
using BoneLib;
using Il2CppSLZ.Marrow;
using UnityEngine;

namespace ThirdEyeBonelabMod.Bots
{
    /// <summary>The bot as the lab sees it: its state in numbers, and a gun put in its hand on command.</summary>
    public partial class Bot
    {
        private float labAimError = -1f;
        // The gun's top, and where the bot wants it (to see a gun held rolled over).
        private Vector3 labGunUp, labWantUp;

        public int LabShots { get; private set; }
        public int LabReloads { get; private set; }

        /// <summary>Which way its body faces.</summary>
        public Vector3 Facing => Quaternion.Euler(0f, bodyYaw, 0f) * Vector3.forward;

        public Transform GunHandTransform => RightHand != null ? RightHand.transform : Head;

        /// <summary>Its state, as a line of JSON.</summary>
        public string LabState()
        {
            StringBuilder s = new StringBuilder(512);
            s.Append("{\"t\":").Append(N(Time.time));
            s.Append(",\"bot\":\"").Append(Name).Append('"');
            s.Append(",\"mode\":\"").Append(mode).Append('"');
            s.Append(",\"goal\":\"").Append(goal).Append('"');
            s.Append(",\"why\":\"").Append(why.Replace("\"", "'")).Append('"');
            Vector3 feet = Feet;
            s.Append(",\"pos\":[").Append(N(feet.x)).Append(',').Append(N(feet.y)).Append(',').Append(N(feet.z)).Append(']');
            s.Append(",\"yaw\":").Append(N(bodyYaw));
            s.Append(",\"play\":\"").Append(play).Append('"');
            bool armed = gun != null && Holding();
            s.Append(",\"armed\":").Append(armed ? "true" : "false");
            if (gun != null)
            {
                s.Append(",\"gun\":\"").Append(gun.name).Append('"');
                s.Append(",\"ammo\":").Append(N(AmmoFraction()));
                s.Append(",\"chamber\":\"").Append(SafeState()).Append('"');
                s.Append(",\"reload\":\"").Append(reload).Append('"');
                s.Append(",\"support\":\"").Append(support != null ? support.name : "none").Append('"');
                s.Append(",\"supportHeld\":").Append(supportHeld ? "true" : "false");
                s.Append(",\"step\":\"").Append(step).Append('"');
                if (item != null)
                    s.Append(",\"spin\":").Append(N(item.angularVelocity.magnitude));
                Transform muzzle = gun.firePointTransform;
                if (muzzle != null)
                {
                    Vector3 f = muzzle.forward;
                    s.Append(",\"muzzlePitch\":").Append(N(-Mathf.Asin(Mathf.Clamp(f.y, -1f, 1f)) * Mathf.Rad2Deg));
                    // How far the gun is rolled from how it is wanted, about its barrel (+ is clockwise from behind).
                    if (labWantUp != Vector3.zero)
                        s.Append(",\"rollErr\":").Append(N(Vector3.SignedAngle(labWantUp, labGunUp, f)));
                    s.Append(",\"gunUpY\":").Append(N(labGunUp.y));
                    s.Append(",\"muzzleYawOff\":").Append(N(Mathf.DeltaAngle(bodyYaw, Recorder.Yaw(f))));
                }
                // How far each hand is from where it should be on the gun as it is wanted.
                if (gunWantSet)
                {
                    Hand right = RightHand;
                    if (right != null)
                        s.Append(",\"rightOff\":").Append(N(((gunWant * rightInGun).position - right.transform.position).magnitude));
                    Hand left = LeftHand;
                    if (left != null && supportHeld)
                        s.Append(",\"leftOff\":").Append(N(((gunWant * leftInGun).position - left.transform.position).magnitude));
                    if (item != null)
                        s.Append(",\"gunOffDeg\":").Append(N(Quaternion.Angle(gunWant.rotation, item.transform.rotation)));
                }
            }
            s.Append(",\"target\":\"").Append(target.transform == null ? "none" : target.zombieHead != null ? "zombie" : target.brain != null ? "npc" : "prop").Append('"');
            if (target.transform != null && Head != null)
                s.Append(",\"targetDist\":").Append(N((TargetPoint() - Head.position).magnitude));
            s.Append(",\"aimErr\":").Append(N(labAimError));
            s.Append(",\"trigger\":").Append(N(trigger));
            s.Append(",\"sinceShot\":").Append(N(Mathf.Min(Time.time - firedAt, 999f)));
            s.Append(",\"shots\":").Append(LabShots);
            s.Append(",\"dryPulls\":").Append(dryPulls);
            s.Append(",\"seenEnemies\":").Append(seenEnemies.Count);
            s.Append(",\"walk\":[").Append(N(walk.x)).Append(',').Append(N(walk.y)).Append(']');
            s.Append(",\"stick\":[").Append(N(stick.x)).Append(',').Append(N(stick.y)).Append(']');
            s.Append(",\"expects\":").Append(N(Knowledge.Worth(goal.ToString(), seen)));
            s.Append('}');
            return s.ToString();
        }

        /// <summary>A short line on it, for the events log.</summary>
        public string LabSummary()
        {
            return mode + "/" + goal + (gun != null ? " gun " + gun.name + " support " + (supportHeld ? "held" : support != null ? "not held" : "none")
                + " aimErr " + N(labAimError) : " unarmed");
        }

        private string SafeState()
        {
            try
            {
                return gun.cartridgeState + "/" + gun.slideState + "/mag " + (gun.HasMagazine() ? gun.AmmoCount().ToString() : "none");
            }
            catch (Exception)
            {
                return "?";
            }
        }

        private static string N(float v)
        {
            return float.IsNaN(v) || float.IsInfinity(v) ? "0" : v.ToString("0.###", CultureInfo.InvariantCulture);
        }

        /// <summary>A gun (by barcode, or by name: AKM, M1911, MP5...) spawned into its hand.</summary>
        public void LabGive(string which)
        {
            string barcode = which;
            if (!which.Contains("."))
            {
                barcode = null;
                foreach (FieldInfo field in typeof(CommonBarcodes.Guns).GetFields(BindingFlags.Public | BindingFlags.Static))
                {
                    if (field.FieldType == typeof(string) && string.Equals(field.Name, which, StringComparison.OrdinalIgnoreCase))
                        barcode = field.GetValue(null) as string;
                }
                if (barcode == null && which.Length > 0)
                {
                    Lab.Event("no gun called " + which);
                    return;
                }
            }
            StopPlaying();
            if (mode != Mode.Brain && mode != Mode.Gunplay)
                SetMode(Mode.Gunplay);
            // (Given another: the one it had put away is let fall.)
            DropStowed();
            StopPlaying();
            if (!SpawnGun(string.IsNullOrEmpty(barcode) ? null : barcode))
                Lab.Event("could not spawn " + which);
        }
    }
}
