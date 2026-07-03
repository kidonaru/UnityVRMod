using UnityEngine;

namespace UnityVRMod.Core
{
    /// <summary>
    /// OpenXR overlay/fade quad layer 用の純関数（UnityEngine + System のみ依存・テスト対象）。
    /// quad layer は UV bounds / per-layer alpha を持たないため、crop/flip は Blit の scale/offset で
    /// intermediate RT へ焼き込む。本クラスはその係数計算と quad geometry のみを担う。
    /// </summary>
    public static class OverlayQuadMath
    {
        // fade alpha の有効しきい値（これ以下は fade 非表示＝quad を submit しない）。
        public const float FadeAlphaEpsilon = 0.001f;

        /// <summary>
        /// 正規化 UV bounds → Graphics.Blit(src, dst, scale, offset) の係数。
        /// scale=(uMax-uMin, vMax-vMin)・offset=(uMin, vMin)。flipV は vMin>vMax のとき scale.y が負になり Blit が反転する。
        /// </summary>
        public static void BlitScaleOffset(float uMin, float vMin, float uMax, float vMax, out Vector2 scale, out Vector2 offset)
        {
            scale = new Vector2(uMax - uMin, vMax - vMin);
            offset = new Vector2(uMin, vMin);
        }

        /// <summary>
        /// overlay quad の実寸（メートル）。横幅は引数固定、縦は source の aspect を保つ。
        /// source 寸法が不正（&lt;=0）なら 0 除算を避けて 1:1（正方）にフォールバックする。
        /// </summary>
        public static void QuadSize(float widthMeters, int srcWidth, int srcHeight, out float width, out float height)
        {
            width = widthMeters;
            height = (srcWidth > 0 && srcHeight > 0)
                ? widthMeters * srcHeight / srcWidth
                : widthMeters;
        }

        /// <summary>
        /// UV crop 後の実画素寸法（atlas sub-rect 対応）。quad の aspect 復元はこの実効寸法で行う。
        /// source が atlas で sprite が sub-rect の場合、表示領域の縦横比は atlas 全体ではなく crop 後の比なので、
        /// QuadSize へはこの実効寸法を渡す。crop が全面（u/v span=1）なら srcWidth/srcHeight と一致する。
        /// V は flip（vMin&gt;vMax）で span が負になりうるため絶対値を取る。最小 1px（0 寸法回避）。
        /// </summary>
        public static void CroppedPixelSize(int srcWidth, int srcHeight, float uMin, float vMin, float uMax, float vMax, out int width, out int height)
        {
            width = Mathf.Max(1, Mathf.RoundToInt(srcWidth * Mathf.Abs(uMax - uMin)));
            height = Mathf.Max(1, Mathf.RoundToInt(srcHeight * Mathf.Abs(vMax - vMin)));
        }

        /// <summary>fade quad を submit すべきか（alpha がしきい値超）。</summary>
        public static bool IsFadeActive(float alpha) => alpha > FadeAlphaEpsilon;
    }
}
