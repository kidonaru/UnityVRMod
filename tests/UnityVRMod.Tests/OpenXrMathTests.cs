using UnityEngine;
using UnityVRMod.Core;
using Xunit;

public class OpenXrMathTests
{
    [Fact] public void Position_negates_z()
    {
        Vector3 p = OpenXrMath.ToUnityPosition(1f, 2f, 3f);
        Assert.Equal(1f, p.x, 5); Assert.Equal(2f, p.y, 5); Assert.Equal(-3f, p.z, 5);
    }

    [Fact] public void Rotation_negates_z_and_w()
    {
        Quaternion q = OpenXrMath.ToUnityRotation(0.1f, 0.2f, 0.3f, 0.4f);
        Assert.Equal(0.1f, q.x, 5); Assert.Equal(0.2f, q.y, 5);
        Assert.Equal(-0.3f, q.z, 5); Assert.Equal(-0.4f, q.w, 5);
    }
}
