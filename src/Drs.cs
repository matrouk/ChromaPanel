using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ChromaPanel
{
    /// <summary>
    /// NVIDIA Driver Settings (DRS) - the store behind "Manage 3D settings".
    /// Function IDs come from NVIDIA's public nvapi_interface.h. Only DWORD
    /// settings on the base (global) profile are supported, which is all the
    /// sharpening controls need.
    /// </summary>
    internal static class Drs
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_OutHandle(out IntPtr h);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_Handle(IntPtr h);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_SessOutProfile(IntPtr s, out IntPtr p);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_GetSetting(IntPtr s, IntPtr p, uint id, IntPtr setting);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_SetSetting(IntPtr s, IntPtr p, IntPtr setting);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_DeleteSetting(IntPtr s, IntPtr p, uint id);

        const uint ID_CreateSession = 0x0694D52E;
        const uint ID_DestroySession = 0xDAD9CFF8;
        const uint ID_LoadSettings = 0x375DBD6B;
        const uint ID_SaveSettings = 0xFCBC7E14;
        const uint ID_GetBaseProfile = 0xDA8466A0;
        const uint ID_GetSetting = 0x73BF8338;
        const uint ID_SetSetting = 0x577DD202;
        const uint ID_DeleteProfileSetting = 0xE4A26362;

        // NVDRS_SETTING_V1 laid out by hand in a raw buffer, so a marshalling
        // mistake cannot corrupt the heap.
        //
        // The current nvapi.h adds an NvU64 to both value unions, which under
        // pack(8) would make the struct 12328 bytes. The driver rejects that
        // with -9 (INCOMPATIBLE_STRUCT_VERSION) and accepts 12320 / 0x13020 -
        // verified on hardware. Do not "fix" this to match the header.
        const int SETTING_SIZE = 12320;
        const int SETTING_VERSION = SETTING_SIZE | (1 << 16);
        const int OFF_VERSION = 0;
        const int OFF_ID = 4100;
        const int OFF_TYPE = 4104;
        const int OFF_CUR = 8220;
        const int DWORD_TYPE = 0;

        const int NVAPI_SETTING_NOT_FOUND = -160;

        public static string LastError = "";

        static T Fn<T>(uint id) where T : class
        {
            IntPtr p = NvApi.Q(id);
            if (p == IntPtr.Zero) throw new InvalidOperationException("NVAPI function 0x" + id.ToString("X8") + " is not exported");
            return (T)(object)Marshal.GetDelegateForFunctionPointer(p, typeof(T));
        }

        delegate T SessionWork<T>(IntPtr session, IntPtr profile);

        /// <summary>Opens a session on the base profile, runs the work, always closes it.</summary>
        static bool WithSession(SessionWork<bool> work)
        {
            LastError = "";
            if (!NvApi.Init()) { LastError = NvApi.LastError; return false; }
            IntPtr session = IntPtr.Zero;
            try
            {
                int rc = Fn<D_OutHandle>(ID_CreateSession)(out session);
                if (rc != 0) { LastError = "DRS CreateSession: " + NvApi.StatusName(rc); return false; }
                rc = Fn<D_Handle>(ID_LoadSettings)(session);
                if (rc != 0) { LastError = "DRS LoadSettings: " + NvApi.StatusName(rc); return false; }
                IntPtr profile;
                rc = Fn<D_SessOutProfile>(ID_GetBaseProfile)(session, out profile);
                if (rc != 0) { LastError = "DRS GetBaseProfile: " + NvApi.StatusName(rc); return false; }
                return work(session, profile);
            }
            catch (Exception ex) { LastError = ex.Message; return false; }
            finally
            {
                if (session != IntPtr.Zero)
                {
                    try { Fn<D_Handle>(ID_DestroySession)(session); } catch { }
                }
            }
        }

        /// <summary>
        /// Reads DWORD settings from the global profile. A setting the profile
        /// does not override comes back in <paramref name="unset"/> rather than
        /// as a value - that means "driver default", not zero.
        /// </summary>
        public static bool Read(uint[] ids, out Dictionary<uint, uint> values, out HashSet<uint> unset)
        {
            var got = new Dictionary<uint, uint>();
            var missing = new HashSet<uint>();
            bool ok = WithSession(delegate (IntPtr session, IntPtr profile)
            {
                var get = Fn<D_GetSetting>(ID_GetSetting);
                IntPtr buf = Marshal.AllocHGlobal(SETTING_SIZE);
                try
                {
                    foreach (uint id in ids)
                    {
                        Zero(buf);
                        Marshal.WriteInt32(buf, OFF_VERSION, SETTING_VERSION);
                        int rc = get(session, profile, id, buf);
                        if (rc == NVAPI_SETTING_NOT_FOUND) { missing.Add(id); continue; }
                        if (rc != 0) { LastError = "GetSetting 0x" + id.ToString("X8") + ": " + NvApi.StatusName(rc); return false; }
                        got[id] = (uint)Marshal.ReadInt32(buf, OFF_CUR);
                    }
                    return true;
                }
                finally { Marshal.FreeHGlobal(buf); }
            });
            values = got;
            unset = missing;
            return ok;
        }

        /// <summary>
        /// Writes DWORD settings and deletes others in one session, then saves
        /// once, so a half-applied change is never persisted.
        /// </summary>
        public static bool Write(Dictionary<uint, uint> set, uint[] delete)
        {
            return WithSession(delegate (IntPtr session, IntPtr profile)
            {
                var setFn = Fn<D_SetSetting>(ID_SetSetting);
                var delFn = Fn<D_DeleteSetting>(ID_DeleteProfileSetting);
                IntPtr buf = Marshal.AllocHGlobal(SETTING_SIZE);
                try
                {
                    if (set != null)
                    {
                        foreach (KeyValuePair<uint, uint> kv in set)
                        {
                            Zero(buf);
                            Marshal.WriteInt32(buf, OFF_VERSION, SETTING_VERSION);
                            Marshal.WriteInt32(buf, OFF_ID, unchecked((int)kv.Key));
                            Marshal.WriteInt32(buf, OFF_TYPE, DWORD_TYPE);
                            Marshal.WriteInt32(buf, OFF_CUR, unchecked((int)kv.Value));
                            int rc = setFn(session, profile, buf);
                            if (rc != 0) { LastError = "SetSetting 0x" + kv.Key.ToString("X8") + ": " + NvApi.StatusName(rc); return false; }
                        }
                    }
                    if (delete != null)
                    {
                        foreach (uint id in delete)
                        {
                            int rc = delFn(session, profile, id);
                            // Deleting something that was never set is fine.
                            if (rc != 0 && rc != NVAPI_SETTING_NOT_FOUND)
                            { LastError = "DeleteProfileSetting 0x" + id.ToString("X8") + ": " + NvApi.StatusName(rc); return false; }
                        }
                    }
                    int save = Fn<D_Handle>(ID_SaveSettings)(session);
                    if (save != 0) { LastError = "SaveSettings: " + NvApi.StatusName(save); return false; }
                    return true;
                }
                finally { Marshal.FreeHGlobal(buf); }
            });
        }

        static void Zero(IntPtr buf)
        {
            for (int i = 0; i < SETTING_SIZE; i += 4) Marshal.WriteInt32(buf, i, 0);
        }
    }
}
