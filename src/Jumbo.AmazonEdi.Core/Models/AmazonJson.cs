using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jumbo.AmazonEdi.Core.Models;

/// <summary>Single JSON configuration used for everything we send to, or archive from, Amazon.
/// Serializing with anything else risks a payload that differs from what we stored.</summary>
public static class AmazonJson
{
    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public static readonly JsonSerializerOptions ArchiveOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, SerializerOptions);

    public static string SerializeForArchive<T>(T value) => JsonSerializer.Serialize(value, ArchiveOptions);
}
