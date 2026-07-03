using UnityVRMod.Config;
using UnityVRMod.Core;
using UnityVRMod.Features.Util;
using UnityEngine.Events;
using UnityEngine.SceneManagement;

#if CPP
using Il2CppInterop.Runtime;
#endif

namespace UnityVRMod.Features.VrVisualization
{
    internal class VrVisualizationManager
    {
        private IVrCameraSetup _cameraSetup;
        private bool _managerInitialized = false;
        private GameObject _currentlyTrackedOriginalCameraGO = null;

        // VR 描画中にデスクトップ（モニタ）側のフラット描画を抑制するための状態。
        // 抑制対象は追従中 1 台ではなく「enabled・targetTexture 無し・rig 配下でない全カメラ」
        // （残留 env scene の GameCamera 等がモニタへ漏れるのを止める）。各カメラの cullingMask を
        // 0 にして全オブジェクトを描画対象外にし GPU を節約する。mask=0 でも clearFlags=Skybox だと
        // skybox（IBL 用環境キューブマップ等）が毎フレーム描かれ続け「背景が残る」（BG2 実機
        // 2026-06-07）ため clear も SolidColor 黒へ差し替え、URP の post-processing も reflection
        // 経由で止める（解像度分の post スタック GPU 節約）。
        // enabled は触らない（disabled カメラは CameraFinder / Camera.main から取りこぼされ
        // rig 再生成ループに陥るため）。
        //
        // map のキー集合 == 現フレで抑制中の desktop カメラ集合（SetDesktopRenderSuppressed の
        // mark-and-sweep で毎フレ同期する）。Unity の == は破棄済オブジェクトを互いに等価
        // （fake-null）と扱うため、Dictionary キーにそのまま使うと別カメラ同士が衝突する。
        // 参照同一性で比較する comparer を使う。
        private readonly Dictionary<Camera, DesktopSuppressionState> _suppressedCameras
            = new Dictionary<Camera, DesktopSuppressionState>(ReferenceComparer.Instance);
        // Camera.GetAllCameras 非 alloc 列挙用バッファ（allCamerasCount に応じて拡張）。
        private Camera[] _cameraEnumBuffer = new Camera[8];
        // mark-and-sweep で「対象外になった抑制中カメラ」を一時収集する（列挙中の map 変更回避）。
        private readonly List<Camera> _suppressionSweepScratch = new List<Camera>();

        // 抑制中 desktop カメラの抑制前の真値。
        private struct DesktopSuppressionState
        {
            public int OriginalCullingMask;
            public CameraClearFlags OriginalClearFlags;
            public Color OriginalBackgroundColor;
            public Component Acd;                       // URP ACD（解決不能 / 非 URP なら null）
            public bool? OriginalRenderPostProcessing;  // null = この環境では postFX 抑制を諦める
        }

        // Dictionary キー用の参照同一性 comparer（破棄済 Unity オブジェクトの fake-null 衝突回避）。
        private sealed class ReferenceComparer : IEqualityComparer<Camera>
        {
            public static readonly ReferenceComparer Instance = new ReferenceComparer();
            public bool Equals(Camera a, Camera b) => ReferenceEquals(a, b);
            public int GetHashCode(Camera c) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(c);
        }

        private bool _isUserSafeModeActive = true;
        private float _autoSafeModeEndTime = -1f;

        // 遷移中フラグ。立っている間は rig 不在（rig-level teardown 済・OpenXR セッションは維持）を保ち、
        // Update() の自動 rig 再構築 / 再 init を保留する。
        private bool _transitionTeardownActive = false;
        // 遷移中の keepalive 成功フレーム数 / 試行フレーム数（検証証拠: EndTransitionTeardown で
        // 「frames=成功/試行」をログする。成功≒試行 なら遷移窓全体で submit が継続していた証拠、
        // 成功≪試行 なら窓内で submit が途切れていた＝keepalive が効いていない兆候）。
        private int _transitionKeepaliveFrames = 0;
        private int _transitionKeepaliveAttempts = 0;

        // --- doff/スタンバイ検知（StandbyPolicy・spec §5.5.3）---
        // session 非 running の開始時刻（realtime）。負 = running 中（タイマー非作動）。
        private float _sessionNotRunningSince = -1f;
        // standby park 中フラグ（Teardown / SoftPark 共用）。
        // Teardown モード: instance ごと破棄して park。毎フレーム自動再 init すると dormant instance が即再生成され
        //   Meta client の時限 fastfail が再武装するため、再 init を probe 間隔に制限する（_cameraSetup==null 経路）。
        // SoftPark モード: teardown せず instance/session/rig を維持して park。event poll のみ回して READY で
        //   自動再開する（_cameraSetup!=null 経路）。instance 喪失時のみ Teardown へ escalate する。
        private bool _standbyParked = false;
        private float _lastStandbyProbeTime = float.NegativeInfinity;

        private UnityAction<Scene, Scene> _sceneChangedActionDelegate;

        private bool _hasVrBeenAttemptedByUser = false;
        // 遷移中（rig-level teardown 中）は rig 不在なので false（companion の「描画可能」ガード契約を維持。
        // full teardown 時代は _cameraSetup == null で自然に false になっていた）。
        // standby park 中も false: Teardown モードは _cameraSetup==null で自然に false、SoftPark モードは
        // instance を維持する（_cameraSetup!=null）ので !_standbyParked で明示的に落とす（doff 中は VR 非提示）。
        internal bool IsVrReady => _hasVrBeenAttemptedByUser && _cameraSetup != null && _cameraSetup.IsVrAvailable && !_transitionTeardownActive && !_standbyParked;

        // ユーザーが F11（Toggle Safe Mode）で VR を無効化中か。RigReinitOnToggle では rig だけ teardown して
        // _cameraSetup/session は維持するため IsVrReady は true のまま＝「VR が実際にユーザーへ提示中か」を
        // 知りたい消費側（入力ガード等）はこれも併せて見る（FullVrReinit では _cameraSetup==null で IsVrReady が
        // 自然に false になるが、RigReinit では落ちない）。
        internal bool IsUserSafeModeActive => _isUserSafeModeActive;

        // XR セッションが running か（Update 内 standby 判定式と同一意味論）。rig ready（IsVrReady）は
        // READY 待ち中も true になるため、「runtime のフレームスロットルが効いているか」はこちらで判定する。
        internal bool IsXrSessionRunning => _cameraSetup != null && _cameraSetup.IsVrAvailable && _cameraSetup.IsSessionRunning;

        internal Camera VrCameraForUIParenting
        {
            get
            {
                if (!IsVrReady) return null;
                VrCameraRig rig = _cameraSetup.GetVrCameraGameObjects();
                if (rig.LeftEye != null) return rig.LeftEye.GetComponent<Camera>();
                if (rig.RightEye != null) return rig.RightEye.GetComponent<Camera>();
                return null;
            }
        }

        // Phase3 WorldUiProjector: rig root transform（eye は rig の子＝parent が rig）。
        internal Transform GetVrRigTransform()
        {
            if (!IsVrReady) return null;
            VrCameraRig rig = _cameraSetup.GetVrCameraGameObjects();
            if (rig.LeftEye != null) return rig.LeftEye.transform.parent;
            if (rig.RightEye != null) return rig.RightEye.transform.parent;
            return null;
        }

        // 抑制中の desktop カメラは保存済の真値 cullingMask を、非抑制カメラは live 値を返す。
        // デスクトップ抑制（cullingMask=0）に「3D カメラ識別」ロジック（companion の CameraBridge）が
        // 盲目化するのを防ぐための公開窓（描画制御と識別の役割分離）。
        internal int GetEffectiveCullingMask(Camera cam)
        {
            if (cam == null) return 0;
            if (_suppressedCameras.TryGetValue(cam, out DesktopSuppressionState st))
                return st.OriginalCullingMask;
            return cam.cullingMask;
        }

        // Phase3-T1 VR ポインタ: コントローラ snapshot（VR 非 ready は Valid=false）。
        internal bool TryGetControllerSnapshot(VrHand hand, out VrControllerSnapshot snapshot)
        {
            snapshot = default;
            if (!IsVrReady) return false;
            return _cameraSetup.TryGetControllerSnapshot(hand, out snapshot);
        }

        internal void TriggerHaptic(VrHand hand, float amplitude, float durationSec)
            => _cameraSetup?.TriggerHaptic(hand, amplitude, durationSec);

        // VR フェード mirror。注意: IsVrReady でガードしない（遷移 teardown 中=false だが、
        // その間こそ黒フェード維持が必要）。session 生死は backend の IsVrAvailable 自己ガードに委ねる。
        internal bool SetCompositorFade(float r, float g, float b, float a)
        {
            var setup = _cameraSetup;
            return setup != null && setup.SetCompositorFade(r, g, b, a);
        }

        // VR トランジション overlay。SetCompositorFade と同じく IsVrReady でガードしない
        //（遷移 teardown 中=false だが、その間こそ overlay 表示が本機能の核心価値）。
        internal bool SetTransitionOverlayTexture(System.IntPtr nativeTex, int srcWidth, int srcHeight, float uMin, float vMin, float uMax, float vMax)
        {
            var setup = _cameraSetup;
            return setup != null && setup.SetTransitionOverlayTexture(nativeTex, srcWidth, srcHeight, uMin, vMin, uMax, vMax);
        }

        internal bool SetTransitionOverlayState(bool visible, float alpha, float widthMeters, float distanceMeters, bool worldLock)
        {
            var setup = _cameraSetup;
            return setup != null && setup.SetTransitionOverlayState(visible, alpha, widthMeters, distanceMeters, worldLock);
        }

        // eye cullingMask/clearFlags override の passthrough。fade/overlay と同じく IsVrReady ガードしない
        //（setup != null のみ）。teardown 中は RenderEye 非呼出で無効果＝ガード有無どちらも安全。
        internal void SetEyeCullingOverride(bool active, int cullingMask, CameraClearFlags clearFlags, Color backgroundColor)
            => _cameraSetup?.SetEyeCullingOverride(active, cullingMask, clearFlags, backgroundColor);

        // eye の URP post-process override の passthrough（cullingMask override と同型・IsVrReady ガードしない）。
        internal void SetEyePostProcessOverride(bool active, int volumeLayerMask, int overlayLayerMask)
            => _cameraSetup?.SetEyePostProcessOverride(active, volumeLayerMask, overlayLayerMask);

        // 選択的深度（コントローラ遮蔽）の occluder 指定の passthrough（post override と同型・IsVrReady ガードしない）。
        internal void SetEyeOverlayOccluder(int occluderMask, Material depthMaterial)
            => _cameraSetup?.SetEyeOverlayOccluder(occluderMask, depthMaterial);

        // VR モデル overlay layer の passthrough（post override と同型・IsVrReady ガードしない）。
        internal void SetVrModelOverlay(int mask)
            => _cameraSetup?.SetVrModelOverlay(mask);

        internal void SetSceneTransparentRedraw(System.Action<Camera, RenderTexture> callback)
            => _cameraSetup?.SetSceneTransparentRedraw(callback);

        // 正面リセット passthrough（IsVrReady ガードしない＝fork 側で session/space を判定）。
        internal void RequestRecenter()
            => _cameraSetup?.RequestRecenter();

        internal void Initialize()
        {
            if (_managerInitialized) return;
            _managerInitialized = true;

            if (!ConfigManager.EnableVrInjection.Value)
            {
                VRModCore.Log("VR Visualization feature is disabled by config.");
                return;
            }

            _isUserSafeModeActive = ConfigManager.SafeModeStartsActive.Value;
            VRModCore.Log($"Initial user safe mode active: {_isUserSafeModeActive}. VR init will be delayed until user first deactivates Safe Mode.");

            if (ConfigManager.EnableAutomaticSafeMode.Value)
            {
                VRModCore.LogRuntimeDebug("Automatic safe mode enabled, subscribing to scene changes.");
#if CPP
                Action<Scene, Scene> csAction = OnActiveSceneChanged;
                _sceneChangedActionDelegate = DelegateSupport.ConvertDelegate<UnityAction<Scene, Scene>>(csAction);
                if (_sceneChangedActionDelegate != null) SceneManager.activeSceneChanged += _sceneChangedActionDelegate;
                else VRModCore.LogError("(IL2CPP) Failed to convert scene change delegate.");
#else
                _sceneChangedActionDelegate = OnActiveSceneChanged;
                SceneManager.activeSceneChanged += _sceneChangedActionDelegate;
#endif
            }
            VRModCore.Log("VrVisualizationManager initialized.");
        }

        private bool EnsureAndInitializeVrSubsystem()
        {
            if (_cameraSetup != null && _cameraSetup.IsVrAvailable)
            {
                VRModCore.LogRuntimeDebug("VR subsystem already initialized and available.");
                return true;
            }

            VRModCore.LogRuntimeDebug("Attempting to initialize VR subsystem...");

            string cameraSetupTypeNameFull;
#if OPENXR_BUILD
            cameraSetupTypeNameFull = "UnityVRMod.Features.VrVisualization.VrCameraSetup_CoreOpenXR";
#else
            VRModCore.LogError("Critical Error! No VR backend build symbol (OPENXR_BUILD) defined!");
            return false;
#endif

            try
            {
                Type setupType = Type.GetType(cameraSetupTypeNameFull);
                if (setupType == null || !typeof(IVrCameraSetup).IsAssignableFrom(setupType))
                {
                    VRModCore.LogError($"Type '{cameraSetupTypeNameFull}' not found or invalid.");
                    return false;
                }
                _cameraSetup = Activator.CreateInstance(setupType) as IVrCameraSetup;
                if (_cameraSetup == null)
                {
                    VRModCore.LogError($"Failed to create instance of '{cameraSetupTypeNameFull}'.");
                    return false;
                }

                VRModCore.Log($"Loaded {_cameraSetup.GetType().Name}. Initializing VR subsystem...");
                if (!_cameraSetup.InitializeVr(ConfigManager.VrApplicationKey.Value))
                {
                    VRModCore.LogWarning("VR subsystem initialization failed.");
                    _cameraSetup = null;
                    return false;
                }

                VRModCore.LogRuntimeDebug("VR subsystem initialization successful.");
                _hasVrBeenAttemptedByUser = true;
                                
                return true;
            }
            catch (Exception ex)
            {
                VRModCore.LogError("Exception during VR subsystem instantiation or initialization:", ex);
                _cameraSetup = null;
                return false;
            }
        }

        private void OnActiveSceneChanged(Scene current, Scene next)
        {
            VRModCore.LogRuntimeDebug($"Scene changed from '{current.name}' to '{next.name}'.");
            CameraFinder.InvalidateCache(); // Invalidate cache on scene change.
            if (!IsVrReady) return;

            if (ConfigManager.EnableAutomaticSafeMode.Value)
            {
                ActivateAutomaticSafeMode($"Scene changed to '{next.name}'");
            }
        }

        private void ActivateAutomaticSafeMode(string reason)
        {
            if (!IsVrReady || !ConfigManager.EnableAutomaticSafeMode.Value) return;

            float duration = ConfigManager.AutomaticSafeModeDurationSecs.Value;
            _autoSafeModeEndTime = Time.time + duration;
            VRModCore.Log($"Automatic Safe Mode ENGAGED for {duration:F1}s. Reason: {reason}.");
        }

        public void ToggleUserSafeMode()
        {
            _isUserSafeModeActive = !_isUserSafeModeActive;
            VRModCore.Log($"User Safe Mode Toggled. Now: {(_isUserSafeModeActive ? "ACTIVE (Rendering OFF)" : "INACTIVE (Rendering ON)")}");

            if (_isUserSafeModeActive)
            {
                RestoreAllDesktopRender();              // VR 停止時はデスクトップ描画を戻す
                // ユーザーが明示的に VR を切る操作なので standby park 状態は破棄する（teardown 後も含め
                // 「_cameraSetup を null にする経路はラッチを揃える」不変条件に park フラグも合わせる）。
                _standbyParked = false;
                _sessionNotRunningSince = -1f;
                if (_cameraSetup != null)
                {
                    var level = ConfigManager.ActiveSafeModeLevel.Value;
                    VRModCore.LogRuntimeDebug($"Entering Safe Mode with level: {level}");

                    if (level == SafeModeLevel.FullVrReinitOnToggle)
                    {
                        VRModCore.Log("Tearing down full VR subsystem for re-initialization.");
                        _cameraSetup.TeardownVr();
                        _cameraSetup = null;
                        _hasVrBeenAttemptedByUser = false;
                        CameraFinder.InvalidateCache();
                    }
                    else if (level == SafeModeLevel.RigReinitOnToggle)
                    {
                        VRModCore.Log("Tearing down VR camera rig for re-initialization.");
                        _cameraSetup.TeardownCameraRig();
                        _currentlyTrackedOriginalCameraGO = null;
                        CameraFinder.InvalidateCache();
                    }
                }
            }
            else
            {
                _autoSafeModeEndTime = -1f;
                if (!_hasVrBeenAttemptedByUser)
                {
                    VRModCore.LogRuntimeDebug("First time user is disabling Safe Mode. Attempting to initialize VR subsystem...");
                    if (!EnsureAndInitializeVrSubsystem())
                    {
                        VRModCore.LogError("Failed to initialize VR subsystem. Re-enabling Safe Mode.");
                        _isUserSafeModeActive = true;
                        _hasVrBeenAttemptedByUser = false;
                    }
                }
            }
        }

        // 遷移開始: rig-level teardown（eye カメラ GO のみ破棄。OpenXR セッション・RT・compositor は維持）。冪等。
        // デッドロックの根本原因は「UnloadUnusedAssets 中に eye カメラが描画すること」なので、
        // カメラ不在ならセッションごと殺す full teardown は不要（Issue 2 / spec 2026-06-04 §1）。
        // セッション維持により EndTransitionTeardown 後の復帰は SetupCameraRig のみ（セッション再 init 不要）になり、
        // Quest Link へのセッション切断/再接続ストレスも消える。eye RT は TeardownCameraRig では破棄されず
        //（RT はセッション所有 — VrCameraSetup_CoreOpenXR 側参照）、managed 参照保持のため
        // UnloadUnusedAssets の回収対象にもならない。
        public void ForceTransitionTeardown()
        {
            if (_transitionTeardownActive) return;
            _transitionTeardownActive = true;
            _transitionKeepaliveFrames = 0;
            _transitionKeepaliveAttempts = 0;
            VRModCore.LogRuntimeDebug("ForceTransitionTeardown: シーン遷移のため VR rig を teardown する（OpenXR セッションは維持）。");
            if (_cameraSetup != null)
            {
                RestoreAllDesktopRender();              // 抑制中の全カメラ cullingMask を戻す
                _cameraSetup.TeardownCameraRig();    // rig のみ破棄（TeardownVr はセッションごと殺すので使わない）
                _currentlyTrackedOriginalCameraGO = null;
                CameraFinder.InvalidateCache();
            }
        }

        // 遷移完了: フラグ解除のみ。次フレームの Update() が SetupCameraRig で自動再 attach する
        //（セッション生存中はセッション再 init は走らない）。冪等。
        public void EndTransitionTeardown()
        {
            if (!_transitionTeardownActive) return;
            _transitionTeardownActive = false;
            VRModCore.LogRuntimeDebug($"EndTransitionTeardown: 遷移完了。Update() による VR 再 init を許可する。keepalive frames={_transitionKeepaliveFrames}/{_transitionKeepaliveAttempts}");
        }

        public bool IsTransitionTeardownActive => _transitionTeardownActive;

        // standby teardown: instance ごと破棄して park する（cameraSetup==null 経路の probe で再 init される）。
        // Teardown モード突入時と、SoftPark の instance 喪失 escalation 時の両方から呼ぶ。
        private void EnterStandbyTeardown()
        {
            RestoreAllDesktopRender();
            _cameraSetup.TeardownVr();
            _cameraSetup = null;
            _hasVrBeenAttemptedByUser = false;   // FullVrReinit teardown と同じ後始末（不変条件コメント参照）
            _currentlyTrackedOriginalCameraGO = null;
            CameraFinder.InvalidateCache();
            _sessionNotRunningSince = -1f;
            _standbyParked = true;
            _lastStandbyProbeTime = Time.realtimeSinceStartup;
        }

        internal void Update()
        {
            if (_cameraSetup == null)
            {
                // safe mode OFF かつ非遷移なら VR を初期化する。
                // 初回 boot（Safe Mode Starts Active=false）と teardown 後の再 init を同一経路で扱う。
                // _transitionTeardownActive ガードは必須: これが無いと teardown 直後の次フレームで
                // rig が復活し遷移中（UnloadUnusedAssets 中）に eye カメラが描画してデッドロックする。
                if (!_isUserSafeModeActive && !_transitionTeardownActive)
                {
                    // standby park 中: 毎フレーム再 init せず probe 間隔でのみ試す（即再 init は dormant
                    // instance を再生成し時限 fastfail を再武装する）。probe の失敗は Safe Mode 化しない
                    //（HMD 休眠が原因の正常系＝次の probe で再試行する）。
                    if (_standbyParked)
                    {
                        if (UnityVRMod.Core.StandbyPolicy.ShouldProbe(
                                _standbyParked,
                                Time.realtimeSinceStartup - _lastStandbyProbeTime,
                                ConfigManager.OpenXR_StandbyReattachProbeSec?.Value ?? 10f))
                        {
                            _lastStandbyProbeTime = Time.realtimeSinceStartup;
                            VRModCore.Log("OpenXR standby probe: VR 再初期化を試行する（READY 不達なら standby 猶予で自動再 teardown）。");
                            EnsureAndInitializeVrSubsystem();
                        }
                        return;
                    }

                    VRModCore.LogRuntimeDebug("VR 未初期化かつ Safe Mode 非アクティブ。VR サブシステムを初期化します。");
                    if (!EnsureAndInitializeVrSubsystem())
                    {
                        VRModCore.LogError("VR サブシステムの初期化に失敗しました。Safe Mode を有効化します。");
                        _isUserSafeModeActive = true;
                    }
                }
                return;
            }

            // 第1ブロックで _cameraSetup==null は return 済 → ここでは非 null 確定。
            // 不変条件「_cameraSetup!=null ⟹ _hasVrBeenAttemptedByUser==true」が全経路で成立する:
            // _cameraSetup を null にするのは init 失敗・FullVrReinit teardown・standby teardown の 3 箇所で、
            // いずれもラッチも false にする / rig-level teardown・transition は cameraSetup を非 null 維持しラッチも保つ。
            // よって旧ガードの !_hasVrBeenAttemptedByUser と _cameraSetup==null は判定に不要。

            // --- doff/スタンバイ検知（spec §5.5.3）---
            // session 非 running（または IsVrAvailable 喪失）が猶予を超えたら standby へ突入する。挙動は
            // OpenXR_StandbyMode で選ぶ: SoftPark（既定）= teardown せず instance/session/rig 維持・desktop 復帰・
            // event poll で自動再開（D3D12 では dormant fastfail 未観測のため churn 不要）/ Teardown = instance ごと
            // full teardown して dormant 状態を断つ（Meta in-process client の時限 fastfail = c0000409 回避・旧挙動）。
            // 下の IsVrAvailable / transition 早期 return より前に置く（前者は喪失後に毎フレーム return して
            // ここに到達しなくなるため・後者は遷移中 doff にも適用するため）。
            // 注: Task 2 以降、正常装着起動でも READY 到達（実測 <1s）まで非 running＝このタイマーは
            // init 直後から回り始め、READY→begin で -1 にリセットされる。猶予を ~2s 未満に縮めると
            // 正常起動が teardown され得る（config description にも注意書きあり）。
            bool xrSessionRunning = _cameraSetup.IsVrAvailable && _cameraSetup.IsSessionRunning;
            OpenXrStandbyMode standbyMode = ConfigManager.OpenXR_StandbyMode?.Value ?? OpenXrStandbyMode.SoftPark;
            if (xrSessionRunning)
            {
                _sessionNotRunningSince = -1f;
                if (_standbyParked)
                {
                    _standbyParked = false;
                    VRModCore.Log("OpenXR standby: session running を確認。VR 復帰。");
                }
            }
            else if (!_transitionTeardownActive)   // 遷移中は standby 突入させない（遷移は keepalive で submit 継続中）
            {
                // SoftPark で既に park 済みなら、instance を維持したまま下の poll 経路へ流す（再 teardown しない）。
                // Teardown モードは park 済みでも probe が作った instance を猶予超過で再 teardown する
                //（短命 instance を維持＝dormant fastfail 回避。docstring 参照）。
                bool softParkedAlready = _standbyParked && standbyMode == OpenXrStandbyMode.SoftPark;
                if (!softParkedAlready)
                {
                    if (_sessionNotRunningSince < 0f) _sessionNotRunningSince = Time.realtimeSinceStartup;
                    if (UnityVRMod.Core.StandbyPolicy.ShouldEnterStandby(
                            xrSessionRunning,
                            Time.realtimeSinceStartup - _sessionNotRunningSince,
                            ConfigManager.OpenXR_StandbyTeardownSec?.Value ?? 5f))
                    {
                        if (standbyMode == OpenXrStandbyMode.Teardown)
                        {
                            VRModCore.Log("OpenXR standby: session 非 running が猶予を超過 → full teardown（dormant fastfail 回避・probe で自動再試行）。");
                            EnterStandbyTeardown();
                            return;
                        }

                        // SoftPark: teardown せず instance/session/rig を維持し、desktop へ復帰して park。
                        // eye 描画は止まる（下の早期 return で SetDesktopRenderSuppressed/FramePumpPolicy を通らない）が、
                        // event poll だけは回し続け READY（HMD 再装着）で自動再開する。
                        VRModCore.Log("OpenXR standby: session 非 running が猶予を超過 → soft-park（teardown せず desktop 復帰・event poll で自動再開）。");
                        RestoreAllDesktopRender();
                        _sessionNotRunningSince = -1f;
                        _standbyParked = true;
                    }
                }
            }

            // park 中の処理（Teardown の probe 再 init 後 / SoftPark の両方）。rig 構築・desktop 抑制・FramePumpPolicy へ
            // は進めない（進めると Teardown では「rig 構築 →（猶予で）teardown → probe …」の churn・desktop 抑制の
            // ON/OFF ちらつきが出る）。UpdatePoses（=PumpFrame）だけ回す: その event poll が READY を受けて
            // xrBeginSession する唯一の経路（READY 前は PumpFrame 早期 return で frame loop は走らない）。
            // _standbyParked の解除は上のブロックが IsSessionRunning=true を観測した時のみ。
            if (_standbyParked)
            {
                // SoftPark 中に runtime が instance ごと喪失（LOSS_PENDING/EXITING/poll エラーで IsVrAvailable=false）
                // すると PumpFrame が event poll 前に早期 return し自動復帰できなくなる。Teardown へ escalate して
                // cameraSetup==null 経路の probe で再接続させる（Teardown モードはここに来る時 IsVrAvailable=true）。
                if (!_cameraSetup.IsVrAvailable)
                {
                    VRModCore.Log("OpenXR standby: instance 喪失を検知 → full teardown へ escalate（probe で再接続）。");
                    EnterStandbyTeardown();
                    return;
                }
                _cameraSetup.UpdatePoses();
                return;
            }

            if (!_cameraSetup.IsVrAvailable) return;

            // 遷移中は rig 不在を維持する（rig-level teardown 中に下のカメラ管理が rig を自動復活
            // させると UnloadUnusedAssets 中に eye カメラが描画しデッドロックが再発する）。
            // 再 attach は EndTransitionTeardown 後の次フレームにこの先の通常フローで行われる。
            if (_transitionTeardownActive)
            {
                // 遷移中 keepalive: rig 不在のまま compositor へフレーム提出を継続する（Camera.Render なし）。
                // PumpFrame で空/前フレーム hold を submit する。submit が途切れると compositor が
                // timed-out→resume し、共有テクスチャ handoff が壊れてフリーズする
                //（session STOPPING 固着。2026-06-07 検死 freeze #1〜#4・統一仮説 v3）。
                // ここは直前のガード通過後＝_cameraSetup 非 null かつ IsVrAvailable が保証済み。
                _transitionKeepaliveAttempts++;
                if (_cameraSetup.SubmitTransitionKeepalive()) _transitionKeepaliveFrames++;
                return;
            }

            bool autoSafeModeEngaged = Time.time < _autoSafeModeEndTime;
            bool shouldRender = !_isUserSafeModeActive && !autoSafeModeEngaged;

            VrCameraRig vrCameras = _cameraSetup.GetVrCameraGameObjects();
            bool rigIsSetUp = vrCameras.LeftEye != null || vrCameras.RightEye != null;

            Camera mainCam = CameraFinder.FindGameCamera();

            if (mainCam != null)
            {
                if (rigIsSetUp)
                {
                    if (_currentlyTrackedOriginalCameraGO != mainCam.gameObject)
                    {
                        VRModCore.Log($"Game's main camera changed to '{mainCam.name}'. Rebinding VR rig.");
                        CameraFinder.InvalidateCache(); // re-bind するので解決キャッシュを無効化
                        RestoreAllDesktopRender();          // ConfigureVrCamera のコピー元 mask を真値へ戻してから rebind
                        _cameraSetup.RebindCameraRig(mainCam);
                        _currentlyTrackedOriginalCameraGO = mainCam.gameObject;
                        if (ConfigManager.EnableAutomaticSafeMode.Value)
                            ActivateAutomaticSafeMode("Main camera changed");
                    }
                }
                else if (shouldRender)
                {
                    VRModCore.Log($"Found game camera '{mainCam.name}'. Setting up VR rig.");
                    RestoreAllDesktopRender();              // 同上: rig setup 前に必ず真値 mask へ
                    _cameraSetup.SetupCameraRig(mainCam);
                    _currentlyTrackedOriginalCameraGO = mainCam.gameObject;
                    if (ConfigManager.EnableAutomaticSafeMode.Value)
                        ActivateAutomaticSafeMode("Initial rig setup");
                }
            }
            else if (rigIsSetUp)
            {
                VRModCore.LogWarning("Game's main camera has become null. Tearing down VR rig to prevent conflicts.");
                _cameraSetup.TeardownCameraRig();
                _currentlyTrackedOriginalCameraGO = null;
                CameraFinder.InvalidateCache();
            }

            // デスクトップ側フラット描画の抑制（config ON かつ VR 描画中のみ）。
            SetDesktopRenderSuppressed(ConfigManager.DisableDesktopView.Value && shouldRender && rigIsSetUp);

            // rig 不在（カメラ未解決 / teardown 直後）でも session 生存中は compositor へフレーム提出を
            // 続ける。提出が途切れると OpenXR/Oculus がアプリを timed-out 扱いにし HMD がフリーズする
            //（2026-06-09 検死）。PumpFrame が空フレームで loop を維持する。
            // 注: 経路 (A) _transitionTeardownActive は上で早期 return 済み＝ここには来ない（二重 keepalive なし）。
            switch (UnityVRMod.Core.FramePumpPolicy.Decide(shouldRender, rigIsSetUp))
            {
                case UnityVRMod.Core.VrFramePump.Render:
                    _cameraSetup.UpdatePoses();
                    break;
                case UnityVRMod.Core.VrFramePump.Keepalive:
                    _cameraSetup.SubmitTransitionKeepalive();
                    break;
                case UnityVRMod.Core.VrFramePump.None:
                    break;
            }
        }

        internal void Shutdown()
        {
            VRModCore.LogRuntimeDebug("Shutdown called.");
            RestoreAllDesktopRender();
            if (ConfigManager.EnableAutomaticSafeMode.Value && _sceneChangedActionDelegate != null)
            {
                SceneManager.activeSceneChanged -= _sceneChangedActionDelegate;
            }

            if (_cameraSetup != null)
            {
                VRModCore.LogRuntimeDebug($"Shutting down camera setup: {_cameraSetup.GetType().Name}.");
                _cameraSetup.TeardownVr();
            }
            _managerInitialized = false;
        }

        public void LiveUpdateWorldScale(float newScale)
        {
            if (IsVrReady)
            {
                Camera cameraComponent = _currentlyTrackedOriginalCameraGO != null
                    ? _currentlyTrackedOriginalCameraGO.GetComponent<Camera>()
                    : null;
                _cameraSetup.SetWorldScale(newScale, cameraComponent);
            }
        }

        public void LiveUpdateCameraNearClip(float newNearClip)
        {
            if (IsVrReady) _cameraSetup.SetCameraNearClip(newNearClip);
        }

        public void LiveUpdateUserEyeHeightOffset(float newOffset)
        {
            if (IsVrReady) _cameraSetup.SetUserEyeHeightOffset(newOffset);
        }

        // config を OFF にしたら即復元する。ON は次の Update() が抑制を適用する。
        public void LiveUpdateDesktopView(bool disableDesktop)
        {
            if (!disableDesktop) RestoreAllDesktopRender();
        }

        // VR 描画中のみ、元カメラの cullingMask=0 + SolidColor 黒 clear + post-processing OFF で
        // デスクトップのフラット描画を止める。suppress=false なら復元する。毎フレーム冪等に呼べる
        // （ゲームがシーン遷移等で clearFlags 等を書き戻しても次フレームで再抑制される）。
        private void SetDesktopRenderSuppressed(bool suppress)
        {
            if (!suppress) { RestoreAllDesktopRender(); return; }

            // 抑制は呼出条件（shouldRender && rigIsSetUp）で IsVrReady=true が保証される＝guarded で可。
            // 仮に rig==null でも IsUnderRig は false を返すだけ＝rig 配下カメラは eye のみで
            // enabled=false＝GetAllCameras に出ないため実害なし（rig フィルタは二重防御の片方）。
            Transform rig = GetVrRigTransform();

            // 1) 現フレの抑制対象を非 alloc で列挙。GetAllCameras は enabled カメラのみ返す
            //    （eye は enabled=false ＝ RenderEye で手動描画のため自動的に除外される）。
            int count = Camera.allCamerasCount;
            if (_cameraEnumBuffer.Length < count) _cameraEnumBuffer = new Camera[count];
            Camera.GetAllCameras(_cameraEnumBuffer);

            // 2) Sweep: 抑制中だが対象外（env unload / disable / RT 化 / 破棄）になったカメラを
            //    真値復元して map から外す。
            _suppressionSweepScratch.Clear();
            foreach (KeyValuePair<Camera, DesktopSuppressionState> kv in _suppressedCameras)
            {
                Camera cam = kv.Key;
                bool stillTarget = cam != null && IsDesktopRenderTarget(cam, rig);  // cam != null は Unity の fake-null 判定
                if (!stillTarget) _suppressionSweepScratch.Add(cam);
            }
            for (int i = 0; i < _suppressionSweepScratch.Count; i++)
            {
                Camera cam = _suppressionSweepScratch[i];
                RestoreOne(cam, _suppressedCameras[cam]);
                _suppressedCameras.Remove(cam);
            }

            // 3) Apply: 対象カメラを冪等に抑制（未捕捉なら抑制適用前の真値を capture）。
            for (int i = 0; i < count; i++)
            {
                Camera cam = _cameraEnumBuffer[i];
                if (cam == null) continue;
                if (!IsDesktopRenderTarget(cam, rig)) continue;

                if (!_suppressedCameras.TryGetValue(cam, out DesktopSuppressionState st))
                {
                    st = new DesktopSuppressionState
                    {
                        OriginalCullingMask = cam.cullingMask,
                        OriginalClearFlags = cam.clearFlags,
                        OriginalBackgroundColor = cam.backgroundColor,
                    };
                    CaptureDesktopRenderPostProcessing(cam, ref st);   // 真値は抑制適用前にこの分岐内で焼く
                    _suppressedCameras[cam] = st;
                }

                if (cam.cullingMask != 0) cam.cullingMask = 0;
                if (cam.clearFlags != CameraClearFlags.SolidColor) cam.clearFlags = CameraClearFlags.SolidColor;
                if (cam.backgroundColor != Color.black) cam.backgroundColor = Color.black;
                ApplyDesktopRenderPostProcessing(st.Acd, false);
            }
        }

        // 抑制対象判定: GetAllCameras が enabled を保証。RT 描画（targetTexture 有り）= BG2VR
        // compositor 等はモニタへ出ないので除外。rig 配下 = VR 所有カメラ（eye 等）も除外。
        private bool IsDesktopRenderTarget(Camera cam, Transform rig)
            => cam.targetTexture == null && !IsUnderRig(cam.transform, rig);

        private static bool IsUnderRig(Transform t, Transform rig)
        {
            if (rig == null) return false;
            for (Transform p = t; p != null; p = p.parent)
                if (p == rig) return true;
            return false;
        }

        // 抑制中の全カメラの真値（cullingMask / clearFlags / backgroundColor / post-processing）を
        // 復元して map を空にする。破棄済（Unity fake-null）カメラは個別に skip する。
        private void RestoreAllDesktopRender()
        {
            if (_suppressedCameras.Count == 0) return;
            foreach (KeyValuePair<Camera, DesktopSuppressionState> kv in _suppressedCameras)
                RestoreOne(kv.Key, kv.Value);
            _suppressedCameras.Clear();
        }

        // 1 カメラ分の真値を復元する。破棄済（Unity fake-null）なら何もしない。
        private void RestoreOne(Camera cam, in DesktopSuppressionState st)
        {
            if (cam == null) return;                              // Unity fake-null チェック
            cam.cullingMask = st.OriginalCullingMask;
            cam.clearFlags = st.OriginalClearFlags;
            cam.backgroundColor = st.OriginalBackgroundColor;
            if (st.OriginalRenderPostProcessing.HasValue)
                ApplyDesktopRenderPostProcessing(st.Acd, st.OriginalRenderPostProcessing.Value);
        }

        // URP の UniversalAdditionalCameraData.renderPostProcessing を reflection で解決する。
        // fork は URP 非参照のため直接型参照できない。URP 不在ゲーム / IL2CPP interop で
        // 解決できない環境では null のまま＝呼び出し側は全て no-op に落ちる。
        private static PropertyInfo _urpRenderPostProcessingProp = null;
        private static bool _urpReflectionResolved = false;

        private static PropertyInfo ResolveUrpRenderPostProcessingProp()
        {
            if (_urpReflectionResolved) return _urpRenderPostProcessingProp;
            _urpReflectionResolved = true;
            try
            {
                foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type t = asm.GetType("UnityEngine.Rendering.Universal.UniversalAdditionalCameraData");
                    if (t == null) continue;
                    _urpRenderPostProcessingProp = t.GetProperty("renderPostProcessing",
                        BindingFlags.Public | BindingFlags.Instance);
                    break;
                }
            }
            catch { _urpRenderPostProcessingProp = null; }
            return _urpRenderPostProcessingProp;
        }

        // 抑制対象カメラの ACD と renderPostProcessing 真値を state へ capture する。
        // 失敗時は Acd=null（= postFX 抑制 no-op。mask / clear 抑制は生きる）。
        private void CaptureDesktopRenderPostProcessing(Camera cam, ref DesktopSuppressionState st)
        {
            st.Acd = null;
            st.OriginalRenderPostProcessing = null;
            PropertyInfo prop = ResolveUrpRenderPostProcessingProp();
            if (prop == null) return;
            try
            {
                Component acd = cam.GetComponent(prop.DeclaringType);
                if (acd == null) return;
                st.Acd = acd;
                st.OriginalRenderPostProcessing = (bool)prop.GetValue(acd);
            }
            catch
            {
                st.Acd = null;
                st.OriginalRenderPostProcessing = null;
            }
        }

        // 指定 ACD に renderPostProcessing を冪等適用する（差分があるときだけ書く）。acd==null は no-op。
        private static void ApplyDesktopRenderPostProcessing(Component acd, bool value)
        {
            if (acd == null) return;
            PropertyInfo prop = ResolveUrpRenderPostProcessingProp();
            if (prop == null) return;
            try
            {
                if ((bool)prop.GetValue(acd) != value) prop.SetValue(acd, value);
            }
            catch { /* 破棄済等は黙って無視（次の capture で立て直す） */ }
        }
    }
}