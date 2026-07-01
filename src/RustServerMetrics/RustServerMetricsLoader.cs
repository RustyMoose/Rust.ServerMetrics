using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace RustServerMetrics;

public class RustServerMetricsLoader : IHarmonyModHooks
{
    public static bool __serverStarted = false;
    
    public static Harmony __harmonyInstance;
    
    public static List<Harmony> __modTimeWarningsHarmonyInstances = [];
    
    public void OnLoaded(OnHarmonyModLoadedArgs args)
    {
        if (!Bootstrap.bootstrapInitRun)
        {
            return;
        }
        
        MetricsLogger.Initialize();

        if (MetricsLogger.Instance != null)
        {
            MetricsLogger.Instance.OnServerStarted();
        }
    }

    public void OnUnloaded(OnHarmonyModUnloadedArgs args)
    {
        MetricsLogger.IsReady = false;

        __harmonyInstance?.UnpatchAll();
        foreach (var instance in __modTimeWarningsHarmonyInstances)
        {
            instance?.UnpatchAll();
        }

        if (MetricsLogger.Instance != null)
        {
            Object.DestroyImmediate(MetricsLogger.Instance);
        }
    }
}