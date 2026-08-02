using HarmonyLib;
using Network;
using Newtonsoft.Json;
using RustServerMetrics.Config;
using RustServerMetrics.HarmonyPatches.Utility;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace RustServerMetrics;

public class MetricsLogger : SingletonComponent<MetricsLogger>
{
    private const string ConfigurationPath = "HarmonyMods_Data/ServerMetrics/Configuration.json";
    private readonly StringBuilder _stringBuilder = new();
    private readonly Dictionary<ulong, uint> _perfReportDelayCounter = new();

    private const int PlayerStatsBucketCount = 10;
    private const float PlayerStatsBucketInterval = 1f / PlayerStatsBucketCount;
    private int _playerStatsBucket;

    private class NetworkUpdateData
    {
        public int Count;
        
        public long Bytes;

        public NetworkUpdateData(int count, long bytes)
        {
            Count = count;
            Bytes = bytes;
        }
    }

    private static readonly Message.Type[] MessageTypes = Enum.GetValues(typeof(Message.Type))
                                                              .Cast<Message.Type>()
                                                              .Distinct()
                                                              .ToArray();
    
    private static readonly int MessageTypeSlotOffset = MessageTypes.Min(x => (int)x);
    private static readonly int MessageTypeSlotCount = MessageTypes.Max(x => (int)x) - MessageTypeSlotOffset + 1;

    private static readonly string[] MessageTypeNames = BuildMessageTypeNames();

    private readonly NetworkUpdateData[] _networkUpdates = BuildNetworkUpdates();

    private static string[] BuildMessageTypeNames()
    {
        var names = new string[MessageTypeSlotCount];
        foreach (var messageType in MessageTypes)
        {
            names[(int)messageType - MessageTypeSlotOffset] = messageType.ToString();
        }

        return names;
    }

    private static NetworkUpdateData[] BuildNetworkUpdates()
    {
        var networkUpdates = new NetworkUpdateData[MessageTypeSlotCount];
        foreach (var messageType in MessageTypes)
        {
            networkUpdates[(int)messageType - MessageTypeSlotOffset] = new NetworkUpdateData(0, 0);
        }

        return networkUpdates;
    }

    public readonly MetricsTimeStorage<MethodInfo> ServerInvokes = new("invoke_execution", LogMethodInfo);
    public readonly MetricsTimeStorage<string> ServerRpcCalls = new("rpc_calls", LogMethodName);
    public readonly MetricsTimeStorage<string> WorkQueueTimes = new("work_queue", LogMethodName);
    public readonly MetricsTimeStorage<string> ServerUpdate = new("server_update", LogMethodName);
    public readonly MetricsTimeStorage<string> TimeWarnings = new("timewarnings", LogMethodName);
    public readonly MetricsTimeStorage<string> ServerConsoleCommands = new("console_commands", (builder, command) =>
    {
        builder.Append(",command=\"");
        builder.Append(command);
    });

    public static bool IsReady;
    public bool Ready { get => IsReady; private set => IsReady = value; }
    internal ConfigData Configuration { get; private set; }

    private Uri _baseUri;
    private readonly int _performanceReportRequestId = UnityEngine.Random.Range(-2147483648, 2147483647);
    private ReportUploader _reportUploader;
    private Message.Type _lastMessageType;
    private bool _firstReportGenerated;
    private int _lastFrameID;
    private System.Diagnostics.Process _currentProcess;

    public Uri BaseUri
    {
        get
        {
            if (_baseUri != null)
            {
                return _baseUri;
            }

            _baseUri = new Uri(new Uri(Configuration.DatabaseUrl), 
                               $"/write?db={Configuration.DatabaseName}&precision=ms&u={Configuration.DatabaseUser}&p={Configuration.DatabasePassword}");
            return _baseUri;
        }
    }

    #region Initialization

    internal static void Initialize()
    {
        new GameObject().AddComponent<MetricsLogger>();
    }

    internal void OnServerStarted()
    {
        RustServerMetricsLoader.__serverStarted = true;
            
        Debug.Log($"[ServerMetrics]: Applying Startup Patches");
        var assembly = GetType().Assembly;

        var harmonyInstance = HarmonyLoader.loadedMods.FirstOrDefault(x => x.Assembly == assembly)?.Harmony.harmonyObject;
        if (harmonyInstance == null)
        {
            RustServerMetricsLoader.__harmonyInstance ??= new Harmony("RustServerMetrics" + "PATCH");
            harmonyInstance = RustServerMetricsLoader.__harmonyInstance;
        }

        var nestedTypes = assembly.GetTypes();
        foreach (var nestedType in nestedTypes)
        {
            if (nestedType.GetCustomAttribute<DelayedHarmonyPatchAttribute>(false) == null) continue;
                
            var patchProcessor = new PatchClassProcessor((Harmony)harmonyInstance, nestedType);
            Debug.Log(patchProcessor.Patch() == null ? $"[ServerMetrics]: Failed to apply patch: {nestedType.Name}" : $"[ServerMetrics]: Applied Startup Patch: {nestedType.Name}");
        }
    }

    public override void Awake()
    {
        base.Awake();
        _reportUploader = gameObject.AddComponent<ReportUploader>();
        RegisterCommands();

        LoadConfiguration();
        if (!ValidateConfiguration())
        {
            return;
        }
            
        if (!Configuration.Enabled)
        {
            Debug.LogWarning("[ServerMetrics]: Metrics gathering has been disabled in the configuration");
            return;
        }

        StartLoggingMetrics();
        Ready = true;
    }

    public void StartLoggingMetrics()
    {
        InvokeRepeating(LogNetworkUpdates, UnityEngine.Random.Range(0.25f, 0.75f), 0.5f);
        InvokeRepeating(GatherPlayerStatsBucket, UnityEngine.Random.Range(0.5f, 1.5f), PlayerStatsBucketInterval);

        InvokeRepeating(ServerInvokes.SerializeToStringBuilder, UnityEngine.Random.Range(0f, 1f), 1f);
        InvokeRepeating(ServerRpcCalls.SerializeToStringBuilder, UnityEngine.Random.Range(0f, 1f), 1f);
        InvokeRepeating(ServerConsoleCommands.SerializeToStringBuilder, UnityEngine.Random.Range(0f, 1f), 1f);
        InvokeRepeating(WorkQueueTimes.SerializeToStringBuilder, UnityEngine.Random.Range(0f, 1f), 1f);
        InvokeRepeating(ServerUpdate.SerializeToStringBuilder, UnityEngine.Random.Range(0f, 1f), 1f);
        InvokeRepeating(TimeWarnings.SerializeToStringBuilder, UnityEngine.Random.Range(0f, 1f), 1f);
    }

    #endregion


    internal void OnPlayerInit(BasePlayer player)
    {
        if (!Ready) return;
        if (!Configuration.GatherPlayerMetrics) return;

        _perfReportDelayCounter[player.userID] = (uint)UnityEngine.Random.Range(0, 5);
    }

    internal void OnPlayerDisconnected(BasePlayer player)
    {
        if (!Ready) return;
        _perfReportDelayCounter.Remove(player.userID);
    }

    internal void OnNetWritePacketID(Message.Type messageType)
    {
        if (!Ready)
        {
            return;
        }
            
        _lastMessageType = messageType;
    }

    internal void OnNetWriteSend(NetWrite write, SendInfo sendInfo)
    {
        if (!Ready)
        {
            return;
        }
            
        var slot = (int)_lastMessageType - MessageTypeSlotOffset;
        if ((uint)slot >= (uint)_networkUpdates.Length)
        {
            return;
        }

        var data = _networkUpdates[slot];
        if (data == null)
        {
            return;
        }

        if (sendInfo.connection != null)
        {
            data.Count++;
            data.Bytes += write.Length;
        }
        else if (sendInfo.connections != null)
        {
            var count = sendInfo.connections.Count;
            data.Count += count;
            data.Bytes += (long)write.Length * count;
        }
    }

    internal void OnOxidePluginMetrics(Dictionary<string, double> metrics)
    {
        if (!Ready) return;
        if (metrics.Count < 1) return;

        foreach (var metric in metrics)
        {
            UploadPacket("oxide_plugins", metric, (builder, report) =>
            {
                builder.Append(",plugin=\"");
                AppendPluginNameSanitized(builder, report.Key);
                builder.Append("\" hookTime=");
                builder.Append(report.Value);
            });
        }
    }

    internal bool OnClientPerformanceReport(ProtoBuf.PerformanceReport clientPerformanceReport)
    {
        if (clientPerformanceReport.request_id != _performanceReportRequestId)
        {
            return false;
        }

        UploadPacket("client_performance", clientPerformanceReport, (builder, report) =>
        {
            builder.Append(",steamid=");
            builder.Append(report.user_id);
            builder.Append(" memory=");
            builder.Append(report.memory_system);
            builder.Append("i,fps=");
            builder.Append(report.fps);
        });

        return true;
    }

    private void GatherPlayerStatsBucket()
    {
        if (!Ready) return;
        if (!Configuration.GatherPlayerMetrics) return;

        var players = BasePlayer.activePlayerList;
        var bucket = _playerStatsBucket;
        _playerStatsBucket = bucket + 1 < PlayerStatsBucketCount ? bucket + 1 : 0;

        for (var i = bucket; i < players.Count; i += PlayerStatsBucketCount)
        {
            var player = players[i];
            if (player == null) continue;

            GatherPlayerSecondStats(player);
        }
    }

    private void GatherPlayerSecondStats(BasePlayer player)
    {
        if (player.net?.connection == null) return;

        if (!player.IsReceivingSnapshot)
        {
            _perfReportDelayCounter.TryGetValue(player.userID, out var perfReportCounter);
            if (perfReportCounter < 4)
            {
                _perfReportDelayCounter[player.userID] = perfReportCounter + 1;
            }
            else
            {
                _perfReportDelayCounter[player.userID] = 0;
                player.ClientRPC(RpcTarget.Player("GetPerformanceReport", player), "legacy", _performanceReportRequestId);
            }
        }

        UploadPacket("connection_latency", player, (builder, basePlayer) =>
        {
            var ip = basePlayer.net.connection.ipaddress;
            var portSeparator = ip.LastIndexOf(':');

            builder.Append(",steamid=");
            builder.Append(basePlayer.UserIDString);
            builder.Append(",ip=");
            builder.Append(ip, 0, portSeparator < 0 ? ip.Length : portSeparator);
            builder.Append(" ping=");
            builder.Append(Net.sv.GetAveragePing(basePlayer.net.connection));
            builder.Append("i,packet_loss=");
            builder.Append(Net.sv.GetStat(basePlayer.net.connection, BaseNetwork.StatTypeLong.PacketLossLastSecond));
            builder.Append("i ");
        });
    }

    private void LogNetworkUpdates()
    {
        if (_networkUpdates.Length < 1) return;
        var serverTag = Configuration.ServerTag;
        var epochNow = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _stringBuilder.Clear();
        _stringBuilder.Append("network_updates,server=");
        _stringBuilder.Append(serverTag);
        _stringBuilder.Append(" ");

        var isFirstField = true;
        for (var slot = 0; slot < _networkUpdates.Length; slot++)
        {
            var value = _networkUpdates[slot];
            if (value == null) continue;

            var key = MessageTypeNames[slot];

            if (!isFirstField)
            {
                _stringBuilder.Append(",");
            }

            isFirstField = false;

            // Count first named {type}
            _stringBuilder.Append(key);
            _stringBuilder.Append("=");
            _stringBuilder.Append(value.Count);
            _stringBuilder.Append("i");
            value.Count = 0;

            // Bytes second named as "{type}_bytes"
            _stringBuilder.Append(",");
            _stringBuilder.Append(key);
            _stringBuilder.Append("_bytes");
            _stringBuilder.Append("=");
            _stringBuilder.Append(value.Bytes);
            _stringBuilder.Append("i");
            value.Bytes = 0;
        }

        _stringBuilder.Append(" ");
        _stringBuilder.Append(epochNow);
        _reportUploader.AddToSendBuffer(_stringBuilder);
    }

    internal void OnPerformanceReportGenerated()
    {
        if (!Ready) return;
        if (!_firstReportGenerated)
        {
            _firstReportGenerated = true;
            return;
        }
        var current = Performance.current;

        if (current.frameID == _lastFrameID) return;
        _lastFrameID = current.frameID;

        var serverTag = Configuration.ServerTag;
        var epochNow = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();

        LogPerformanceReport(current, epochNow, serverTag);
    }

    private void LogPerformanceReport(Performance.Tick current, string epochNow, string serverTag)
    {
        _stringBuilder.Clear();

        _stringBuilder.Append("framerate,server=");
        _stringBuilder.Append(serverTag);
        _stringBuilder.Append(" instant=");
        _stringBuilder.Append(current.frameRate);
        _stringBuilder.Append(",average=");
        _stringBuilder.Append(current.frameRateAverage);
        _stringBuilder.Append(" ");
        _stringBuilder.Append(epochNow);
        _stringBuilder.Append("\n");

        _stringBuilder.Append("frametime,server=");
        _stringBuilder.Append(serverTag);
        _stringBuilder.Append(" instant=");
        _stringBuilder.Append(current.frameTime);
        _stringBuilder.Append(",average=");
        _stringBuilder.Append(current.frameTimeAverage);
        _stringBuilder.Append(" ");
        _stringBuilder.Append(epochNow);
        _stringBuilder.Append("\n");

        _stringBuilder.Append("memory,server=");
        _stringBuilder.Append(serverTag);
        _stringBuilder.Append(" used=");
        _stringBuilder.Append(GetMemoryUsage(current));
        _stringBuilder.Append("i,collections=");
        _stringBuilder.Append(current.memoryCollections);
        _stringBuilder.Append("i,allocations=");
        _stringBuilder.Append(current.memoryAllocations);
        _stringBuilder.Append("i,gc=");
        _stringBuilder.Append(current.gcTriggered);
        _stringBuilder.Append(" ");
        _stringBuilder.Append(epochNow);
        _stringBuilder.Append("\n");

        _stringBuilder.Append("tasks,server=");
        _stringBuilder.Append(serverTag);
        _stringBuilder.Append(" load_balancer=");
        _stringBuilder.Append(current.loadBalancerTasks);
        _stringBuilder.Append("i,invoke_handler=");
        _stringBuilder.Append(current.invokeHandlerTasks);
        _stringBuilder.Append("i,workshop_skins_queue=");
        _stringBuilder.Append(current.workshopSkinsQueued);
        _stringBuilder.Append("i ");
        _stringBuilder.Append(epochNow);
        _stringBuilder.Append("\n");

        var bytesReceivedLastSecond = Net.sv.GetStat(null, BaseNetwork.StatTypeLong.BytesReceived_LastSecond);
        var bytesSentLastSecond = Net.sv.GetStat(null, BaseNetwork.StatTypeLong.BytesSent_LastSecond);
        var packetLossLastSecond = Net.sv.GetStat(null, BaseNetwork.StatTypeLong.PacketLossLastSecond);

        _stringBuilder.Append("network,server=");
        _stringBuilder.Append(serverTag);
        _stringBuilder.Append(" bytes_received=");
        _stringBuilder.Append(bytesReceivedLastSecond);
        _stringBuilder.Append("i,bytes_sent=");
        _stringBuilder.Append(bytesSentLastSecond);
        _stringBuilder.Append("i,packet_loss=");
        _stringBuilder.Append(packetLossLastSecond);
        _stringBuilder.Append("i ");
        _stringBuilder.Append(epochNow);
        _stringBuilder.Append("\n");

        _stringBuilder.Append("players,server=");
        _stringBuilder.Append(serverTag);
        _stringBuilder.Append(" count=");
        _stringBuilder.Append(BasePlayer.activePlayerList.Count);
        _stringBuilder.Append("i,joining=");
        _stringBuilder.Append(ServerMgr.Instance.connectionQueue.Joining);
        _stringBuilder.Append("i,queued=");
        _stringBuilder.Append(ServerMgr.Instance.connectionQueue.Queued);
        _stringBuilder.Append("i ");
        _stringBuilder.Append(epochNow);
        _stringBuilder.Append("\n");

        _stringBuilder.Append("entities,server=");
        _stringBuilder.Append(serverTag);
        _stringBuilder.Append(" count=");
        _stringBuilder.Append(BaseNetworkable.serverEntities.Count);
        _stringBuilder.Append("i ");
        _stringBuilder.Append(epochNow);

        _reportUploader.AddToSendBuffer(_stringBuilder);
    }


    #region Helpers

    public void UploadPacket<T>(string id, T data, Action<StringBuilder, T> serializer)
    {
        var serverTag = Configuration.ServerTag;
        var epochNow = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _stringBuilder.Clear();
        _stringBuilder.Append(id);
        _stringBuilder.Append(",server=");
        _stringBuilder.Append(serverTag);

        serializer.Invoke(_stringBuilder, data);

        _stringBuilder.Append(" ");
        _stringBuilder.Append(epochNow);

        AddToSendBuffer(_stringBuilder);
    }

    public void AddToSendBuffer(string toString) => _reportUploader.AddToSendBuffer(toString);

    public void AddToSendBuffer(StringBuilder payload) => _reportUploader.AddToSendBuffer(payload);

    private long GetMemoryUsage(Performance.Tick performanceTick)
    {
        if (performanceTick.memoryUsageSystem > 0)
            return performanceTick.memoryUsageSystem;

        _currentProcess ??= System.Diagnostics.Process.GetCurrentProcess();

        _currentProcess.Refresh();
        return _currentProcess.WorkingSet64 / 1024 / 1024;
    }

    private static void AppendPluginNameSanitized(StringBuilder builder, string name)
    {
        foreach (var c in name)
        {
            if (char.IsLetterOrDigit(c))
                builder.Append(c);
        }
    }

    private static void LogMethodInfo(StringBuilder builder, MethodInfo info)
    {
        builder.Append(",behaviour=\"");
        builder.Append(info.DeclaringType?.Name);
        builder.Append("\",method=\"");
        builder.Append(info.Name);
    }

    private static void LogMethodName(StringBuilder builder, string info)
    {
        builder.Append(",behaviour=\"");

        var start = 0;
        var dot = info.IndexOf('.');
        while (dot >= 0)
        {
            builder.Append(info, start, dot - start);
            builder.Append("\",method=\"");
            start = dot + 1;
            dot = info.IndexOf('.', start);
        }
        builder.Append(info, start, info.Length - start);
    }
    #endregion
        
    #region Commands

    private void RegisterCommands()
    {
        const string commandPrefix = "servermetrics";
        var reloadCfgCommand = new ConsoleSystem.Command()
        {
            Name = "reloadcfg",
            Parent = commandPrefix,
            FullName = commandPrefix + "." + "reloadcfg",
            ServerAdmin = true,
            Variable = false,
            Call = ReloadCfgCommand
        };

        var statusCommand = new ConsoleSystem.Command()
        {
            Name = "status",
            Parent = commandPrefix,
            FullName = commandPrefix + "." + "status",
            ServerAdmin = true,
            Variable = false,
            Call = StatusCommand
        };

        ConsoleSystem.Index.Server.Dict[commandPrefix + "." + "reloadcfg"] = reloadCfgCommand;
        ConsoleSystem.Index.Server.Dict[commandPrefix + "." + "status"] = statusCommand;

        // Would be nice if this had a public setter, or better yet, a register command helper
        // update: now it does
        ConsoleSystem.Index.All = ConsoleSystem.Index.All.Concat(new[] { reloadCfgCommand, statusCommand }).ToArray();
    }

    private void StatusCommand(ConsoleSystem.Arg arg)
    {
        _stringBuilder.Clear();
        _stringBuilder.AppendLine("[ServerMetrics]: Status");
        _stringBuilder.AppendLine("Overview");
        _stringBuilder.Append("\tReady: "); _stringBuilder.Append(Ready); _stringBuilder.AppendLine();
        _stringBuilder.AppendLine("Report Uploader:");
        _stringBuilder.Append("\tRunning: "); _stringBuilder.Append(_reportUploader.IsRunning); _stringBuilder.AppendLine();
        _stringBuilder.Append("\tIn Buffer: "); _stringBuilder.Append(_reportUploader.BufferSize); _stringBuilder.AppendLine();
        _stringBuilder.Append("\tIn Flight: "); _stringBuilder.Append(_reportUploader.PendingBatches); _stringBuilder.AppendLine();
        _stringBuilder.Append("\tDropped (total): "); _stringBuilder.Append(_reportUploader.TotalDroppedReports); _stringBuilder.AppendLine();
        arg.ReplyWith(_stringBuilder.ToString());
    }

    private void ReloadCfgCommand(ConsoleSystem.Arg arg)
    {
        LoadConfiguration();
        if (!ValidateConfiguration() || Configuration.Enabled == false)
        {
            Ready = false;

            // why is there no cancel all invokes method ...
            var list = new List<InvokeAction>();
            InvokeHandler.FindInvokes(this, list);
            foreach (var invoke in list)
            {
                CancelInvoke(invoke.action);
            }

            _perfReportDelayCounter.Clear();
            _reportUploader.Stop();

            if (!Configuration.Enabled)
            {
                arg.ReplyWith("[ServerMetrics]: Metrics gathering has been disabled in the configuration");
                return;
            }
        }
        else if (!Ready)
        {
            Ready = true;
            foreach (var player in BasePlayer.activePlayerList)
            {
                OnPlayerInit(player);
            }

            StartLoggingMetrics();
        }
        arg.ReplyWith("[ServerMetrics]: Configuration reloaded");
    }

    #endregion
        
    #region Configuration

    private bool ValidateConfiguration()
    {
        if (Configuration == null) return false;

        var valid = true;
        if (Configuration.DatabaseUrl == ConfigData.DefaultInfluxDbUrl)
        {
            Debug.LogError("[ServerMetrics]: Default database url detected in configuration, loading aborted");
            valid = false;
        }

        if (Configuration.DatabaseName == ConfigData.DefaultInfluxDBName)
        {
            Debug.LogError("[ServerMetrics]: Default database name detected in configuration, loading aborted");
            valid = false;
        }

        if (Configuration.ServerTag == ConfigData.DefaultServerTag)
        {
            Debug.LogError("[ServerMetrics]: Default server tag detected in configuration, loading aborted");
            valid = false;
        }

        return valid;
    }

    private void LoadConfiguration()
    {
        try
        {
            var configStr = File.ReadAllText(ConfigurationPath);
            Configuration = JsonConvert.DeserializeObject<ConfigData>(configStr) ?? new ConfigData();
            var uri = new Uri(Configuration.DatabaseUrl);
            _baseUri = new Uri(uri, $"/write?db={Configuration.DatabaseName}&precision=ms&u={Configuration.DatabaseUser}&p={Configuration.DatabasePassword}");
        }
        catch
        {
            Debug.LogError("[ServerMetrics]: The configuration seems to be missing or malformed. Defaults will be loaded.");
            Configuration = new ConfigData();

            if (File.Exists(ConfigurationPath))
            {
                return;
            }
        }
        SaveConfiguration();
    }

    private void SaveConfiguration()
    {
        try
        {
            var configFileInfo = new FileInfo(ConfigurationPath);
            if (configFileInfo.Directory is { Exists: false })
            {
                configFileInfo.Directory.Create();
            }
                
            var serializedConfiguration = JsonConvert.SerializeObject(Configuration, Formatting.Indented);
            File.WriteAllText(ConfigurationPath, serializedConfiguration);
        }
        catch (Exception ex)
        {
            Debug.LogError("[ServerMetrics]: Failed to write configuration file");
            Debug.LogException(ex);
        }
    }

    #endregion
}