using UnityVRMod.Core;
using Xunit;

namespace UnityVRMod.Tests
{
    public class StandbyPolicyTests
    {
        // --- ShouldEnterStandby: session 非 running が猶予を超えたら standby（teardown / soft-park）突入 ---

        [Fact]
        public void EnterStandby_SessionRunning中は経過時間に関わらずfalse()
            => Assert.False(StandbyPolicy.ShouldEnterStandby(sessionRunning: true, secsSessionNotRunning: 999f, graceSec: 5f));

        [Fact]
        public void EnterStandby_猶予未満はfalse()
            => Assert.False(StandbyPolicy.ShouldEnterStandby(sessionRunning: false, secsSessionNotRunning: 4.9f, graceSec: 5f));

        [Fact]
        public void EnterStandby_猶予到達でtrue()
            => Assert.True(StandbyPolicy.ShouldEnterStandby(sessionRunning: false, secsSessionNotRunning: 5f, graceSec: 5f));

        [Fact]
        public void EnterStandby_grace0は機能無効()
            => Assert.False(StandbyPolicy.ShouldEnterStandby(sessionRunning: false, secsSessionNotRunning: 999f, graceSec: 0f));

        [Fact]
        public void EnterStandby_grace負も機能無効()
            => Assert.False(StandbyPolicy.ShouldEnterStandby(sessionRunning: false, secsSessionNotRunning: 999f, graceSec: -1f));

        // --- ShouldProbe: park 中の自動再 init 周期 ---

        [Fact]
        public void Probe_非parkedはfalse()
            => Assert.False(StandbyPolicy.ShouldProbe(parked: false, secsSinceLastAttempt: 999f, probeIntervalSec: 10f));

        [Fact]
        public void Probe_間隔未満はfalse()
            => Assert.False(StandbyPolicy.ShouldProbe(parked: true, secsSinceLastAttempt: 9.9f, probeIntervalSec: 10f));

        [Fact]
        public void Probe_間隔到達でtrue()
            => Assert.True(StandbyPolicy.ShouldProbe(parked: true, secsSinceLastAttempt: 10f, probeIntervalSec: 10f));

        [Fact]
        public void Probe_interval0は自動probe無効()
            => Assert.False(StandbyPolicy.ShouldProbe(parked: true, secsSinceLastAttempt: 999f, probeIntervalSec: 0f));
    }
}
