using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Linq;

namespace Visive;

/// <summary>
/// In-memory cache for a single decompressed chunk of frames.
/// Keeps one chunk in memory, compresses/decompresses on swap.
/// </summary>
public class ChunkCache : IDisposable
{
    private byte[]? currentChunkData;
    private uint? currentChunkStartFrame;
    private uint? currentChunkEndFrame;
    private bool currentChunkDirty;
    private readonly string chunksDirectory;
    private bool disposed;
    private const long MAX_CACHE_SIZE_BYTES = 10L * 1024 * 1024 * 1024; // 10 GB

    public ChunkCache(string chunksDirectory)
    {
        this.chunksDirectory = chunksDirectory;
        Directory.CreateDirectory(chunksDirectory);
    }

    /// <summary>
    /// Get decompressed chunk data. Loads from disk and decompresses if not in memory.
    /// </summary>
    public byte[] GetChunk(ChunkEntry entry)
    {
        // If already in cache, return it
        if (currentChunkStartFrame == entry.StartFrame && currentChunkData != null)
            return currentChunkData;

        // Flush current chunk if dirty
        if (currentChunkDirty && currentChunkData != null && currentChunkStartFrame.HasValue)
            FlushChunk(currentChunkStartFrame.Value, currentChunkEndFrame ?? currentChunkStartFrame.Value);
        else if (currentChunkData != null)
            Clear(); // Clear clean chunk from RAM without writing

        // Load and decompress requested chunk
        string chunkPath = GetChunkPath(entry.StartFrame, entry.EndFrame);
        if (!File.Exists(chunkPath))
            throw new FileNotFoundException($"Chunk file not found: {chunkPath}");

        // Update access time for LRU cache manager
        File.SetLastAccessTimeUtc(chunkPath, DateTime.UtcNow);

        currentChunkData = DecompressChunk(chunkPath);
        currentChunkStartFrame = entry.StartFrame;
        currentChunkEndFrame = entry.EndFrame;
        currentChunkDirty = false;

        return currentChunkData;
    }

    /// <summary>
    /// Set (modify) a chunk in cache. Marks it dirty for later compression.
    /// </summary>
    public void SetChunk(uint startFrame, uint endFrame, byte[] data, bool isDirty = true)
    {
        // Flush existing chunk if different and dirty
        if (currentChunkDirty && currentChunkStartFrame.HasValue && currentChunkStartFrame != startFrame)
            FlushChunk(currentChunkStartFrame.Value, currentChunkEndFrame ?? currentChunkStartFrame.Value);
        else if (currentChunkStartFrame.HasValue && currentChunkStartFrame != startFrame)
            Clear();

        currentChunkData = data;
        currentChunkStartFrame = startFrame;
        currentChunkEndFrame = endFrame;
        currentChunkDirty = isDirty;
    }

    /// <summary>
    /// Compress and save current chunk to disk if dirty.
    /// </summary>
    public void FlushChunk(uint startFrame, uint endFrame)
    {
        if (!currentChunkDirty || currentChunkData == null)
        {
            Clear();
            return;
        }

        // Ensure we don't exceed 10GB limit before writing a new chunk
        EnforceDiskLimit();

        string chunkPath = GetChunkPath(startFrame, endFrame);
        Directory.CreateDirectory(chunksDirectory);

        CompressChunk(currentChunkData, chunkPath);
        currentChunkDirty = false;
        
        // Clear memory cache after flushing to free RAM
        Console.WriteLine($"[ChunkCache] Cleared chunk {startFrame}-{endFrame} from memory after flushing");
        Clear();
    }

    private void EnforceDiskLimit()
    {
        if (!Directory.Exists(chunksDirectory)) return;

        var files = new DirectoryInfo(chunksDirectory).GetFiles("*.gz");
        long totalSize = files.Sum(f => f.Length);

        if (totalSize <= MAX_CACHE_SIZE_BYTES) return;

        Console.WriteLine($"[ChunkCache] Disk limit exceeded ({(totalSize / 1024 / 1024)}MB). Cleaning up...");
        
        // Sort by last access time (oldest first)
        foreach (var file in files.OrderBy(f => f.LastAccessTimeUtc))
        {
            try
            {
                totalSize -= file.Length;
                file.Delete();
                Console.WriteLine($"[ChunkCache] Deleted {file.Name} to free space.");
                if (totalSize <= MAX_CACHE_SIZE_BYTES)
                    break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ChunkCache] Failed to delete {file.Name}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Clear memory cache (does not flush to disk).
    /// </summary>
    public void Clear()
    {
        currentChunkData = null;
        currentChunkStartFrame = null;
        currentChunkEndFrame = null;
        currentChunkDirty = false;
    }

    /// <summary>
    /// Flush and clear cache on disposal.
    /// </summary>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        if (currentChunkDirty && currentChunkData != null && currentChunkStartFrame.HasValue)
            FlushChunk(currentChunkStartFrame.Value, currentChunkEndFrame ?? currentChunkStartFrame.Value);

        Clear();
    }

    /// <summary>
    /// Compress chunk data using DeflateStream and save to disk.
    /// </summary>
    private void CompressChunk(byte[] data, string outputPath)
    {
        using var fs = File.Create(outputPath);
        // Using Fastest compression instead of default to prevent massive CPU blocking
        using var deflate = new System.IO.Compression.DeflateStream(fs, System.IO.Compression.CompressionLevel.Fastest);
        deflate.Write(data, 0, data.Length);
    }

    /// <summary>
    /// Decompress chunk from disk using DeflateStream.
    /// </summary>
    private byte[] DecompressChunk(string filePath)
    {
        using var fs = File.OpenRead(filePath);
        using var deflate = new System.IO.Compression.DeflateStream(fs, System.IO.Compression.CompressionMode.Decompress);
        using var ms = new MemoryStream();
        deflate.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Generate chunk file path: chunks/name_startFrame_endFrame.gz
    /// </summary>
    private string GetChunkPath(uint startFrame, uint endFrame)
    {
        return Path.Combine(chunksDirectory, $"chunk_{startFrame}_{endFrame}.gz");
    }

    /// <summary>
    /// Compute SHA256 hash of chunk data for integrity checking.
    /// </summary>
    public static string ComputeHash(byte[] data)
    {
        using var sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(data);
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// Delete chunk file from disk.
    /// </summary>
    public void DeleteChunk(uint startFrame, uint endFrame)
    {
        string chunkPath = GetChunkPath(startFrame, endFrame);
        if (File.Exists(chunkPath))
            File.Delete(chunkPath);
    }

    /// <summary>
    /// Get memory footprint of current cached chunk (bytes).
    /// </summary>
    public long MemoryUsage => currentChunkData?.Length ?? 0;
}
