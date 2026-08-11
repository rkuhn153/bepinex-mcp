using UnityEngine;

namespace BepInExMCP.IL2CPP;

internal sealed class BridgeSelfTestComponent : MonoBehaviour
{
    public BridgeSelfTestComponent(IntPtr pointer) : base(pointer)
    {
    }

    public int Echo(int value)
    {
        return value;
    }

    public int Add(int left, int right)
    {
        return left + right;
    }
}
