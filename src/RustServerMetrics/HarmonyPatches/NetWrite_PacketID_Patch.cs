using HarmonyLib;
using Network;

// ReSharper disable InconsistentNaming

namespace RustServerMetrics.HarmonyPatches;

[HarmonyPatch(typeof(NetWrite), nameof(NetWrite.PacketID))]
public class NetWrite_PacketID_Patch
{
    [HarmonyPostfix]
    public static void Postfix(Message.Type val)
    {
        if (!MetricsLogger.IsReady) return;
        SingletonComponent<MetricsLogger>.Instance.OnNetWritePacketID(val);
    }
}