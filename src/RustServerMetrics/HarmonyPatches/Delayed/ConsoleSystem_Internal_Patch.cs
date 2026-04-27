using HarmonyLib;
using RustServerMetrics.HarmonyPatches.Utility;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace RustServerMetrics.HarmonyPatches.Delayed
{
    [DelayedHarmonyPatch]
    [HarmonyPatch]
    internal class ConsoleSystem_Internal_Patch
    {
        private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

        [HarmonyPrepare]
        public static bool Prepare()
        {
            if (!RustServerMetricsLoader.__serverStarted)
            {
                Debug.Log("Note: Cannot patch ConsoleSystem_Internal_Patch yet. We will patch it upon server start.");
                return false;
            }

            return true;
        }
        
        [HarmonyTargetMethods]
        public static IEnumerable<MethodBase> TargetMethods(Harmony harmonyInstance)
        {
            yield return AccessTools.DeclaredMethod(typeof(ConsoleSystem), nameof(ConsoleSystem.Internal));
        }

        [HarmonyPrefix]
        public static void Prefix(ref long __state)
        {
            __state = Stopwatch.GetTimestamp();
        }

        [HarmonyPostfix]
        public static void Postfix(ConsoleSystem.Arg arg, long __state)
        {
            if (MetricsLogger.Instance == null)
                return;

            var ms = (Stopwatch.GetTimestamp() - __state) * TicksToMs;
            MetricsLogger.Instance.ServerConsoleCommands.LogTime(arg.cmd.FullName, ms);
        }
    }
}
