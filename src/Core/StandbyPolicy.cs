namespace UnityVRMod.Core
{
    /// <summary>
    /// doff（HMD 取り外し）/スタンバイ対策の純判定（UnityEngine 非依存・テストで固定する）。
    ///
    /// 背景（検死 2026-06-11・OpenXR 移行 spec §5.5.3）: Meta の in-process runtime client (v85) は
    /// 「session が非 running のまま OpenXR instance を保持して走り続ける dormant プロセス」を
    /// 可変遅延（最短実測 ~12s）で __fastfail(c0000409) させる。session が非 running になったら
    /// 猶予内に instance ごと full teardown して dormant 状態を作らないのが crash 回避の核心。
    /// teardown 後は park し、probe 間隔で再 init を試す（READY が来る＝再装着まで running に
    /// ならないので、probe が作る短命 instance は同じ猶予で自動的に再 teardown される）。
    /// </summary>
    public static class StandbyPolicy
    {
        /// <summary>session 非 running が graceSec 続いたら standby（teardown / soft-park）へ突入すべきか。graceSec &lt;= 0 は機能無効。</summary>
        public static bool ShouldEnterStandby(bool sessionRunning, float secsSessionNotRunning, float graceSec)
        {
            if (graceSec <= 0f) return false;
            if (sessionRunning) return false;
            return secsSessionNotRunning >= graceSec;
        }

        /// <summary>park 中（standby teardown 済み）に再 init probe を打つべきか。probeIntervalSec &lt;= 0 は自動 probe 無効。</summary>
        public static bool ShouldProbe(bool parked, float secsSinceLastAttempt, float probeIntervalSec)
        {
            if (!parked) return false;
            if (probeIntervalSec <= 0f) return false;
            return secsSinceLastAttempt >= probeIntervalSec;
        }
    }
}
