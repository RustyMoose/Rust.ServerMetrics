using HarmonyLib;
using RustServerMetrics.HarmonyPatches.Utility;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

// ReSharper disable InconsistentNaming
// ReSharper disable PossibleMultipleEnumeration

namespace RustServerMetrics.HarmonyPatches.Delayed;

[DelayedHarmonyPatch]
[HarmonyPatch]
internal class RPCServer_Attribute_Method_Patch
{
    private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

    [HarmonyPrepare]
    public static bool Prepare()
    {
        if (RustServerMetricsLoader.__serverStarted)
        {
            return true;
        }

        Debug.Log($"[ServerMetrics] Note: Cannot patch {nameof(RPCServer_Attribute_Method_Patch)} yet. We will patch it upon server start.");
        return false;
    }

    [HarmonyTargetMethods]
    public static IEnumerable<MethodBase> TargetMethods()
    {
        var baseNetworkableType = typeof(BaseNetworkable);
        var baseNetworkableAssembly = baseNetworkableType.Assembly;
        var typesToScan = new Stack<Type>(baseNetworkableAssembly.GetTypes());

        while (typesToScan.TryPop(out var type))
        {
            foreach (var subType in type.GetNestedTypes())
            {
                typesToScan.Push(subType);
            }

            foreach (var method in type.GetMethods())
            {
                if (method.DeclaringType == method.ReflectedType && method.GetCustomAttribute<BaseEntity.RPC_Server>() != null)
                {
                    yield return method;
                }
            }
        }
    }

    [HarmonyTranspiler]
    public static IEnumerable<CodeInstruction> Transpiler(
        IEnumerable<CodeInstruction> instructions, 
        MethodBase methodBase, 
        ILGenerator ilGenerator)
    {
        try
        {
            var invokedMethod = $"{methodBase.DeclaringType?.Name}.{methodBase.Name}";
            
            var matcher = new CodeMatcher(instructions, ilGenerator)
                .Start()
                .DeclareLocal(typeof(long), out var timestampLocal)
                .InsertAndAdvance(
                    new CodeInstruction(
                        OpCodes.Call, 
                        AccessTools.Method(
                            typeof(Stopwatch), 
                            nameof(Stopwatch.GetTimestamp))),
                    new CodeInstruction(OpCodes.Stloc, timestampLocal))
                .MatchStartForward(new CodeMatch(OpCodes.Ret))
                .Repeat(repeatingMatcher =>
                {
                    CodeInstruction[] toInsert =
                    [
                        new(OpCodes.Ldstr, invokedMethod),
                        new(OpCodes.Ldloc, timestampLocal),
                        new(OpCodes.Call, AccessTools.Method(typeof(RPCServer_Attribute_Method_Patch), nameof(RecordInvokeTime)))
                    ];
                    
                    repeatingMatcher.Instruction.MoveLabelsTo(toInsert[0]);
                    repeatingMatcher.InsertAndAdvance(toInsert);
                    repeatingMatcher.Advance(1);
                });
            
            return matcher.Instructions();
        }
        catch (Exception e)
        {
            Debug.LogError($"[ServerMetrics] {nameof(RPCServer_Attribute_Method_Patch)}: " + e.Message);
            return instructions;
        }
    }
    
    private static void RecordInvokeTime(string methodName, long startTimestamp)
    {
        if (!MetricsLogger.IsReady)
        {
            return;
        }

        var ms = (Stopwatch.GetTimestamp() - startTimestamp) * TicksToMs;
        MetricsLogger.Instance.ServerRpcCalls.LogTime(methodName, ms);
    }
}