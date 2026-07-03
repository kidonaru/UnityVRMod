using System.Diagnostics;
using System.Runtime.CompilerServices;
using UnityVRMod.Config;
using UnityVRMod.Loader;

#pragma warning disable IDE0130
namespace UnityVRMod.Core
#pragma warning restore IDE0130
{
    public static class VRModCore
    {
        public const string GUID = "com.newunitymodder.unityvrmod";
        public const string MOD_NAME = "Unity VR Mod";
        public const string VERSION = "0.1.0";
        public const string AUTHOR = "New Unity Modder";

        public static IVRModLoader Loader { get; private set; }

        internal static Features.VrVisualization.VrVisualizationManager VrVisualizationFeature { get; private set; }

        public static void Init(IVRModLoader loader)
        {
            if (Loader != null)
            {
                loader.LogWarning($"{MOD_NAME} is already loaded!");
                return;
            }
            Loader = loader;

            Log($"{MOD_NAME} {VERSION} initializing...");

            ConfigManager.Init(Loader.ConfigHandler);

            var universeConfig = new UniverseLib.Config.UniverseLibConfig
            {
                Disable_EventSystem_Override = ConfigManager.Disable_EventSystem_Override?.Value ?? false,
                Force_Unlock_Mouse = ConfigManager.Force_Unlock_Mouse?.Value ?? true,
                Unhollowed_Modules_Folder = Loader.InteropAssembliesPath
            };
            LogRuntimeDebug($"UniverseLibConfig prepared: EventSystemOverrideDisabled={universeConfig.Disable_EventSystem_Override}, ForceUnlockMouse={universeConfig.Force_Unlock_Mouse}");

            Universe.Init(ConfigManager.Startup_Delay_Time?.Value ?? 1.0f, LateInit, UniverseLib_Log, universeConfig);

            VRModBehaviour.Setup();
            LogRuntimeDebug("Core VRModCore.Init phase complete.");
        }

        static void LateInit()
        {
            LogRuntimeDebug("Executing LateInit tasks...");

            if (ConfigManager.EnableVrInjection?.Value ?? false)
            {
                Log("EnableVrInjection is true, initializing VrVisualizationFeature...");
                try
                {
                    VrVisualizationFeature = new Features.VrVisualization.VrVisualizationManager();
                    VrVisualizationFeature.Initialize();
                }
                catch (Exception ex)
                {
                    LogError("Exception during VrVisualizationManager creation or initialization:", ex);
                }
            }
            else
            {
                Log("EnableVrInjection is false, skipping VR initialization.");
            }
            Log($"{MOD_NAME} {VERSION} ({Universe.Context}) fully initialized.");
        }

        // 遷移セーフ API（companion の TransitionGuard が呼ぶ）。
        // シーン遷移前に VR を真に teardown し、遷移後に再 attach を許可する。冪等。
        public static void BeginTransitionGuard() => VrVisualizationFeature?.ForceTransitionTeardown();
        public static void EndTransitionGuard() => VrVisualizationFeature?.EndTransitionTeardown();

        // VR rig が描画可能な状態か（companion の UI/入力ガード用）。
        public static bool IsVrActive => VrVisualizationFeature?.IsVrReady ?? false;

        // ユーザーが F11 で VR を無効化中か（companion の入力ガード用）。RigReinitOnToggle は rig だけ
        // teardown して session を維持するため IsVrActive は true のまま＝「VR がユーザーへ実提示中か」を
        // 判定したい消費側はこれも併せて見る。未 init / feature 不在は true（= VR 非提示扱い）。
        public static bool IsUserSafeModeActive => VrVisualizationFeature?.IsUserSafeModeActive ?? true;

        /// <summary>セーフモードを切り替える（companion から呼ぶ public API）。</summary>
        public static void ToggleUserSafeMode() => VrVisualizationFeature?.ToggleUserSafeMode();

        // XR セッション running（= runtime の xrWaitFrame スロットルが効いている）。IsVrActive（rig ready）は
        // READY 待ち中も true になるため、フレームペーシング系はこちらも併せて見ること。未 init は false。
        public static bool IsXrSessionRunning => VrVisualizationFeature?.IsXrSessionRunning ?? false;

        // Phase3 WorldUiProjector facade（VR 非 ready 時は null）。
        public static UnityEngine.Transform GetRigTransform()
            => VrVisualizationFeature?.GetVrRigTransform();

        // 抑制中カメラの真値 cullingMask（VR 未 init / 非抑制は live 値）。companion の
        // カメラ識別が desktop 抑制(mask=0)に盲目化しないための窓。
        public static int GetEffectiveCullingMask(UnityEngine.Camera cam)
            => VrVisualizationFeature != null
                ? VrVisualizationFeature.GetEffectiveCullingMask(cam)
                : (cam != null ? cam.cullingMask : 0);

        public static UnityEngine.Camera GetVrEyeCamera()
            => VrVisualizationFeature?.VrCameraForUIParenting;

        // Phase3-T1 VR ポインタ: コントローラ snapshot facade（非 ready/未接続は Valid=false）。
        public static VrControllerSnapshot GetControllerSnapshot(VrHand hand)
        {
            var f = VrVisualizationFeature;
            if (f != null && f.TryGetControllerSnapshot(hand, out var snap)) return snap;
            return default;
        }

        public static void TriggerHaptic(VrHand hand, float amplitude, float durationSec)
            => VrVisualizationFeature?.TriggerHaptic(hand, amplitude, durationSec);

        // VR フェード: ゲームの ScreenFade を compositor へミラーする（companion VrFadeRunner 用）。
        // 戻り値=実際に push されたか（session 非生存は false。呼び出し側は false 時に lastPushed を更新しない）。
        public static bool SetCompositorFade(float r, float g, float b, float a)
            => VrVisualizationFeature?.SetCompositorFade(r, g, b, a) ?? false;

        // VR トランジション overlay: 遷移絵柄テクスチャを HMD 固定 overlay に表示する
        //（companion TransitionOverlayRunner 用）。戻り値=実際に push されたか
        //（session 非生存は false。呼び出し側は false 時に last 状態を更新しない）。
        public static bool SetTransitionOverlayTexture(System.IntPtr nativeTex, int srcWidth, int srcHeight, float uMin, float vMin, float uMax, float vMax)
            => VrVisualizationFeature?.SetTransitionOverlayTexture(nativeTex, srcWidth, srcHeight, uMin, vMin, uMax, vMax) ?? false;

        // worldLock=true で world 固定（頭ロックでなく可視 rising edge の頭正面に固定）。
        public static bool SetTransitionOverlayState(bool visible, float alpha, float widthMeters, float distanceMeters, bool worldLock)
            => VrVisualizationFeature?.SetTransitionOverlayState(visible, alpha, widthMeters, distanceMeters, worldLock) ?? false;

        // eye の cullingMask/clearFlags override（companion の EyeCullingCoordinator が所有）。
        // active=false で fork の game-copy へ戻す。VR 未 init は no-op。
        public static void SetEyeCullingOverride(bool active, int cullingMask, UnityEngine.CameraClearFlags clearFlags, UnityEngine.Color backgroundColor)
            => VrVisualizationFeature?.SetEyeCullingOverride(active, cullingMask, clearFlags, backgroundColor);

        // eye の URP post-process override（companion の PostProcessCoordinator が所有）。
        // active=true で eye カメラの renderPostProcessing を有効化し volumeLayerMask を指定 Volume へ向ける
        //（= ゲームのグレーディング+Bloom を VR 両眼に反映）。active=false で renderPostProcessing=false（既定）
        // へ戻す。実適用は fork の RenderEye が描画直前にリフレクションで行う。VR 未 init は no-op。
        public static void SetEyePostProcessOverride(bool active, int volumeLayerMask, int overlayLayerMask)
            => VrVisualizationFeature?.SetEyePostProcessOverride(active, volumeLayerMask, overlayLayerMask);

        // 選択的深度（コントローラだけが UI を遮る）の遮蔽源指定（companion の PostProcessCoordinator が所有）。
        // occluderMask の layer（コントローラ層）だけを depthMaterial で深度書き直しし、UI の遮蔽源を限定する。
        // occluderMask=0 / depthMaterial=null で無効。実適用は fork の DrawEyeOverlay。VR 未 init は no-op。
        public static void SetEyeOverlayOccluder(int occluderMask, UnityEngine.Material depthMaterial)
            => VrVisualizationFeature?.SetEyeOverlayOccluder(occluderMask, depthMaterial);

        // VR モデル（手モデル等）専用 overlay layer（companion の HandLightingRunner が所有）。
        // PostProcess 有無と独立に常時 overlay 描画される＝UI 同様の最前面化（main pass 除外 + post 後重ね描き）。
        // mask=0 で無効。実適用は fork の DrawEyeOverlay と eye cullingMask 除外計算。VR 未 init は no-op。
        public static void SetVrModelOverlay(int mask)
            => VrVisualizationFeature?.SetVrModelOverlay(mask);

        /// <summary>後段 transparent redraw callback。Camera.Render 後・DrawEyeOverlay 前。
        /// renderQueue を使わずに透過描画順を復元する。null で無効。</summary>
        public static void SetSceneTransparentRedraw(System.Action<UnityEngine.Camera, UnityEngine.RenderTexture> callback)
            => VrVisualizationFeature?.SetSceneTransparentRedraw(callback);

        // 正面リセット: 次フレームで _appSpace を「今の頭 pose が新原点・正面」へ作り直す（companion の
        // RecenterRunner が起動時 / 両手 Grip 長押しで呼ぶ）。VR 未 init / session 非生存は no-op（pending のみ）。
        public static void RequestRecenter()
            => VrVisualizationFeature?.RequestRecenter();

        internal static void Update()
        {
            try
            {
                VRModKeybind.Update();
                VrVisualizationFeature?.Update();
            }
            catch (Exception ex)
            {
                LogError("Exception during VRModCore.Update():", ex);
            }
        }

        #region LOGGING
        private const string UNIVERSELIB_IGNORE_WARNING_PHRASE = "THIS WARNING IS NOT BUG!!!! DON'T REPORT THIS!!!!!";

        // This method is specifically for UniverseLib's logger delegate.
        private static void UniverseLib_Log(string message, UnityEngine.LogType logType)
        {
            if (logType == UnityEngine.LogType.Warning && message.Contains(UNIVERSELIB_IGNORE_WARNING_PHRASE))
            {
                return;
            }
            LogImpl(message, logType, "UniverseLib", "", 0);
        }

        private static void LogImpl(object message, UnityEngine.LogType logType, string callerName, string callerFile, int callerLine, bool debugLevel = false)
        {
            if (Loader == null || message == null) return;
            string messageStr = message.ToString();
            string formattedMessage;

#if DEBUG
            bool isDebug = true;
#else
            bool isDebug = ConfigManager.EnableRuntimeDebugLogging?.Value ?? false;
#endif

            if (isDebug)
            {
                string prefix = string.IsNullOrEmpty(callerFile)
                    ? $"[{callerName}]"
                    : $"[{Path.GetFileNameWithoutExtension(callerFile)}.{callerName}:{callerLine}]";
                formattedMessage = $"{prefix} {messageStr}";
            }
            else
            {
                formattedMessage = $"[{Path.GetFileNameWithoutExtension(callerFile)}] {messageStr}";
            }

            switch (logType)
            {
                case UnityEngine.LogType.Log:
                case UnityEngine.LogType.Assert:
                    // debugLevel:true を渡す呼出は LogRuntimeDebug/LogSpammyDebug のみで、
                    // どちらも LogType.Log を渡す（Assert 経由で debugLevel:true は来ない）。
                    if (debugLevel) Loader.LogDebug(formattedMessage);
                    else Loader.LogMessage(formattedMessage);
                    break;
                case UnityEngine.LogType.Warning:
                    Loader.LogWarning(formattedMessage);
                    break;
                case UnityEngine.LogType.Error:
                case UnityEngine.LogType.Exception:
                    Loader.LogError(formattedMessage);
                    break;
            }
        }

        public static void Log(object message, [CallerMemberName] string callerName = "", [CallerFilePath] string callerFile = "", [CallerLineNumber] int callerLine = 0)
        {
            LogImpl(message, UnityEngine.LogType.Log, callerName, callerFile, callerLine);
        }

        public static void LogWarning(object message, [CallerMemberName] string callerName = "", [CallerFilePath] string callerFile = "", [CallerLineNumber] int callerLine = 0)
        {
            LogImpl(message, UnityEngine.LogType.Warning, callerName, callerFile, callerLine);
        }

        public static void LogError(object message, object exception = null, [CallerMemberName] string callerName = "", [CallerFilePath] string callerFile = "", [CallerLineNumber] int callerLine = 0)
        {
            string finalMessage = (exception != null) ? $"{message}\n{exception}" : message.ToString();
            LogImpl(finalMessage, UnityEngine.LogType.Error, callerName, callerFile, callerLine);
        }

        public static void LogRuntimeDebug(object message, [CallerMemberName] string callerName = "", [CallerFilePath] string callerFile = "", [CallerLineNumber] int callerLine = 0)
        {
#if DEBUG
            LogImpl(message, UnityEngine.LogType.Log, callerName, callerFile, callerLine, debugLevel: true);
#else
            if (ConfigManager.EnableRuntimeDebugLogging?.Value ?? false)
            {
                LogImpl(message, UnityEngine.LogType.Log, callerName, callerFile, callerLine, debugLevel: true);
            }
#endif
        }

        [Conditional("ENABLE_VDEBUG_LOGGING")]
        public static void LogSpammyDebug(object message, [CallerMemberName] string callerName = "", [CallerFilePath] string callerFile = "", [CallerLineNumber] int callerLine = 0)
        {
            LogImpl($"[VDEBUG] {message}", UnityEngine.LogType.Log, callerName, callerFile, callerLine, debugLevel: true);
        }
        #endregion
    }
}