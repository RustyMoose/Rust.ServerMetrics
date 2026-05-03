using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RustServerMetrics.HarmonyPatches.Utility;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace RustServerMetrics.HarmonyPatches.Delayed;

[DelayedHarmonyPatch]
[HarmonyPatch]
internal static class ServerMgr_Metrics_Patches
{
    private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

    [HarmonyPrepare]
    public static bool Prepare()
    {
        if (!RustServerMetricsLoader.__serverStarted)
        {
            Debug.Log("Note: Cannot patch ServerMgr_Metrics_Patches yet. We will patch it upon server start.");
            return false;
        }

        return true;
    }
    
    [HarmonyTargetMethods]
    public static IEnumerable<MethodBase> TargetMethods(Harmony harmonyInstance)
    {
        yield return AccessTools.Method(typeof(ServerMgr), nameof(ServerMgr.Update));
        yield return AccessTools.Method(typeof(ServerBuildingManager), nameof(ServerBuildingManager.Cycle));
        yield return AccessTools.Method(typeof(ServerBuildingManager), nameof(ServerBuildingManager.Merge));
        yield return AccessTools.Method(typeof(ServerBuildingManager), nameof(ServerBuildingManager.Split));
        
        yield return AccessTools.Method(typeof(BasePlayer), nameof(BasePlayer.ServerCycle));
        yield return AccessTools.Method(typeof(ConnectionQueue), nameof(ConnectionQueue.Cycle));
        
        yield return AccessTools.Method(typeof(AIThinkManager), nameof(AIThinkManager.ProcessQueue));
        yield return AccessTools.Method(typeof(IOEntity), nameof(IOEntity.ProcessQueue));
        
        yield return AccessTools.Method(typeof(BasePet), nameof(BasePet.ProcessMovementQueue));
        yield return AccessTools.Method(typeof(BaseMountable), nameof(BaseMountable.FixedUpdateCycle));
        yield return AccessTools.Method(typeof(Buoyancy), nameof(Buoyancy.Cycle));

        yield return AccessTools.Method(typeof(BaseEntity), nameof(BaseEntity.Kill));
        yield return AccessTools.Method(typeof(BaseEntity), nameof(BaseEntity.Spawn));

        yield return AccessTools.Method(typeof(Facepunch.Network.Raknet.Server), nameof(Facepunch.Network.Raknet.Server.Cycle));
    }
    
    [HarmonyTranspiler]
    public static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> originalInstructions, MethodBase methodBase, ILGenerator ilGenerator)
    {
        List<CodeInstruction> ret = originalInstructions.ToList();
        LocalBuilder local = ilGenerator.DeclareLocal(typeof(long));

        ret.InsertRange(0, new CodeInstruction []
        {
            new (OpCodes.Call, AccessTools.Method(typeof(Stopwatch), nameof(Stopwatch.GetTimestamp))),
            new (OpCodes.Stloc, local)
        });
        return Helpers.Postfix(
            ret,
            CustomPostfix,
            new CodeInstruction(OpCodes.Ldstr, $"{methodBase.DeclaringType?.Name}.{methodBase.Name}"),
            new CodeInstruction(OpCodes.Ldloc, local));
    }

    public static void CustomPostfix(string methodName, long __state)
    {
        if (!MetricsLogger.IsReady)
            return;

        var ms = (Stopwatch.GetTimestamp() - __state) * TicksToMs;
        MetricsLogger.Instance.ServerUpdate.LogTime(methodName, ms);
    }
}

