using UnityVRMod.Core;
using Xunit;

namespace UnityVRMod.Tests
{
    public class EyeRtPolicyTests
    {
        // --- SanitizeMsaa: 任意の config int → RenderTexture.antiAliasing が受ける {1,2,4,8} へ正規化 ---

        [Theory]
        [InlineData(-5, 1)]
        [InlineData(0, 1)]
        [InlineData(1, 1)]
        [InlineData(2, 2)]
        [InlineData(3, 2)]
        [InlineData(4, 4)]
        [InlineData(5, 4)]
        [InlineData(7, 4)]
        [InlineData(8, 8)]
        [InlineData(16, 8)]
        [InlineData(int.MaxValue, 8)]
        public void SanitizeMsaa_正規化(int configured, int expected)
        {
            Assert.Equal(expected, EyeRtPolicy.SanitizeMsaa(configured));
        }

        // --- NeedsRecreate: RT 再生成判定 ---

        [Fact]
        public void NeedsRecreate_未生成ならtrue()
        {
            Assert.True(EyeRtPolicy.NeedsRecreate(exists: false, 2064, 2208, 4, 2064, 2208, 4));
        }

        [Theory]
        [InlineData(1000, 2208, 4)] // 幅不一致
        [InlineData(2064, 1000, 4)] // 高さ不一致
        [InlineData(2064, 2208, 1)] // MSAA 不一致（config 変更の live 反映経路）
        public void NeedsRecreate_属性不一致ならtrue(int curW, int curH, int curAa)
        {
            Assert.True(EyeRtPolicy.NeedsRecreate(exists: true, curW, curH, curAa, 2064, 2208, 4));
        }

        [Fact]
        public void NeedsRecreate_全一致ならfalse()
        {
            Assert.False(EyeRtPolicy.NeedsRecreate(exists: true, 2064, 2208, 4, 2064, 2208, 4));
        }

        // RenderTexture.antiAliasing は {1,2,4,8} 以外を実機で拒否するため、
        // 将来 SanitizeMsaa を弄っても集合の外へ出ないことを property 的に保証する
        [Fact]
        public void SanitizeMsaa_結果は常に有効集合内()
        {
            for (int v = -16; v <= 64; v++)
            {
                int r = EyeRtPolicy.SanitizeMsaa(v);
                Assert.True(r == 1 || r == 2 || r == 4 || r == 8, $"SanitizeMsaa({v}) = {r}");
            }
        }
    }
}
