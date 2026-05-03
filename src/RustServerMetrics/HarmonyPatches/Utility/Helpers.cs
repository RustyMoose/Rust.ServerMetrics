using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;

namespace RustServerMetrics.HarmonyPatches.Utility;

public static class Helpers
{
    public static IEnumerable<CodeInstruction> Postfix(IEnumerable<CodeInstruction> originalInstructions, Delegate postfix, params CodeInstruction[] loads)
    {
        var ret = originalInstructions.ToList();

        for (var i = ret.Count - 1; i >= 0; i--)
        {
            var code = ret[i];
            if (code.opcode != OpCodes.Ret)
            {
                continue;
            }
            
            ret.Insert(i, new CodeInstruction(OpCodes.Call, postfix.Method));

            if (loads is { Length: > 0 })
            {
                for (var j = loads.Length - 1; j >= 0; j--)
                { 
                    ret.Insert(i, new CodeInstruction(loads[j]));
                }
            }

            if (code == ret[i] || code.labels is not { Count: > 0 })
            {
                continue;
            }
            
            while (code.labels.Count > 0)
            {
                var label = code.labels[0];
                ret[i].labels.Add(label);
                code.labels.RemoveAt(0);
            }
        } 
        
        return ret;
    }
}