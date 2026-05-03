using System;
using System.Collections.Generic;
using System.Text;

namespace RustServerMetrics.HarmonyPatches.Utility;

public class MetricsTimeStorage<TKey>(string metricKey, Action<StringBuilder, TKey> stringBuilderSerializer)
{
    private readonly Dictionary<TKey, double> _dict = new ();
    
    private readonly StringBuilder _sb = new();

    public void LogTime(TKey key, double milliseconds)
    {
        if (!MetricsLogger.IsReady)
            return;
        
        if (!_dict.TryGetValue(key, out var currentDuration))
        {
            _dict.Add(key, milliseconds);
            return;
        }
        
        _dict[key] = currentDuration + milliseconds;        
    }

    public void SerializeToStringBuilder()
    {
        if (!MetricsLogger.IsReady)
            return;

        var instance = MetricsLogger.Instance;
        var serverTag = instance.Configuration.ServerTag;
        var epochNow = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        foreach (var item in _dict)
        {
            _sb.Clear();

            _sb.Append(metricKey);
            _sb.Append(",server=");
            _sb.Append(serverTag);

            stringBuilderSerializer.Invoke(_sb, item.Key);

            _sb.Append("\" duration=");
            _sb.Append((float)item.Value);
            _sb.Append(" ");
            _sb.Append(epochNow);
            instance.AddToSendBuffer(_sb.ToString());
        }

        _dict.Clear();
    }
}