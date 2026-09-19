using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Visive;

/// <summary>
/// Metadata for a single compressed chunk of raw video frames.
/// </summary>
public class ChunkEntry
{
    [JsonPropertyName("startFrame")]
    public uint StartFrame { get; set; }

    [JsonPropertyName("endFrame")]
    public uint EndFrame { get; set; }

    [JsonPropertyName("compressedBytes")]
    public long CompressedBytes { get; set; }

    [JsonPropertyName("uncompressedBytes")]
    public long UncompressedBytes { get; set; }

    [JsonPropertyName("hash")]
    public string Hash { get; set; } = "";

    [JsonPropertyName("isDirty")]
    public bool IsDirty { get; set; }

    public uint FrameCount => EndFrame - StartFrame;
}

/// <summary>
/// Manifest describing all chunks for a video and how to reconstruct it.
/// Serialized to {name}.manifest.json in the chunks directory.
/// </summary>
public class ChunkManifest
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("resolution")]
    public string Resolution { get; set; } = ""; // "640x360"

    [JsonPropertyName("fps")]
    public float Fps { get; set; }

    [JsonPropertyName("totalFrames")]
    public uint TotalFrames { get; set; }

    [JsonPropertyName("chunks")]
    public List<ChunkEntry> Chunks { get; set; } = new();

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public static ChunkManifest Create(string name, System.Numerics.Vector2 resolution, float fps, uint totalFrames)
    {
        return new ChunkManifest
        {
            Name = name,
            Resolution = $"{(int)resolution.X}x{(int)resolution.Y}",
            Fps = fps,
            TotalFrames = totalFrames,
            CreatedAt = DateTime.UtcNow
        };
    }

    public void Save(string manifestPath)
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        string json = JsonSerializer.Serialize(this, options);
        File.WriteAllText(manifestPath, json);
    }

    public static ChunkManifest Load(string manifestPath)
    {
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException($"Manifest not found: {manifestPath}");

        string json = File.ReadAllText(manifestPath);
        return JsonSerializer.Deserialize<ChunkManifest>(json)
            ?? throw new InvalidOperationException("Failed to deserialize manifest");
    }

    private readonly object _manifestLock = new();

    /// <summary>
    /// Find which chunk contains the given frame.
    /// </summary>
    public ChunkEntry? FindChunkForFrame(uint frameIndex)
    {
        lock (_manifestLock)
        {
            return Chunks.Find(c => frameIndex >= c.StartFrame && frameIndex < c.EndFrame);
        }
    }

    /// <summary>
    /// Add or update a chunk entry. Maintains sorted order by StartFrame.
    /// </summary>
    public void AddOrUpdateChunk(ChunkEntry entry)
    {
        lock (_manifestLock)
        {
            var existing = Chunks.FindIndex(c => c.StartFrame == entry.StartFrame);
            if (existing >= 0)
                Chunks[existing] = entry;
            else
                Chunks.Add(entry);

            Chunks.Sort((a, b) => a.StartFrame.CompareTo(b.StartFrame));
        }
    }

    /// <summary>
    /// Get total disk space used by all chunks.
    /// </summary>
    public long TotalCompressedBytes
    {
        get
        {
            lock (_manifestLock)
            {
                return Chunks.Sum(c => c.CompressedBytes);
            }
        }
    }

    /// <summary>
    /// Get compression ratio: compressed / uncompressed.
    /// </summary>
    public double CompressionRatio
    {
        get
        {
            lock (_manifestLock)
            {
                long total = Chunks.Sum(c => c.UncompressedBytes);
                return total > 0 ? (double)TotalCompressedBytes / total : 0;
            }
        }
    }
}
