using System.Text.Json;
using System.Text.Json.Serialization;

namespace bleak.Data.Transformer.Config;

/// <summary>Loads an <see cref="EtlConfig"/> from JSON. Comments and trailing commas are allowed so the
/// human/AI-authored config can be annotated (jsonc-style).</summary>
public static class EtlConfigLoader
{
    public static readonly JsonSerializerOptions Options = BuildOptions();

    private static JsonSerializerOptions BuildOptions()
    {
        var o = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return o;
    }

    public static EtlConfig FromJson(string json)
    {
        var config = JsonSerializer.Deserialize<EtlConfig>(json, Options)
            ?? throw new InvalidOperationException("Config deserialized to null.");
        return config;
    }

    public static EtlConfig FromFile(string path)
    {
        var json = File.ReadAllText(path);
        return FromJson(json);
    }
}
