using System.Text.Json.Serialization;
using CounterStrikeSharp.API.Core;

namespace SundayScrims;

public class Config : BasePluginConfig
{
    [JsonPropertyName("DbServer")] public string DbServer { get; set; } = "";
    [JsonPropertyName("DbUsername")] public string DbUsername { get; set; } = "";
    [JsonPropertyName("DbPassword")] public string DbPassword { get; set; } = "";
    [JsonPropertyName("DbDatabase")] public string DbDatabase { get; set; } = "";
}