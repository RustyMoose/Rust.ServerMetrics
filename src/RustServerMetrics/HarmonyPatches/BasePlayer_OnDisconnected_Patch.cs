using System;
using HarmonyLib;
using System.Collections.Generic;
using System.Reflection.Emit;
using UnityEngine;

// ReSharper disable InconsistentNaming
// ReSharper disable PossibleMultipleEnumeration

namespace RustServerMetrics.HarmonyPatches;

[HarmonyPatch(typeof(BasePlayer), nameof(BasePlayer.OnDisconnected))]
public class BasePlayer_OnDisconnected_Patch
{
    [HarmonyTranspiler]
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        try
        {
            var matcher = new CodeMatcher(instructions)
                          .Start()
                          .InsertAndAdvance(
                              new CodeInstruction(OpCodes.Ldsfld,
                                                  AccessTools.Field(typeof(SingletonComponent<MetricsLogger>),
                                                                    nameof(SingletonComponent<MetricsLogger>.Instance))),
                              new CodeInstruction(OpCodes.Ldarg_0),
                              new CodeInstruction(OpCodes.Call,
                                                  AccessTools.Method(typeof(MetricsLogger),
                                                                     nameof(MetricsLogger.OnPlayerDisconnected))));

            return matcher.Instructions();
        }
        catch (Exception e)
        {
            Debug.LogError($"[ServerMetrics] {nameof(BasePlayer_OnDisconnected_Patch)}: " + e.Message);
            return instructions;
        }
    }
}