namespace UnityVRMod.Features.VrVisualization
{
    /// <summary>
    /// doff（HMD 取り外し）/スタンバイ突入時の挙動を選ぶ。
    /// </summary>
    public enum OpenXrStandbyMode
    {
        /// <summary>
        /// teardown せず instance / session / rig を維持し、デスクトップ描画へ復帰する。event poll のみ回し、
        /// HMD 再装着（READY）で自動再開する（D3D12 既定・churn なし・推奨）。instance ごと喪失する doff
        /// （LOSS_PENDING / Link 切断）を検知したら自動的に Teardown へ escalate して probe 再接続で救済する。
        /// </summary>
        SoftPark,
        /// <summary>
        /// instance ごと full teardown して park し、probe 間隔で再 init を試す（旧挙動）。dormant fastfail
        /// （c0000409）が出る環境（D3D11 rollback 等）向け fallback。再接続のたびに OpenXR フル init/teardown
        /// が走るためメインスレッドに周期的な負荷がかかる。
        /// </summary>
        Teardown
    }
}
