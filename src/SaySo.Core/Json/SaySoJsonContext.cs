using System.Text.Json.Serialization;
using SaySo.Core.Settings;
using SaySo.Core.Storage;

namespace SaySo.Core.Json;

/// <summary>Source-generated JSON (совместимо с NativeAOT).</summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(Secrets))]
[JsonSerializable(typeof(List<HistoryEntry>))]
public partial class SaySoJsonContext : JsonSerializerContext;
