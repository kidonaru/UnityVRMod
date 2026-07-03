using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityVRMod.Config;
using UnityVRMod.Core;
using UnityVRMod.Features.VrVisualization;   // VrCameraSetup_CoreOpenXR の backend 判定アクセサ（namespace casing が別）

namespace UnityVRMod.Features.VRVisualization.OpenXR
{
    /// <summary>
    /// fade/overlay quad layer 1 枚分の OpenXR swapchain。固定サイズ・単一 format。
    /// 毎フレ TryStage(srcNativePtr) で src（Unity RT の native ptr）を swapchain image へ CopyResource する。
    /// acquire→wait→copy→release を 1 フレーム内で完結させ、release 後に layer が swapchain を参照できる
    /// （eye 経路と同じ規約: release は wait 成功後のみ／submit は release 後）。
    /// wait が有限 timeout で ready にならなければ image を持ち越して次フレ再 wait する（メインスレッド無限ハング回避）。
    /// </summary>
    internal sealed class QuadLayerSwapchain
    {
        public ulong Handle { get; private set; } = OpenXRConstants.XR_NULL_HANDLE;
        public int Width { get; private set; }
        public int Height { get; private set; }

        private readonly List<IntPtr> _images = new();
        private bool _acquired;
        private uint _imgIdx;

        /// <summary>swapchain を生成。失敗時は false（呼び出し側は quad を諦める）。</summary>
        public bool Create(ulong session, long format, int width, int height)
        {
            Width = width; Height = height;
            var info = new XrSwapchainCreateInfo
            {
                type = XrStructureType.XR_TYPE_SWAPCHAIN_CREATE_INFO,
                // quad swapchain も TryStage() で D3D12 CopyResource の dst として使うため TRANSFER_DST を宣言する。
                usageFlags = XrSwapchainUsageFlags.XR_SWAPCHAIN_USAGE_COLOR_ATTACHMENT_BIT | XrSwapchainUsageFlags.XR_SWAPCHAIN_USAGE_TRANSFER_DST_BIT | XrSwapchainUsageFlags.XR_SWAPCHAIN_USAGE_SAMPLED_BIT,
                format = format,
                sampleCount = 1,
                width = (uint)width,
                height = (uint)height,
                faceCount = 1,
                arraySize = 1,
                mipCount = 1,
            };
            if (OpenXRAPI.xrCreateSwapchain(session, in info, out ulong handle) < 0)
            {
                VRModCore.LogError("QuadLayerSwapchain: xrCreateSwapchain 失敗。");
                return false;
            }
            Handle = handle;

            OpenXRAPI.xrEnumerateSwapchainImages(handle, 0, out uint imgCount, IntPtr.Zero);
            int stride = Marshal.SizeOf<XrSwapchainImageD3D11KHR>();
            // image struct は D3D11/D3D12 でレイアウト同一（流用）・type 値のみ backend で切替。
            var imgType = VrCameraSetup_CoreOpenXR.UseD3D12 ? XrStructureType.XR_TYPE_SWAPCHAIN_IMAGE_D3D12_KHR : XrStructureType.XR_TYPE_SWAPCHAIN_IMAGE_D3D11_KHR;
            IntPtr buf = Marshal.AllocHGlobal((int)imgCount * stride);
            try
            {
                for (int j = 0; j < imgCount; j++)
                    Marshal.StructureToPtr(new XrSwapchainImageD3D11KHR { type = imgType }, buf + j * stride, false);
                OpenXRAPI.xrEnumerateSwapchainImages(handle, imgCount, out imgCount, buf);
                _images.Clear();
                for (int j = 0; j < imgCount; j++)
                    _images.Add(Marshal.PtrToStructure<XrSwapchainImageD3D11KHR>(buf + j * stride).texture);
            }
            finally { Marshal.FreeHGlobal(buf); }
            return _images.Count > 0;
        }

        /// <summary>
        /// src（Unity RT の native D3D11 texture ptr）を swapchain image へコピーして release する。
        /// 戻り値 true = release 済み＝この swapchain を参照する layer を当フレーム submit してよい。
        /// false = acquire/wait timeout で当フレーム未確定（layer を append しない）。
        /// </summary>
        public bool TryStage(IntPtr srcNativeTex, long timeoutNs)
        {
            if (Handle == OpenXRConstants.XR_NULL_HANDLE || srcNativeTex == IntPtr.Zero) return false;

            var acquireInfo = new XrSwapchainImageAcquireInfo { type = XrStructureType.XR_TYPE_SWAPCHAIN_IMAGE_ACQUIRE_INFO };
            var waitInfo = new XrSwapchainImageWaitInfo { type = XrStructureType.XR_TYPE_SWAPCHAIN_IMAGE_WAIT_INFO, timeout = timeoutNs };
            var releaseInfo = new XrSwapchainImageReleaseInfo { type = XrStructureType.XR_TYPE_SWAPCHAIN_IMAGE_RELEASE_INFO };

            if (!_acquired)
            {
                if (OpenXRAPI.xrAcquireSwapchainImage(Handle, in acquireInfo, out _imgIdx) < 0) return false;
                _acquired = true;
            }

            XrResult wr = OpenXRAPI.xrWaitSwapchainImage(Handle, in waitInfo);
            if (wr != XrResult.XR_SUCCESS)
            {
                // timeout(>0) は acquire 保持で次フレ再 wait。負(エラー)は acquire を捨て次フレ再取得（image リーク防止）。
                if (wr < 0) _acquired = false;
                return false;
            }

            IntPtr dst = _images[(int)_imgIdx];
            if (dst != IntPtr.Zero)
            {
                if (VrCameraSetup_CoreOpenXR.UseD3D12)
                {
                    // render thread 経由（設計 §3）: enqueue → event → submit 完了の有限待機 → release
                    int ticket = NativeBridge.EnqueueCopyD3D12_Internal(srcNativeTex, dst);
                    VrCameraSetup_CoreOpenXR.NoteCopyTicket(ticket);   // cleanup drain 用に最終チケットを記録
                    if (ticket > 0)
                    {
                        GL.IssuePluginEvent(VrCameraSetup_CoreOpenXR.RenderEventFunc, NativeBridge.kEventExecutePendingCopies);
                        if (!NativeBridge.WaitForCopyTicket(ticket, ConfigManager.OpenXR_D3D12SubmitWaitMs?.Value ?? 50))
                            VRModCore.LogRuntimeDebug("OpenXR/D3D12: quad copy submit wait timeout（stale release で続行）。");
                    }
                }
                else
                {
                    NativeBridge.DirectCopyResource_Internal(dst, srcNativeTex);
                }
            }

            OpenXRAPI.xrReleaseSwapchainImage(Handle, in releaseInfo);
            _acquired = false;
            return true;
        }

        public void Destroy()
        {
            if (Handle != OpenXRConstants.XR_NULL_HANDLE && OpenXRAPI.xrDestroySwapchain != null)
                OpenXRAPI.xrDestroySwapchain(Handle);
            Handle = OpenXRConstants.XR_NULL_HANDLE;
            _images.Clear();
            _acquired = false;
        }
    }
}
