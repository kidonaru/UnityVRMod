using UnityVRMod.Core;
using Xunit;

public class FramePumpPolicyTests
{
    [Fact] public void Render_when_should_render_and_rig_up()
        => Assert.Equal(VrFramePump.Render, FramePumpPolicy.Decide(shouldRender: true, rigIsSetUp: true));

    [Fact] public void None_when_rig_up_but_paused()  // safe mode: rig 構築済みで VR 一時停止
        => Assert.Equal(VrFramePump.None, FramePumpPolicy.Decide(shouldRender: false, rigIsSetUp: true));

    [Fact] public void Keepalive_when_rig_down_and_would_render()  // 遷移/カメラ未解決の本命ケース
        => Assert.Equal(VrFramePump.Keepalive, FramePumpPolicy.Decide(shouldRender: true, rigIsSetUp: false));

    [Fact] public void Keepalive_when_rig_down_even_if_paused()  // rig 不在は常に keepalive 優先
        => Assert.Equal(VrFramePump.Keepalive, FramePumpPolicy.Decide(shouldRender: false, rigIsSetUp: false));
}
