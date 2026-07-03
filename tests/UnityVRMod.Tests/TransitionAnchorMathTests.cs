using UnityEngine;
using UnityVRMod.Core;
using Xunit;

public class TransitionAnchorMathTests
{
    // RH の +Y 軸まわり yaw 角からクォータニオン成分を作る（テスト入力用・ComputeWorldAnchor とは独立）。
    private static Quaternion YawQuat(float yawRad)
        => new Quaternion(0f, Mathf.Sin(yawRad * 0.5f), 0f, Mathf.Cos(yawRad * 0.5f));

    // RH の +X 軸まわり pitch 角からクォータニオン成分を作る。
    private static Quaternion PitchQuat(float pitchRad)
        => new Quaternion(Mathf.Sin(pitchRad * 0.5f), 0f, 0f, Mathf.Cos(pitchRad * 0.5f));

    // identity 姿勢（-Z 凝視）→ 旧頭ロック（VIEW space identity・(0,0,-d)）と相対幾何が一致。
    [Fact]
    public void Identity_look_minus_z_matches_headlock_geometry()
    {
        var head = new Vector3(0f, 1.5f, 0f);
        bool ok = TransitionAnchorMath.ComputeWorldAnchor(
            head, Quaternion.identity, 1.5f, out Vector3 anchor, out float yaw);
        Assert.True(ok);
        Assert.Equal(0f, anchor.x, 4);
        Assert.Equal(1.5f, anchor.y, 4);
        Assert.Equal(-1.5f, anchor.z, 4);
        Assert.Equal(0f, yaw, 4);
    }

    // +X 凝視（yaw=-90° で forward が +X）→ anchor は +X 方向・距離ぶん・yaw=atan2(-1,0)=-π/2。
    [Fact]
    public void Look_plus_x_anchors_in_plus_x()
    {
        var head = new Vector3(0f, 1.5f, 0f);
        // forward(0,0,-1) を +X に向ける RH yaw は -90°。
        bool ok = TransitionAnchorMath.ComputeWorldAnchor(
            head, YawQuat(-Mathf.PI * 0.5f), 2f, out Vector3 anchor, out float yaw);
        Assert.True(ok);
        Assert.Equal(2f, anchor.x, 4);
        Assert.Equal(1.5f, anchor.y, 4);
        Assert.Equal(0f, anchor.z, 4);
        Assert.Equal(-Mathf.PI * 0.5f, yaw, 4);
    }

    // 高さは常に頭の高さを維持する（pitch を多少入れても anchor.y == head.y）。
    [Fact]
    public void Anchor_height_equals_head_height()
    {
        var head = new Vector3(3f, 0.8f, -2f);
        bool ok = TransitionAnchorMath.ComputeWorldAnchor(
            head, PitchQuat(0.3f), 1.5f, out Vector3 anchor, out _);
        Assert.True(ok);
        Assert.Equal(0.8f, anchor.y, 4);
    }

    // 真上凝視（pitch +90°→forward 垂直）→ 水平退化で false（頭ロック fallback）。
    [Fact]
    public void Look_straight_up_is_degenerate()
    {
        bool ok = TransitionAnchorMath.ComputeWorldAnchor(
            Vector3.zero, PitchQuat(Mathf.PI * 0.5f), 1.5f, out _, out _);
        Assert.False(ok);
    }

    // 真下凝視（pitch -90°）→ 同じく false。
    [Fact]
    public void Look_straight_down_is_degenerate()
    {
        bool ok = TransitionAnchorMath.ComputeWorldAnchor(
            Vector3.zero, PitchQuat(-Mathf.PI * 0.5f), 1.5f, out _, out _);
        Assert.False(ok);
    }

    // 距離は線形（同一姿勢で d=2 は d=1 の 2 倍オフセット）。
    [Fact]
    public void Distance_scales_offset_linearly()
    {
        var head = new Vector3(0f, 1.5f, 0f);
        var ori = YawQuat(0.7f);
        TransitionAnchorMath.ComputeWorldAnchor(head, ori, 1f, out Vector3 a1, out _);
        TransitionAnchorMath.ComputeWorldAnchor(head, ori, 2f, out Vector3 a2, out _);
        Vector3 d1 = a1 - head;
        Vector3 d2 = a2 - head;
        Assert.Equal(2f * d1.x, d2.x, 4);
        Assert.Equal(2f * d1.z, d2.z, 4);
        Assert.Equal(1f, d1.magnitude, 3); // d=1 のオフセット長は 1m
    }
}
