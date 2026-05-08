using System;
using HarmonyLib;
using System.Collections.Generic;
using System.Reflection.Emit;
using UnityEngine;

// ReSharper disable InconsistentNaming
// ReSharper disable PossibleMultipleEnumeration

namespace RustServerMetrics.HarmonyPatches;

[HarmonyPatch(typeof(BasePlayer), nameof(BasePlayer.PlayerInit))]
public class BasePlayer_PlayerInit_Patch
{
    [HarmonyTranspiler]
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        try
        {
            var matcher = new CodeMatcher(instructions)
                          .MatchEndForward(
                              new CodeMatch(OpCodes.Call,
                                            AccessTools.Method(typeof(EACServer),
                                                               nameof(EACServer.OnStartLoading))))
                          .ThrowIfInvalid("Failed to find insertion point for BasePlayer.PlayerInit")
                          .Advance(1)
                          .InsertAndAdvance(
                              new CodeInstruction(OpCodes.Ldsfld,
                                                  AccessTools.Field(typeof(SingletonComponent<MetricsLogger>),
                                                                    nameof(SingletonComponent<MetricsLogger>.Instance))),
                              new CodeInstruction(OpCodes.Ldarg_0),
                              new CodeInstruction(OpCodes.Call,
                                                  AccessTools.Method(typeof(MetricsLogger),
                                                                     nameof(MetricsLogger.OnPlayerInit))));

            return matcher.Instructions();
        }
        catch (Exception e)
        {
            Debug.LogError($"[ServerMetrics] {nameof(BasePlayer_PlayerInit_Patch)}: " + e.Message);
            return instructions;
        }
    }
}