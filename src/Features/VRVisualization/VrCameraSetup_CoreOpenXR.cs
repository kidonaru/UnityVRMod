using System.Runtime.InteropServices;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityVRMod.Config;
using UnityVRMod.Core;
using UnityVRMod.Features.Util;
using UnityVRMod.Features.VRVisualization.OpenXR;
namespace UnityVRMod.Features.VrVisualization
{
    internal class VrCameraSetup_CoreOpenXR : IVrCameraSetup
    {
        private ulong _xrInstance = OpenXRConstants.XR_NULL_HANDLE;
        private ulong _xrSystemId = OpenXRConstants.XR_NULL_SYSTEM_ID;
        private ulong _xrSession = OpenXRConstants.XR_NULL_HANDLE;

        public bool IsVrAvailable { get; private set; } = false;

        public bool IsSessionRunning => _isSessionRunning;

        private GameObject _vrRig = null;
        private float _currentAppliedRigScale = 1.0f;

        private XrFrameState _xrFrameState;
        private XrSessionState _currentSessionState = XrSessionState.XR_SESSION_STATE_UNKNOWN;
        private readonly XrViewConfigurationType _viewConfigType = XrViewConfigurationType.XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO;
        private readonly List<XrViewConfigurationView> _viewConfigViews = [];

        private readonly List<long> _supportedSwapchainFormats = [];
        private long _selectedSwapchainFormat = 0;
        private readonly List<ulong> _eyeSwapchains = [];
        private readonly List<List<IntPtr>> _eyeSwapchainImages = [];
        private ulong _appSpace = OpenXRConstants.XR_NULL_HANDLE;
        private bool _isSessionRunning = false;
        private XrReferenceSpaceType _appSpaceType = XrReferenceSpaceType.XR_REFERENCE_SPACE_TYPE_LOCAL;
        // 正面リセット: 現在の _appSpace を作った poseInReferenceSpace（natural STAGE/LOCAL 原点基準）。
        // recenter のたびに delta を合成して累積し、_appSpace を作り直す。初期 = identity。
        private XrPosef _appSpacePoseOffset = new XrPosef { orientation = new XrQuaternionf { w = 1f } };
        private bool _recenterPending;
        private XrView[] _locatedViews;
        private XrViewState _locatedViewState;

        private Camera _leftVrCamera = null;
        private GameObject _leftVrCameraGO = null;
        private Camera _rightVrCamera = null;
        private GameObject _rightVrCameraGO = null;

        private RenderTexture _leftEyeIntermediateRT = null;
        private RenderTexture _rightEyeIntermediateRT = null;

        private IntPtr _d3d11Device = IntPtr.Zero;

        // D3D12 backend（true = D3D12 経路。SystemInfo と native plugin 観測の AND で確定）。
        // backend 判定・event ptr・active インスタンスを static 共有するのは「VR セッションは同時に 1 つのみ」
        // という前提による（QuadLayerSwapchain 等の別クラスからインスタンス参照なしで読む）。
        private static bool _useD3D12;
        private IntPtr _d3d12Device = IntPtr.Zero;
        private IntPtr _d3d12Queue = IntPtr.Zero;
        private static IntPtr _renderEventFunc = IntPtr.Zero;
        private int _lastCopyTicketIssued;   // drain 用: 最後に Enqueue したチケット

        // QuadLayerSwapchain（別クラス）から backend 判定と event ptr を参照するための共有アクセサ。
        internal static bool UseD3D12 => _useD3D12;
        internal static IntPtr RenderEventFunc => _renderEventFunc;

        // InitializeVr 成功時に this を代入・TeardownVr で null。quad copy のチケットを集約するため。
        private static VrCameraSetup_CoreOpenXR _activeInstance;
        internal static void NoteCopyTicket(int ticket)
        {
            var inst = _activeInstance;
            if (inst != null && ticket > inst._lastCopyTicketIssued) inst._lastCopyTicketIssued = ticket;
        }

        private readonly OpenXRInput _input = new OpenXRInput(); // コントローラ入力（action system）

        private XrCompositionLayerProjectionView[] _projectionLayerViews;
        private IntPtr _pProjectionLayerViews = IntPtr.Zero;
        private XrCompositionLayerProjection _projectionLayer;
        private IntPtr _pProjectionLayer = IntPtr.Zero;
        private IntPtr _pLayersForSubmit = IntPtr.Zero;

        // --- fade / overlay quad composition layer（session 寿命・rig teardown 中も維持）---
        private const int FADE_TEX_SIZE = 4;        // 単色 fade quad の swapchain 解像度（小で十分）
        private const int OVERLAY_TEX_SIZE = 1024;  // overlay quad の swapchain 解像度（正方・aspect は quad size で復元）
        private const float FADE_QUAD_DISTANCE = 0.5f; // fade quad の頭からの距離(m)
        private const float FADE_QUAD_SIZE = 12f;      // fade quad の一辺(m)。FOV 全面を確実に覆う大きさ

        private ulong _viewSpace = OpenXRConstants.XR_NULL_HANDLE; // head-lock 用 VIEW reference space

        // fade 状態（SetCompositorFade で更新・PumpFrame で消費）
        private UnityEngine.Color _fadeColor;
        private bool _fadeActive;
        private RenderTexture _fadeRT;
        private QuadLayerSwapchain _fadeSwapchain;
        private IntPtr _pFadeLayer = IntPtr.Zero;

        // overlay 状態（SetTransitionOverlayTexture/State で更新・PumpFrame で消費）
        private bool _overlayVisible;
        private float _overlayWidthM;
        private float _overlayDistanceM;
        private float _overlayAlpha;            // v1 では未使用（show/hide のみ）。follow-up の alpha 焼き込み用に保持
        private IntPtr _overlaySrcPtr = IntPtr.Zero;
        private int _overlaySrcW, _overlaySrcH;
        private float _overlayUMin, _overlayVMin, _overlayUMax, _overlayVMax;
        private Texture2D _overlayExternalTex; // source native ptr を wrap した外部テクスチャ（ptr/寸法変化で再生成）
        private RenderTexture _overlayRT;       // crop+flip+format を焼き込む intermediate（OVERLAY_TEX_SIZE 正方）
        private QuadLayerSwapchain _overlaySwapchain;
        private IntPtr _pOverlayLayer = IntPtr.Zero;
        // world 固定モード（companion VrTransitionOverlayWorldLock）。ON のとき overlay quad を
        // 頭ロック VIEW space でなく app space に固定する。anchor は可視 rising edge で 1 回だけ凍結する。
        private bool _overlayWorldLock;
        private bool _prevOverlayVisible;       // 可視 rising edge 検出用（PumpFrame の単一地点で更新）
        private bool _overlayAnchorValid;       // false = locate 失敗/水平退化 → 当セッションは頭ロック fallback
        private XrVector3f _overlayAnchorPos;   // app space に凍結した quad 中心（RH）
        private XrQuaternionf _overlayAnchorOri;// app space に凍結した quad 姿勢（yaw のみ・RH）

        private CameraClearFlags _mainCameraClearFlags;
        private Color _mainCameraBackgroundColor;
        private int _mainCameraCullingMask;

        // eye cullingMask/clearFlags の companion override（EyeCullingCoordinator が毎フレ push）。
        // active=false で _mainCamera*（game-copy）へ戻る。RenderEye が描画直前に適用する単一所有点。
        private bool _eyeCullOverrideActive;
        private int _eyeCullOverrideMask;
        private CameraClearFlags _eyeCullOverrideClear;
        private Color _eyeCullOverrideBg;

        // eye の URP post-process の companion override（PostProcessCoordinator が毎フレ push）。
        // active=true で eye の renderPostProcessing を有効化し volumeLayerMask を指定値へ（= ゲームの
        // グレーディング+Bloom を反映）。active=false で renderPostProcessing=false。RenderEye が描画直前に
        // リフレクションで適用する（fork は URP を直参照しないため）。
        private bool _eyePpOverrideActive;
        private int _eyePpVolumeMask;

        // post 後に post 無しで描く overlay layer 集合（VR ビジュアル＝layer 30 等）。0 で除外なし。
        // 本描画から除外し、RenderEye で CommandBuffer によって post 済み RT へ重ね描く。
        private int _eyeOverlayLayerMask;
        private CommandBuffer _overlayCmd;
        private readonly System.Collections.Generic.List<Renderer> _overlayRenderers = new System.Collections.Generic.List<Renderer>();

        // 選択的深度（コントローラだけが UI を遮る）の遮蔽源 layer 集合（= layer 29）と深度専用 material。
        // 0 / null で無効。DrawEyeOverlay が UI 描画の直前に RT 深度を far へ消し、この layer だけを
        // depthMat で描き直す＝遮蔽源をコントローラのみに限定する（companion の PostProcessCoordinator が push）。
        private int _eyeOverlayOccluderMask;
        private Material _eyeOverlayOccluderMat;

        // VR モデル（手モデル等）専用 overlay layer 集合（companion の HandLightingRunner が毎フレ push）。
        // PostProcess の有無と独立に常時 overlay 描画される（PostProcess 由来の overlayMask とは別 channel）。
        // effective overlayMask = _eyeOverlayLayerMask | _vrModelOverlayMask＝main pass から除外（二重描画防止）
        // + post 後の overlay pass で crisp 重ね描き＝UI(layer 30) と同じく最前面化。
        private int _vrModelOverlayMask;

        private System.Action<Camera, RenderTexture> _sceneTransparentRedraw;

        private GameObject _currentlyTrackedOriginalCameraGO = null;
        private float _lastCalculatedVerticalOffset;
        private static CommandBuffer _flushCommandBuffer;

        private readonly List<List<IntPtr>> _eyeSwapchainSRVs = [];

        // eye swapchain image の acquire/wait 状態（index 0=左, 1=右）。有限タイムアウト wait を跨フレームで
        // 持ち越すために保持する。timeout した eye は image を解放せず次フレームで再 wait する（OpenXR spec:
        // release は wait 成功後のみ・acquire 済み image は再 wait 可）。両 eye が ready になって初めて render→release。
        // wait が負(エラー)を返したら状態不定として acquire を捨て次フレ再取得する（image リーク=片眼恒久停止を防ぐ・#2）。
        // _eyeImgIdx は acquire 時に out で必ず上書きされ参照は _eyeReady ガード下でのみ＝reset 不要（リセットは _eyeAcquired/_eyeReady のみ）。
        private readonly bool[] _eyeAcquired = new bool[2];
        private readonly bool[] _eyeReady = new bool[2];
        private readonly uint[] _eyeImgIdx = new uint[2];

        public bool InitializeVr(string applicationKey)
        {
            VRModCore.Log("Attempting to initialize OpenXR via P/Invoke...");

            try
            {
                if (!OpenXRNativeLoader.LoadOpenXRLibrary())
                    throw new Exception("Failed to load OpenXR native library (openxr_loader.dll).");

                if (!OpenXRAPI.InitializeCoreFunctions(OpenXRNativeLoader.xrGetInstanceProcAddr_ptr_delegate))
                    throw new Exception("Failed to initialize core OpenXR functions.");

                _useD3D12 = SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Direct3D12
                    && NativeBridge.IsD3D12Active_Internal() == 1;
                string[] requestedExtensions = [_useD3D12
                    ? OpenXRConstants.XR_KHR_D3D12_ENABLE_EXTENSION_NAME
                    : OpenXRConstants.XR_KHR_D3D11_ENABLE_EXTENSION_NAME];
                VRModCore.Log($"OpenXR graphics backend: {(_useD3D12 ? "D3D12" : "D3D11")}");
                IntPtr pRequestedExtensions = MarshallStringUtils.MarshalStringArrayToAnsi(requestedExtensions);

                try
                {
                    // 1.1 で試行 → 1.0 専用ランタイム（VDXR 等）が API_VERSION_UNSUPPORTED を返したら 1.0 で再試行。
                    // 本実装は 1.1 専用機能を使っていない（reference space は STAGE/LOCAL/VIEW・拡張は KHR_D3D11 のみ）ため 1.0 で完全動作する
                    ulong[] apiVersionCandidates = [OpenXRConstants.XR_API_VERSION_1_1, OpenXRConstants.XR_API_VERSION_1_0];
                    XrResult createResult = XrResult.XR_ERROR_API_VERSION_UNSUPPORTED;
                    foreach (ulong apiVersion in apiVersionCandidates)
                    {
                        var instanceCreateInfo = new XrInstanceCreateInfo
                        {
                            type = XrStructureType.XR_TYPE_INSTANCE_CREATE_INFO,
                            applicationInfo = new XrApplicationInfo
                            {
                                applicationName = "UnityVRMod",
                                applicationVersion = 1,
                                engineName = "Unity",
                                engineVersion = 1,
                                apiVersion = apiVersion
                            },
                            enabledExtensionCount = (uint)requestedExtensions.Length,
                            enabledExtensionNames = pRequestedExtensions
                        };

                        createResult = OpenXRAPI.xrCreateInstance(in instanceCreateInfo, out _xrInstance);
                        if (createResult != XrResult.XR_ERROR_API_VERSION_UNSUPPORTED)
                        {
                            if (createResult == XrResult.XR_SUCCESS)
                                VRModCore.Log($"OpenXR API version {apiVersion >> 48}.{(apiVersion >> 32) & 0xFFFF} で instance を作成");
                            break;
                        }
                        VRModCore.Log($"OpenXR API version {apiVersion >> 48}.{(apiVersion >> 32) & 0xFFFF} はランタイム非対応。フォールバックを試行");
                    }
                    OpenXRHelper.CheckResult(createResult, "xrCreateInstance");
                }
                finally
                {
                    MarshallStringUtils.FreeMarshalledStringArray(pRequestedExtensions, requestedExtensions.Length);
                }
                if (_xrInstance == OpenXRConstants.XR_NULL_HANDLE) throw new Exception("xrCreateInstance returned a null handle.");

                VRModCore.Log($"OpenXR Instance created. Handle: {_xrInstance}");

                if (!OpenXRAPI.InitializeInstanceFunctions(_xrInstance))
                    throw new Exception("Failed to initialize instance-specific OpenXR functions.");

                if (_useD3D12)
                {
                    // device/queue は一発 plugin event（render thread）で cache → 取れなければ main thread 直読み fallback
                    _renderEventFunc = NativeBridge.GetRenderEventFunc_Internal();
                    GL.IssuePluginEvent(_renderEventFunc, NativeBridge.kEventCacheDeviceObjects);
                    var swInit = System.Diagnostics.Stopwatch.StartNew();
                    while (NativeBridge.GetD3D12Device_Internal() == IntPtr.Zero && swInit.ElapsedMilliseconds < 1000)
                        System.Threading.Thread.Sleep(1);
                    _d3d12Device = NativeBridge.GetD3D12Device_Internal();   // 未 cache でも export 側が直読み fallback する
                    _d3d12Queue = NativeBridge.GetD3D12CommandQueue_Internal();
                    if (_d3d12Device == IntPtr.Zero || _d3d12Queue == IntPtr.Zero)
                        throw new Exception("D3D12 device/queue の取得に失敗。");
                }
                else
                {
                    _d3d11Device = NativeBridge.GetD3D11DevicePointer(Texture2D.whiteTexture);
                    if (_d3d11Device == IntPtr.Zero)
                        throw new Exception("Failed to get D3D11 device pointer.");
                    NativeBridge.SetDevicePointerFromCSharp(_d3d11Device);
                }

                var systemGetInfo = new XrSystemGetInfo { type = XrStructureType.XR_TYPE_SYSTEM_GET_INFO, formFactor = XrFormFactor.XR_FORM_FACTOR_HEAD_MOUNTED_DISPLAY };
                OpenXRHelper.CheckResult(OpenXRAPI.xrGetSystem(_xrInstance, in systemGetInfo, out _xrSystemId), "xrGetSystem");
                if (_xrSystemId == OpenXRConstants.XR_NULL_SYSTEM_ID) throw new Exception("xrGetSystem failed for HMD.");
                VRModCore.Log($"OpenXR SystemId obtained: {_xrSystemId}");

                IntPtr pGraphicsBinding;
                if (_useD3D12)
                {
                    if (OpenXRAPI.xrGetD3D12GraphicsRequirementsKHR == null)
                        throw new Exception("xrGetD3D12GraphicsRequirementsKHR がランタイムから取得できない（XR_KHR_D3D12_enable 非対応）。");
                    var d3d12Req = new XrGraphicsRequirementsD3D11KHR { type = XrStructureType.XR_TYPE_GRAPHICS_REQUIREMENTS_D3D12_KHR }; // レイアウト同一の流用
                    OpenXRHelper.CheckResult(OpenXRAPI.xrGetD3D12GraphicsRequirementsKHR(_xrInstance, _xrSystemId, out d3d12Req), "xrGetD3D12GraphicsRequirementsKHR");
                    var binding12 = new XrGraphicsBindingD3D12KHR { type = XrStructureType.XR_TYPE_GRAPHICS_BINDING_D3D12_KHR, device = _d3d12Device, queue = _d3d12Queue };
                    pGraphicsBinding = Marshal.AllocHGlobal(Marshal.SizeOf(binding12));
                    Marshal.StructureToPtr(binding12, pGraphicsBinding, false);
                }
                else
                {
                    if (OpenXRAPI.xrGetD3D11GraphicsRequirementsKHR == null)
                        throw new Exception("xrGetD3D11GraphicsRequirementsKHR がランタイムから取得できない（XR_KHR_D3D11_enable 非対応）。");
                    var d3d11GraphicsRequirements = new XrGraphicsRequirementsD3D11KHR { type = XrStructureType.XR_TYPE_GRAPHICS_REQUIREMENTS_D3D11_KHR };
                    OpenXRHelper.CheckResult(OpenXRAPI.xrGetD3D11GraphicsRequirementsKHR(_xrInstance, _xrSystemId, out d3d11GraphicsRequirements), "xrGetD3D11GraphicsRequirementsKHR");
                    var graphicsBinding = new XrGraphicsBindingD3D11KHR { type = XrStructureType.XR_TYPE_GRAPHICS_BINDING_D3D11_KHR, device = _d3d11Device };
                    pGraphicsBinding = Marshal.AllocHGlobal(Marshal.SizeOf(graphicsBinding));
                    Marshal.StructureToPtr(graphicsBinding, pGraphicsBinding, false);
                }
                var sessionCreateInfo = new XrSessionCreateInfo { type = XrStructureType.XR_TYPE_SESSION_CREATE_INFO, next = pGraphicsBinding, systemId = _xrSystemId };
                OpenXRHelper.CheckResult(OpenXRAPI.xrCreateSession(_xrInstance, in sessionCreateInfo, out _xrSession), "xrCreateSession");
                Marshal.FreeHGlobal(pGraphicsBinding);
                if (_xrSession == OpenXRConstants.XR_NULL_HANDLE) throw new Exception("xrCreateSession returned a null handle.");
                VRModCore.Log($"OpenXR Session created. Handle: {_xrSession}");

                // begin は PumpFrame の event poll が READY を受けてから（既存 resume 経路と同一）。
                // ここで即 begin すると HMD 休眠（doff）起動時に非 READY session で frame loop が走り、
                // compositor 非消費の xrEndFrame で起動 100% フリーズする（実機 2026-06-11・spec §5.5.3）。
                // READY が来るまで PumpFrame は !_isSessionRunning 早期 return（event poll のみ継続）。
                _currentSessionState = XrSessionState.XR_SESSION_STATE_UNKNOWN;
                _isSessionRunning = false;
                VRModCore.Log("OpenXR Session created。begin は READY イベント受信後（HMD 休眠中は frame loop 停止のまま待機）。");

                var refSpaceCreateInfo = new XrReferenceSpaceCreateInfo { type = XrStructureType.XR_TYPE_REFERENCE_SPACE_CREATE_INFO, referenceSpaceType = XrReferenceSpaceType.XR_REFERENCE_SPACE_TYPE_STAGE, poseInReferenceSpace = new XrPosef { orientation = new XrQuaternionf { w = 1f } } };
                if (OpenXRAPI.xrCreateReferenceSpace(_xrSession, in refSpaceCreateInfo, out _appSpace) < 0)
                {
                    VRModCore.LogWarning("Failed to create STAGE space. Trying LOCAL.");
                    _appSpaceType = XrReferenceSpaceType.XR_REFERENCE_SPACE_TYPE_LOCAL;
                    refSpaceCreateInfo.referenceSpaceType = _appSpaceType;
                    OpenXRHelper.CheckResult(OpenXRAPI.xrCreateReferenceSpace(_xrSession, in refSpaceCreateInfo, out _appSpace), "xrCreateReferenceSpace_Fallback");
                }
                else
                {
                    _appSpaceType = XrReferenceSpaceType.XR_REFERENCE_SPACE_TYPE_STAGE;
                }
                if (_appSpace == OpenXRConstants.XR_NULL_HANDLE) throw new Exception("Failed to create any reference space.");
                VRModCore.Log($"Created {_appSpaceType} space.");

                // 新規 _appSpace は identity offset で生成されている。recenter 累積と pending をリセットして整合させる。
                _appSpacePoseOffset = new XrPosef { orientation = new XrQuaternionf { w = 1f } };
                _recenterPending = false;

                // head-lock 用 VIEW space（fade/overlay quad 用）。失敗は致命でない（fade/overlay のみ無効）。
                var viewSpaceInfo = new XrReferenceSpaceCreateInfo { type = XrStructureType.XR_TYPE_REFERENCE_SPACE_CREATE_INFO, referenceSpaceType = XrReferenceSpaceType.XR_REFERENCE_SPACE_TYPE_VIEW, poseInReferenceSpace = new XrPosef { orientation = new XrQuaternionf { w = 1f } } };
                if (OpenXRAPI.xrCreateReferenceSpace(_xrSession, in viewSpaceInfo, out _viewSpace) < 0)
                {
                    _viewSpace = OpenXRConstants.XR_NULL_HANDLE;
                    VRModCore.LogWarning("OpenXR: VIEW space 生成失敗（fade/overlay は無効・描画は継続）。");
                }

                InitializeViews();
                InitializeSwapchains();

                // コントローラ入力（action system）。失敗しても描画は継続（入力のみ無効＝ベストエフォート）。
                if (!_input.Setup(_xrInstance, _xrSession, _appSpace))
                    VRModCore.LogWarning("OpenXR: コントローラ入力の初期化に失敗（描画は継続・入力のみ無効）。");

                if (_viewConfigViews.Count > 0)
                {
                    _pProjectionLayerViews = Marshal.AllocHGlobal(Marshal.SizeOf<XrCompositionLayerProjectionView>() * _viewConfigViews.Count);
                    _projectionLayer = new XrCompositionLayerProjection { type = XrStructureType.XR_TYPE_COMPOSITION_LAYER_PROJECTION };
                    _pProjectionLayer = Marshal.AllocHGlobal(Marshal.SizeOf<XrCompositionLayerProjection>());
                    // projection + fade + overlay の最大 3 layer ポインタ枠。中身は PumpFrame が毎フレ active 分だけ書く。
                    _pLayersForSubmit = Marshal.AllocHGlobal(Marshal.SizeOf<IntPtr>() * 3);
                }

                // fade/overlay quad の swapchain・layer struct buffer（VIEW space がある時のみ）。
                // _selectedSwapchainFormat は InitializeSwapchains で確定済み。intermediate RT は遅延生成（Prepare*）。
                if (_viewSpace != OpenXRConstants.XR_NULL_HANDLE)
                {
                    _pFadeLayer = Marshal.AllocHGlobal(Marshal.SizeOf<XrCompositionLayerQuad>());
                    _pOverlayLayer = Marshal.AllocHGlobal(Marshal.SizeOf<XrCompositionLayerQuad>());

                    _fadeSwapchain = new QuadLayerSwapchain();
                    if (!_fadeSwapchain.Create(_xrSession, _selectedSwapchainFormat, FADE_TEX_SIZE, FADE_TEX_SIZE))
                        _fadeSwapchain = null;

                    _overlaySwapchain = new QuadLayerSwapchain();
                    if (!_overlaySwapchain.Create(_xrSession, _selectedSwapchainFormat, OVERLAY_TEX_SIZE, OVERLAY_TEX_SIZE))
                        _overlaySwapchain = null;
                }

                _flushCommandBuffer ??= new CommandBuffer
                    {
                        name = "VRModFlush"
                    };

                VRModCore.Log("OpenXR fully initialized.");
                _activeInstance = this;   // quad copy チケット集約（NoteCopyTicket）用
                IsVrAvailable = true;
                return true;
            }
            catch (Exception ex)
            {
                VRModCore.LogError("Exception during OpenXR initialization:", ex);
                TeardownVr();
                return false;
            }
        }

        private void InitializeViews()
        {
            OpenXRAPI.xrEnumerateViewConfigurations(_xrInstance, _xrSystemId, 0, out uint viewConfigCountOutput, IntPtr.Zero);
            if (viewConfigCountOutput == 0) throw new Exception("No view configurations available.");

            OpenXRAPI.xrEnumerateViewConfigurationViews(_xrInstance, _xrSystemId, _viewConfigType, 0, out uint viewCountOutput, IntPtr.Zero);
            if (viewCountOutput == 0) throw new Exception("No views for primary stereo config.");

            _viewConfigViews.Clear();
            IntPtr pViewStructs = Marshal.AllocHGlobal((int)(viewCountOutput * Marshal.SizeOf<XrViewConfigurationView>()));
            try
            {
                for (int i = 0; i < viewCountOutput; ++i) Marshal.StructureToPtr(new XrViewConfigurationView { type = XrStructureType.XR_TYPE_VIEW_CONFIGURATION_VIEW }, pViewStructs + (i * Marshal.SizeOf<XrViewConfigurationView>()), false);
                OpenXRAPI.xrEnumerateViewConfigurationViews(_xrInstance, _xrSystemId, _viewConfigType, viewCountOutput, out viewCountOutput, pViewStructs);
                for (int i = 0; i < viewCountOutput; ++i) _viewConfigViews.Add(Marshal.PtrToStructure<XrViewConfigurationView>(pViewStructs + (i * Marshal.SizeOf<XrViewConfigurationView>())));
            }
            finally { Marshal.FreeHGlobal(pViewStructs); }

            if (_viewConfigViews.Count > 0)
            {
                _locatedViews = new XrView[_viewConfigViews.Count];
                for (int i = 0; i < _locatedViews.Length; i++) _locatedViews[i].type = XrStructureType.XR_TYPE_VIEW;

                _projectionLayerViews = new XrCompositionLayerProjectionView[_viewConfigViews.Count];
                for (int i = 0; i < _projectionLayerViews.Length; i++) _projectionLayerViews[i].type = XrStructureType.XR_TYPE_COMPOSITION_LAYER_PROJECTION_VIEW;
            }
            VRModCore.LogRuntimeDebug("View configurations enumerated.");
        }

        private void InitializeSwapchains()
        {
            OpenXRAPI.xrEnumerateSwapchainFormats(_xrSession, 0, out uint formatCount, null);
            if (formatCount == 0) throw new Exception("No swapchain formats.");
            long[] formatsArray = new long[formatCount];
            OpenXRAPI.xrEnumerateSwapchainFormats(_xrSession, formatCount, out formatCount, formatsArray);
            _supportedSwapchainFormats.Clear();
            _supportedSwapchainFormats.AddRange(formatsArray);

            long DXGI_FORMAT_B8G8R8A8_UNORM_SRGB = 91;
            if (_supportedSwapchainFormats.Contains(DXGI_FORMAT_B8G8R8A8_UNORM_SRGB))
                _selectedSwapchainFormat = DXGI_FORMAT_B8G8R8A8_UNORM_SRGB;
            else if (_supportedSwapchainFormats.Count > 0)
                _selectedSwapchainFormat = _supportedSwapchainFormats[0];
            else
                throw new Exception("No suitable swapchain format found.");

            VRModCore.Log($"Selected Swapchain Format (DXGI): {_selectedSwapchainFormat}");

            _eyeSwapchains.Clear();
            _eyeSwapchainImages.Clear();
            _eyeSwapchainSRVs.Clear();

            // swapchain image struct は D3D11/D3D12 でレイアウト同一（XrSwapchainImageD3D11KHR を流用）・type 値のみ切替。
            var imgType = _useD3D12 ? XrStructureType.XR_TYPE_SWAPCHAIN_IMAGE_D3D12_KHR : XrStructureType.XR_TYPE_SWAPCHAIN_IMAGE_D3D11_KHR;

            for (int i = 0; i < _viewConfigViews.Count; i++)
            {
                var view = _viewConfigViews[i];
                var swapchainCreateInfo = new XrSwapchainCreateInfo
                {
                    type = XrStructureType.XR_TYPE_SWAPCHAIN_CREATE_INFO,
                    // CopyResource の dst として使うため TRANSFER_DST も宣言する（実使用と usage 宣言を一致させる）。
                    usageFlags = XrSwapchainUsageFlags.XR_SWAPCHAIN_USAGE_COLOR_ATTACHMENT_BIT | XrSwapchainUsageFlags.XR_SWAPCHAIN_USAGE_TRANSFER_DST_BIT | XrSwapchainUsageFlags.XR_SWAPCHAIN_USAGE_SAMPLED_BIT,
                    format = _selectedSwapchainFormat,
                    sampleCount = view.recommendedSwapchainSampleCount,
                    width = view.recommendedImageRectWidth,
                    height = view.recommendedImageRectHeight,
                    faceCount = 1,
                    arraySize = 1,
                    mipCount = 1
                };
                OpenXRHelper.CheckResult(OpenXRAPI.xrCreateSwapchain(_xrSession, in swapchainCreateInfo, out ulong scHandle), "xrCreateSwapchain");
                _eyeSwapchains.Add(scHandle);

                OpenXRAPI.xrEnumerateSwapchainImages(scHandle, 0, out uint imgCount, IntPtr.Zero);
                IntPtr scImagesPtr = Marshal.AllocHGlobal((int)(imgCount * Marshal.SizeOf<XrSwapchainImageD3D11KHR>()));
                List<IntPtr> currentEyeTexList = [];
                List<IntPtr> currentEyeSrvList = [];
                try
                {
                    for (int j = 0; j < imgCount; ++j) Marshal.StructureToPtr(new XrSwapchainImageD3D11KHR { type = imgType }, scImagesPtr + (j * Marshal.SizeOf<XrSwapchainImageD3D11KHR>()), false);
                    OpenXRAPI.xrEnumerateSwapchainImages(scHandle, imgCount, out imgCount, scImagesPtr);
                    for (int j = 0; j < imgCount; j++)
                    {
                        var swapchainImage = Marshal.PtrToStructure<XrSwapchainImageD3D11KHR>(scImagesPtr + (j * Marshal.SizeOf<XrSwapchainImageD3D11KHR>()));
                        currentEyeTexList.Add(swapchainImage.texture);
                        // SRV は D3D11 専用の dead code（描画には不使用）。D3D12 では生成せず D3D11 挙動を不変に保つ。
                        if (!_useD3D12)
                        {
                            int hResultSrv = NativeBridge.CreateAndRegisterSRV_Internal(swapchainImage.texture, (int)_selectedSwapchainFormat, out IntPtr srvPtr);
                            if (hResultSrv == 0 && srvPtr != IntPtr.Zero)
                                currentEyeSrvList.Add(srvPtr);
                            else
                                currentEyeSrvList.Add(IntPtr.Zero);
                        }
                    }
                }
                finally { Marshal.FreeHGlobal(scImagesPtr); }

                _eyeSwapchainImages.Add(currentEyeTexList);
                _eyeSwapchainSRVs.Add(currentEyeSrvList);
                VRModCore.Log($"Swapchain for view {i} created. Images: {currentEyeTexList.Count}, sampleCount: {view.recommendedSwapchainSampleCount}");
            }
        }

        public void UpdatePoses() => PumpFrame();  // 戻り値は破棄

        // session イベント（state walk）を非ブロッキングで全部 drain する。PumpFrame 冒頭と xrEndFrame 直前の
        // 2 箇所から呼ぶ（後者は standby 突入とフレーム submit のレース窓縮小＝spec §5.5.3 結論2）。
        private void PollSessionEvents()
        {
            // --- RESTORED: Full event polling and session state management ---
            IntPtr eventBufferPtr = IntPtr.Zero;
            try
            {
                int eventBufferSize = XrEventDataBuffer.GetSize();
                eventBufferPtr = Marshal.AllocHGlobal(eventBufferSize);
                while (true)
                {
                    var headerForInput = new XrEventDataBaseHeader { type = XrStructureType.XR_TYPE_EVENT_DATA_BUFFER };
                    Marshal.StructureToPtr(headerForInput, eventBufferPtr, false);
                    XrResult pollResult = OpenXRAPI.xrPollEvent(_xrInstance, eventBufferPtr);
                    if (pollResult == XrResult.XR_EVENT_UNAVAILABLE) break;

                    if (pollResult < 0) { IsVrAvailable = false; break; }

                    var actualEventType = (XrStructureType)Marshal.ReadInt32(eventBufferPtr);
                    if (actualEventType == XrStructureType.XR_TYPE_EVENT_DATA_SESSION_STATE_CHANGED)
                    {
                        var stateEvent = Marshal.PtrToStructure<XrEventDataSessionStateChanged>(eventBufferPtr);
                        if (stateEvent.state != _currentSessionState)
                        {
                            VRModCore.Log($"OpenXR Session State Changed: {_currentSessionState} -> {stateEvent.state} (frame={UnityEngine.Time.frameCount} t={UnityEngine.Time.realtimeSinceStartup:F3})");
                            _currentSessionState = stateEvent.state;

                            switch (_currentSessionState)
                            {
                                case XrSessionState.XR_SESSION_STATE_EXITING:
                                case XrSessionState.XR_SESSION_STATE_LOSS_PENDING:
                                    _isSessionRunning = false;
                                    IsVrAvailable = false;
                                    break;
                                case XrSessionState.XR_SESSION_STATE_STOPPING:
                                    if (_isSessionRunning)
                                    {
                                        VRModCore.Log("Session is stopping, calling xrEndSession.");
                                        OpenXRAPI.xrEndSession(_xrSession);
                                        _isSessionRunning = false;
                                    }
                                    break;
                                case XrSessionState.XR_SESSION_STATE_READY:
                                    if (!_isSessionRunning)
                                    {
                                        VRModCore.Log("Session is ready, calling xrBeginSession to resume.");
                                        var beginInfo = new XrSessionBeginInfo { type = XrStructureType.XR_TYPE_SESSION_BEGIN_INFO, primaryViewConfigurationType = _viewConfigType };
                                        // 非負＝成功（コードベース慣習・xrEndFrame の >= 0 判定と対称）。正の qualified-success でも復帰を取りこぼさない。
                                        if (OpenXRAPI.xrBeginSession(_xrSession, in beginInfo) >= 0)
                                        {
                                            _isSessionRunning = true;
                                        }
                                    }
                                    break;
                            }
                        }
                    }
                }
            }
            finally { if (eventBufferPtr != IntPtr.Zero) Marshal.FreeHGlobal(eventBufferPtr); }
        }

        // OpenXR フレームを 1 回回す（event poll → xrWaitFrame → xrBeginFrame → eye 描画 → xrEndFrame）。
        // rig 不在（keepalive）でも同じ経路を通す: shouldRender 時は両 eye swapchain を acquire/wait/release するが
        // RenderEye が null カメラで早期 return＝未描画。D3D11 では swapchain スロットは上書きまで内容を保持するので、
        // 直前数フレームに描いた絵が head-tracked pose で submit される（≒直前フレーム hold・厳密な最後の 1 枚では
        // なくリング長ぶん古い場合あり）。空フレーム layerCount=0 だと Oculus が「非提示」と見なし FOCUSED→STOPPING を
        // 繰り返しバウンスしたため（検死 2026-06-09 gate#2）、コンテンツ付き layerCount=1 で submit して回避する。
        // shouldRender=false 時は下の render ブロックを skip し layerCount=0 で submit する（OpenXR 作法）。
        // event poll / session 状態管理は下の :341 ガードの前にあり、非 running 時も poll が走り READY で再開する。
        // _input.Sync も同区間で keepalive 中継続＝意図的（pose は _appSpace 基準で rig 非依存・復帰時に即温まる）。
        // 戻り値 = フレームを実際に submit できたか（xrEndFrame 成功）。早期 return / DISCARDED は false。
        // 共有 GPU メモリ leak 調査用の 1Hz polling 出力（Config が 0 のとき完全 no-op）。
        // Time.unscaledTime で throttle＝Time.timeScale=0 の menu 中も等間隔に出る。
        private float _lastDxgiMemLogTime;

        private void PollDxgiMemoryLogIfDue()
        {
            if (!_useD3D12) return;
            int intervalMs = ConfigManager.OpenXR_DxgiMemoryLogIntervalMs?.Value ?? 0;
            if (intervalMs <= 0) return;
            float now = Time.unscaledTime;
            float intervalSec = intervalMs / 1000f;
            if (_lastDxgiMemLogTime > 0f && now - _lastDxgiMemLogTime < intervalSec) return;
            _lastDxgiMemLogTime = now;
            if (!NativeBridge.TryQueryDxgiVideoMemoryInfo(out ulong localCur, out ulong localBudget,
                out ulong nonLocalCur, out ulong nonLocalBudget)) return;
            const double MB = 1024.0 * 1024.0;
            // frame= は per-frame rate 比較用（Step 2 等で fps が変わっても正しい単位で比較できる）。
            VRModCore.Log(
                $"[DxgiMem] frame={Time.frameCount} LOCAL: cur={localCur / MB:F0} MB / budget={localBudget / MB:F0} MB, " +
                $"NON_LOCAL: cur={nonLocalCur / MB:F0} MB / budget={nonLocalBudget / MB:F0} MB");
        }

        private bool PumpFrame()
        {
            if (!IsVrAvailable || _xrSession == OpenXRConstants.XR_NULL_HANDLE) return false;

            PollDxgiMemoryLogIfDue();

            PollSessionEvents();

            if (!_isSessionRunning) return false;

            var frameWaitInfo = new XrFrameWaitInfo { type = XrStructureType.XR_TYPE_FRAME_WAIT_INFO };
            _xrFrameState.type = XrStructureType.XR_TYPE_FRAME_STATE;
            OpenXRAPI.xrWaitFrame(_xrSession, in frameWaitInfo, out _xrFrameState);

            // 正面リセット要求があれば _appSpace を作り直す（_input.Sync / xrLocateViews より前＝同フレームで
            // eye とコントローラ両方に新 space を効かせ、コントローラの 1 フレーム遅延を避ける）。
            TryApplyRecenter();

            // predictedDisplayTime 確定後に入力を同期（pose の xrLocateSpace が同フレームの予測時刻を使える）。
            _input.Sync(_xrFrameState.predictedDisplayTime);

            var frameBeginInfo = new XrFrameBeginInfo { type = XrStructureType.XR_TYPE_FRAME_BEGIN_INFO };
            if (OpenXRAPI.xrBeginFrame(_xrSession, in frameBeginInfo) == XrResult.XR_FRAME_DISCARDED)
            {
                var discardedFrameEndInfo = new XrFrameEndInfo { type = XrStructureType.XR_TYPE_FRAME_END_INFO, displayTime = _xrFrameState.predictedDisplayTime, layerCount = 0, layers = IntPtr.Zero, environmentBlendMode = XrEnvironmentBlendMode.XR_ENVIRONMENT_BLEND_MODE_OPAQUE };
                OpenXRAPI.xrEndFrame(_xrSession, in discardedFrameEndInfo);
                return false;   // フレーム破棄＝submit 不成立
            }

            var frameEndInfo = new XrFrameEndInfo { type = XrStructureType.XR_TYPE_FRAME_END_INFO, displayTime = _xrFrameState.predictedDisplayTime, environmentBlendMode = XrEnvironmentBlendMode.XR_ENVIRONMENT_BLEND_MODE_OPAQUE, layerCount = 0, layers = IntPtr.Zero };

            // fade/overlay は shouldRender に無関係に submit するためブロック外で持つ（teardown 継続を完全にする）。
            bool projectionReady = false;
            long quadTimeoutNs = (long)(ConfigManager.OpenXR_SwapchainWaitTimeoutMs?.Value ?? 100) * 1_000_000L;

            if (_xrFrameState.shouldRender == XrBool32.XR_TRUE)
            {
                var viewLocateInfo = new XrViewLocateInfo { type = XrStructureType.XR_TYPE_VIEW_LOCATE_INFO, viewConfigurationType = _viewConfigType, displayTime = _xrFrameState.predictedDisplayTime, space = _appSpace };
                _locatedViewState.type = XrStructureType.XR_TYPE_VIEW_STATE;

                IntPtr pLocatedViews = Marshal.AllocHGlobal(Marshal.SizeOf<XrView>() * _locatedViews.Length);
                try
                {
                    for (int i = 0; i < _locatedViews.Length; i++) Marshal.StructureToPtr(_locatedViews[i], pLocatedViews + (i * Marshal.SizeOf<XrView>()), false);
                    OpenXRAPI.xrLocateViews(_xrSession, in viewLocateInfo, out _locatedViewState, (uint)_locatedViews.Length, out _, pLocatedViews);
                    for (int i = 0; i < _locatedViews.Length; i++) _locatedViews[i] = Marshal.PtrToStructure<XrView>(pLocatedViews + (i * Marshal.SizeOf<XrView>()));
                }
                finally { Marshal.FreeHGlobal(pLocatedViews); }

                bool poseIsValid = (_locatedViewState.viewStateFlags & XrViewStateFlags.XR_VIEW_STATE_ORIENTATION_VALID_BIT) != 0;

                var acquireInfo = new XrSwapchainImageAcquireInfo { type = XrStructureType.XR_TYPE_SWAPCHAIN_IMAGE_ACQUIRE_INFO };
                var waitInfo = new XrSwapchainImageWaitInfo
                {
                    type = XrStructureType.XR_TYPE_SWAPCHAIN_IMAGE_WAIT_INFO,
                    // INFINITE をやめメインスレッドの無限ハングを構造的に除去。値は live config（既定 100ms）。
                    timeout = (long)(ConfigManager.OpenXR_SwapchainWaitTimeoutMs?.Value ?? 100) * 1_000_000L
                };
                var releaseInfo = new XrSwapchainImageReleaseInfo { type = XrStructureType.XR_TYPE_SWAPCHAIN_IMAGE_RELEASE_INFO };

                // acquire: 描画意図がある（pose 有効）ときのみ新規取得。取得済みは前フレームの持ち越し。
                if (poseIsValid)
                    for (int i = 0; i < 2; i++)
                        if (!_eyeAcquired[i] && OpenXRAPI.xrAcquireSwapchainImage(_eyeSwapchains[i], in acquireInfo, out _eyeImgIdx[i]) >= 0)
                            _eyeAcquired[i] = true;

                // wait: 取得済みかつ未 ready を有限 wait（pose 無効でも持ち越し解消のため走らせる＝#1）。
                //   XR_SUCCESS  → ready。 XR_TIMEOUT_EXPIRED(>0) → 持ち越して次フレ再 wait。
                //   負(エラー)  → 状態不定として acquire を捨て次フレ再取得（image リーク=片眼恒久停止を防ぐ＝#2）。
                for (int i = 0; i < 2; i++)
                {
                    if (!_eyeAcquired[i] || _eyeReady[i]) continue;
                    XrResult wr = OpenXRAPI.xrWaitSwapchainImage(_eyeSwapchains[i], in waitInfo);
                    if (wr == XrResult.XR_SUCCESS) _eyeReady[i] = true;
                    else if (wr < 0) _eyeAcquired[i] = false;
                }

                // 両 eye ready のときだけ確定。pose 有効時のみ実描画し、pose 無効でも release して lifecycle を進める（#1）。
                if (_eyeReady[0] && _eyeReady[1])
                {
                    int lastCopyTicket = 0;
                    if (poseIsValid)
                    {
                        int t0 = RenderEye(0, _eyeImgIdx[0]);
                        int t1 = RenderEye(1, _eyeImgIdx[1]);
                        lastCopyTicket = Math.Max(t0, t1);
                        _lastCopyTicketIssued = Math.Max(_lastCopyTicketIssued, lastCopyTicket);
                        PopulateProjectionLayer();
                        projectionReady = true;
                    }
                    // D3D12: copy event を render thread に流し、release 前に submit 完了を有限待機する
                    // （timeout 時は stale 内容のまま release＝frame loop を止めない。設計 §3）
                    if (_useD3D12 && lastCopyTicket > 0)
                    {
                        GL.IssuePluginEvent(_renderEventFunc, NativeBridge.kEventExecutePendingCopies);
                        if (!NativeBridge.WaitForCopyTicket(lastCopyTicket, ConfigManager.OpenXR_D3D12SubmitWaitMs?.Value ?? 50))
                            VRModCore.LogRuntimeDebug("OpenXR/D3D12: copy submit wait timeout（stale release で続行）。");

                    }
                    for (int i = 0; i < 2; i++)
                    {
                        OpenXRAPI.xrReleaseSwapchainImage(_eyeSwapchains[i], in releaseInfo);
                        _eyeAcquired[i] = false;
                        _eyeReady[i] = false;
                    }
                }
                else
                {
                    // 片 eye でも未 ready（wait timeout）: 当フレームは layerCount=0 で閉じ、取得済み image を次フレへ
                    // 持ち越して再 wait。メインスレッドは最長 timeout×2 でしか待たない＝無限ハングしない（wedge 解消で復帰）。
                    VRModCore.LogRuntimeDebug($"OpenXR: swapchain wait timeout (acquired={_eyeAcquired[0]},{_eyeAcquired[1]} ready={_eyeReady[0]},{_eyeReady[1]})。当フレーム skip。");
                }
            }

            // world 固定 overlay の anchor を可視 rising edge で 1 回だけ凍結する（PumpFrame の単一地点）。
            // shouldRender ブロック外＝通常 Update / teardown keepalive どちらの PumpFrame 経路でも必ず通過する。
            MaybeSnapshotOverlayAnchor();

            // layer 集約: [projection?][fade?][overlay?] を _pLayersForSubmit へ詰める。
            // shouldRender==false でも fade/overlay は submit する（head-lock quad は eye 非依存・teardown 中も継続）。
            // quad swapchain の acquire/wait/release は begin/end frame 間なら shouldRender に関係なく合法。
            AppendCompositionLayers(ref frameEndInfo, projectionReady, quadTimeoutNs);

            // xrEndFrame 直前の最終 event poll: doff→standby の state walk がフレーム冒頭 poll の後に
            // 届くレースを潰す（検死 2026-06-11: standby 突入と submit が同フレーム衝突＝hang）。
            // STOPPING を受けたら PollSessionEvents が xrEndSession 済み＝この begun frame は runtime が破棄し、
            // _isSessionRunning=false で下の return が xrEndFrame を呼ばず抜ける。復帰は次セッション
            // （READY→xrBeginSession 後）の frame loop が xrWaitFrame から fresh に再ペアリングする
            // （STOPPING→READY が同一 drain に重なる稀ケースは unmatched xrEndFrame=単発エラー→次フレ回復）。
            // xrEndFrame は timeout を持たないため「ブロックする呼び出しに到達させない」方向しか対策が無い。
            PollSessionEvents();
            if (!_isSessionRunning) return false;

            // XrResult 非負＝成功（コードベース慣習・他の < 0 判定と対称）。
            return OpenXRAPI.xrEndFrame(_xrSession, in frameEndInfo) >= 0;
        }

        // 戻り値 = D3D12 で enqueue した copy チケット（0 = D3D11 経路 / 未 copy）。
        private int RenderEye(int eyeIndex, uint swapchainImageIndex)
        {
            Camera currentEyeCamera = (eyeIndex == 0) ? _leftVrCamera : _rightVrCamera;
            // シーン遷移（scene unload で rig 道連れ破棄）や rebind 待ちの間、eye camera は fake-null になる。
            // ここで throw すると UpdatePoses の xrReleaseSwapchainImage/xrEndFrame がスキップされ
            // フレームループが begin/acquire のまま壊れて HMD が固着する（実機 2026-06-09）。
            // 描画をスキップして frame loop を健全に閉じる（直前フレーム面を再提示）。
            if (currentEyeCamera == null) return 0;
            // 診断: RenderEye 全体を bypass する（acquire/release だけ通して frame loop 維持）。
            // swapchain image cycle / OpenXR runtime 側の leak かを切り分ける最終手段。
            RenderTexture currentIntermediateRT = (eyeIndex == 0) ? _leftEyeIntermediateRT : _rightEyeIntermediateRT;
            IntPtr nativeTextureResourcePtr = _eyeSwapchainImages[eyeIndex][(int)swapchainImageIndex];
            XrViewConfigurationView viewConfig = _viewConfigViews[eyeIndex];

            // config 読みは fork 慣習（?.Value ?? 既定）。SanitizeMsaa で {1,2,4,8} へ正規化。
            int desiredMsaa = EyeRtPolicy.SanitizeMsaa(ConfigManager.VrEyeMsaa?.Value ?? 4);
            bool rtExists = currentIntermediateRT != null && currentIntermediateRT.IsCreated();
            bool rtNeedsRecreation = EyeRtPolicy.NeedsRecreate(
                rtExists,
                rtExists ? currentIntermediateRT.width : 0,
                rtExists ? currentIntermediateRT.height : 0,
                rtExists ? currentIntermediateRT.antiAliasing : 0,
                (int)viewConfig.recommendedImageRectWidth,
                (int)viewConfig.recommendedImageRectHeight,
                desiredMsaa);
            if (rtNeedsRecreation)
            {
                if (currentIntermediateRT != null) { currentIntermediateRT.Release(); UnityEngine.Object.Destroy(currentIntermediateRT); }

                var renderTextureFormat = QualitySettings.activeColorSpace == ColorSpace.Linear
                    ? GraphicsFormat.B8G8R8A8_SRGB
                    : GraphicsFormat.B8G8R8A8_UNorm;

                VRModCore.Log($"Creating intermediate RenderTexture: format={renderTextureFormat} msaa={desiredMsaa}x");
                currentIntermediateRT = new RenderTexture((int)viewConfig.recommendedImageRectWidth, (int)viewConfig.recommendedImageRectHeight, 24, renderTextureFormat)
                {
                    // MSAA 時は「MSAA 面 + resolve 済み非 MSAA テクスチャ」の 2 枚構造になり、
                    // GetNativeTexturePtr() は resolve 済みの方を返す＝下流の CopyResource は sample count 1 のまま不変
                    antiAliasing = desiredMsaa
                };

                if (eyeIndex == 0) _leftEyeIntermediateRT = currentIntermediateRT; else _rightEyeIntermediateRT = currentIntermediateRT;
            }

            currentEyeCamera.targetTexture = currentIntermediateRT;
            // eye の cullingMask/clearFlags は companion override が active ならそれを、無ければ game-copy を
            // 適用する（単一所有点＝描画直前。EyeCullingCoordinator の void/dim/normal をここで反映）。
            CameraClearFlags eyeClear = _eyeCullOverrideActive ? _eyeCullOverrideClear : _mainCameraClearFlags;
            currentEyeCamera.clearFlags = eyeClear;
            if (eyeClear == CameraClearFlags.SolidColor) { currentEyeCamera.backgroundColor = _eyeCullOverrideActive ? _eyeCullOverrideBg : _mainCameraBackgroundColor; }
            // overlay channel が有効なときは本描画から overlay layer（VR ビジュアル + VR モデル）を外す
            //（post 後に CommandBuffer で別途描く＝グレーディング/Bloom を乗せず main pass と二重描画にならないため。単一所有点）。
            // PostProcess 由来 overlayMask は post override active 時のみ有効（KeepUiCrisp は post 反映時のみ効く）。
            // VR モデル overlay（手モデル等）は PostProcess の有無と独立＝常時有効＝UI 同様の最前面化。
            int eyeMask = _eyeCullOverrideActive ? _eyeCullOverrideMask : _mainCameraCullingMask;
            int effectiveOverlayMask = (_eyePpOverrideActive ? _eyeOverlayLayerMask : 0) | _vrModelOverlayMask;
            if (effectiveOverlayMask != 0) eyeMask &= ~effectiveOverlayMask;
            currentEyeCamera.cullingMask = eyeMask;
            // 診断: cullingMask=0 で scene の全 renderer を cull＝scene draw 0 にする（clear / Camera.Render / URP pipeline setup
            // / post-process / DrawEyeOverlay は通常実行）。Round 9A で RT サイズ依存と確定後、leak が scene draw 経路の
            // eye の URP post-process（PostProcessCoordinator が push）も描画直前にここで適用する（単一所有点）。
            ApplyEyePostProcessOverride(currentEyeCamera);

            XrPosef eyePose = _locatedViews[eyeIndex].pose;
            Vector3 position = new(eyePose.position.x, eyePose.position.y, -eyePose.position.z);
            Quaternion rotation = new(eyePose.orientation.x, eyePose.orientation.y, -eyePose.orientation.z, -eyePose.orientation.w);
#if MONO
            currentEyeCamera.transform.localPosition = position;
            currentEyeCamera.transform.localRotation = rotation;
#elif CPP
            currentEyeCamera.transform.SetLocalPositionAndRotation(position, rotation);
#endif

            Matrix4x4 projM = CreateProjectionMatrixFromFovUsingFrustum(_locatedViews[eyeIndex].fov, currentEyeCamera.nearClipPlane, currentEyeCamera.farClipPlane);
            projM = Matrix4x4.Scale(new Vector3(1, -1, 1)) * projM;
            currentEyeCamera.projectionMatrix = projM;

            try
            {
                bool originalInvertCulling = GL.invertCulling;
                GL.invertCulling = true;
                currentEyeCamera.Render();
                // 後段 transparent redraw（leak 回避で scene draw から除外した透過 MR の再描画）。
                // GL.invertCulling=true 区間内＝winding は本描画と一致。post 後の intermediate RT に直接描く。
                _sceneTransparentRedraw?.Invoke(currentEyeCamera, currentIntermediateRT);
                // post 後に overlay layer（VR ビジュアル）を post 無しで intermediate RT へ直接重ねる
                //（本描画と同じ invertCulling=true 区間＝winding 一致。copy/flush より前。MSAA 面に積み自動 resolve）。
                DrawEyeOverlay(currentEyeCamera, currentIntermediateRT);
                GL.invertCulling = originalInvertCulling;
                Graphics.ExecuteCommandBuffer(_flushCommandBuffer);
                RenderTexture.active = null;

                if (currentIntermediateRT != null && currentIntermediateRT.IsCreated())
                {
                    // 診断: swapchain copy 経路（GetNativeTexturePtr + CopyResource）を bypass。intermediate RT への描画は行われるが
                    IntPtr sourceNativePtr = currentIntermediateRT.GetNativeTexturePtr();
                    if (sourceNativePtr != IntPtr.Zero && nativeTextureResourcePtr != IntPtr.Zero)
                    {
                        if (_useD3D12)
                            return NativeBridge.EnqueueCopyD3D12_Internal(sourceNativePtr, nativeTextureResourcePtr); // 実行は PumpFrame 側の event 発行で
                        NativeBridge.DirectCopyResource_Internal(nativeTextureResourcePtr, sourceNativePtr);
                    }
                }
                return 0;
            }
            finally
            {
                // 設計不変条件 (ConfigureVrCamera 参照): 本カメラは enabled=false で手動描画のみ。
                // auto-render が URP intermediate RT を二重に走らせて共有 GPU メモリを leak させるのを防ぐ
                // (vr-shared-gpu-memory-leak.md / plans/2026-06-23-vr-leak-fix-eyecam-autorender.md)。
                currentEyeCamera.enabled = false;
            }
        }

        private void PopulateProjectionLayer()
        {
            for (int i = 0; i < 2; i++)
            {
                _projectionLayerViews[i].pose = _locatedViews[i].pose;
                _projectionLayerViews[i].fov = _locatedViews[i].fov;
                _projectionLayerViews[i].subImage.swapchain = _eyeSwapchains[i];
                _projectionLayerViews[i].subImage.imageRect.offset = new XrOffset2Di { x = 0, y = 0 };
                _projectionLayerViews[i].subImage.imageRect.extent.width = (int)_viewConfigViews[i].recommendedImageRectWidth;
                _projectionLayerViews[i].subImage.imageRect.extent.height = (int)_viewConfigViews[i].recommendedImageRectHeight;
                _projectionLayerViews[i].subImage.imageArrayIndex = 0;
                Marshal.StructureToPtr(_projectionLayerViews[i], _pProjectionLayerViews + (i * Marshal.SizeOf<XrCompositionLayerProjectionView>()), false);
            }
            _projectionLayer.layerFlags = XrCompositionLayerFlags.None;
            _projectionLayer.space = _appSpace;
            _projectionLayer.viewCount = (uint)_viewConfigViews.Count;
            _projectionLayer.views = _pProjectionLayerViews;
            Marshal.StructureToPtr(_projectionLayer, _pProjectionLayer, false);
        }

        // fade 単色 RT を塗って fade swapchain へ stage し、quad layer struct を _pFadeLayer へ marshal する。
        // 戻り値 = この quad を当フレーム submit してよいか（stage 成功）。
        private bool PrepareFadeLayer(long timeoutNs)
        {
            // _pFadeLayer==Zero は VIEW space 生成失敗時（buffer 未確保）。native null 書き込みクラッシュを防ぐ。
            if (_fadeSwapchain == null || _pFadeLayer == IntPtr.Zero) return false;

            // 単色 RT を GL.Clear で (r,g,b,a) に塗る。format は swapchain（InitializeSwapchains が color space
            // 非依存で常に DXGI 91 = B8G8R8A8_UNORM_SRGB を選択）と一致させる＝CopyResource の同 format 制約。
            // eye 経路の color-space 三項分岐はゲームが Linear 運用のため SRGB に解決され問題化していないだけで、
            // quad RT は分岐させず SRGB 固定にする（Gamma 運用でも CopyResource format 不一致を出さない）。
            var fmt = UnityEngine.Experimental.Rendering.GraphicsFormat.B8G8R8A8_SRGB;
            if (_fadeRT == null || !_fadeRT.IsCreated())
            {
                if (_fadeRT != null) { _fadeRT.Release(); UnityEngine.Object.Destroy(_fadeRT); }
                _fadeRT = new RenderTexture(FADE_TEX_SIZE, FADE_TEX_SIZE, 0, fmt);
                _fadeRT.Create();
            }
            var prevActive = RenderTexture.active;
            RenderTexture.active = _fadeRT;
            GL.Clear(false, true, _fadeColor); // alpha 含めて塗る（fade の核心）
            RenderTexture.active = prevActive;

            IntPtr src = _fadeRT.GetNativeTexturePtr();
            if (src == IntPtr.Zero || !_fadeSwapchain.TryStage(src, timeoutNs)) return false;

            var quad = new XrCompositionLayerQuad
            {
                type = XrStructureType.XR_TYPE_COMPOSITION_LAYER_QUAD,
                next = IntPtr.Zero,
                layerFlags = XrCompositionLayerFlags.XR_COMPOSITION_LAYER_BLEND_TEXTURE_SOURCE_ALPHA_BIT | XrCompositionLayerFlags.XR_COMPOSITION_LAYER_UNPREMULTIPLIED_ALPHA_BIT,
                space = _viewSpace,
                eyeVisibility = XrEyeVisibility.XR_EYE_VISIBILITY_BOTH,
                subImage = new XrSwapchainSubImage
                {
                    swapchain = _fadeSwapchain.Handle,
                    imageRect = new XrRect2Di { offset = new XrOffset2Di { x = 0, y = 0 }, extent = new XrExtent2Di { width = _fadeSwapchain.Width, height = _fadeSwapchain.Height } },
                    imageArrayIndex = 0,
                },
                // head-lock: VIEW space で identity 回転・前方 FADE_QUAD_DISTANCE（-Z）。
                pose = new XrPosef { orientation = new XrQuaternionf { w = 1f }, position = new XrVector3f { x = 0f, y = 0f, z = -FADE_QUAD_DISTANCE } },
                size = new XrExtent2Df { width = FADE_QUAD_SIZE, height = FADE_QUAD_SIZE },
            };
            Marshal.StructureToPtr(quad, _pFadeLayer, false);
            return true;
        }

        // 可視 rising edge（hidden→visible）で頭 pose を app space に取得し、world 固定 anchor を 1 回だけ凍結する。
        // _appSpace は session 所有（rig 非依存）＝ teardown 中（eye GO 破棄後）でも locate 有効。
        // locate 失敗 / 水平退化（真上下凝視）→ _overlayAnchorValid=false ＝ PrepareOverlayLayer が頭ロック fallback する。
        private void MaybeSnapshotOverlayAnchor()
        {
            bool rising = _overlayVisible && !_prevOverlayVisible;
            _prevOverlayVisible = _overlayVisible;
            if (!rising || !_overlayWorldLock) return;

            _overlayAnchorValid = false;
            if (_viewSpace == OpenXRConstants.XR_NULL_HANDLE) return;

            var loc = new XrSpaceLocation { type = XrStructureType.XR_TYPE_SPACE_LOCATION };
            if (OpenXRAPI.xrLocateSpace(_viewSpace, _appSpace, _xrFrameState.predictedDisplayTime, ref loc) < 0) return;
            bool posValid = (loc.locationFlags & XrSpaceLocationFlags.XR_SPACE_LOCATION_POSITION_VALID_BIT) != 0;
            bool oriValid = (loc.locationFlags & XrSpaceLocationFlags.XR_SPACE_LOCATION_ORIENTATION_VALID_BIT) != 0;
            if (!posValid || !oriValid) return; // 初回 Sync 前の無効時刻 locate もここで弾く

            // 生 XrPosef 成分（RH・LH 変換なし）を TransitionAnchorMath へ渡す。
            var headPos = new Vector3(loc.pose.position.x, loc.pose.position.y, loc.pose.position.z);
            var headOri = new Quaternion(loc.pose.orientation.x, loc.pose.orientation.y, loc.pose.orientation.z, loc.pose.orientation.w);
            if (!TransitionAnchorMath.ComputeWorldAnchor(headPos, headOri, _overlayDistanceM, out Vector3 anchorPos, out float yaw))
                return; // 水平退化 → 頭ロック fallback

            _overlayAnchorPos = new XrVector3f { x = anchorPos.x, y = anchorPos.y, z = anchorPos.z };
            _overlayAnchorOri = new XrQuaternionf { x = 0f, y = Mathf.Sin(yaw * 0.5f), z = 0f, w = Mathf.Cos(yaw * 0.5f) };
            _overlayAnchorValid = true;
        }

        // IVrCameraSetup.RequestRecenter。即時には触らず pending を立て、次の PumpFrame 先頭で適用する
        //（valid な頭 pose と predictedDisplayTime が要るため）。
        public void RequestRecenter() => _recenterPending = true;

        // pending な正面リセットを適用する。PumpFrame の xrWaitFrame 後・_input.Sync / xrLocateViews 前で呼ぶ
        //（同フレームで eye とコントローラ両方に新 _appSpace を効かせる）。valid pose が無ければ pending を保持して次フレーム再試行。
        private void TryApplyRecenter()
        {
            if (!_recenterPending) return;
            if (!_isSessionRunning
                || _viewSpace == OpenXRConstants.XR_NULL_HANDLE
                || _appSpace == OpenXRConstants.XR_NULL_HANDLE
                || OpenXRAPI.xrCreateReferenceSpace == null)
            {
                // 構造的に不可能（未対応 loader 等）→ spin を避けて諦める。可能性のある未確定（session/space）は保持。
                if (OpenXRAPI.xrCreateReferenceSpace == null) _recenterPending = false;
                return;
            }

            var loc = new XrSpaceLocation { type = XrStructureType.XR_TYPE_SPACE_LOCATION };
            if (OpenXRAPI.xrLocateSpace(_viewSpace, _appSpace, _xrFrameState.predictedDisplayTime, ref loc) < 0)
                return; // locate 失敗 → 次フレーム再試行（pending 保持）
            bool posValid = (loc.locationFlags & XrSpaceLocationFlags.XR_SPACE_LOCATION_POSITION_VALID_BIT) != 0;
            bool oriValid = (loc.locationFlags & XrSpaceLocationFlags.XR_SPACE_LOCATION_ORIENTATION_VALID_BIT) != 0;
            if (!posValid || !oriValid) return; // valid pose 待ち（起動直後 / トラッキングロスト）

            // 頭 pose H（_appSpace 基準・RH）から yaw-only delta D を作る。高さは維持（y=0）。
            var q = loc.pose.orientation;
            float yawMag = Mathf.Sqrt(q.y * q.y + q.w * q.w); // Y 軸 twist 成分（pitch/roll を落とす）
            XrQuaternionf dOri = yawMag < 1e-6f
                ? new XrQuaternionf { w = 1f }
                : new XrQuaternionf { y = q.y / yawMag, w = q.w / yawMag };
            var dPos = new XrVector3f { x = loc.pose.position.x, y = 0f, z = loc.pose.position.z };

            // newOffset = Compose(oldOffset, D)。D を旧 offset 座標系で適用し natural 原点基準の新 offset を得る。
            XrPosef newOffset = ComposePose(_appSpacePoseOffset, dOri, dPos);

            var createInfo = new XrReferenceSpaceCreateInfo
            {
                type = XrStructureType.XR_TYPE_REFERENCE_SPACE_CREATE_INFO,
                referenceSpaceType = _appSpaceType,
                poseInReferenceSpace = newOffset,
            };
            if (OpenXRAPI.xrCreateReferenceSpace(_xrSession, in createInfo, out ulong newSpace) < 0
                || newSpace == OpenXRConstants.XR_NULL_HANDLE)
            {
                VRModCore.LogWarning("OpenXR: 正面リセット用 reference space の生成に失敗。現状維持。");
                _recenterPending = false; // hard failure → 無限リトライしない（ユーザーは再ジェスチャで再試行可）
                return;
            }

            ulong old = _appSpace;
            _appSpace = newSpace;
            _appSpacePoseOffset = newOffset;
            _input.SetAppSpace(_appSpace); // 入力側 cache を新ハンドルへ
            if (old != OpenXRConstants.XR_NULL_HANDLE && OpenXRAPI.xrDestroySpace != null)
                OpenXRAPI.xrDestroySpace(old);
            // world-locked overlay の anchor（_overlayAnchorPos/Ori）は旧 _appSpace 座標で凍結されている。
            // 原点が動いたので無効化し、次の可視 rising edge まで頭ロック fallback へ倒す
            //（recenter 適用時に overlay が瞬間移動するのを防ぐ）。
            _overlayAnchorValid = false;
            _recenterPending = false;
            VRModCore.Log("OpenXR: 正面リセット（reference space recenter）を適用。");
        }

        // 剛体 pose 合成 result = A ∘ {dOri, dPos}（D を A 座標系で適用）。
        // 成分は OpenXR RH のまま UnityEngine.Quaternion/Vector3 で代数計算する（MaybeSnapshotOverlayAnchor と同方式・
        // RH→LH 変換は挟まない）。A.orientation は yaw-only 前提。
        private static XrPosef ComposePose(XrPosef a, XrQuaternionf dOri, XrVector3f dPos)
        {
            var aOri = new Quaternion(a.orientation.x, a.orientation.y, a.orientation.z, a.orientation.w);
            var dOriU = new Quaternion(dOri.x, dOri.y, dOri.z, dOri.w);
            Quaternion rOri = aOri * dOriU;
            Vector3 rPos = new Vector3(a.position.x, a.position.y, a.position.z)
                         + aOri * new Vector3(dPos.x, dPos.y, dPos.z);
            return new XrPosef
            {
                orientation = new XrQuaternionf { x = rOri.x, y = rOri.y, z = rOri.z, w = rOri.w },
                position = new XrVector3f { x = rPos.x, y = rPos.y, z = rPos.z },
            };
        }

        // source 遷移絵柄（BG2VR の ARGB32 RT native ptr）を external texture として wrap し、
        // crop+flip+format を OVERLAY_TEX_SIZE 正方の intermediate RT へ Blit で焼き込んでから overlay
        // swapchain へ stage し、quad layer を _pOverlayLayer へ marshal する。
        // 戻り値 = この quad を当フレーム submit してよいか。
        private bool PrepareOverlayLayer(long timeoutNs)
        {
            // _pOverlayLayer==Zero は VIEW space 生成失敗時（buffer 未確保）。native null 書き込みクラッシュを防ぐ。
            if (_overlaySwapchain == null || _pOverlayLayer == IntPtr.Zero || _overlaySrcPtr == IntPtr.Zero) return false;

            // external texture を source ptr/寸法に追従して（再）生成。RGBA32=ARGB32 RT のバイト順に一致。
            if (_overlayExternalTex == null || _overlayExternalTex.GetNativeTexturePtr() != _overlaySrcPtr
                || _overlayExternalTex.width != _overlaySrcW || _overlayExternalTex.height != _overlaySrcH)
            {
                if (_overlayExternalTex != null) UnityEngine.Object.Destroy(_overlayExternalTex);
                _overlayExternalTex = Texture2D.CreateExternalTexture(_overlaySrcW, _overlaySrcH, TextureFormat.RGBA32, false, false, _overlaySrcPtr);
            }

            // intermediate RT（正方・swapchain と同 format）。fade と同じく color space 非依存で SRGB 固定にして
            // swapchain（DXGI 91）と一致させる＝CopyResource の同 format 制約。
            var fmt = UnityEngine.Experimental.Rendering.GraphicsFormat.B8G8R8A8_SRGB;
            if (_overlayRT == null || !_overlayRT.IsCreated())
            {
                if (_overlayRT != null) { _overlayRT.Release(); UnityEngine.Object.Destroy(_overlayRT); }
                _overlayRT = new RenderTexture(OVERLAY_TEX_SIZE, OVERLAY_TEX_SIZE, 0, fmt);
                _overlayRT.Create();
            }

            // crop(atlas)+flipV を Blit の scale/offset で焼き込む（quad layer は UV を持てないため）。
            UnityVRMod.Core.OverlayQuadMath.BlitScaleOffset(_overlayUMin, _overlayVMin, _overlayUMax, _overlayVMax, out Vector2 scale, out Vector2 offset);
            Graphics.Blit(_overlayExternalTex, _overlayRT, scale, offset);

            IntPtr src = _overlayRT.GetNativeTexturePtr();
            if (src == IntPtr.Zero || !_overlaySwapchain.TryStage(src, timeoutNs)) return false;

            // aspect は atlas 全体でなく UV crop 後の実効画素比で復元する（atlas sub-rect の歪み防止）。
            UnityVRMod.Core.OverlayQuadMath.CroppedPixelSize(_overlaySrcW, _overlaySrcH, _overlayUMin, _overlayVMin, _overlayUMax, _overlayVMax, out int cropW, out int cropH);
            UnityVRMod.Core.OverlayQuadMath.QuadSize(_overlayWidthM, cropW, cropH, out float qw, out float qh);
            // world 固定（anchor 凍結済）なら app space + 凍結 pose、それ以外は従来の VIEW space 頭ロック（byte 等価）。
            bool worldLocked = _overlayWorldLock && _overlayAnchorValid;
            ulong quadSpace = worldLocked ? _appSpace : _viewSpace;
            XrPosef quadPose = worldLocked
                ? new XrPosef { orientation = _overlayAnchorOri, position = _overlayAnchorPos }
                : new XrPosef { orientation = new XrQuaternionf { w = 1f }, position = new XrVector3f { x = 0f, y = 0f, z = -_overlayDistanceM } };
            var quad = new XrCompositionLayerQuad
            {
                type = XrStructureType.XR_TYPE_COMPOSITION_LAYER_QUAD,
                next = IntPtr.Zero,
                layerFlags = XrCompositionLayerFlags.XR_COMPOSITION_LAYER_BLEND_TEXTURE_SOURCE_ALPHA_BIT | XrCompositionLayerFlags.XR_COMPOSITION_LAYER_UNPREMULTIPLIED_ALPHA_BIT,
                space = quadSpace,
                eyeVisibility = XrEyeVisibility.XR_EYE_VISIBILITY_BOTH,
                subImage = new XrSwapchainSubImage
                {
                    swapchain = _overlaySwapchain.Handle,
                    imageRect = new XrRect2Di { offset = new XrOffset2Di { x = 0, y = 0 }, extent = new XrExtent2Di { width = _overlaySwapchain.Width, height = _overlaySwapchain.Height } },
                    imageArrayIndex = 0,
                },
                pose = quadPose,
                size = new XrExtent2Df { width = qw, height = qh },
            };
            Marshal.StructureToPtr(quad, _pOverlayLayer, false);
            return true;
        }

        // projection / fade / overlay を順に _pLayersForSubmit へ詰めて frameEndInfo を更新する。
        // 順序（下→上）= projection → fade → overlay。各 quad は stage 成功時のみ append。
        // shouldRender に無関係に呼ばれる（fade/overlay は VIEW space head-lock で eye 非依存・teardown 中も継続）。
        private void AppendCompositionLayers(ref XrFrameEndInfo frameEndInfo, bool projectionReady, long timeoutNs)
        {
            int count = 0;
            if (projectionReady)
                Marshal.WriteIntPtr(_pLayersForSubmit, count++ * IntPtr.Size, _pProjectionLayer);

            if (_fadeActive && _viewSpace != OpenXRConstants.XR_NULL_HANDLE && PrepareFadeLayer(timeoutNs))
                Marshal.WriteIntPtr(_pLayersForSubmit, count++ * IntPtr.Size, _pFadeLayer);

            if (_overlayVisible && _viewSpace != OpenXRConstants.XR_NULL_HANDLE && PrepareOverlayLayer(timeoutNs))
                Marshal.WriteIntPtr(_pLayersForSubmit, count++ * IntPtr.Size, _pOverlayLayer);

            frameEndInfo.layerCount = (uint)count;
            frameEndInfo.layers = count > 0 ? _pLayersForSubmit : IntPtr.Zero;
        }

        // IVrCameraSetup.RebindCameraRig の実装。rig 再バインドは SetupCameraRig と同等処理でよいため一行委譲。
        public void RebindCameraRig(Camera mainCamera) => SetupCameraRig(mainCamera);

        public void SetupCameraRig(Camera mainCamera)
        {
            if (mainCamera == null)
            {
                VRModCore.LogError("SetupCameraRig FAILED: mainCamera is null.");
                return;
            }
            VRModCore.LogRuntimeDebug($"SetupCameraRig for camera '{mainCamera.name}'.");

            if (_vrRig != null) TeardownCameraRig();

            _vrRig = new GameObject("UnityVRMod_XrRig");
            _currentlyTrackedOriginalCameraGO = mainCamera.gameObject;

            Vector3 targetPosition = mainCamera.transform.position;
            Quaternion targetRotation = Quaternion.Euler(0, mainCamera.transform.eulerAngles.y, 0);

            var poseOverrides = PoseParser.Parse(ConfigManager.ScenePoseOverrides.Value);
            string currentSceneName = mainCamera.gameObject.scene.name;

            if (poseOverrides.TryGetValue(currentSceneName, out PoseOverride poseOverride))
            {
                VRModCore.Log($"Applying pose override for scene '{currentSceneName}'");
                var p = poseOverride.Position;
                var r = poseOverride.Rotation;
                var originalPos = mainCamera.transform.position;
                var originalRot = mainCamera.transform.eulerAngles;

                Vector3 finalPos = new(float.IsNaN(p.x) ? originalPos.x : p.x, float.IsNaN(p.y) ? originalPos.y : p.y, float.IsNaN(p.z) ? originalPos.z : p.z);
                Vector3 finalRot = new(float.IsNaN(r.x) ? originalRot.x : r.x, float.IsNaN(r.y) ? originalRot.y : r.y, float.IsNaN(r.z) ? originalRot.z : r.z);
                targetPosition = finalPos;
                targetRotation = Quaternion.Euler(finalRot);
            }
            _vrRig.transform.SetPositionAndRotation(targetPosition, targetRotation);

            _mainCameraClearFlags = mainCamera.clearFlags;
            _mainCameraBackgroundColor = mainCamera.backgroundColor;
            _mainCameraCullingMask = mainCamera.cullingMask;

            _currentAppliedRigScale = 1.0f / Mathf.Max(0.01f, ConfigManager.VrWorldScale.Value);
            _vrRig.transform.localScale = new Vector3(_currentAppliedRigScale, _currentAppliedRigScale, _currentAppliedRigScale);

            _leftVrCameraGO = new GameObject("XrVrCamera_Left");
            _leftVrCameraGO.transform.SetParent(_vrRig.transform, false);
            _leftVrCamera = _leftVrCameraGO.AddComponent<Camera>();
            ConfigureVrCamera(_leftVrCamera, mainCamera, "Left");

            _rightVrCameraGO = new GameObject("XrVrCamera_Right");
            _rightVrCameraGO.transform.SetParent(_vrRig.transform, false);
            _rightVrCamera = _rightVrCameraGO.AddComponent<Camera>();
            ConfigureVrCamera(_rightVrCamera, mainCamera, "Right");

            _lastCalculatedVerticalOffset = 0f;
            UpdateVerticalOffset();

            VRModCore.Log("OpenXR: VR Camera Rig setup complete.");
        }

        private void ConfigureVrCamera(Camera vrCam, Camera mainCamRef, string eyeName)
        {
            VRModCore.LogRuntimeDebug($"Configuring VR Camera properties for: {eyeName}");
            // 組み込みレンダラでのみ stereoTargetEye を設定する。URP 等の SRP 下では本 API が非対応で
            // 「Camera.stereoTargetEye only with the built-in renderer」assertion を毎回出す。
            // 本カメラは enabled=false で手動描画されるため stereoTargetEye 設定は描画に無影響。
            if (UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline == null)
                vrCam.stereoTargetEye = StereoTargetEyeMask.None;
            vrCam.enabled = false;
            EnsureUrpCameraData(vrCam.gameObject);

            vrCam.clearFlags = _mainCameraClearFlags;
            vrCam.cullingMask = _mainCameraCullingMask;
            if (_mainCameraClearFlags == CameraClearFlags.SolidColor)
            {
                vrCam.backgroundColor = _mainCameraBackgroundColor;
            }

            float nearClipPlaneUserValue = ConfigManager.VrCameraNearClipPlane.Value;
            vrCam.nearClipPlane = Mathf.Max(0.001f, nearClipPlaneUserValue * _currentAppliedRigScale);

            float referenceFarClip = 1000f;
            if (mainCamRef != null)
            {
                referenceFarClip = mainCamRef.farClipPlane;
            }
            else if (_currentlyTrackedOriginalCameraGO != null)
            {
                if (_currentlyTrackedOriginalCameraGO.TryGetComponent<Camera>(out var trackedCam)) referenceFarClip = trackedCam.farClipPlane;
            }
            vrCam.farClipPlane = referenceFarClip * _currentAppliedRigScale;
        }

        // URP プロジェクトのゲームコードは「全カメラに UniversalAdditionalCameraData がある」前提で
        // カメラを列挙して .antialiasing 等へアクセスすることがある（例: GB.Scene.FirstScene.Start の
        // FindObjectsByType<Camera> ループ）。ランタイム生成の VR カメラには付かず NRE を誘発するため、
        // URP の遅延付与（GetUniversalAdditionalCameraData）と同じく明示的に AddComponent する。
        // URP 不在（built-in パイプラインのゲーム）では型が見つからず no-op＝移植性を保つ。
        private static void EnsureUrpCameraData(GameObject cameraGo)
        {
            System.Type t = ResolveAcdType();
            if (t == null) return;
            if (cameraGo.GetComponent(t) == null) cameraGo.AddComponent(t);
        }

        // URP の UniversalAdditionalCameraData 型と post-process プロパティのリフレクションキャッシュ。
        // fork は URP アセンブリを直参照しないため（移植性・参照を増やさない方針）型・プロパティは reflection 解決。
        private static System.Type _acdType;
        private static System.Reflection.PropertyInfo _acdRenderPostProcessing;
        private static System.Reflection.PropertyInfo _acdVolumeLayerMask;
        private static bool _acdReflectionResolved;

        private static System.Type ResolveAcdType()
        {
            if (_acdReflectionResolved) return _acdType;
            _acdReflectionResolved = true;
            _acdType = System.Type.GetType("UnityEngine.Rendering.Universal.UniversalAdditionalCameraData, Unity.RenderPipelines.Universal.Runtime");
            if (_acdType == null)
            {
                foreach (System.Reflection.Assembly asm in System.AppDomain.CurrentDomain.GetAssemblies())
                {
                    _acdType = asm.GetType("UnityEngine.Rendering.Universal.UniversalAdditionalCameraData");
                    if (_acdType != null) break;
                }
            }
            if (_acdType != null)
            {
                _acdRenderPostProcessing = _acdType.GetProperty("renderPostProcessing");
                _acdVolumeLayerMask = _acdType.GetProperty("volumeLayerMask");
            }
            return _acdType;
        }

        // eye カメラの URP post-process を override 状態に合わせて適用する（描画直前・両眼で呼ばれる）。
        // active 時: renderPostProcessing=true + volumeLayerMask=指定値（ゲームのグレーディング+Bloom を反映）。
        // 非 active 時: renderPostProcessing=false（VR eye は post-process なし＝従来動作）。
        // URP 不在（built-in パイプライン）/ プロパティ未解決時は no-op＝移植性を保つ。
        private void ApplyEyePostProcessOverride(Camera cam)
        {
            if (ResolveAcdType() == null || _acdRenderPostProcessing == null) return;
            var acd = cam.GetComponent(_acdType);
            if (acd == null) return;
            _acdRenderPostProcessing.SetValue(acd, _eyePpOverrideActive);
            if (_eyePpOverrideActive && _acdVolumeLayerMask != null)
                _acdVolumeLayerMask.SetValue(acd, (LayerMask)_eyePpVolumeMask);
        }

        // CommandBuffer.DrawRenderer 経由で描画可能な pass index を返す（URP context 不要な pass を優先選択）。
        // URP の UniversalForward pass は RenderPipeline context 内でしか描画されない（global lighting buffer 等が
        // CommandBuffer 経由では unset で fail＝Toon 手モデルが消える 2026-06-19）。
        // 既知 shader 名で hardcode 分岐＝fork のビルド参照 Unity dll に Shader.GetPassCountInSubshader/FindPassTagValue
        // が無いため runtime introspection は不可。新規 shader 追加時はここに entry を足す。
        //
        // UTS "Toon" の pass 構造（実機 bridge 採取 2026-06-19）:
        //   ss0: pass0=UniversalForward(URP only) / pass1=SRPDEFAULTUNLIT / pass2=SHADOWCASTER / pass3=DepthOnly / pass4=DepthNormals
        // → pass 1 (SRPDEFAULTUNLIT) を選ぶ＝SRP 共通 fallback で CommandBuffer 経由でも描画可能。
        // UI/Default / BG2VR/ControllerUnlit など single-pass shader は pass 0 (LightMode 空 or 規定パス)。
        private static int FindRenderablePass(Material mat)
        {
            string sn = mat.shader.name;
            if (sn == "Toon") return 1;
            return 0;
        }

        // post 済み intermediate RT へ overlay layer（VR ビジュアル）を post 無しで重ね描く。
        // rig サブツリーのみ列挙（全シーン非走査・非 alloc List 再利用）。本描画と同じ eye の view/proj を
        // CommandBuffer に渡す（projectionMatrix は反転込み＝本描画と同一 GPU 行列）。呼び出し側が
        // GL.invertCulling=true 区間で呼ぶ前提（winding 一致）。post override + overlay 指定 + rig 生存が条件。
        private void DrawEyeOverlay(Camera eyeCam, RenderTexture target)
        {
            // PostProcess 由来 overlay（UI 等）と VR モデル overlay（手等）の和集合を effective として扱う。
            // PostProcess 由来は active 時のみ有効。VR モデルは常時有効（HandLightingRunner が push し続けるとき）。
            int effectiveOverlayMask = (_eyePpOverrideActive ? _eyeOverlayLayerMask : 0) | _vrModelOverlayMask;
            if (effectiveOverlayMask == 0 || _vrRig == null || target == null || eyeCam == null) return;
            _vrRig.transform.GetComponentsInChildren(false, _overlayRenderers); // includeInactive:false・非 alloc
            if (_overlayRenderers.Count == 0) return;

            // CommandBuffer.DrawRenderer は renderQueue を無視し「後に描いた方が前面」になる（ZWrite Off）。
            // BG2VR の renderQueue 契約（UiOverlayRenderPolicy: パネル<ボタン<ポインタ<設定<設定レーザー）を
            // honor するため描画前に renderQueue 昇順で安定ソートする。生成タイミング非依存（render 時に毎フレ
            // 確定）＝遷移で sibling が入れ替わっても前面性が壊れない。null material は最背面扱い。
            // ラムダは非キャプチャ（r=引数・int.MinValue=定数）＝コンパイラが static デリゲートにキャッシュし
            // 毎フレ alloc しない。ローカル変数をキャプチャするよう変更しないこと（静かに毎フレ alloc 化する）。
            UnityVRMod.Core.OverlayDrawOrder.StableSortByKey(_overlayRenderers,
                r => (r != null && r.sharedMaterial != null) ? r.sharedMaterial.renderQueue : int.MinValue);

            if (_overlayCmd == null) _overlayCmd = new CommandBuffer { name = "BG2VR_EyeOverlay" };
            _overlayCmd.Clear();
            _overlayCmd.SetRenderTarget(target);
            // renderIntoTexture:false が正（実機実証 2026-06-14）。eyeCam.projectionMatrix は既に Scale(1,-1,1)
            // 反転込み（本描画 line 718-719）で、本描画 Camera.Render はこの行列をそのまま使う（追加の RT flip 無し）。
            // ここで true を渡すと RT flip が二重に掛かり overlay が上下反転する（ground-truth＝カメラ自描画と比較で確定）。
            _overlayCmd.SetViewProjectionMatrices(eyeCam.worldToCameraMatrix, GL.GetGPUProjectionMatrix(eyeCam.projectionMatrix, false));

            // 選択的深度プリパス（VrControllerOccludeUi）: UI 描画の直前に「コントローラだけが UI を遮る」深度を作る。
            // main Camera.Render 時点で RT 深度には scene と controller(29) が混在している。そのまま UI を LEqual で
            // 描くと scene も UI を遮る（＝不採用のオプション①）。そこで一旦深度のみ far へ消し（color は load＝post 済み
            // のまま温存）、occluder layer（29＝コントローラ）だけを depthMat（ColorMask 0・ZWrite On）で描き直す
            // → 遮蔽源をコントローラのみに限定する。UI 本描画（layer 30）の ZTest は BG2VR が LEqual/Always を設定。
            if (_eyeOverlayOccluderMask != 0 && _eyeOverlayOccluderMat != null)
            {
                // 深度のみ far へ clear（clearColor:false＝post 済み color は load されて不変）。
                // far の clear 値は depth buffer の向き依存: reversed-Z（D3D 等）は far=0、非 reversed は far=1。
                // ClearRenderTarget は値をそのまま書く（reversed 補正しない）ため明示的に分ける。
                // RTClearFlags 版はこの fork の UnityEngine 参照に無い＝普遍的な bool overload を使う。
                float farDepth = UnityEngine.SystemInfo.usesReversedZBuffer ? 0f : 1f;
                _overlayCmd.ClearRenderTarget(true, false, Color.clear, farDepth);
                for (int i = 0; i < _overlayRenderers.Count; i++)
                {
                    Renderer r = _overlayRenderers[i];
                    if (r == null || !r.enabled) continue;
                    if (((1 << r.gameObject.layer) & _eyeOverlayOccluderMask) == 0) continue;
                    int subs = r.sharedMaterials.Length; // submesh 数ぶん深度を描く（material は専用 depthMat 固定）
                    for (int sm = 0; sm < subs; sm++)
                        _overlayCmd.DrawRenderer(r, _eyeOverlayOccluderMat, sm, -1);
                }
            }

            for (int i = 0; i < _overlayRenderers.Count; i++)
            {
                Renderer r = _overlayRenderers[i];
                if (r == null || !r.enabled) continue;
                if (((1 << r.gameObject.layer) & effectiveOverlayMask) == 0) continue; // = !PostProcessPolicy.IsLayerInMask（手書き複製・PostProcess + VR モデル overlay の和集合）
                Material[] mats = r.sharedMaterials;
                for (int sm = 0; sm < mats.Length; sm++)
                {
                    Material mat = mats[sm];
                    if (mat == null || mat.shader == null) continue;
                    // shaderPass 選択: LightMode=UniversalForward は URP の RenderPipeline context 内でしか描画されない
                    // （CommandBuffer 経由の overlay では URP global state 不在で fail＝Toon 手モデルが消える 2026-06-19）。
                    // SRPDEFAULTUNLIT（SRP 共通 fallback）か LightMode 空（Built-in 互換）の pass を優先する。
                    int pass = FindRenderablePass(mat);
                    if (pass >= 0) _overlayCmd.DrawRenderer(r, mat, sm, pass);
                }
            }
            Graphics.ExecuteCommandBuffer(_overlayCmd);
        }

        public void TeardownCameraRig()
        {
            VRModCore.LogRuntimeDebug("Tearing down VR camera rig.");
            if (_leftEyeIntermediateRT != null) { _leftEyeIntermediateRT.Release(); UnityEngine.Object.Destroy(_leftEyeIntermediateRT); _leftEyeIntermediateRT = null; }
            if (_rightEyeIntermediateRT != null) { _rightEyeIntermediateRT.Release(); UnityEngine.Object.Destroy(_rightEyeIntermediateRT); _rightEyeIntermediateRT = null; }
            if (_vrRig != null) { UnityEngine.Object.Destroy(_vrRig); _vrRig = null; }
            _leftVrCameraGO = null; _leftVrCamera = null; _rightVrCameraGO = null; _rightVrCamera = null;
            _currentlyTrackedOriginalCameraGO = null;
            _eyeCullOverrideActive = false; // 新 rig は game-copy 既定で開始（coordinator が次フレ再 push）。
            _eyePpOverrideActive = false;   // 新 rig は post-process 無効で開始（coordinator が次フレ再 push）。
            _eyeOverlayLayerMask = 0;       // 同上（overlay 除外も無効で開始）。
            _eyeOverlayOccluderMask = 0;    // 同上（コントローラ遮蔽も無効で開始）。material は companion 所有＝ここでは参照を捨てるのみ。
            _eyeOverlayOccluderMat = null;
            _vrModelOverlayMask = 0;        // VR モデル overlay も無効で開始（HandLightingRunner が次フレ再 push）。
            _sceneTransparentRedraw = null;
        }

        public void TeardownVr()
        {
            VRModCore.LogRuntimeDebug("Tearing down OpenXR system.");
            TeardownCameraRig();
            _input.Teardown();

            if (_pProjectionLayerViews != IntPtr.Zero) { Marshal.FreeHGlobal(_pProjectionLayerViews); _pProjectionLayerViews = IntPtr.Zero; }
            if (_pProjectionLayer != IntPtr.Zero) { Marshal.FreeHGlobal(_pProjectionLayer); _pProjectionLayer = IntPtr.Zero; }
            if (_pLayersForSubmit != IntPtr.Zero) { Marshal.FreeHGlobal(_pLayersForSubmit); _pLayersForSubmit = IntPtr.Zero; }

            // fade/overlay quad リソース（session 寿命）。
            _fadeSwapchain?.Destroy(); _fadeSwapchain = null;
            _overlaySwapchain?.Destroy(); _overlaySwapchain = null;
            if (_viewSpace != OpenXRConstants.XR_NULL_HANDLE && OpenXRAPI.xrDestroySpace != null)
                OpenXRAPI.xrDestroySpace(_viewSpace);
            _viewSpace = OpenXRConstants.XR_NULL_HANDLE;
            if (_fadeRT != null) { _fadeRT.Release(); UnityEngine.Object.Destroy(_fadeRT); _fadeRT = null; }
            if (_overlayRT != null) { _overlayRT.Release(); UnityEngine.Object.Destroy(_overlayRT); _overlayRT = null; }
            if (_overlayExternalTex != null) { UnityEngine.Object.Destroy(_overlayExternalTex); _overlayExternalTex = null; }
            if (_overlayCmd != null) { _overlayCmd.Release(); _overlayCmd = null; } // eye overlay 用 CommandBuffer（rig 非依存・session 寿命）。
            if (_pFadeLayer != IntPtr.Zero) { Marshal.FreeHGlobal(_pFadeLayer); _pFadeLayer = IntPtr.Zero; }
            if (_pOverlayLayer != IntPtr.Zero) { Marshal.FreeHGlobal(_pOverlayLayer); _pOverlayLayer = IntPtr.Zero; }
            _fadeActive = false; _overlayVisible = false;

            if (_appSpace != OpenXRConstants.XR_NULL_HANDLE && OpenXRAPI.xrDestroySpace != null)
                OpenXRAPI.xrDestroySpace(_appSpace);

            if (_eyeSwapchainSRVs != null)
            {
                foreach (var srvList in _eyeSwapchainSRVs)
                    foreach (var srv in srvList)
                        if (srv != IntPtr.Zero) NativeBridge.ReleaseNativeObject_Internal(srv);
            }

            // D3D12: 破棄予定 image を参照する copy が render thread に残らないよう drain（有限待機）
            if (_useD3D12 && _renderEventFunc != IntPtr.Zero && _lastCopyTicketIssued > 0)
            {
                GL.IssuePluginEvent(_renderEventFunc, NativeBridge.kEventExecutePendingCopies);
                if (!NativeBridge.WaitForCopyTicket(_lastCopyTicketIssued, ConfigManager.OpenXR_D3D12SubmitWaitMs?.Value ?? 50))
                    VRModCore.LogWarning("OpenXR/D3D12: cleanup drain timeout（未 submit copy が残った可能性）。");
            }
            // drain が timeout しても（render thread 遅延/wedge）、未処理 copy を完了扱いに倒して
            // 破棄予定 image への use-after-free を防ぐ安全網（残存 race=既に処理中の event のみ）。
            if (_useD3D12) NativeBridge.CancelPendingCopiesD3D12_Internal();

            if (_eyeSwapchains.Count > 0)
            {
                foreach (ulong sc in _eyeSwapchains)
                    if (sc != OpenXRConstants.XR_NULL_HANDLE && OpenXRAPI.xrDestroySwapchain != null)
                        OpenXRAPI.xrDestroySwapchain(sc);
            }

            // swapchain 破棄で持ち越し状態が無効になるためリセット（次セッションは acquire からやり直す）。
            for (int i = 0; i < 2; i++) { _eyeAcquired[i] = false; _eyeReady[i] = false; }

            if (_xrSession != OpenXRConstants.XR_NULL_HANDLE && OpenXRAPI.xrDestroySession != null)
                OpenXRAPI.xrDestroySession(_xrSession);

            if (_xrInstance != OpenXRConstants.XR_NULL_HANDLE && OpenXRAPI.xrDestroyInstance != null)
                OpenXRAPI.xrDestroyInstance(_xrInstance);

            _xrInstance = OpenXRConstants.XR_NULL_HANDLE;
            _xrSession = OpenXRConstants.XR_NULL_HANDLE;
            _appSpace = OpenXRConstants.XR_NULL_HANDLE;

            OpenXRNativeLoader.FreeOpenXRLibrary();
            IsVrAvailable = false;

            if (_activeInstance == this) _activeInstance = null;
            _lastCopyTicketIssued = 0;
        }

        private static Matrix4x4 CreateProjectionMatrixFromFovUsingFrustum(XrFovf fov, float nearClip, float farClip)
        {
            float nPR = Mathf.Tan(fov.angleRight) * nearClip;
            float nPL = Mathf.Tan(fov.angleLeft) * nearClip;
            float nPT = Mathf.Tan(fov.angleUp) * nearClip;
            float nPB = Mathf.Tan(fov.angleDown) * nearClip;

            Matrix4x4 m = new();
            m[0, 0] = (2.0f * nearClip) / (nPR - nPL);
            m[0, 2] = (nPR + nPL) / (nPR - nPL);
            m[1, 1] = (2.0f * nearClip) / (nPT - nPB);
            m[1, 2] = (nPT + nPB) / (nPT - nPB);
            m[2, 2] = -(farClip + nearClip) / (farClip - nearClip);
            m[2, 3] = -(2.0f * farClip * nearClip) / (farClip - nearClip);
            m[3, 2] = -1.0f;
            return m;
        }

        public void SetCameraNearClip(float newNearClipBaseValue)
        {
            if (_vrRig == null) return;
            Camera mainCamera = null;
            if (_currentlyTrackedOriginalCameraGO != null)
                mainCamera = _currentlyTrackedOriginalCameraGO.GetComponent<Camera>();

            if (_leftVrCamera != null) ConfigureVrCamera(_leftVrCamera, mainCamera, "Left");
            if (_rightVrCamera != null) ConfigureVrCamera(_rightVrCamera, mainCamera, "Right");
        }

        private void UpdateVerticalOffset()
        {
            if (_vrRig == null) return;
            float newTotalOffset = ConfigManager.VrUserEyeHeightOffset.Value * _currentAppliedRigScale;
            float delta = newTotalOffset - _lastCalculatedVerticalOffset;
            _vrRig.transform.position += new Vector3(0, delta, 0);
            _lastCalculatedVerticalOffset = newTotalOffset;
        }

        public void SetWorldScale(float newWorldScale, Camera mainCameraRef)
        {
            if (_vrRig == null) return;
            _currentAppliedRigScale = 1.0f / Mathf.Max(0.01f, newWorldScale);
            _vrRig.transform.localScale = new Vector3(_currentAppliedRigScale, _currentAppliedRigScale, _currentAppliedRigScale);

            Camera mainCam = mainCameraRef;
            if (mainCam == null && _currentlyTrackedOriginalCameraGO != null)
                mainCam = _currentlyTrackedOriginalCameraGO.GetComponent<Camera>();

            if (_leftVrCamera != null) ConfigureVrCamera(_leftVrCamera, mainCam, "Left");
            if (_rightVrCamera != null) ConfigureVrCamera(_rightVrCamera, mainCam, "Right");

            UpdateVerticalOffset();
        }

        public void SetUserEyeHeightOffset(float newOffset)
        {
            UpdateVerticalOffset();
        }

        public VrCameraRig GetVrCameraGameObjects()
        {
            return new VrCameraRig { LeftEye = _leftVrCameraGO, RightEye = _rightVrCameraGO };
        }

        public bool TryGetControllerSnapshot(UnityVRMod.Core.VrHand hand, out UnityVRMod.Core.VrControllerSnapshot snapshot)
            => _input.TryGet(hand, out snapshot);

        public void TriggerHaptic(UnityVRMod.Core.VrHand hand, float amplitude, float durationSec)
            => _input.ApplyHaptic(hand, amplitude, durationSec);

        public bool SetCompositorFade(float r, float g, float b, float a)
        {
            if (!IsVrAvailable || _viewSpace == OpenXRConstants.XR_NULL_HANDLE || _fadeSwapchain == null) return false;
            _fadeColor = new UnityEngine.Color(r, g, b, a);
            _fadeActive = UnityVRMod.Core.OverlayQuadMath.IsFadeActive(a);
            return true; // 実描画は PumpFrame で。状態を受理できた＝true。
        }

        // eye の cullingMask/clearFlags override（companion の EyeCullingCoordinator が毎フレ push）。
        // 実適用は RenderEye が描画直前に行う。active=false で game-copy(_mainCamera*) へ戻る。
        public void SetEyeCullingOverride(bool active, int cullingMask, CameraClearFlags clearFlags, Color backgroundColor)
        {
            _eyeCullOverrideActive = active;
            _eyeCullOverrideMask = cullingMask;
            _eyeCullOverrideClear = clearFlags;
            _eyeCullOverrideBg = backgroundColor;
        }

        // eye の URP post-process override（companion の PostProcessCoordinator が毎フレ push）。
        // 実適用は RenderEye が描画直前に行う。active=false で renderPostProcessing=false（VR eye 既定）へ戻る。
        public void SetEyePostProcessOverride(bool active, int volumeLayerMask, int overlayLayerMask)
        {
            _eyePpOverrideActive = active;
            _eyePpVolumeMask = volumeLayerMask;
            _eyeOverlayLayerMask = active ? overlayLayerMask : 0;
        }

        // 選択的深度（コントローラ遮蔽）の遮蔽源を push（companion の PostProcessCoordinator が毎フレ）。
        // 実適用は DrawEyeOverlay（UI 描画の直前）。occluderMask=0 / depthMaterial=null で無効＝深度カーブなし。
        // 前提: overlay 機構（SetEyePostProcessOverride active かつ overlay layer 非ゼロ）が動いていること。
        // それ無しでは DrawEyeOverlay 自体が冒頭 return する＝occluder だけ push しても無効（呼び出し側で整合させる）。
        public void SetEyeOverlayOccluder(int occluderMask, Material depthMaterial)
        {
            _eyeOverlayOccluderMask = occluderMask;
            _eyeOverlayOccluderMat = depthMaterial;
        }

        // VR モデル overlay layer（companion の HandLightingRunner が毎フレ push）。
        // PostProcess channel と独立＝PostProcess OFF でも常時 overlay 描画される（UI と同じ最前面扱い）。
        // mask=0 で無効。実適用は DrawEyeOverlay と RenderEye の eye cullingMask 除外計算（effective overlayMask）。
        public void SetVrModelOverlay(int mask)
        {
            _vrModelOverlayMask = mask;
        }

        public void SetSceneTransparentRedraw(System.Action<Camera, RenderTexture> callback)
        {
            _sceneTransparentRedraw = callback;
        }

        public bool SetTransitionOverlayTexture(System.IntPtr nativeTex, int srcWidth, int srcHeight, float uMin, float vMin, float uMax, float vMax)
        {
            if (!IsVrAvailable || _viewSpace == OpenXRConstants.XR_NULL_HANDLE || _overlaySwapchain == null) return false;
            if (nativeTex == System.IntPtr.Zero || srcWidth <= 0 || srcHeight <= 0) return false;
            _overlaySrcPtr = nativeTex;
            _overlaySrcW = srcWidth; _overlaySrcH = srcHeight;
            _overlayUMin = uMin; _overlayVMin = vMin; _overlayUMax = uMax; _overlayVMax = vMax;
            return true; // 実 Blit は PumpFrame で。
        }

        public bool SetTransitionOverlayState(bool visible, float alpha, float widthMeters, float distanceMeters, bool worldLock)
        {
            if (!IsVrAvailable || _viewSpace == OpenXRConstants.XR_NULL_HANDLE || _overlaySwapchain == null) return false;
            _overlayVisible = visible;
            _overlayAlpha = alpha;
            _overlayWidthM = widthMeters;
            _overlayDistanceM = distanceMeters;
            _overlayWorldLock = worldLock;
            return true; // submit は PumpFrame で（visible のときのみ append）。anchor 凍結は MaybeSnapshotOverlayAnchor。
        }

        // 遷移 teardown / カメラ未解決中（rig 不在・session 生存）の compositor keepalive。
        // PumpFrame を 1 回回す＝rig 不在なら未描画 swapchain（D3D11 では直前数フレームの内容が残存）を
        // head-tracked pose で submit する。実コンテンツ提示で Oculus が FOCUSED を維持し、
        // 空フレームで起きた FOCUSED→STOPPING バウンス（検死 2026-06-09 gate#2）を防ぐ。
        // submit が途切れると runtime がアプリを timed-out 扱いにし HMD が固着する（gate#1）。
        // 戻り値 = フレームを実際に submit できたか（submit 成否）。
        public bool SubmitTransitionKeepalive()
        {
            if (!(ConfigManager.OpenXR_TransitionKeepalive?.Value ?? true)) return false;
            // _isSessionRunning はチェックしない: session 状態の処理（event poll）が PumpFrame 内にあるため、
            // ここで弾くと poll が止まる。重い遷移で runtime が session を STOPPING→IDLE に落とした後、
            // keepalive 経路でこのガードに弾かれると READY イベントを拾えず xrBeginSession で再開できず、
            // IDLE から永久に復帰できない（検死 2026-06-09 gate#1）。PumpFrame は非 running 時 event poll 後に
            // :341 で安全に bail する＝呼ぶだけで session 再開を試みる。
            if (!IsVrAvailable || _xrSession == OpenXRConstants.XR_NULL_HANDLE) return false;
            return PumpFrame();   // 直前フレーム hold（PumpFrame が event poll で session も復帰させる）
        }

    }
}