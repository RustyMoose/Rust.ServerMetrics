using HarmonyLib;
using RustServerMetrics.HarmonyPatches.Utility;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

// ReSharper disable InconsistentNaming
// ReSharper disable PossibleMultipleEnumeration

namespace RustServerMetrics.HarmonyPatches.Delayed;

[DelayedHarmonyPatch]
[HarmonyPatch]
internal static class ObjectWorkQueue_RunJob_Patch
{
    private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

    [HarmonyPrepare]
    public static bool Prepare()
    {
        if (RustServerMetricsLoader.__serverStarted)
        {
            return true;
        }
        
        Debug.Log($"[ServerMetrics] Note: Cannot patch {nameof(ObjectWorkQueue_RunJob_Patch)} yet. We will patch it upon server start.");
        return false;
    }

    [HarmonyTargetMethods]
    public static IEnumerable<MethodBase> TargetMethods()
    {
        var assemblyCSharp = typeof(BaseNetworkable).Assembly;
        var typesToScan = new Stack<Type>(assemblyCSharp.GetTypes());
        var yielded = new HashSet<string>();
        
        while (typesToScan.TryPop(out var type))
        {
            foreach (var t in type.GetNestedTypes())
            {
                typesToScan.Push(t);
            }

            if (type.BaseType == null || !type.BaseType.Name.Contains("ObjectWorkQueue"))
            {
                continue;
            }

            if (yielded.Add(type.FullName))
            {
                yield return AccessTools.Method(type, "RunJob");
            }
        }
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
                    new CodeInstruction(
                        OpCodes.Call, 
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
                        new(OpCodes.Call, AccessTools.Method(typeof(ObjectWorkQueue_RunJob_Patch), nameof(RecordInvokeTime)))
                    ];
                    
                    repeatingMatcher.Instruction.MoveLabelsTo(toInsert[0]);
                    repeatingMatcher.InsertAndAdvance(toInsert);
                    repeatingMatcher.Advance(1);
                });
            
            return matcher.Instructions();
        }
        catch (Exception e)
        {
            Debug.LogError($"[ServerMetrics] {nameof(ObjectWorkQueue_RunJob_Patch)}: " + e.Message);
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
        MetricsLogger.Instance.WorkQueueTimes.LogTime(methodName, ms);
    }
}