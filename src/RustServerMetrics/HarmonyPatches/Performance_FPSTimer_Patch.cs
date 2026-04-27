using HarmonyLib;

namespace RustServerMetrics.HarmonyPatches
{
    [HarmonyPatch(typeof(Performance), "FPSTimer")]
    public class Performance_FPSTimer_Patch
    {
        [HarmonyPostfix]
        public static void Postfix()
        {
            if (!MetricsLogger.IsReady) return;
            MetricsLogger.Instance.OnPerformanceReportGenerated();
        }
    }
}
