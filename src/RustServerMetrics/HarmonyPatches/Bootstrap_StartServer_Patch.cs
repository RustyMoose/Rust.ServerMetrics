using System;
using HarmonyLib;
using System.Collections.Generic;
using System.Reflection.Emit;
using UnityEngine;

// ReSharper disable InconsistentNaming
// ReSharper disable PossibleMultipleEnumeration

namespace RustServerMetrics.HarmonyPatches;

[HarmonyPatch(typeof(Bootstrap), nameof(Bootstrap.StartServer))]
public class Bootstrap_StartServer_Patch
{
    [HarmonyTranspiler]
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        try
        {
            var matcher = new CodeMatcher(instructions)
                .Start()
                .InsertAndAdvance(
                    new CodeInstruction(
                        OpCodes.Call,
                        AccessTools.Method(
                            typeof(MetricsLogger),
                            nameof(MetricsLogger.Initialize))));

            return matcher.Instructions();
        }
        catch (Exception e)
        {
            Debug.LogError($"[ServerMetrics] {nameof(Bootstrap_StartServer_Patch)}: " + e.Message);
            return instructions;
        }
    }
}