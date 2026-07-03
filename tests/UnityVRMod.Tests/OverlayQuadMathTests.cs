using UnityEngine;
using UnityVRMod.Core;
using Xunit;

public class OverlayQuadMathTests
{
    // BlitScaleOffset: UV bounds → Graphics.Blit(scale, offset)。
    [Fact] public void BlitScaleOffset_full_uv_is_identity()
    {
        OverlayQuadMath.BlitScaleOffset(0f, 0f, 1f, 1f, out Vector2 scale, out Vector2 offset);
        Assert.Equal(1f, scale.x, 5); Assert.Equal(1f, scale.y, 5);
        Assert.Equal(0f, offset.x, 5); Assert.Equal(0f, offset.y, 5);
    }

    // flipV（vMin>vMax）→ scale.y が負・offset.y は vMin。
    [Fact] public void BlitScaleOffset_flipped_v_gives_negative_y_scale()
    {
        OverlayQuadMath.BlitScaleOffset(0f, 1f, 1f, 0f, out Vector2 scale, out Vector2 offset);
        Assert.Equal(1f, scale.x, 5); Assert.Equal(-1f, scale.y, 5);
        Assert.Equal(0f, offset.x, 5); Assert.Equal(1f, offset.y, 5);
    }

    // atlas sub-rect: u[0.25,0.75] v[0.1,0.6]。
    [Fact] public void BlitScaleOffset_subrect()
    {
        OverlayQuadMath.BlitScaleOffset(0.25f, 0.1f, 0.75f, 0.6f, out Vector2 scale, out Vector2 offset);
        Assert.Equal(0.5f, scale.x, 5); Assert.Equal(0.5f, scale.y, 5);
        Assert.Equal(0.25f, offset.x, 5); Assert.Equal(0.1f, offset.y, 5);
    }

    // QuadSize: width=3, src 16:9 → height=3*9/16=1.6875。
    [Fact] public void QuadSize_preserves_source_aspect()
    {
        OverlayQuadMath.QuadSize(3f, 1920, 1080, out float w, out float h);
        Assert.Equal(3f, w, 5); Assert.Equal(1.6875f, h, 4);
    }

    // QuadSize: 不正な src 寸法は 1:1 へフォールバック（0 除算回避）。
    [Fact] public void QuadSize_falls_back_to_square_on_bad_dims()
    {
        OverlayQuadMath.QuadSize(3f, 0, 0, out float w, out float h);
        Assert.Equal(3f, w, 5); Assert.Equal(3f, h, 5);
    }

    // CroppedPixelSize: 全面 UV は src そのまま。
    [Fact] public void CroppedPixelSize_full_uv_equals_source()
    {
        OverlayQuadMath.CroppedPixelSize(2048, 2048, 0f, 0f, 1f, 1f, out int w, out int h);
        Assert.Equal(2048, w); Assert.Equal(2048, h);
    }

    // CroppedPixelSize: atlas sub-rect（2048 中 1024×512）→ crop 後 1024×512（aspect 2:1）。
    [Fact] public void CroppedPixelSize_atlas_subrect()
    {
        // u[0,0.5] v[0,0.25] = 1024×512 領域
        OverlayQuadMath.CroppedPixelSize(2048, 2048, 0f, 0f, 0.5f, 0.25f, out int w, out int h);
        Assert.Equal(1024, w); Assert.Equal(512, h);
    }

    // CroppedPixelSize: flipV（vMin>vMax）でも span は絶対値。
    [Fact] public void CroppedPixelSize_flipped_v_uses_abs()
    {
        OverlayQuadMath.CroppedPixelSize(1000, 800, 0f, 0.25f, 1f, 0f, out int w, out int h);
        Assert.Equal(1000, w); Assert.Equal(200, h); // 800*0.25=200
    }

    // CroppedPixelSize → QuadSize: atlas sub-rect でも aspect が crop 後の比で復元される。
    [Fact] public void CroppedThenQuadSize_preserves_subrect_aspect()
    {
        OverlayQuadMath.CroppedPixelSize(2048, 2048, 0f, 0f, 0.5f, 0.25f, out int cw, out int ch); // 1024×512 = 2:1
        OverlayQuadMath.QuadSize(3f, cw, ch, out float w, out float h);
        Assert.Equal(3f, w, 5); Assert.Equal(1.5f, h, 4); // 3 * 512/1024 = 1.5
    }

    // IsFadeActive: alpha epsilon ゲート。
    [Fact] public void IsFadeActive_threshold()
    {
        Assert.False(OverlayQuadMath.IsFadeActive(0f));
        Assert.False(OverlayQuadMath.IsFadeActive(0.0005f));
        Assert.True(OverlayQuadMath.IsFadeActive(0.5f));
        Assert.True(OverlayQuadMath.IsFadeActive(1f));
    }
}
