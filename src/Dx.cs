using System;
using System.Runtime.InteropServices;
using System.Text;

namespace ChromaPanel
{
    /// <summary>
    /// The handful of Direct3D 11 / DXGI calls desktop sharpening needs, made
    /// through raw vtable slots so there is no dependency on any wrapper
    /// library. Every slot number below was derived from the method order in
    /// Wine's d3d11.idl / dxgi.idl / dxgi1_2.idl / d3dcommon.idl - a wrong slot
    /// silently calls a different function, so do not edit them from memory.
    /// </summary>
    internal static class Dx
    {
        // ---------------- HRESULTs ----------------
        public const int S_OK = 0;
        public const int DXGI_ERROR_NOT_FOUND = unchecked((int)0x887A0002);
        public const int DXGI_ERROR_ACCESS_LOST = unchecked((int)0x887A0026);
        public const int DXGI_ERROR_WAIT_TIMEOUT = unchecked((int)0x887A0027);
        public const int DXGI_ERROR_UNSUPPORTED = unchecked((int)0x887A0004);

        // ---------------- constants ----------------
        public const uint DXGI_FORMAT_B8G8R8A8_UNORM = 87;
        public const uint BIND_CONSTANT_BUFFER = 0x4, BIND_SHADER_RESOURCE = 0x8, BIND_RENDER_TARGET = 0x20;
        public const uint USAGE_DEFAULT = 0, USAGE_STAGING = 3;
        public const uint CPU_ACCESS_READ = 0x20000;
        public const uint MAP_READ = 1;
        public const uint DXGI_USAGE_RENDER_TARGET_OUTPUT = 0x20;
        public const uint SWAP_EFFECT_DISCARD = 0;
        public const uint TOPOLOGY_TRIANGLELIST = 4;
        const uint D3D11_SDK_VERSION = 7;
        const uint CREATE_DEVICE_BGRA_SUPPORT = 0x20;

        public static Guid IID_IDXGIFactory1 = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
        public static Guid IID_IDXGIOutput1 = new Guid("00cddea8-939b-4b83-a340-a685226666cc");
        public static Guid IID_ID3D11Texture2D = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

        // ---------------- structs ----------------
        [StructLayout(LayoutKind.Sequential)]
        public struct TEXTURE2D_DESC
        {
            public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CPUAccessFlags, MiscFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BUFFER_DESC { public uint ByteWidth, Usage, BindFlags, CPUAccessFlags, MiscFlags, StructureByteStride; }

        [StructLayout(LayoutKind.Sequential)]
        public struct VIEWPORT { public float X, Y, Width, Height, MinDepth, MaxDepth; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MAPPED_SUBRESOURCE { public IntPtr pData; public uint RowPitch, DepthPitch; }

        [StructLayout(LayoutKind.Sequential)]
        public struct SWAP_CHAIN_DESC
        {
            public uint Width, Height, RefreshNum, RefreshDen, Format, ScanlineOrdering, Scaling;
            public uint SampleCount, SampleQuality;
            public uint BufferUsage, BufferCount;
            public IntPtr OutputWindow;
            public int Windowed;
            public uint SwapEffect, Flags;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct OUTPUT_DESC
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            public int Left, Top, Right, Bottom;
            public int AttachedToDesktop;
            public uint Rotation;      // 1 = identity
            public IntPtr Monitor;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct OUTDUPL_FRAME_INFO
        {
            public long LastPresentTime, LastMouseUpdateTime;
            public uint AccumulatedFrames;
            public int RectsCoalesced, ProtectedContentMaskedOut;
            public int PointerX, PointerY, PointerVisible;
            public uint TotalMetadataBufferSize, PointerShapeBufferSize;
        }

        // ---------------- vtable plumbing ----------------
        internal static T Slot<T>(IntPtr obj, int slot) where T : class
        {
            IntPtr vtbl = Marshal.ReadIntPtr(obj);
            IntPtr fn = Marshal.ReadIntPtr(vtbl, slot * IntPtr.Size);
            return (T)(object)Marshal.GetDelegateForFunctionPointer(fn, typeof(T));
        }

        public static void Release(ref IntPtr p)
        {
            if (p != IntPtr.Zero) { Marshal.Release(p); p = IntPtr.Zero; }
        }

        public static IntPtr QueryInterface(IntPtr p, Guid iid)
        {
            IntPtr r;
            Check(Marshal.QueryInterface(p, ref iid, out r), "QueryInterface " + iid);
            return r;
        }

        public static void Check(int hr, string what)
        {
            if (hr < 0) throw new DxException(hr, what);
        }

        // ---------------- exports ----------------
        [DllImport("dxgi.dll")]
        static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

        [DllImport("d3d11.dll")]
        static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags,
            IntPtr featureLevels, uint numLevels, uint sdkVersion, out IntPtr device, out int featureLevel, out IntPtr context);

        [DllImport("d3dcompiler_47.dll", CharSet = CharSet.Ansi)]
        static extern int D3DCompile(byte[] src, IntPtr size, string name, IntPtr defines, IntPtr include,
            string entry, string target, uint flags1, uint flags2, out IntPtr code, out IntPtr errors);

        // ---------------- delegates (stdcall COM methods) ----------------
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int D_EnumAdapters1(IntPtr self, uint i, out IntPtr adapter);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int D_CreateSwapChain(IntPtr self, IntPtr device, ref SWAP_CHAIN_DESC desc, out IntPtr swap);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int D_EnumOutputs(IntPtr self, uint i, out IntPtr output);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int D_OutputGetDesc(IntPtr self, out OUTPUT_DESC desc);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int D_DuplicateOutput(IntPtr self, IntPtr device, out IntPtr dup);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int D_AcquireNextFrame(IntPtr self, uint timeoutMs, out OUTDUPL_FRAME_INFO info, out IntPtr resource);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int D_ReleaseFrame(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int D_Present(IntPtr self, uint sync, uint flags);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int D_GetBuffer(IntPtr self, uint i, ref Guid riid, out IntPtr surface);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int D_CreateTexture2D(IntPtr self, ref TEXTURE2D_DESC desc, IntPtr init, out IntPtr tex);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int D_CreateBuffer(IntPtr self, ref BUFFER_DESC desc, IntPtr init, out IntPtr buf);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int D_CreateView(IntPtr self, IntPtr resource, IntPtr desc, out IntPtr view);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int D_CreateShader(IntPtr self, IntPtr code, IntPtr len, IntPtr linkage, out IntPtr shader);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void D_TexGetDesc(IntPtr self, out TEXTURE2D_DESC desc);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate IntPtr D_BlobPtr(IntPtr self);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void D_SetViews(IntPtr self, uint start, uint num, ref IntPtr views);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void D_SetViewsArray(IntPtr self, uint start, uint num, IntPtr[] views);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void D_SetShader(IntPtr self, IntPtr shader, IntPtr instances, uint num);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void D_Draw(IntPtr self, uint count, uint start);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void D_SetPtr(IntPtr self, IntPtr p);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void D_SetUInt(IntPtr self, uint v);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void D_SetRenderTargets(IntPtr self, uint num, ref IntPtr rtvs, IntPtr dsv);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void D_SetViewports(IntPtr self, uint num, ref VIEWPORT vp);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void D_Copy(IntPtr self, IntPtr dst, IntPtr src);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void D_Update(IntPtr self, IntPtr dst, uint sub, IntPtr box, IntPtr data, uint rowPitch, uint depthPitch);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void D_Void(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int D_Map(IntPtr self, IntPtr res, uint sub, uint type, uint flags, out MAPPED_SUBRESOURCE mapped);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void D_Unmap(IntPtr self, IntPtr res, uint sub);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void D_ClearRTV(IntPtr self, IntPtr rtv, float[] rgba);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void D_GetContext(IntPtr self, out IntPtr ctx);

        // ---------------- factory / adapter / output ----------------
        public static IntPtr CreateFactory()
        {
            IntPtr f;
            Check(CreateDXGIFactory1(ref IID_IDXGIFactory1, out f), "CreateDXGIFactory1");
            return f;
        }

        /// <summary>Returns false when there are no more adapters.</summary>
        public static bool EnumAdapter(IntPtr factory, uint i, out IntPtr adapter)
        {
            int hr = Slot<D_EnumAdapters1>(factory, 12)(factory, i, out adapter);
            if (hr == DXGI_ERROR_NOT_FOUND) return false;
            Check(hr, "EnumAdapters1");
            return true;
        }

        public static bool EnumOutput(IntPtr adapter, uint i, out IntPtr output)
        {
            int hr = Slot<D_EnumOutputs>(adapter, 7)(adapter, i, out output);
            if (hr == DXGI_ERROR_NOT_FOUND) return false;
            Check(hr, "EnumOutputs");
            return true;
        }

        public static OUTPUT_DESC OutputDesc(IntPtr output)
        {
            OUTPUT_DESC d;
            Check(Slot<D_OutputGetDesc>(output, 7)(output, out d), "IDXGIOutput::GetDesc");
            return d;
        }

        /// <summary>Finds the adapter + output that drive a \\.\DISPLAYn. Caller releases both.</summary>
        public static bool FindOutput(string gdiName, out IntPtr adapter, out IntPtr output, out OUTPUT_DESC desc)
        {
            adapter = IntPtr.Zero; output = IntPtr.Zero; desc = new OUTPUT_DESC();
            IntPtr factory = CreateFactory();
            try
            {
                IntPtr a;
                for (uint ai = 0; EnumAdapter(factory, ai, out a); ai++)
                {
                    IntPtr o;
                    for (uint oi = 0; EnumOutput(a, oi, out o); oi++)
                    {
                        OUTPUT_DESC d = OutputDesc(o);
                        if (string.Equals(d.DeviceName, gdiName, StringComparison.OrdinalIgnoreCase))
                        {
                            adapter = a; output = o; desc = d;
                            return true;
                        }
                        Release(ref o);
                    }
                    Release(ref a);
                }
                return false;
            }
            finally { Release(ref factory); }
        }

        public static IntPtr DuplicateOutput(IntPtr output, IntPtr device)
        {
            IntPtr o1 = QueryInterface(output, IID_IDXGIOutput1);
            try
            {
                IntPtr dup;
                Check(Slot<D_DuplicateOutput>(o1, 22)(o1, device, out dup), "DuplicateOutput");
                return dup;
            }
            finally { Release(ref o1); }
        }

        public static D_AcquireNextFrame AcquireNextFrameFn(IntPtr dup) { return Slot<D_AcquireNextFrame>(dup, 8); }
        public static D_ReleaseFrame ReleaseFrameFn(IntPtr dup) { return Slot<D_ReleaseFrame>(dup, 14); }

        // ---------------- device ----------------
        /// <summary>A device on a specific adapter (null = default adapter, for tests).</summary>
        public static IntPtr CreateDevice(IntPtr adapter, out IntPtr context)
        {
            IntPtr dev; int fl;
            // D3D_DRIVER_TYPE_UNKNOWN (0) is required when an adapter is given; HARDWARE (1) otherwise.
            int hr = D3D11CreateDevice(adapter, adapter == IntPtr.Zero ? 1 : 0, IntPtr.Zero, CREATE_DEVICE_BGRA_SUPPORT,
                IntPtr.Zero, 0, D3D11_SDK_VERSION, out dev, out fl, out context);
            Check(hr, "D3D11CreateDevice");
            return dev;
        }

        /// <summary>
        /// Microsoft's software rasteriser. Lets the shader tests run on a
        /// machine with no GPU, such as a CI runner.
        /// </summary>
        public static IntPtr CreateWarpDevice(out IntPtr context)
        {
            IntPtr dev; int fl;
            Check(D3D11CreateDevice(IntPtr.Zero, 5 /* D3D_DRIVER_TYPE_WARP */, IntPtr.Zero, CREATE_DEVICE_BGRA_SUPPORT,
                IntPtr.Zero, 0, D3D11_SDK_VERSION, out dev, out fl, out context), "D3D11CreateDevice(WARP)");
            return dev;
        }

        public static IntPtr CreateTexture2D(IntPtr dev, ref TEXTURE2D_DESC desc, IntPtr init)
        {
            IntPtr t;
            Check(Slot<D_CreateTexture2D>(dev, 5)(dev, ref desc, init, out t), "CreateTexture2D");
            return t;
        }

        public static IntPtr CreateConstantBuffer(IntPtr dev, uint bytes)
        {
            var d = new BUFFER_DESC { ByteWidth = bytes, Usage = USAGE_DEFAULT, BindFlags = BIND_CONSTANT_BUFFER };
            IntPtr b;
            Check(Slot<D_CreateBuffer>(dev, 3)(dev, ref d, IntPtr.Zero, out b), "CreateBuffer");
            return b;
        }

        public static IntPtr CreateSRV(IntPtr dev, IntPtr res)
        {
            IntPtr v;
            Check(Slot<D_CreateView>(dev, 7)(dev, res, IntPtr.Zero, out v), "CreateShaderResourceView");
            return v;
        }

        public static IntPtr CreateRTV(IntPtr dev, IntPtr res)
        {
            IntPtr v;
            Check(Slot<D_CreateView>(dev, 9)(dev, res, IntPtr.Zero, out v), "CreateRenderTargetView");
            return v;
        }

        public static TEXTURE2D_DESC TextureDesc(IntPtr tex)
        {
            TEXTURE2D_DESC d;
            Slot<D_TexGetDesc>(tex, 10)(tex, out d);
            return d;
        }

        public static IntPtr CreateSwapChain(IntPtr dev, IntPtr hwnd, uint w, uint h)
        {
            IntPtr dxgiDevice = QueryInterface(dev, new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c")); // IDXGIDevice
            IntPtr adapter = IntPtr.Zero, factory = IntPtr.Zero;
            try
            {
                // IDXGIObject::GetParent (slot 6) walks device -> adapter -> factory.
                Check(Slot<D_GetParent>(dxgiDevice, 6)(dxgiDevice, ref IID_IDXGIAdapter, out adapter), "GetParent(adapter)");
                Check(Slot<D_GetParent>(adapter, 6)(adapter, ref IID_IDXGIFactory1, out factory), "GetParent(factory)");
                var d = new SWAP_CHAIN_DESC
                {
                    Width = w, Height = h, Format = DXGI_FORMAT_B8G8R8A8_UNORM,
                    SampleCount = 1, BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT, BufferCount = 1,
                    OutputWindow = hwnd, Windowed = 1, SwapEffect = SWAP_EFFECT_DISCARD
                };
                IntPtr swap;
                Check(Slot<D_CreateSwapChain>(factory, 10)(factory, dev, ref d, out swap), "CreateSwapChain");
                return swap;
            }
            finally { Release(ref factory); Release(ref adapter); Release(ref dxgiDevice); }
        }

        static Guid IID_IDXGIAdapter = new Guid("2411e7e1-12ac-4ccf-bd14-9798e8534dc0");
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int D_GetParent(IntPtr self, ref Guid riid, out IntPtr parent);

        public static IntPtr SwapChainBuffer(IntPtr swap)
        {
            IntPtr tex;
            Check(Slot<D_GetBuffer>(swap, 9)(swap, 0, ref IID_ID3D11Texture2D, out tex), "GetBuffer");
            return tex;
        }

        public static D_Present PresentFn(IntPtr swap) { return Slot<D_Present>(swap, 8); }

        // ---------------- shaders ----------------
        public static IntPtr CompileVS(IntPtr dev, string src, string entry) { return Compile(dev, src, entry, "vs_4_0", 12); }
        public static IntPtr CompilePS(IntPtr dev, string src, string entry) { return Compile(dev, src, entry, "ps_4_0", 15); }

        static IntPtr Compile(IntPtr dev, string src, string entry, string target, int createSlot)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(src);
            IntPtr code, errors;
            const uint OPTIMIZATION_LEVEL3 = 1 << 15;
            int hr = D3DCompile(bytes, new IntPtr(bytes.Length), "chromapanel", IntPtr.Zero, IntPtr.Zero,
                entry, target, OPTIMIZATION_LEVEL3, 0, out code, out errors);
            if (hr < 0)
            {
                string msg = errors != IntPtr.Zero ? Marshal.PtrToStringAnsi(Slot<D_BlobPtr>(errors, 3)(errors)) : "";
                Release(ref errors);
                throw new DxException(hr, "D3DCompile " + entry + ": " + msg);
            }
            Release(ref errors);
            try
            {
                IntPtr ptr = Slot<D_BlobPtr>(code, 3)(code);    // GetBufferPointer
                IntPtr len = Slot<D_BlobPtr>(code, 4)(code);    // GetBufferSize (SIZE_T)
                IntPtr shader;
                Check(Slot<D_CreateShader>(dev, createSlot)(dev, ptr, len, IntPtr.Zero, out shader), "Create" + target);
                return shader;
            }
            finally { Release(ref code); }
        }

        // ---------------- immediate context ----------------
        public static IntPtr ImmediateContext(IntPtr dev)
        {
            IntPtr c;
            Slot<D_GetContext>(dev, 40)(dev, out c);
            return c;
        }

        /// <summary>Delegates for the context calls made every frame, created once.</summary>
        public sealed class Context
        {
            public readonly IntPtr Ptr;
            public readonly D_SetViews PSSetShaderResources, PSSetConstantBuffers;
            public readonly D_SetViewsArray PSSetShaderResourcesArray;
            public readonly D_SetShader PSSetShader, VSSetShader;
            public readonly D_Draw Draw;
            public readonly D_SetPtr IASetInputLayout;
            public readonly D_SetUInt IASetPrimitiveTopology;
            public readonly D_SetRenderTargets OMSetRenderTargets;
            public readonly D_SetViewports RSSetViewports;
            public readonly D_Copy CopyResource;
            public readonly D_Update UpdateSubresource;
            public readonly D_Void ClearState, Flush;
            public readonly D_Map Map;
            public readonly D_Unmap Unmap;
            public readonly D_ClearRTV ClearRenderTargetView;

            public Context(IntPtr ctx)
            {
                Ptr = ctx;
                PSSetShaderResources = Slot<D_SetViews>(ctx, 8);
                PSSetShaderResourcesArray = Slot<D_SetViewsArray>(ctx, 8);
                PSSetShader = Slot<D_SetShader>(ctx, 9);
                VSSetShader = Slot<D_SetShader>(ctx, 11);
                Draw = Slot<D_Draw>(ctx, 13);
                Map = Slot<D_Map>(ctx, 14);
                Unmap = Slot<D_Unmap>(ctx, 15);
                PSSetConstantBuffers = Slot<D_SetViews>(ctx, 16);
                IASetInputLayout = Slot<D_SetPtr>(ctx, 17);
                IASetPrimitiveTopology = Slot<D_SetUInt>(ctx, 24);
                OMSetRenderTargets = Slot<D_SetRenderTargets>(ctx, 33);
                RSSetViewports = Slot<D_SetViewports>(ctx, 44);
                CopyResource = Slot<D_Copy>(ctx, 47);
                UpdateSubresource = Slot<D_Update>(ctx, 48);
                ClearRenderTargetView = Slot<D_ClearRTV>(ctx, 50);
                ClearState = Slot<D_Void>(ctx, 110);
                Flush = Slot<D_Void>(ctx, 111);
            }
        }
    }

    internal sealed class DxException : Exception
    {
        public readonly int HResult32;
        public DxException(int hr, string what) : base(what + " failed (0x" + hr.ToString("X8") + ")") { HResult32 = hr; }
    }
}
