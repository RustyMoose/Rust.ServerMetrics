using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection.Emit;

// ReSharper disable InconsistentNaming
// ReSharper disable PossibleMultipleEnumeration

namespace RustServerMetrics.HarmonyPatches;

[HarmonyPatch(typeof(BasePlayer), nameof(BasePlayer.PerformanceReport))]
public class BasePlayer_PerformanceReport_Patch
{
    [HarmonyTranspiler]
    public static IEnumerable<CodeInstruction> Transpiler(
        IEnumerable<CodeInstruction> instructions,
        ILGenerator ilGenerator)
    {
        try
        {
            var matcher = new CodeMatcher(instructions, ilGenerator)
                .End()
                .CreateLabel(out var retLabel)
                .MatchEndBackwards(
                    new CodeMatch(OpCodes.Ldloc_0),
                    new CodeMatch(OpCodes.Ldstr, "legacy"),
                    new CodeMatch(OpCodes.Call, AccessTools.Method(typeof(string), "op_Equality")),
                    new CodeMatch(OpCodes.Brfalse))
                .ThrowIfInvalid("Unable to find the expected injection point")
                .Advance(1)
                .InsertAndAdvance(
                    new CodeInstruction(
                        OpCodes.Ldsfld,
                        AccessTools.Field(
                            typeof(SingletonComponent<MetricsLogger>),
                            nameof(SingletonComponent<MetricsLogger>.Instance))),
                    new CodeInstruction(OpCodes.Ldloc_1),
                    new CodeInstruction(
                        OpCodes.Call,
                        AccessTools.Method(
                            typeof(MetricsLogger),
                            nameof(MetricsLogger.OnClientPerformanceReport))),
                    new CodeInstruction(OpCodes.Brtrue, retLabel));

            return matcher.Instructions();
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogError($"[ServerMetrics] {nameof(BasePlayer_PerformanceReport_Patch)}: " + e.Message);
            return instructions;
        }
    }
}