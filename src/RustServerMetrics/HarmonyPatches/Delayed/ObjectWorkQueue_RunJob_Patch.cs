using HarmonyLib;
using RustServerMetrics.HarmonyPatches.Utility;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace RustServerMetrics.HarmonyPatches.Delayed
{
    [DelayedHarmonyPatch]
    [HarmonyPatch]
    internal static class ObjectWorkQueue_RunJob_Patch
    {
        private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

        [HarmonyPrepare]
        public static bool Prepare()
        {
            if (!RustServerMetricsLoader.__serverStarted)
            {
                Debug.Log("Note: Cannot patch ObjectWorkQueue_RunJob_Patch yet. We will patch it upon server start.");
                return false;
            }

            return true;
        }
        
        [HarmonyTargetMethods]
        public static IEnumerable<MethodBase> TargetMethods(Harmony harmonyInstance)
        {
            var assemblyCSharp = typeof(BaseNetworkable).Assembly;
            Stack<Type> typesToScan = new Stack<Type>(assemblyCSharp.GetTypes());
            HashSet<string> yielded = new ();
            
            while (typesToScan.TryPop(out Type type))
            {
                var subTypes = type.GetNestedTypes();
                foreach (var t in subTypes)
                    typesToScan.Push(t);

                if (type.BaseType == null || !type.BaseType.Name.Contains("ObjectWorkQueue"))
                    continue;

                if (!yielded.Contains(type.FullName))
                {
                    yielded.Add(type.FullName);
                    yield return AccessTools.Method(type, "RunJob");
                }
            }
        }

        
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
            if (MetricsLogger.Instance == null)
                return;

            var ms = (Stopwatch.GetTimestamp() - __state) * TicksToMs;
            MetricsLogger.Instance.WorkQueueTimes.LogTime(methodName, ms);
        }
    }
}
