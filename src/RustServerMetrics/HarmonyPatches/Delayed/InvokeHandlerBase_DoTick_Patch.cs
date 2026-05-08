using HarmonyLib;
using RustServerMetrics.HarmonyPatches.Utility;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;

// ReSharper disable once InconsistentNaming
// ReSharper disable PossibleMultipleEnumeration

namespace RustServerMetrics.HarmonyPatches.Delayed;

[DelayedHarmonyPatch]
[HarmonyPatch]
internal static class InvokeHandlerBase_DoTick_Patch
{
    #region Members

    private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

    #endregion

    #region Patching

    [HarmonyPrepare]
    public static bool Prepare()
    {
        if (RustServerMetricsLoader.__serverStarted)
        {
            return true;
        }

        UnityEngine.Debug.Log($"[ServerMetrics] Note: Cannot patch {nameof(InvokeHandlerBase_DoTick_Patch)} yet. We will patch it upon server start.");
        return false;
    }

    [HarmonyTargetMethods]
    public static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.DeclaredMethod(
            typeof(InvokeHandlerBase<InvokeHandler>),
            nameof(InvokeHandlerBase<InvokeHandler>.DoTick));
    }

    [HarmonyTranspiler]
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        try
        {
            var codeMatcher = new CodeMatcher(instructions)
                .MatchStartForward(
                    CodeMatch.LoadsField(
                        AccessTools.Field(
                            typeof(InvokeAction),
                            nameof(InvokeAction.action))),
                    CodeMatch.Calls(
                        AccessTools.Method(
                            typeof(Action),
                            nameof(Action.Invoke))))
                .ThrowIfInvalid("Unable to find the expected injection point")
                .RemoveInstructions(2)
                .InsertAndAdvance(
                    new CodeInstruction(
                        OpCodes.Call, 
                        AccessTools.Method(
                            typeof(InvokeHandlerBase_DoTick_Patch), 
                            nameof(InvokeWrapper))));

            return codeMatcher.Instructions();
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogError($"[ServerMetrics] {nameof(InvokeHandlerBase_DoTick_Patch)}: " + e.Message);
            return instructions;
        }
    }

    #endregion

    #region Handler

    private static void InvokeWrapper(InvokeAction invokeAction)
    {
        if (!MetricsLogger.IsReady)
        {
            invokeAction.action.Invoke();
            return;
        }

        var start = Stopwatch.GetTimestamp();
        try
        {
            invokeAction.action.Invoke();
        }
        finally
        {
            var ms = (Stopwatch.GetTimestamp() - start) * TicksToMs;
            MetricsLogger.Instance.ServerInvokes.LogTime(invokeAction.action.Method, ms);
        }
    }

    #endregion
}