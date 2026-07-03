using UnityVRMod.Config;
using UniverseLib.Input; // Using UniverseLib's InputManager for universal input

#pragma warning disable IDE0130
namespace UnityVRMod.Core
#pragma warning restore IDE0130
{
    public static class VRModKeybind
    {
        /// <summary>companion (BG2VR) がキーバインドを管理する場合 true。fork 側の処理をスキップする。</summary>
        public static bool ExternallyManaged;

        public static void Update()
        {
            if (ExternallyManaged) return;

            if (ConfigManager.ToggleSafeModeKey != null && InputManager.GetKeyDown(ConfigManager.ToggleSafeModeKey.Value))
            {
                VRModCore.LogRuntimeDebug("Toggle Safe Mode key pressed!");
                if (VRModCore.VrVisualizationFeature != null)
                {
                    VRModCore.VrVisualizationFeature.ToggleUserSafeMode();
                }
                else
                {
                    VRModCore.LogWarning("VrVisualizationManager (VrVisFeature) is null. Cannot toggle safe mode.");
                }
            }

            // REMINDER: Add other mod-specific keybind checks here if needed in the future.
        }
    }
}