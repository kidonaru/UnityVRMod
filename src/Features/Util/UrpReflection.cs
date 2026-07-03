using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityVRMod.Features.Util
{
    /// <summary>
    /// URP の reflection を 1 箇所に集約する静的ヘルパ。fork も BG2VR も URP アセンブリを直参照しない
    /// （移植性・参照を増やさない方針）ため、型・プロパティは reflection で解決し静的キャッシュする。
    /// 型未解決（built-in パイプラインのゲーム）では全 API が安全に no-op／false を返す。
    /// PropertyInfo はキャッシュ済＝ホットパスでも毎フレの GetProperty は発生しない。
    /// </summary>
    public static class UrpReflection
    {
        private static bool s_acdResolved;
        private static Type s_acdType;
        private static PropertyInfo s_renderPostProcessing; // bool
        private static PropertyInfo s_volumeLayerMask;      // LayerMask
        private static PropertyInfo s_antialiasing;         // enum AntialiasingMode
        private static PropertyInfo s_antialiasingQuality;  // enum AntialiasingQuality

        private static Type AcdType()
        {
            if (s_acdResolved) return s_acdType;
            s_acdResolved = true;
            s_acdType = Type.GetType("UnityEngine.Rendering.Universal.UniversalAdditionalCameraData, Unity.RenderPipelines.Universal.Runtime");
            if (s_acdType == null)
                foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    s_acdType = asm.GetType("UnityEngine.Rendering.Universal.UniversalAdditionalCameraData");
                    if (s_acdType != null) break;
                }
            if (s_acdType != null)
            {
                s_renderPostProcessing = s_acdType.GetProperty("renderPostProcessing");
                s_volumeLayerMask = s_acdType.GetProperty("volumeLayerMask");
                s_antialiasing = s_acdType.GetProperty("antialiasing");
                s_antialiasingQuality = s_acdType.GetProperty("antialiasingQuality");
            }
            return s_acdType;
        }

        /// <summary>ACD 型 + renderPostProcessing を解決できたか（false なら ACD 系 API は no-op）。</summary>
        public static bool AcdAvailable => AcdType() != null && s_renderPostProcessing != null;

        /// <summary>cam に ACD コンポーネントが付いているか（型未解決／未付与は false）。</summary>
        public static bool HasCameraData(Camera cam)
        {
            if (cam == null || AcdType() == null) return false;
            return cam.GetComponent(s_acdType) != null;
        }

        /// <summary>ACD 未付与なら AddComponent で明示確保する（型未解決は no-op）。</summary>
        public static void EnsureCameraData(GameObject cameraGo)
        {
            Type t = AcdType();
            if (t == null || cameraGo == null) return;
            if (cameraGo.GetComponent(t) == null) cameraGo.AddComponent(t);
        }

        /// <summary>ACD.renderPostProcessing を設定する（ACD/型未解決は no-op）。</summary>
        public static void SetRenderPostProcessing(Camera cam, bool enabled)
        {
            if (cam == null || !AcdAvailable) return;
            var acd = cam.GetComponent(s_acdType);
            if (acd == null) return;
            s_renderPostProcessing.SetValue(acd, enabled);
        }

        /// <summary>
        /// antialiasing / antialiasingQuality / volumeLayerMask を src→dst の ACD へコピーする。
        /// renderPostProcessing は含めない（有効化は呼び出し側が制御）。いずれかの ACD 無しは no-op。
        /// </summary>
        public static void CopyPostProcessSettings(Camera src, Camera dst)
        {
            if (src == null || dst == null || AcdType() == null) return;
            var srcAcd = src.GetComponent(s_acdType);
            var dstAcd = dst.GetComponent(s_acdType);
            if (srcAcd == null || dstAcd == null) return;
            if (s_antialiasing != null) s_antialiasing.SetValue(dstAcd, s_antialiasing.GetValue(srcAcd));
            if (s_antialiasingQuality != null) s_antialiasingQuality.SetValue(dstAcd, s_antialiasingQuality.GetValue(srcAcd));
            if (s_volumeLayerMask != null) s_volumeLayerMask.SetValue(dstAcd, s_volumeLayerMask.GetValue(srcAcd));
        }

        private static PropertyInfo s_msaaProp;

        private static PropertyInfo MsaaProp(object rp)
        {
            // 旧 EyeMsaaRunner.Prop と同じく解決できるまで再試行する（set-once にしない＝初回 rp が
            // 非 URP でも、後続フレームで URP asset が currentRenderPipeline に来れば解決できる）。
            if (s_msaaProp == null && rp != null)
                s_msaaProp = rp.GetType().GetProperty("msaaSampleCount");
            return s_msaaProp;
        }

        /// <summary>
        /// pipeline（URP asset）の msaaSampleCount を取得する。URP 非使用（built-in）／プロパティ未解決は false。
        /// </summary>
        public static bool TryGetPipelineMsaa(out int value)
        {
            value = 1;
            var rp = GraphicsSettings.currentRenderPipeline;
            if (rp == null) return false;
            var p = MsaaProp(rp);
            if (p == null) return false;
            value = (int)p.GetValue(rp);
            return true;
        }

        /// <summary>pipeline（URP asset）の msaaSampleCount を設定する（URP 非使用／未解決は no-op）。</summary>
        public static void SetPipelineMsaa(int value)
        {
            var rp = GraphicsSettings.currentRenderPipeline;
            if (rp == null) return;
            var p = MsaaProp(rp);
            if (p != null) p.SetValue(rp, value);
        }
    }
}
