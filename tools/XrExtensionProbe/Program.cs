using System;
using System.Runtime.InteropServices;

// OpenXR active runtime の instance 拡張を列挙する dev プローブ（D3D12 移行 Phase0 ゲート①）。
// xrEnumerateInstanceExtensionProperties は instance 不要のグローバル関数＝ゲーム・HMD 不要。
// マーシャリングは fork 既存実装（OpenXR.cs）と同型に揃える: IntPtr 手動確保 + ByValTStr struct。
// （2 回目呼び出しは各要素の type 初期化済みが仕様必須＝手動マーシャルで確実に渡す）
internal static class Program
{
    private const int XR_TYPE_EXTENSION_PROPERTIES = 2; // openxr.h（fork OpenXR.cs:77 と同値）

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct XrExtensionProperties
    {
        public int type;
        public IntPtr next;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] // XR_MAX_EXTENSION_NAME_SIZE
        public string extensionName;
        public uint extensionVersion;
    }

    [DllImport("openxr_loader", CallingConvention = CallingConvention.StdCall)]
    private static extern int xrEnumerateInstanceExtensionProperties(
        IntPtr layerName, uint propertyCapacityInput, out uint propertyCountOutput, IntPtr properties);

    private static int Main()
    {
        int result = xrEnumerateInstanceExtensionProperties(IntPtr.Zero, 0, out uint count, IntPtr.Zero);
        if (result < 0 || count == 0)
        {
            Console.WriteLine($"拡張数の取得に失敗: XrResult={result} count={count}（active runtime 未設定の可能性＝環境未整備・再試行要。撤退判定ではない）");
            return 1;
        }

        int stride = Marshal.SizeOf<XrExtensionProperties>();
        IntPtr buf = Marshal.AllocHGlobal((int)count * stride);
        try
        {
            for (int i = 0; i < count; i++)
                Marshal.StructureToPtr(new XrExtensionProperties { type = XR_TYPE_EXTENSION_PROPERTIES }, buf + i * stride, false);

            result = xrEnumerateInstanceExtensionProperties(IntPtr.Zero, count, out count, buf);
            if (result < 0)
            {
                Console.WriteLine($"拡張列挙に失敗: XrResult={result}");
                return 1;
            }

            bool hasD3D12 = false;
            for (int i = 0; i < count; i++)
            {
                var p = Marshal.PtrToStructure<XrExtensionProperties>(buf + i * stride);
                Console.WriteLine($"{p.extensionName} (rev {p.extensionVersion})");
                if (p.extensionName == "XR_KHR_D3D12_enable") hasD3D12 = true;
            }
            Console.WriteLine($"--- XR_KHR_D3D12_enable: {(hasD3D12 ? "あり" : "なし")} ---");
            return hasD3D12 ? 0 : 2;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }
}
