namespace UnityVRMod.Core
{
    /// <summary>eye intermediate RT の MSAA 正規化と再生成判定（純関数・backend 非依存）。
    /// MSAA は {1,2,4,8} のみ有効（RenderTexture.antiAliasing の制約）。範囲外の config 値は
    /// 下方向の最寄り有効値へ丸める（不正値でも例外にせずベストエフォートで描画継続）。</summary>
    public static class EyeRtPolicy
    {
        public static int SanitizeMsaa(int configured)
        {
            if (configured >= 8) return 8;
            if (configured >= 4) return 4;
            if (configured >= 2) return 2;
            return 1;
        }

        /// <summary>RT の再生成が必要か。MSAA 不一致を含む＝config 変更が次フレームで live 反映される。</summary>
        public static bool NeedsRecreate(bool exists, int curW, int curH, int curAa, int reqW, int reqH, int reqAa)
            => !exists || curW != reqW || curH != reqH || curAa != reqAa;
    }
}
