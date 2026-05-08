using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;

// ReSharper disable InconsistentNaming

namespace RustServerMetrics.HarmonyPatches;

[HarmonyPatch(typeof(BasePlayer), nameof(BasePlayer.PerformanceReport))]
public class BasePlayer_PerformanceReport_Patch
{
    [HarmonyTranspiler]
    public static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> originalInstructions, 
                                                         ILGenerator ilGenerator)
    {
        var instructionsList = originalInstructions.ToList();
        var jumpLabel = ilGenerator.DefineLabel();
        
        CodeMatch[] needle = 
        [
            new(OpCodes.Ldloc_0),
            new(OpCodes.Ldstr, "legacy"),
            new(OpCodes.Call, AccessTools.Method(typeof(String), "op_Equality")),
            new(OpCodes.Brfalse)
        ];

        CodeInstruction[] injection =
        [
            new(OpCodes.Ldsfld, AccessTools.Field(typeof(SingletonComponent<MetricsLogger>), nameof(SingletonComponent<MetricsLogger>.Instance))),
            new(OpCodes.Ldloc_1),
            new(OpCodes.Call, AccessTools.Method(typeof(MetricsLogger), nameof(MetricsLogger.OnClientPerformanceReport))),
            new(OpCodes.Brtrue, jumpLabel)
        ];

        try
        {
            var codeMatcher = new CodeMatcher(instructionsList);

            codeMatcher.MatchEndForward(needle)
                       .ThrowIfInvalid("Unable to find the expected injection point")
                       .Advance(1)
                       .InsertAndAdvance(injection)
                       .End()
                       .AddLabels([jumpLabel]);

            return codeMatcher.Instructions();
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogError($"[ServerMetrics] {nameof(BasePlayer_PerformanceReport_Patch)}: " + e.Message);
            return instructionsList;
        }
    }
}