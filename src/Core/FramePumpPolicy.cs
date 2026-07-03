namespace UnityVRMod.Core
{
    /// <summary>
    /// マネージャが毎フレーム決める「VR フレームをどう回すか」の純粋な方針。
    /// Render    = 通常描画（UpdatePoses）。
    /// Keepalive = rig 不在（遷移 teardown / カメラ未解決）でも compositor へフレーム提出を継続する。
    ///             提出が途切れると OpenXR/Oculus がアプリを timed-out 扱いにし HMD がフリーズする
    ///             （2026-06-09 検死: 2 回目 Bar 遷移で rig teardown 後にフレーム提出が止まり固着）。
    /// None      = 描画も keepalive もしない（safe mode で rig 構築済みのまま VR 一時停止中）。
    /// </summary>
    public enum VrFramePump { None, Render, Keepalive }

    /// <summary>UnityEngine 非依存の純判定（bool→enum）。両 backend / manager で共有しテストで固定する。</summary>
    public static class FramePumpPolicy
    {
        public static VrFramePump Decide(bool shouldRender, bool rigIsSetUp)
        {
            // rig 不在は safe mode 中でも無条件 keepalive（frame loop を絶対に止めない＝compositor timeout 防止）。
            if (!rigIsSetUp) return VrFramePump.Keepalive;
            // rig あり: 描画可否で分岐（safe mode 中は None＝VR 一時停止）。
            return shouldRender ? VrFramePump.Render : VrFramePump.None;
        }
    }
}
