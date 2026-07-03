namespace UnityVRMod.Core
{
    /// <summary>VR トリガー押し込みの hysteresis 判定（純関数・backend 非依存）。
    /// 押し込み≥0.7 で ON、≤0.4 で OFF（チャタリング防止のデッドバンド）。
    /// btn=true（トリガー click ビットを持つ機種）は軸値に関わらず即 ON。</summary>
    public static class TriggerHysteresis
    {
        public const float PressThreshold = 0.7f;
        public const float ReleaseThreshold = 0.4f;

        public static bool Update(bool wasHeld, float axis, bool btn)
        {
            if (wasHeld) return !(axis <= ReleaseThreshold && !btn);
            return axis >= PressThreshold || btn;
        }
    }
}
