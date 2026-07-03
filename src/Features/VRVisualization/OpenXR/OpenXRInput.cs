using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityVRMod.Core;

namespace UnityVRMod.Features.VRVisualization.OpenXR
{
    /// <summary>OpenXR action system（Oculus Touch profile）。コントローラ snapshot を供給する。
    /// pose は eye と同じ _appSpace 基準で xrLocateSpace → RH→LH 変換し rig-local 化する。
    /// action は subaction path（左右）方式・A/B のみ per-hand に別 component を bind する。
    /// 失敗（古い loader / profile 非対応 runtime 等）は入力無効として扱い、描画は継続する。</summary>
    internal sealed class OpenXRInput
    {
        private ulong _instance, _session, _appSpace;
        private ulong _actionSet;
        private ulong _pathLeft, _pathRight;
        // actions
        private ulong _aTrigger, _aSqueeze, _aPrimary, _aSecondary, _aStick, _aStickClick, _aPose, _aVibrate;
        private ulong _aMenu; // 左メニューボタン（≡）。Touch profile は左のみ menu/click を公開
        private ulong _spaceLeft, _spaceRight; // pose action space（左右）
        private bool _heldLeft, _heldRight;    // trigger hysteresis 状態
        private bool _ready;
        private bool _synced;                   // 初回 Sync 完了フラグ（pose を引けるか・値依存センチネルを避ける）
        private long _lastDisplayTime;          // 直近 Sync の predictedDisplayTime（pose 取得に使う）
        // Touch squeeze（アナログ）→ Grip bool のしきい値。OpenVR は物理ボタンのため不要だった体感依存定数。
        // CLAUDE.md「実機で調整が必要な定数は最初から Configs 化する」に該当するが、squeeze はほぼ二値で
        // 0.5 が妥当な公算が高いため Phase1 は const + 実機確認に留める。NG なら Phase2 で ConfigManager 化。
        private const float GripThreshold = 0.5f;

        /// <summary>recenter で _appSpace を作り直した際に、入力側 cache を新ハンドルへ再ポイントする。</summary>
        public void SetAppSpace(ulong appSpace) => _appSpace = appSpace;

        public bool Setup(ulong instance, ulong session, ulong appSpace)
        {
            _instance = instance; _session = session; _appSpace = appSpace;
            try
            {
                if (_ready) Teardown(); // Teardown を挟まない再 Setup でも前回ハンドルをリークさせない（冪等化）
                if (OpenXRAPI.xrCreateActionSet == null) { VRModCore.LogWarning("OpenXRInput: input 関数が未ロード（古い loader?）。入力を無効化。"); return false; }

                Str2Path("/user/hand/left", out _pathLeft);
                Str2Path("/user/hand/right", out _pathRight);

                var setInfo = new XrActionSetCreateInfo { type = XrStructureType.XR_TYPE_ACTION_SET_CREATE_INFO, actionSetName = "vrmod_input", localizedActionSetName = "VRMod Input", priority = 0 };
                OpenXRHelper.CheckResult(OpenXRAPI.xrCreateActionSet(_instance, in setInfo, out _actionSet), "xrCreateActionSet");

                _aTrigger    = CreateAction("trigger", "Trigger", XrActionType.XR_ACTION_TYPE_FLOAT_INPUT);
                _aSqueeze    = CreateAction("squeeze", "Squeeze", XrActionType.XR_ACTION_TYPE_FLOAT_INPUT);
                _aPrimary    = CreateAction("primary", "Primary Button (A/X)", XrActionType.XR_ACTION_TYPE_BOOLEAN_INPUT);
                _aSecondary  = CreateAction("secondary", "Secondary Button (B/Y)", XrActionType.XR_ACTION_TYPE_BOOLEAN_INPUT);
                _aStick      = CreateAction("stick", "Thumbstick", XrActionType.XR_ACTION_TYPE_VECTOR2F_INPUT);
                _aStickClick = CreateAction("stick_click", "Thumbstick Click", XrActionType.XR_ACTION_TYPE_BOOLEAN_INPUT);
                _aMenu       = CreateAction("menu", "Menu Button", XrActionType.XR_ACTION_TYPE_BOOLEAN_INPUT);
                _aPose       = CreateAction("hand_pose", "Hand Pose", XrActionType.XR_ACTION_TYPE_POSE_INPUT);
                _aVibrate    = CreateAction("vibrate", "Vibration", XrActionType.XR_ACTION_TYPE_VIBRATION_OUTPUT);

                // OpenXR 順序制約（厳守・入替禁止）:
                //   ① xrSuggestInteractionProfileBindings は attach より前（attach 後は binding 変更不可）
                //   ② xrAttachSessionActionSets は session ごとに 1 回（再 attach 不可）
                //   ③ action state query / xrCreateActionSpace は attach より後
                if (!SuggestTouchBindings()) return false;

                // attach（session 生成後 1 回）
                ulong[] sets = { _actionSet };
                IntPtr pSets = Marshal.AllocHGlobal(sizeof(ulong) * sets.Length);
                try
                {
                    Marshal.Copy(BitConverterToLong(sets), 0, pSets, sets.Length);
                    var attach = new XrSessionActionSetsAttachInfo { type = XrStructureType.XR_TYPE_SESSION_ACTION_SETS_ATTACH_INFO, countActionSets = (uint)sets.Length, actionSets = pSets };
                    OpenXRHelper.CheckResult(OpenXRAPI.xrAttachSessionActionSets(_session, in attach), "xrAttachSessionActionSets");
                }
                finally { Marshal.FreeHGlobal(pSets); }

                _spaceLeft = CreatePoseSpace(_pathLeft);
                _spaceRight = CreatePoseSpace(_pathRight);

                _ready = true;
                VRModCore.Log("OpenXRInput: action system 初期化完了（Oculus Touch profile）。");
                return true;
            }
            catch (Exception ex) { VRModCore.LogError("OpenXRInput: Setup 失敗。入力を無効化。", ex); Teardown(); return false; }
        }

        private void Str2Path(string s, out ulong path)
            => OpenXRHelper.CheckResult(OpenXRAPI.xrStringToPath(_instance, s, out path), "xrStringToPath:" + s);

        private static long[] BitConverterToLong(ulong[] a) => Array.ConvertAll(a, x => unchecked((long)x));

        private ulong CreateAction(string name, string localized, XrActionType type)
        {
            // 全 action に subaction paths {left,right} を付ける（query を per-hand で分離するため）。
            ulong[] subs = { _pathLeft, _pathRight };
            IntPtr pSubs = Marshal.AllocHGlobal(sizeof(ulong) * subs.Length);
            try
            {
                Marshal.Copy(BitConverterToLong(subs), 0, pSubs, subs.Length);
                var info = new XrActionCreateInfo { type = XrStructureType.XR_TYPE_ACTION_CREATE_INFO, actionName = name, actionType = type, countSubactionPaths = (uint)subs.Length, subactionPaths = pSubs, localizedActionName = localized };
                OpenXRHelper.CheckResult(OpenXRAPI.xrCreateAction(_actionSet, in info, out ulong action), "xrCreateAction:" + name);
                return action;
            }
            finally { Marshal.FreeHGlobal(pSubs); }
        }

        private bool SuggestTouchBindings()
        {
            Str2Path("/interaction_profiles/oculus/touch_controller", out ulong profile);
            var binds = new List<XrActionSuggestedBinding>
            {
                Bind(_aTrigger,    "/user/hand/left/input/trigger/value"),
                Bind(_aTrigger,    "/user/hand/right/input/trigger/value"),
                Bind(_aSqueeze,    "/user/hand/left/input/squeeze/value"),
                Bind(_aSqueeze,    "/user/hand/right/input/squeeze/value"),
                Bind(_aStick,      "/user/hand/left/input/thumbstick"),
                Bind(_aStick,      "/user/hand/right/input/thumbstick"),
                Bind(_aStickClick, "/user/hand/left/input/thumbstick/click"),
                Bind(_aStickClick, "/user/hand/right/input/thumbstick/click"),
                Bind(_aPrimary,    "/user/hand/left/input/x/click"),
                Bind(_aPrimary,    "/user/hand/right/input/a/click"),
                Bind(_aSecondary,  "/user/hand/left/input/y/click"),
                Bind(_aSecondary,  "/user/hand/right/input/b/click"),
                // メニューボタンは左のみ（Touch profile は右の system ボタンを公開しない）。
                Bind(_aMenu,       "/user/hand/left/input/menu/click"),
                Bind(_aPose,       "/user/hand/left/input/grip/pose"),
                Bind(_aPose,       "/user/hand/right/input/grip/pose"),
                Bind(_aVibrate,    "/user/hand/left/output/haptic"),
                Bind(_aVibrate,    "/user/hand/right/output/haptic"),
            };
            XrActionSuggestedBinding[] arr = binds.ToArray();
            int stride = Marshal.SizeOf<XrActionSuggestedBinding>();
            IntPtr pArr = Marshal.AllocHGlobal(stride * arr.Length);
            try
            {
                for (int i = 0; i < arr.Length; i++) Marshal.StructureToPtr(arr[i], pArr + i * stride, false);
                var sb = new XrInteractionProfileSuggestedBinding { type = XrStructureType.XR_TYPE_INTERACTION_PROFILE_SUGGESTED_BINDING, interactionProfile = profile, countSuggestedBindings = (uint)arr.Length, suggestedBindings = pArr };
                // 失敗（profile 非対応 runtime 等）は致命でなく入力無効として扱う。
                return OpenXRHelper.CheckResultAndLog(OpenXRAPI.xrSuggestInteractionProfileBindings(_instance, in sb), "xrSuggestInteractionProfileBindings", "Touch profile", false);
            }
            finally { Marshal.FreeHGlobal(pArr); }
        }

        private XrActionSuggestedBinding Bind(ulong action, string path)
        {
            Str2Path(path, out ulong p);
            return new XrActionSuggestedBinding { action = action, binding = p };
        }

        private ulong CreatePoseSpace(ulong subactionPath)
        {
            var info = new XrActionSpaceCreateInfo { type = XrStructureType.XR_TYPE_ACTION_SPACE_CREATE_INFO, action = _aPose, subactionPath = subactionPath, poseInActionSpace = new XrPosef { orientation = new XrQuaternionf { w = 1f } } };
            OpenXRHelper.CheckResult(OpenXRAPI.xrCreateActionSpace(_session, in info, out ulong space), "xrCreateActionSpace");
            return space;
        }

        public void Sync(long predictedDisplayTime)
        {
            if (!_ready) return;
            var active = new XrActiveActionSet { actionSet = _actionSet, subactionPath = OpenXRConstants.XR_NULL_PATH };
            IntPtr pActive = Marshal.AllocHGlobal(Marshal.SizeOf<XrActiveActionSet>());
            try
            {
                Marshal.StructureToPtr(active, pActive, false);
                var sync = new XrActionsSyncInfo { type = XrStructureType.XR_TYPE_ACTIONS_SYNC_INFO, countActiveActionSets = 1, activeActionSets = pActive };
                // SESSION_NOT_FOCUSED（8・正値）は非フォーカス中の正常応答 → 無視。
                // 負値（SESSION_LOST 等）と SESSION_LOSS_PENDING（正値だが session 喪失の予兆）はログ
                //（入力不達時の切り分け用・戻り値を捨てない）。
                XrResult sr = OpenXRAPI.xrSyncActions(_session, in sync);
                if (sr < 0 || sr == XrResult.XR_SESSION_LOSS_PENDING) VRModCore.LogRuntimeDebug($"OpenXRInput: xrSyncActions returned {sr}");
            }
            finally { Marshal.FreeHGlobal(pActive); }
            _lastDisplayTime = predictedDisplayTime;
            _synced = true;
        }

        public bool TryGet(VrHand hand, out VrControllerSnapshot snap)
        {
            snap = default;
            if (!_ready) return false;
            if (!_synced) return false; // 初回 Sync 前は predictedDisplayTime 未確定＝pose を引けない
            ulong sub = (hand == VrHand.Left) ? _pathLeft : _pathRight;
            ulong space = (hand == VrHand.Left) ? _spaceLeft : _spaceRight;

            // pose（rig-local）+ velocity（XrSpaceVelocity を next チェーン＝同一呼び出しで取得）。
            var vel = new XrSpaceVelocity { type = XrStructureType.XR_TYPE_SPACE_VELOCITY };
            IntPtr pVel = Marshal.AllocHGlobal(Marshal.SizeOf<XrSpaceVelocity>());
            try
            {
                Marshal.StructureToPtr(vel, pVel, false);
                var loc = new XrSpaceLocation { type = XrStructureType.XR_TYPE_SPACE_LOCATION, next = pVel };
                if (OpenXRAPI.xrLocateSpace(space, _appSpace, _lastDisplayTime, ref loc) < 0) return false;
                bool posValid = (loc.locationFlags & XrSpaceLocationFlags.XR_SPACE_LOCATION_POSITION_VALID_BIT) != 0;
                bool oriValid = (loc.locationFlags & XrSpaceLocationFlags.XR_SPACE_LOCATION_ORIENTATION_VALID_BIT) != 0;
                if (!posValid || !oriValid) return false;

                // velocity 読み出し（非対応ランタイムは velocityFlags=0 → VelocityValid=false で pose のみ供給）。
                Vector3 linVel = Vector3.zero, angVel = Vector3.zero;
                bool velValid = false;
                vel = Marshal.PtrToStructure<XrSpaceVelocity>(pVel);
                if ((vel.velocityFlags & XrSpaceVelocityFlags.XR_SPACE_VELOCITY_LINEAR_VALID_BIT) != 0)
                {
                    // 線速度: pose と同じ RH→LH（z 反転）。角速度: magnitude のみ使うため軸 handedness 不問。
                    linVel = OpenXrMath.ToUnityPosition(vel.linearVelocity.x, vel.linearVelocity.y, vel.linearVelocity.z);
                    angVel = OpenXrMath.ToUnityPosition(vel.angularVelocity.x, vel.angularVelocity.y, vel.angularVelocity.z);
                    velValid = true;
                }

                float trigger = GetFloat(_aTrigger, sub);
                float squeeze = GetFloat(_aSqueeze, sub);
                bool wasHeld = (hand == VrHand.Left) ? _heldLeft : _heldRight;
                bool held = TriggerHysteresis.Update(wasHeld, trigger, btn: false); // Touch は trigger click 無し
                if (hand == VrHand.Left) _heldLeft = held; else _heldRight = held;

                snap = new VrControllerSnapshot
                {
                    Valid = true,
                    RigLocalPosition = OpenXrMath.ToUnityPosition(loc.pose.position.x, loc.pose.position.y, loc.pose.position.z),
                    RigLocalRotation = OpenXrMath.ToUnityRotation(loc.pose.orientation.x, loc.pose.orientation.y, loc.pose.orientation.z, loc.pose.orientation.w),
                    Trigger = held,
                    TriggerValue = trigger,
                    Grip = squeeze >= GripThreshold,
                    GripValue = squeeze,
                    A = GetBool(_aPrimary, sub),
                    B = GetBool(_aSecondary, sub),
                    Menu = GetBool(_aMenu, sub), // 左のみ bind＝右手 query は isActive=0 → false
                    Stick = GetVec2(_aStick, sub),
                    StickClick = GetBool(_aStickClick, sub),
                    LinearVelocity = linVel,
                    AngularVelocity = angVel,
                    VelocityValid = velValid,
                };
                return true;
            }
            finally { Marshal.FreeHGlobal(pVel); }
        }

        private float GetFloat(ulong action, ulong sub)
        {
            var gi = new XrActionStateGetInfo { type = XrStructureType.XR_TYPE_ACTION_STATE_GET_INFO, action = action, subactionPath = sub };
            var st = new XrActionStateFloat { type = XrStructureType.XR_TYPE_ACTION_STATE_FLOAT };
            if (OpenXRAPI.xrGetActionStateFloat(_session, in gi, ref st) < 0 || st.isActive == 0) return 0f;
            return st.currentState;
        }

        private bool GetBool(ulong action, ulong sub)
        {
            var gi = new XrActionStateGetInfo { type = XrStructureType.XR_TYPE_ACTION_STATE_GET_INFO, action = action, subactionPath = sub };
            var st = new XrActionStateBoolean { type = XrStructureType.XR_TYPE_ACTION_STATE_BOOLEAN };
            if (OpenXRAPI.xrGetActionStateBoolean(_session, in gi, ref st) < 0 || st.isActive == 0) return false;
            return st.currentState != 0;
        }

        private Vector2 GetVec2(ulong action, ulong sub)
        {
            var gi = new XrActionStateGetInfo { type = XrStructureType.XR_TYPE_ACTION_STATE_GET_INFO, action = action, subactionPath = sub };
            var st = new XrActionStateVector2f { type = XrStructureType.XR_TYPE_ACTION_STATE_VECTOR2F };
            if (OpenXRAPI.xrGetActionStateVector2f(_session, in gi, ref st) < 0 || st.isActive == 0) return Vector2.zero;
            return new Vector2(st.currentState.x, st.currentState.y);
        }

        /// <summary>指定手にハプティクスを適用する（_ready / 関数未解決時は no-op）。
        /// frequency=0（UNSPECIFIED）でランタイム既定周波数。amplitude は 0..1 にクランプ。</summary>
        public void ApplyHaptic(VrHand hand, float amplitude, float durationSec)
        {
            if (!_ready || OpenXRAPI.xrApplyHapticFeedback == null) return;
            ulong sub = (hand == VrHand.Left) ? _pathLeft : _pathRight;
            var info = new XrHapticActionInfo { type = XrStructureType.XR_TYPE_HAPTIC_ACTION_INFO, action = _aVibrate, subactionPath = sub };
            long durNs = (durationSec <= 0f) ? -1L /* XR_MIN_HAPTIC_DURATION */ : (long)(durationSec * 1e9);
            var vib = new XrHapticVibration { type = XrStructureType.XR_TYPE_HAPTIC_VIBRATION, duration = durNs, frequency = 0f /* XR_FREQUENCY_UNSPECIFIED */, amplitude = UnityEngine.Mathf.Clamp01(amplitude) };
            OpenXRAPI.xrApplyHapticFeedback(_session, in info, in vib);
        }

        public void Teardown()
        {
            _ready = false;
            _synced = false;
            if (_spaceLeft != 0 && OpenXRAPI.xrDestroySpace != null) OpenXRAPI.xrDestroySpace(_spaceLeft);
            if (_spaceRight != 0 && OpenXRAPI.xrDestroySpace != null) OpenXRAPI.xrDestroySpace(_spaceRight);
            if (_actionSet != 0 && OpenXRAPI.xrDestroyActionSet != null) OpenXRAPI.xrDestroyActionSet(_actionSet); // action は set 破棄で連鎖破棄
            _spaceLeft = _spaceRight = _actionSet = 0;
        }
    }
}
