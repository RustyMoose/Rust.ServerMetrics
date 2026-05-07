using Newtonsoft.Json;

namespace RustServerMetrics.Config;

class ConfigData
{
    #region Defaults
    
    public const string DefaultInfluxDbUrl = "http://exampledb.com";
    
    public const string DefaultInfluxDBName = "CHANGEME_rust_server_example";
    
    public const string DefaultInfluxDBUser = "admin";
    
    public const string DefaultInfluxDBPassword = "adminadmin";
    
    public const string DefaultServerTag = "CHANGEME-01";
    
    #endregion

    [JsonProperty(PropertyName = "Enabled")]
    public bool Enabled;

    [JsonProperty(PropertyName = "Influx Database Url")]
    public string DatabaseUrl = DefaultInfluxDbUrl;

    [JsonProperty(PropertyName = "Influx Database Name")]
    public string DatabaseName = DefaultInfluxDBName;

    [JsonProperty(PropertyName = "Influx Database User")]
    public string DatabaseUser = DefaultInfluxDBUser;

    [JsonProperty(PropertyName = "Influx Database Password")]
    public string DatabasePassword = DefaultInfluxDBPassword;

    [JsonProperty(PropertyName = "Server Tag")]
    public string ServerTag = DefaultServerTag;

    [JsonProperty(PropertyName = "Debug Logging")]
    public bool DebugLogging;

    [JsonProperty(PropertyName = "Amount of metrics to submit in each request")]
    public ushort BatchSize = 1000;

    [JsonProperty(PropertyName = "Gather Player Averages (Client FPS, Client Latency, Player FPS, Player Memory, Player Latency, Player Packet Loss)")]
    public bool GatherPlayerMetrics = true;
}