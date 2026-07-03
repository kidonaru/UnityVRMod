using System.Collections.Generic;
using UnityVRMod.Core;
using Xunit;

public class OverlayDrawOrderTests
{
    [Fact]
    public void Sorts_ascending_by_key()
    {
        var list = new List<int> { 4005, 4000, 4002, 4001 };
        OverlayDrawOrder.StableSortByKey(list, x => x);
        Assert.Equal(new[] { 4000, 4001, 4002, 4005 }, list);
    }

    [Fact]
    public void Stable_preserves_order_within_equal_keys()
    {
        // queue でソートしても同一 queue 内は元順（rig sibling 順）を保つ＝既存ケース非回帰。
        var list = new List<(int q, char tag)>
        {
            (4002, 'a'), (4000, 'b'), (4002, 'c'), (4002, 'd'), (4000, 'e')
        };
        OverlayDrawOrder.StableSortByKey(list, x => x.q);
        Assert.Equal(new[] { 'b', 'e', 'a', 'c', 'd' }, list.ConvertAll(x => x.tag));
    }

    [Fact]
    public void Empty_and_single_are_noops()
    {
        var empty = new List<int>();
        OverlayDrawOrder.StableSortByKey(empty, x => x);
        Assert.Empty(empty);
        var single = new List<int> { 7 };
        OverlayDrawOrder.StableSortByKey(single, x => x);
        Assert.Equal(new[] { 7 }, single);
    }

    [Fact]
    public void Already_sorted_is_unchanged()
    {
        var list = new List<int> { 4000, 4001, 4002, 4005, 4006 };
        OverlayDrawOrder.StableSortByKey(list, x => x);
        Assert.Equal(new[] { 4000, 4001, 4002, 4005, 4006 }, list);
    }
}
