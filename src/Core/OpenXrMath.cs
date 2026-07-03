using UnityEngine;

namespace UnityVRMod.Core
{
    /// <summary>OpenXR（右手系・-Z 前方）→ Unity（左手系・+Z 前方）の pose 変換（純関数）。
    /// eye pose 変換（VrCameraSetup_CoreOpenXR の RenderEye）と同一の符号則。
    /// XrPosef 等の OPENXR_BUILD 限定型を持ち込まないよう raw float を引数に取る。</summary>
    public static class OpenXrMath
    {
        public static Vector3 ToUnityPosition(float x, float y, float z) => new Vector3(x, y, -z);
        public static Quaternion ToUnityRotation(float x, float y, float z, float w) => new Quaternion(x, y, -z, -w);
    }
}
