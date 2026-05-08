using HarmonyLib;
using RustServerMetrics.HarmonyPatches.Utility;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

// ReSharper disable InconsistentNaming

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
        
        Debug.Log($"Note: Cannot patch {nameof(ObjectWorkQueue_RunJob_Patch)} yet. We will patch it upon server start.");
        return false;
    }

    [HarmonyTargetMethods]
    public static IEnumerable<MethodBase> TargetMethods(Harmony harmonyInstance)
    {
        var assemblyCSharp = typeof(BaseNetworkable).Assembly;
        var typesToScan = new Stack<Type>(assemblyCSharp.GetTypes());
        HashSet<string> yielded = [];

        while (typesToScan.TryPop(out var type))
        {
            var subTypes = type.GetNestedTypes();
            foreach (var t in subTypes)
                typesToScan.Push(t);

            if (type.BaseType == null || !type.BaseType.Name.Contains("ObjectWorkQueue"))
                continue;

            if (yielded.Add(type.FullName))
            {
                yield return AccessTools.Method(type, "RunJob");
            }
        }
    }

    [HarmonyTranspiler]
    public static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> originalInstructions,
                                                         MethodBase methodBase,
                                                         ILGenerator ilGenerator)
    {
        var ret = originalInstructions.ToList();
        var local = ilGenerator.DeclareLocal(typeof(long));

        ret.InsertRange(0, [
            new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Stopwatch), nameof(Stopwatch.GetTimestamp))),
            new CodeInstruction(OpCodes.Stloc, local)
        ]);

        return Helpers.Postfix(ret,
                               CustomPostfix,
                               new CodeInstruction(OpCodes.Ldstr, $"{methodBase.DeclaringType?.Name}.{methodBase.Name}"),
                               new CodeInstruction(OpCodes.Ldloc, local));
    }

    private static void CustomPostfix(string methodName, long __state)
    {
        if (!MetricsLogger.IsReady)
        {
            return;
        }

        var ms = (Stopwatch.GetTimestamp() - __state) * TicksToMs;
        MetricsLogger.Instance.WorkQueueTimes.LogTime(methodName, ms);
    }
}