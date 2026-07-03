using UnityEngine;

namespace UnityVRMod.Core
{
    /// <summary>
    /// 遷移絵柄 overlay を world 固定（頭ロックでなく空間固定＝UI パネル同様）にするための anchor pose 計算。
    /// すべて OpenXR RH 座標で完結する純関数（UnityEngine + System のみ依存・テスト対象）。
    /// 入力の <paramref name="headOri"/> は app space で取得した頭の生 XrQuaternionf 成分を
    /// UnityEngine.Quaternion の入れ物に詰めたもの（LH 変換は通さない）。回転適用は RH Hamilton を手実装する
    /// （UnityEngine.Quaternion の LH 乗算を使うと符号が狂うため）。
    /// </summary>
    public static class TransitionAnchorMath
    {
        // 水平 forward の最小長。これ未満（頭が真上/真下を向いた瞬間）は anchor を出さず頭ロック fallback。
        public const float MinHorizForward = 0.01f;

        /// <summary>
        /// 可視 rising edge 時点の頭 pose（app space RH）から、絵柄を視聴者の正面に world 固定する
        /// anchor を計算する。高さは頭と同じ（eye 高）を保ち、向きは yaw のみ水平化して視聴者に正対させる。
        /// 戻り値 false = 水平退化（真上/真下凝視）→ 呼出側は頭ロックで描画する。
        /// </summary>
        /// <param name="headPos">app space RH の頭位置。</param>
        /// <param name="headOri">app space RH の頭姿勢（生 XrQuaternionf 成分・LH 変換なし）。</param>
        /// <param name="distance">絵柄までの水平距離(m)。</param>
        /// <param name="anchorPos">出力: app space RH の quad 中心位置。</param>
        /// <param name="yawRadians">出力: quad 姿勢の Y 軸回転（quad 前面 +Z を視聴者向き -f に向ける）。</param>
        public static bool ComputeWorldAnchor(
            Vector3 headPos, Quaternion headOri, float distance,
            out Vector3 anchorPos, out float yawRadians)
        {
            // OpenXR view-forward = -Z。頭姿勢で回した look 方向（RH）。
            Vector3 forward = RotateRH(headOri, new Vector3(0f, 0f, -1f));
            // 水平投影（pitch/roll を落とす）。
            Vector3 horiz = new Vector3(forward.x, 0f, forward.z);
            float len = horiz.magnitude;
            if (len < MinHorizForward)
            {
                anchorPos = headPos;
                yawRadians = 0f;
                return false;
            }
            Vector3 f = horiz / len;             // 水平 forward 単位ベクトル
            anchorPos = headPos + f * distance;  // 高さは headPos.y 維持
            // quad 前面(+Z) を視聴者向き（anchor→head = -f）に向ける yaw。
            // RH の +Y 回転 θ は (0,0,1)→(sinθ,0,cosθ)。-f=(−f.x,0,−f.z) と一致させ θ=atan2(−f.x,−f.z)。
            yawRadians = Mathf.Atan2(-f.x, -f.z);
            return true;
        }

        // RH Hamilton: v' = v + 2w(u×v) + 2u×(u×v)。Vector3.Cross は数値的な外積（handedness 非依存の式）。
        private static Vector3 RotateRH(Quaternion q, Vector3 v)
        {
            Vector3 u = new Vector3(q.x, q.y, q.z);
            Vector3 uxv = Vector3.Cross(u, v);
            Vector3 uxuxv = Vector3.Cross(u, uxv);
            return v + 2f * q.w * uxv + 2f * uxuxv;
        }
    }
}
