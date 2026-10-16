using System;
using System.Collections.Generic;

namespace ChromaPanel
{
    /// <summary>
    /// NVIDIA Image Scaling (NIS): the driver's spatial upscaler + sharpener,
    /// the same control as "Manage 3D settings > Image Scaling". It is a 3D
    /// setting, so it affects games, not the Windows desktop. A game running
    /// below native resolution is upscaled with NIS; at native it is sharpened
    /// only (NVIDIA's overlay shows a green or a blue "NIS" respectively).
    /// Changes take effect the next time a game starts.
    /// </summary>
    internal static class Sharpening
    {
        // ---- candidate IDs - UNCONFIRMED, see WritesVerified below ---------
        public const uint ID_NIS_ENABLE = 0x10444444;      // NVIDIA Image Scaling: 0 off / 1 on
        public const uint ID_SHARPEN_ON = 0x00598928;      // driver sharpening filter: 0 / 1
        public const uint ID_SHARPEN_VALUE = 0x002ED8CD;   // 0..100 = 0.00..1.00

        static readonly uint[] All = { ID_NIS_ENABLE, ID_SHARPEN_ON, ID_SHARPEN_VALUE };

        /// <summary>NVIDIA's panel opens at 50% the first time Image Scaling is switched on.</summary>
        public const int DefaultPercent = 50;

        /// <summary>
        /// False until the IDs above are confirmed by diffing the driver profile
        /// before and after NVIDIA's own panel enables Image Scaling. The
        /// legacy sharpening IDs were already rejected by a current driver
        /// (SetSetting returned SETTING_NOT_FOUND), so this does not guess:
        /// while false, reading works and writing is refused.
        /// </summary>
        public static readonly bool WritesVerified = false;
        public const string PendingNote = "Locked until a one-time check against NVIDIA Control Panel confirms which driver settings to write. Nothing here touches your driver yet.";

        public class State
        {
            public bool Available;      // NVAPI and the driver settings store answered
            public bool Enabled;
            public int Percent = DefaultPercent;
            public string Error = "";
            public Dictionary<uint, uint> Raw = new Dictionary<uint, uint>();
            public HashSet<uint> Unset = new HashSet<uint>();
        }

        public static State Read()
        {
            var st = new State();
            Dictionary<uint, uint> values;
            HashSet<uint> unset;
            if (!Drs.Read(All, out values, out unset))
            {
                st.Error = Drs.LastError;
                return st;
            }
            st.Available = true;
            st.Raw = values;
            st.Unset = unset;

            uint v;
            st.Enabled = values.TryGetValue(ID_NIS_ENABLE, out v) && v != 0;
            if (values.TryGetValue(ID_SHARPEN_VALUE, out v)) st.Percent = (int)Math.Min(100u, v);
            return st;
        }

        /// <summary>
        /// Off removes the overrides instead of writing zeros, so the profile
        /// goes back to exactly how the driver shipped - the same as NVIDIA's
        /// panel's "Restore" button for these settings.
        /// </summary>
        public static bool Write(bool enabled, int percent, out string error)
        {
            error = null;
            if (!WritesVerified) { error = "sharpening writes are not verified on this driver yet"; return false; }
            percent = Math.Max(0, Math.Min(100, percent));
            bool ok;
            if (enabled)
            {
                var set = new Dictionary<uint, uint>();
                set[ID_NIS_ENABLE] = 1;
                set[ID_SHARPEN_ON] = 1;
                set[ID_SHARPEN_VALUE] = (uint)percent;
                ok = Drs.Write(set, null);
            }
            else
            {
                ok = Drs.Write(null, All);
            }
            if (!ok) error = Drs.LastError;
            return ok;
        }
    }
}
