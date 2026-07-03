using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HdrScope.Calibration;

public sealed class StepRecord
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public Dictionary<string, object?> Params { get; set; } = new();
    public Dictionary<string, object?> Result { get; set; } = new();
    public string? Notes { get; set; }
}

/// <summary>
/// Calibration session log. Everything the wizard learns goes here;
/// the resulting JSON is designed to be pasted back to an AI assistant for analysis.
/// </summary>
public sealed class Session
{
    public string Version { get; set; } = "HdrScope 1.0";
    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
    public Dictionary<string, object?> Environment { get; set; } = new();
    public Dictionary<string, object?> MonitorState { get; set; } = new();
    public List<StepRecord> Steps { get; set; } = new();
    public Dictionary<string, object?> Conclusions { get; set; } = new();

    [JsonIgnore]
    public string OutputDirectory { get; }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public Session(string outputDirectory)
    {
        OutputDirectory = outputDirectory;
        Directory.CreateDirectory(outputDirectory);
    }

    public StepRecord AddStep(string id, string title)
    {
        var s = new StepRecord { Id = id, Title = title };
        Steps.Add(s);
        Save();
        return s;
    }

    public string JsonPath => Path.Combine(OutputDirectory, $"session-{StartedUtc:yyyyMMdd-HHmmss}.json");

    public void Save()
    {
        File.WriteAllText(JsonPath, JsonSerializer.Serialize(this, JsonOpts));
    }
}
