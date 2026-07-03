using UnityVRMod.Core;
using UnityVRMod.Features.VrVisualization;

namespace UnityVRMod.Config
{
    public static class ConfigManager
    {
        internal static readonly Dictionary<string, IConfigElement> ConfigElements = [];
        internal static readonly Dictionary<string, IConfigElement> InternalConfigs = [];
        public static ConfigHandler Handler { get; private set; }
        internal static InternalConfigHandler InternalHandler { get; private set; }

        // -------- CONFIG ELEMENT DEFINITIONS --------
        // --- Internal Settings (Require Restart) ---
        public static ConfigElement<float> Startup_Delay_Time;
        public static ConfigElement<bool> Disable_EventSystem_Override;
        public static ConfigElement<string> Reflection_Signature_Blacklist;
        public static ConfigElement<bool> EnableVrInjection;
        public static ConfigElement<string> VrApplicationKey;
        public static ConfigElement<bool> SafeModeStartsActive;

        // --- VR Rendering Settings ---
        public static ConfigElement<float> VrCameraNearClipPlane;
        public static ConfigElement<float> VrWorldScale;
        public static ConfigElement<float> VrUserEyeHeightOffset;
        public static ConfigElement<string> ScenePoseOverrides;
        public static ConfigElement<int> VrEyeMsaa;
        // --- Backend-Specific Stability Settings ---
#if OPENXR_BUILD
        public static ConfigElement<bool> OpenXR_TransitionKeepalive;
        public static ConfigElement<int> OpenXR_SwapchainWaitTimeoutMs;
        public static ConfigElement<int> OpenXR_D3D12SubmitWaitMs;
        public static ConfigElement<int> OpenXR_DxgiMemoryLogIntervalMs;
        public static ConfigElement<OpenXrStandbyMode> OpenXR_StandbyMode;
        public static ConfigElement<float> OpenXR_StandbyTeardownSec;
        public static ConfigElement<float> OpenXR_StandbyReattachProbeSec;
#endif

        // --- General Settings ---
        public static ConfigElement<bool> Force_Unlock_Mouse;
        public static ConfigElement<KeyCode> ToggleSafeModeKey;
        public static ConfigElement<SafeModeLevel> ActiveSafeModeLevel;
        public static ConfigElement<bool> EnableAutomaticSafeMode;
        public static ConfigElement<float> AutomaticSafeModeDurationSecs;
        public static ConfigElement<bool> EnableRuntimeDebugLogging;
        public static ConfigElement<string> AssertedCameraOverrides;
        public static ConfigElement<bool> DisableDesktopView;
        // -------- END OF CONFIG ELEMENT DEFINITIONS --------

        internal static void Init(ConfigHandler mainHandler)
        {
            Handler = mainHandler;
            Handler.Init();

            InternalHandler = new InternalConfigHandler();
            InternalHandler.Init();

            CreateConfigElements();

            VRModCore.LogRuntimeDebug("Loading main configuration...");
            Handler.LoadConfig();
            VRModCore.LogRuntimeDebug("Loading internal configuration...");
            InternalHandler.LoadConfig();
            VRModCore.Log("Configuration initialized and loaded.");
        }

        internal static void RegisterConfigElement<T>(ConfigElement<T> configElement)
        {
            if (!configElement.IsInternal)
            {
                Handler.RegisterConfigElement(configElement);
                ConfigElements.Add(configElement.Name, configElement);
            }
            else
            {
                InternalHandler.RegisterConfigElement(configElement);
                InternalConfigs.Add(configElement.Name, configElement);
            }
        }

        private static void CreateConfigElements()
        {
            VRModCore.Log("Creating configuration elements...");

            // --- Internal Settings (Require Restart) ---
            Startup_Delay_Time = new ConfigElement<float>("Startup Delay Time",
                "The delay (in seconds) before the mod fully initializes after game start.", 1.0f, isInternal: true);

            Disable_EventSystem_Override = new ConfigElement<bool>("Disable EventSystem Override",
                "If true, the mod will not override the game's EventSystem.", false, isInternal: true);

            Reflection_Signature_Blacklist = new ConfigElement<string>("Member Signature Blacklist",
                "Prevents the mod from reflecting on specific class members. Separate with ';'. Ex: 'UnityEngine.Camera.main;'", "", isInternal: true);

            EnableVrInjection = new ConfigElement<bool>("Enable VR Injection",
                "Master switch for all VR-related functionality. Change requires restart.", true, isInternal: true);

            VrApplicationKey = new ConfigElement<string>("VR Application Key",
                "The application key used when initializing OpenXR. Change requires restart.", "unityvrmod.default.key", isInternal: true);

            SafeModeStartsActive = new ConfigElement<bool>("Safe Mode Starts Active",
                "If true, VR rendering is disabled when the mod first loads.", false, isInternal: true);

            // --- VR Rendering Settings ---
            VrCameraNearClipPlane = new ConfigElement<float>("VR Camera Near Clip",
                "The closest distance (in meters) that the VR cameras can see. This value is scaled by World Scale.", 0.01f);
            VrCameraNearClipPlane.OnValueChanged += value => VRModCore.VrVisualizationFeature?.LiveUpdateCameraNearClip(value);

            VrWorldScale = new ConfigElement<float>("VR World Scale",
                "Adjusts the perceived size of the world. >1 makes the world feel larger; <1 makes it feel smaller.", 1.17f);
            VrWorldScale.OnValueChanged += value => VRModCore.VrVisualizationFeature?.LiveUpdateWorldScale(value);

            VrUserEyeHeightOffset = new ConfigElement<float>("User Eye Height Offset",
                "How much taller (+) or shorter (-) you want to feel in the virtual world (in meters). This value is scaled by World Scale.", -1.0f);
            VrUserEyeHeightOffset.OnValueChanged += value => VRModCore.VrVisualizationFeature?.LiveUpdateUserEyeHeightOffset(value);

            ScenePoseOverrides = new ConfigElement<string>("Scene-Specific Pose Overrides",
                "Defines a starting position and, optionally, rotation for the VR rig in specific scenes. Format: 'SceneName|X Y Z|Pitch Yaw Roll;'. Use '~' to keep a game's original value for any axis.", "");

            VrEyeMsaa = new ConfigElement<int>("VR Eye MSAA",
                "Anti-aliasing sample count for VR rendering (MSAA). Valid values: 1 (off), 2, 4, 8. " +
                "Other values are rounded down to the nearest valid one. Higher = smoother edges but more GPU " +
                "load and VRAM. Takes effect live while VR is active.", 4);

            // --- Backend-Specific Stability Settings ---
#if OPENXR_BUILD
            OpenXR_TransitionKeepalive = new ConfigElement<bool>("OpenXR Transition Keepalive",
                "[OpenXR ONLY] Keep pumping the OpenXR frame loop (xrWaitFrame/xrBeginFrame/xrEndFrame) while the " +
                "VR rig is torn down (scene transitions, unresolved camera). If frame submission stops, the " +
                "Oculus/OpenXR compositor flags the app as timed-out and the HMD freezes until the rig is rebuilt. " +
                "Resubmits the last rendered eye image head-tracked during the gap so the compositor stays focused.", true);
            OpenXR_SwapchainWaitTimeoutMs = new ConfigElement<int>("OpenXR Swapchain Wait Timeout (ms)",
                "[OpenXR ONLY] Max time to block the main thread waiting for a swapchain image (xrWaitSwapchainImage). " +
                "Normal waits are <16ms. A wait exceeding this is treated as a transient compositor wedge: the frame is " +
                "skipped (the acquired image is carried to the next frame and re-waited) instead of blocking forever. " +
                "Lower = snappier recovery but more risk of false timeouts under load. " +
                "Both eyes are waited serially, so a frame where both wedge can block up to ~2x this value. Takes effect live.", 100);
            OpenXR_D3D12SubmitWaitMs = new ConfigElement<int>("OpenXR D3D12 Submit Wait (ms)",
                "[OpenXR + D3D12 ONLY] Max time to wait for the render thread to submit eye/quad copies before " +
                "xrReleaseSwapchainImage. Normal waits are a few ms (render thread catching up). On timeout the frame " +
                "is released with stale content instead of blocking (same degradation as transition keepalive). " +
                "Takes effect live.", 50);
            OpenXR_DxgiMemoryLogIntervalMs = new ConfigElement<int>("OpenXR DXGI Memory Log Interval (ms)",
                "[OpenXR + D3D12 ONLY · diagnostic] Interval in ms to log IDXGIAdapter3::QueryVideoMemoryInfo's " +
                "LOCAL / NON_LOCAL CurrentUsage and Budget to BepInEx log. Used to check whether the shared GPU memory " +
                "increase reported by Task Manager corresponds to the D3D12 NON_LOCAL segment. " +
                "0 = disabled (default). 1000 = 1 Hz polling. Takes effect live.", 0);
            OpenXR_StandbyMode = new ConfigElement<OpenXrStandbyMode>("OpenXR Standby Mode",
                "[OpenXR ONLY] What to do when the session goes non-running (HMD doffed / standby). " +
                "SoftPark (default): keep the OpenXR instance alive, return rendering to the desktop, poll events only, " +
                "and auto-resume VR when the HMD is worn again — no teardown churn / micro-stutter. If the instance is " +
                "lost (LOSS_PENDING / Link disconnect) it auto-escalates to Teardown so the probe can reconnect. " +
                "Teardown: fully tear the instance down and reconnect via probe (old behavior) — needed only where the " +
                "Meta Link runtime client fail-fasts a dormant instance (c0000409), e.g. a D3D11 rollback.",
                OpenXrStandbyMode.SoftPark);
            OpenXR_StandbyTeardownSec = new ConfigElement<float>("OpenXR Standby Teardown (sec)",
                "[OpenXR ONLY] Grace period before entering standby (teardown or soft-park, see OpenXR Standby Mode) " +
                "when the OpenXR session is not running (HMD doffed / standby). Why a teardown matters in Teardown mode: " +
                "the in-process Meta Link runtime client (v85) fail-fasts the whole game process (c0000409) if it stays " +
                "dormant with a live OpenXR instance after standby (observed minimum delay ~12s). " +
                "Must stay well below 12, but above ~2: a normal worn launch is also non-running until the " +
                "runtime reports READY (<1s typical), and a too-small grace would trip standby during startup or a " +
                "scene-transition blip (desktop flicker). 0 = disabled (no standby handling at all).", 5f);
            OpenXR_StandbyReattachProbeSec = new ConfigElement<float>("OpenXR Standby Reattach Probe (sec)",
                "[OpenXR ONLY] While parked after a Teardown-mode standby (or after a SoftPark instance-loss escalation), " +
                "retry VR initialization at this interval. A probe creates a fresh instance+session and waits for the " +
                "runtime to report READY (= HMD worn again). If READY never comes, the standby grace tears the probe " +
                "instance down again until the next probe. 0 = no automatic reattach (toggle Safe Mode to reattach manually).", 10f);
#endif

            // --- General Settings ---
            Force_Unlock_Mouse = new ConfigElement<bool>("Force Unlock Mouse",
               "Forces the mouse cursor to be visible and unlocked when any mod UI is open.", true);
            Force_Unlock_Mouse.OnValueChanged += value => UniverseLib.Config.ConfigManager.Force_Unlock_Mouse = value;

            ToggleSafeModeKey = new ConfigElement<KeyCode>("Toggle Safe Mode Keybind",
                "The key used to toggle VR rendering on and off.", KeyCode.F11);

            ActiveSafeModeLevel = new ConfigElement<SafeModeLevel>("Safe Mode Level",
                "Defines the behavior of the Safe Mode toggle. Fast is quickest. RigReinit tears down the rig but keeps the session. FullVrReinit fully reinitializes the VR subsystem (default; cleanest reset).", SafeModeLevel.FullVrReinitOnToggle);

            EnableAutomaticSafeMode = new ConfigElement<bool>("Enable Automatic Safe Mode",
                "If true, VR rendering will be temporarily disabled during scene loads or when the game's main camera changes.", false);

            AutomaticSafeModeDurationSecs = new ConfigElement<float>("Automatic Safe Mode Duration",
                "The time (in seconds) that automatic safe mode will remain active.", 3.0f);

            EnableRuntimeDebugLogging = new ConfigElement<bool>("Enable Runtime Debug Logging",
                "Enables detailed, non-spammy debug messages to be printed to the console.", false);

            AssertedCameraOverrides = new ConfigElement<string>("Asserted Camera Overrides",
                "Manual overrides for camera detection if heuristics fail. Format: 'SceneName|GameObjectPath;GameObjectPath2'. Use full hierarchy or just the name. An empty scene name applies the override to all scenes.",
                "HoleFix|/HoleSceneProxy/HoleScene/CubemapMakeOffUseOn/CameraSetting/Center/CameraRotation/GameCamera");

            DisableDesktopView = new ConfigElement<bool>("Disable Desktop View",
                "If true, the game's flat view on the monitor is not rendered while VR is actively rendering, saving GPU. The monitor shows a blank (cleared) view during VR. No effect while VR rendering is paused (Safe Mode).", true);
            DisableDesktopView.OnValueChanged += value => VRModCore.VrVisualizationFeature?.LiveUpdateDesktopView(value);

            VRModCore.Log($"Finished creating {ConfigElements.Count + InternalConfigs.Count} config elements.");
        }

        public static void SaveAll()
        {
            VRModCore.LogRuntimeDebug("Saving main configuration...");
            Handler?.SaveConfig();
            VRModCore.LogRuntimeDebug("Saving internal configuration...");
            InternalHandler?.SaveConfig();
        }

        public static void LoadAll()
        {
            VRModCore.LogRuntimeDebug("Reloading main configuration...");
            Handler?.LoadConfig();
            VRModCore.LogRuntimeDebug("Reloading internal configuration...");
            InternalHandler?.LoadConfig();
        }
    }
}