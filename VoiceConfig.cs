using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeTts;

/// <summary>
/// Loaded from config.json next to the executable. Edit config.json to change settings
/// without recompiling. The file is created with defaults if missing.
/// </summary>
public sealed class VoiceConfig
{
    /// <summary>
    /// Speaking rate. Maps to WinRT SpeakingRate (0.5–6.0).
    /// Range: -10 (slowest) to 10 (fastest). 0 = natural default. Decimals supported (e.g. 0.5).
    /// </summary>
    [JsonPropertyName("rate")]
    public double Rate { get; set; } = 0;

    /// <summary>
    /// Volume from 0 (silent) to 100 (full).
    /// </summary>
    [JsonPropertyName("volume")]
    public int Volume { get; set; } = 100;

    /// <summary>
    /// Path to piper.exe, relative to the config file or absolute.
    /// When set and the file exists, Piper is used instead of WinRT.
    /// Example: "piper\\piper.exe"
    /// </summary>
    [JsonPropertyName("piperExe")]
    public string? PiperExe { get; set; }

    /// <summary>
    /// Path to the Piper voice model (.onnx), relative to the config file or absolute.
    /// Example: "piper\\en_US-ryan-high.onnx"
    /// </summary>
    [JsonPropertyName("piperModel")]
    public string? PiperModel { get; set; }

    /// <summary>
    /// Named pipe identifier. Must match the name used in tts-hook.ps1.
    /// </summary>
    [JsonPropertyName("pipeName")]
    public string PipeName { get; set; } = "ClaudeTTS";

    // -------------------------------------------------------------------------

    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    public static VoiceConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            var defaults = new VoiceConfig();
            Save(defaults, path);
            Console.WriteLine($"Created default config at: {path}");
            return defaults;
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<VoiceConfig>(json) ?? new VoiceConfig();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: could not parse config.json ({ex.Message}). Using defaults.");
            return new VoiceConfig();
        }
    }

    public static void Save(VoiceConfig config, string path)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(config, _jsonOptions));
    }
}
