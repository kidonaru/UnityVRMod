using System.Runtime.InteropServices;
using UnityVRMod.Core;

namespace UnityVRMod.Features.VRVisualization.OpenXR
{
    internal static class NativeBridge
    {
        private const string NativeHelperDll = "UnityGraphicsHelper";

        [DllImport(NativeHelperDll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "DirectCopyResource")]
        public static extern void DirectCopyResource_Internal(IntPtr pDest, IntPtr pSrc);

        [DllImport(NativeHelperDll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "SetDevicePointerFromCSharp")]
        public static extern void SetDevicePointerFromCSharp(IntPtr d3d11DevicePtr);

        [DllImport(NativeHelperDll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "CreateAndRegisterSRV")]
        public static extern int CreateAndRegisterSRV_Internal(IntPtr pTextureResource, int srvFormatDXGI, out IntPtr ppSRV);

        [DllImport(NativeHelperDll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "ReleaseNativeObject")]
        public static extern void ReleaseNativeObject_Internal(IntPtr pObject);

        [DllImport(NativeHelperDll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "GetD3D11Device")]
        private static extern IntPtr GetCachedD3D11Device_Internal();

        [DllImport(NativeHelperDll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "GetDeviceFromResource")]
        private static extern IntPtr GetDeviceFromResource_Internal(IntPtr pResource);

        // ===== D3D12 backend（render-thread 駆動 copy。設計 = bg2-vr-d3d12-migration-phase1-design.md §4） =====

        [DllImport(NativeHelperDll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "IsD3D12Active")]
        public static extern int IsD3D12Active_Internal();

        [DllImport(NativeHelperDll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "GetD3D12Device")]
        public static extern IntPtr GetD3D12Device_Internal();

        [DllImport(NativeHelperDll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "GetD3D12CommandQueue")]
        public static extern IntPtr GetD3D12CommandQueue_Internal();

        [DllImport(NativeHelperDll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "GetRenderEventFunc")]
        public static extern IntPtr GetRenderEventFunc_Internal();

        [DllImport(NativeHelperDll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "EnqueueCopyD3D12")]
        public static extern int EnqueueCopyD3D12_Internal(IntPtr src, IntPtr dst);

        [DllImport(NativeHelperDll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "GetCompletedCopyTicket")]
        public static extern int GetCompletedCopyTicket_Internal();

        [DllImport(NativeHelperDll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "CancelPendingCopiesD3D12")]
        public static extern void CancelPendingCopiesD3D12_Internal();

        [DllImport(NativeHelperDll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "QueryVideoMemoryInfo")]
        private static extern int QueryVideoMemoryInfo_Internal(
            out ulong localCurrent, out ulong localBudget,
            out ulong nonLocalCurrent, out ulong nonLocalBudget);

        /// <summary>
        /// 診断用: IDXGIAdapter3::QueryVideoMemoryInfo の LOCAL / NON_LOCAL を取得する。
        /// 旧 dll（QueryVideoMemoryInfo 未 export）と一緒に動いても CrashGuard で false 返却＝呼び出し側は無視する設計。
        /// </summary>
        public static bool TryQueryDxgiVideoMemoryInfo(
            out ulong localCurrentBytes, out ulong localBudgetBytes,
            out ulong nonLocalCurrentBytes, out ulong nonLocalBudgetBytes)
        {
            try
            {
                return QueryVideoMemoryInfo_Internal(
                    out localCurrentBytes, out localBudgetBytes,
                    out nonLocalCurrentBytes, out nonLocalBudgetBytes) != 0;
            }
            catch
            {
                localCurrentBytes = 0; localBudgetBytes = 0;
                nonLocalCurrentBytes = 0; nonLocalBudgetBytes = 0;
                return false;
            }
        }

        public const int kEventCacheDeviceObjects = 1;
        public const int kEventExecutePendingCopies = 2;

        /// <summary>
        /// copy チケットが render thread で submit 済みになるまで有限待機する。
        /// 戻り値 false = timeout（copy 未 submit のまま進む＝stale 内容の release を許容する設計）。
        /// 通常 wait は数 ms（render thread が追いつくだけ）。SpinWait で短時間はスピン→以降 yield/Sleep へ
        /// 適応的に移行し、render thread 遅延時に 1 コアを timeout 一杯まで焼かない（別スレッド待ちの定石）。
        /// </summary>
        public static bool WaitForCopyTicket(int ticket, int timeoutMs)
        {
            if (ticket <= 0) return true;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var spinner = new System.Threading.SpinWait();
            while (GetCompletedCopyTicket_Internal() < ticket)
            {
                if (sw.ElapsedMilliseconds >= timeoutMs) return false;
                spinner.SpinOnce();
            }
            return true;
        }

        public static IntPtr GetD3D11DevicePointer(Texture textureForFallback)
        {
            if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Direct3D11)
            {
                VRModCore.LogWarning("Graphics device is not Direct3D 11.");
                return IntPtr.Zero;
            }

            try
            {
                IntPtr devicePtr = GetCachedD3D11Device_Internal();
                if (devicePtr != IntPtr.Zero) return devicePtr;
            }
            catch { }

            if (textureForFallback == null) return IntPtr.Zero;

            try
            {
                IntPtr nativeTexturePtr = textureForFallback.GetNativeTexturePtr();
                if (nativeTexturePtr == IntPtr.Zero) return IntPtr.Zero;
                return GetDeviceFromResource_Internal(nativeTexturePtr);
            }
            catch { }

            return IntPtr.Zero;
        }
    }
}