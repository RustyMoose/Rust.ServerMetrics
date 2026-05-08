using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RustServerMetrics.HarmonyPatches.Utility;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

// ReSharper disable InconsistentNaming
// ReSharper disable PossibleMultipleEnumeration

namespace RustServerMetrics.HarmonyPatches.Delayed;

[DelayedHarmonyPatch]
[HarmonyPatch]
internal static class ServerMgr_Metrics_Patches
{
    private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

    [HarmonyPrepare]
    public static bool Prepare()
    {
        if (RustServerMetricsLoader.__serverStarted)
        {
            return true;
        }
        
        Debug.Log($"[ServerMetrics] Note: Cannot patch {nameof(ServerMgr_Metrics_Patches)} yet. We will patch it upon server start.");
        return false;
    }

    [HarmonyTargetMethods]
    public static IEnumerable<MethodBase> TargetMethods()
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
    public static IEnumerable<CodeInstruction> Transpiler(
        IEnumerable<CodeInstruction> instructions, 
        MethodBase methodBase, 
        ILGenerator ilGenerator)
    {
        try
        {
            var invokedMethod = $"{methodBase.DeclaringType?.Name}.{methodBase.Name}";
            
            var matcher = new CodeMatcher(instructions, ilGenerator)
                .Start()
                .DeclareLocal(typeof(long), out var timestampLocal)
                .InsertAndAdvance(
                    new CodeInstruction(OpCodes.Call, 
                        AccessTools.Method(
                            typeof(Stopwatch), 
                            nameof(Stopwatch.GetTimestamp))),
                    new CodeInstruction(OpCodes.Stloc, timestampLocal))
                .MatchStartForward(new CodeMatch(OpCodes.Ret))
                .Repeat(repeatingMatcher =>
                {
                    CodeInstruction[] toInsert =
                    [
                        new(OpCodes.Ldstr, invokedMethod),
                        new(OpCodes.Ldloc, timestampLocal),
                        new(OpCodes.Call, AccessTools.Method(typeof(ServerMgr_Metrics_Patches), nameof(RecordInvokeTime)))
                    ];
                    
                    repeatingMatcher.Instruction.MoveLabelsTo(toInsert[0]);
                    repeatingMatcher.InsertAndAdvance(toInsert);
                    repeatingMatcher.Advance(1);
                });
            
            return matcher.Instructions();
        }
        catch (Exception e)
        {
            Debug.LogError($"[ServerMetrics] {nameof(ServerMgr_Metrics_Patches)}: " + e.Message);
            return instructions;
        }
    }

    private static void RecordInvokeTime(string methodName, long startTimestamp)
    {
        if (!MetricsLogger.IsReady)
        {
            return;
        }

        var ms = (Stopwatch.GetTimestamp() - startTimestamp) * TicksToMs;
        MetricsLogger.Instance.ServerUpdate.LogTime(methodName, ms);
    }
}