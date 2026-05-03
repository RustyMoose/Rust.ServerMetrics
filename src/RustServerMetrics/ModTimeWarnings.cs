using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RustServerMetrics.HarmonyPatches.Utility;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace RustServerMetrics;

[HarmonyPatch]
public static class ModTimeWarnings
{
    public static List<MethodInfo> Methods = new ();

    private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

    [HarmonyPrepare]
    public static bool Prepare()
    {
        if (!RustServerMetricsLoader.__serverStarted)
        {
            Debug.Log("Note: Cannot patch any time warnings yet. We will patch it upon server start.");
            return false;
        }

        return true;
    }
    
    [HarmonyTargetMethods]
    public static IEnumerable<MethodBase> TargetMethods(Harmony harmonyInstance) => Methods;
    
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
        MetricsLogger.Instance.TimeWarnings.LogTime(methodName, ms);
    }
}