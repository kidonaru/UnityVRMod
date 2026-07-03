using System;
using System.Collections.Generic;

namespace UnityVRMod.Core
{
    /// <summary>
    /// overlay renderer の描画順を renderQueue で確定する純関数（backend/Unity 非依存）。
    /// fork の DrawEyeOverlay は CommandBuffer.DrawRenderer で重ね描くため material.renderQueue が
    /// 効かず、描画順 = リスト順 = 後勝ち（ZWrite Off）。リストを renderQueue 昇順で**安定**ソートして
    /// 「queue が大きいほど前面」という BG2VR 側契約（UiOverlayRenderPolicy）を fork が honor する。
    /// 安定ソート = 同一 queue 内は元順序（rig sibling 順）を保持し、既存の正しいケースを非回帰にする。
    /// </summary>
    public static class OverlayDrawOrder
    {
        /// <summary>list を keyOf 昇順で安定 in-place ソートする（挿入ソート・要素数小・GC なし）。</summary>
        public static void StableSortByKey<T>(IList<T> list, Func<T, int> keyOf)
        {
            if (list == null || keyOf == null) return;
            int n = list.Count;
            for (int i = 1; i < n; i++)
            {
                T item = list[i];
                int key = keyOf(item);
                int j = i - 1;
                // 厳密に大きい要素だけ後ろへずらす（> ＝同一 key は動かさない＝安定）。
                while (j >= 0 && keyOf(list[j]) > key)
                {
                    list[j + 1] = list[j];
                    j--;
                }
                list[j + 1] = item;
            }
        }
    }
}
