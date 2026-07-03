using UnityEngine;

namespace UnityVRMod.Core
{
    /// <summary>VR ポインタが使うコントローラの手（左右）。</summary>
    public enum VrHand { Left, Right }

    /// <summary>
    /// コントローラの 1 フレームスナップショット。pose は rig-local（rig transform 基準＝eye と同空間）。
    /// VR 非 ready / コントローラ未接続時は Valid=false。
    /// Linear/AngularVelocity は xrLocateSpace の XrSpaceVelocity チェーンで充填（VelocityValid で有効性判定）。
    /// </summary>
    public struct VrControllerSnapshot
    {
        public bool Valid;
        public Vector3 RigLocalPosition;
        public Quaternion RigLocalRotation;
        public bool Trigger; // fork でデバウンス済（axis hysteresis OR button bit）
        public float TriggerValue; // rAxis1.x 生値 0〜1。companion の押し込み開始(onset)検知用。button bit のみの機種では 0 のまま
        public bool Grip;
        public float GripValue; // squeeze rAxis 生値 0〜1。指ベンドの握り量に使う。bool Grip は閾値化済（併存）
        public bool A;
        public bool B;
        public bool Menu; // 左コントローラのメニューボタン（≡）。右の system ボタンは runtime 予約のため非対応＝常に false
        public Vector2 Stick; // rAxis0（Touch の thumbstick）。未接続/非対応機種では 0 のまま
        public bool StickClick; // スティック押し込み（k_EButton_Axis0 ビット）
        public Vector3 LinearVelocity;   // rig-local m/s（VelocityValid=false 時は未定義）
        public Vector3 AngularVelocity;  // rig-local rad/s（同上。BG2VR は magnitude のみ使用）
        public bool VelocityValid;       // ランタイムが線速度を供給したか
    }
}
