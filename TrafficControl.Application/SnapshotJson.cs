using System.Text.Json;
using System.Text.Json.Serialization;

namespace TrafficControl.Application;

/// <summary>JSON used for the config and state snapshots stored in the database. Enums are stored as readable names.</summary>
public static class SnapshotJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options) ?? throw new InvalidOperationException("Stored snapshot is empty or corrupt.");
}
