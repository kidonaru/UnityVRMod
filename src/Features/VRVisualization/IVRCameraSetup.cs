namespace UnityVRMod.Features.VrVisualization
{
    public struct VrCameraRig
    {
        public GameObject LeftEye;
        public GameObject RightEye;
    }

    internal interface IVrCameraSetup
    {
        bool IsVrAvailable { get; }
        /// <summary>OpenXR session が running（xrBeginSession 済・未 xrEndSession）か。doff/standby 検知（StandbyPolicy）用。</summary>
        bool IsSessionRunning { get; }
        bool InitializeVr(string applicationKey);
        void TeardownVr();
        void SetupCameraRig(Camera mainCamera);
        // camera-flip 時に rig GO を破棄せず既存 eye を新主カメラへ再バインドする（rig 未構築なら setup へ委譲）。
        void RebindCameraRig(Camera mainCamera);
        void TeardownCameraRig();
        VrCameraRig GetVrCameraGameObjects();
        void UpdatePoses();

        // 正面リセット: 次フレームで _appSpace を「今の頭 pose が新原点・正面」になるよう作り直す（システムメニュー
        // の Reset View 相当）。eye / コントローラ / world-locked overlay は全て _appSpace 基準のため一括で整合する。
        void RequestRecenter();

        // --- METHODS FOR LIVE RELOADING ---
        void SetWorldScale(float newWorldScale, Camera mainCamera);
        void SetCameraNearClip(float newNearClip);
        void SetUserEyeHeightOffset(float newEyeHeightOffset);

        // Phase3-T1 VR ポインタ: コントローラの rig-local pose + ボタンを取得（未対応 backend は false）。
        bool TryGetControllerSnapshot(UnityVRMod.Core.VrHand hand, out UnityVRMod.Core.VrControllerSnapshot snapshot);
        void TriggerHaptic(UnityVRMod.Core.VrHand hand, float amplitude, float durationSec);

        // VR フェード mirror: compositor の全画面 fade を即時設定する。
        // 戻り値=実際に設定できたか（session 非生存/未対応 backend は false）。
        bool SetCompositorFade(float r, float g, float b, float a);

        // eye カメラの cullingMask/clearFlags を companion(BG2VR EyeCullingCoordinator)が所有するための override。
        // RenderEye が描画直前に適用する単一所有点。active=false で fork の game-copy(_mainCamera*) へ戻す。
        // 毎フレ push 前提（rig 生存時のみ実効・teardown 中は RenderEye 非呼出で無効果）。
        void SetEyeCullingOverride(bool active, int cullingMask, CameraClearFlags clearFlags, Color backgroundColor);

        // eye カメラの URP post-process を companion(BG2VR PostProcessCoordinator)が所有するための override。
        // active=true で renderPostProcessing=true + volumeLayerMask を指定（ゲームのグレーディング+Bloom を反映）。
        // RenderEye が描画直前にリフレクションで適用する。active=false で renderPostProcessing=false（既定）へ戻す。
        void SetEyePostProcessOverride(bool active, int volumeLayerMask, int overlayLayerMask);

        // 選択的深度（コントローラだけが UI を遮る）の遮蔽源指定。DrawEyeOverlay が UI 描画の直前に
        // RT 深度を一旦 far へ消し、occluderMask の layer（= コントローラ層）だけを depthMaterial で描き直す
        // ＝遮蔽源をコントローラのみに限定する（シーンは UI を遮らない）。occluderMask=0 / depthMaterial=null で無効。
        // SetEyePostProcessOverride（overlay 機構）が active なときのみ実効（overlay 不在では DrawEyeOverlay 自体が走らない）。
        void SetEyeOverlayOccluder(int occluderMask, Material depthMaterial);

        // VR モデル（手モデル等）専用 overlay layer。PostProcess の有無と独立に常時 overlay 描画される layer。
        // companion(BG2VR HandLightingRunner) が毎フレ push。effective overlayMask = PostProcess由来 | このマスク
        // ＝main pass から除外（二重描画防止）+ post 後の overlay pass で crisp 重ね描き＝UI と同じく最前面化。
        // mask=0 で無効。VR 未 init は no-op。
        void SetVrModelOverlay(int mask);

        // 後段 transparent redraw callback。Camera.Render() 後・DrawEyeOverlay() 前に呼ぶ。
        // companion(BG2VR TransparentRedrawRunner) が登録。null で無効。
        void SetSceneTransparentRedraw(System.Action<Camera, RenderTexture> callback);

        // VR トランジション overlay: 遷移絵柄テクスチャを overlay として表示する。
        // SetCompositorFade と同じく session レベル（rig teardown 中も有効）。未対応 backend は false。
        // worldLock=true で頭ロックでなく world 固定（可視 rising edge で anchor を凍結）。
        bool SetTransitionOverlayTexture(System.IntPtr nativeTex, int srcWidth, int srcHeight, float uMin, float vMin, float uMax, float vMax);
        bool SetTransitionOverlayState(bool visible, float alpha, float widthMeters, float distanceMeters, bool worldLock);

        // 遷移 teardown 中（rig 不在・セッション生存）の compositor keepalive。
        // WaitGetPoses + 前フレーム eye texture の再 submit を 1 フレーム分行う（Camera.Render なし）。
        // submit が途切れると compositor が timed-out→resume し、共有テクスチャの keyed mutex handoff が
        // 壊れてフリーズ/切断する（2026-06-07 検死）。戻り値 = 実際に両眼 submit できたか
        //（session 非生存 / 未対応 backend / config OFF は false）。
        bool SubmitTransitionKeepalive();

        // コントローラ render model は companion(BG2VR) が同梱 bundle から直接ロードする（Phase3）＝facade なし。
    }
}