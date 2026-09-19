using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Linq;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Visive;

/// <summary>
/// In-memory cache for decompressed chunks of frames.
/// Keeps multiple chunks in memory using an LRU policy to enable read-ahead buffering.
/// </summary>
public class ChunkCache : IDisposable
{
    private class CachedChunk
    {
        public uint StartFrame { get; set; }
        public uint EndFrame { get; set; }
        public byte[] Data { get; set; } = Array.Empty<byte>();
        public bool IsDirty { get; set; }
        public DateTime LastAccessed { get; set; }
    }

    private readonly ConcurrentDictionary<uint, CachedChunk> memoryCache = new();
    private readonly object cacheLock = new object();
    private readonly int maxMemoryChunks = 4; // e.g. 4 chunks * 500MB = 2GB RAM
    
    // Pool to avoid LOH (Large Object Heap) Garbage Collection pauses
    private static readonly ConcurrentStack<byte[]> bufferPool = new();

    public static byte[] RentBuffer(int size)
    {
        while (bufferPool.TryPop(out var buffer))
        {
            if (buffer.Length == size) return buffer;
        }
        return new byte[size];
    }

    public static void ReturnBuffer(byte[] buffer)
    {
        if (buffer != null && buffer.Length > 0)
            bufferPool.Push(buffer);
    }

    private readonly string chunksDirectory;
    private bool disposed;
    public static long MaxCacheSizeBytes { get; set; } = 10L * 1024 * 1024 * 1024; // 10 GB default

    public ChunkCache(string chunksDirectory)
    {
        this.chunksDirectory = chunksDirectory;
        Directory.CreateDirectory(chunksDirectory);
    }

    /// <summary>
    /// Get decompressed chunk data. Loads from disk and decompresses if not in memory.
    /// </summary>
    public byte[]? GetChunk(ChunkEntry entry)
    {
        lock (cacheLock)
        {
            if (memoryCache.TryGetValue(entry.StartFrame, out var cachedChunk))
            {
                cachedChunk.LastAccessed = DateTime.UtcNow;
                return cachedChunk.Data;
            }
            return null; // pure RAM cache, no disk loads
        }
    }

    /// <summary>
    /// Set (modify) a chunk in cache. Marks it dirty for later compression.
    /// </summary>
    public void SetChunk(uint startFrame, uint endFrame, byte[] data, bool isDirty = true)
    {
        lock (cacheLock)
        {
            AddOrUpdateMemoryCache(startFrame, endFrame, data, isDirty);
        }
    }

    private void AddOrUpdateMemoryCache(uint startFrame, uint endFrame, byte[] data, bool isDirty)
    {
        // Enforce max RAM usage via LRU eviction
        if (!memoryCache.ContainsKey(startFrame) && memoryCache.Count >= maxMemoryChunks)
        {
            var lru = memoryCache.Values.OrderBy(c => c.LastAccessed).First();
            memoryCache.TryRemove(lru.StartFrame, out _);
            ReturnBuffer(lru.Data);
        }

        memoryCache[startFrame] = new CachedChunk
        {
            StartFrame = startFrame,
            EndFrame = endFrame,
            Data = data,
            IsDirty = isDirty,
            LastAccessed = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Compress and save a specific chunk to disk if dirty.
    /// </summary>
    public void FlushChunk(uint startFrame, uint endFrame)
    {
        // No-op for pure RAM cache
    }

    private void FlushChunkToDisk(CachedChunk chunk)
    {
        // No-op for pure RAM cache
    }

    /// <summary>
    /// Clear memory cache (does not flush to disk).
    /// </summary>
    public void Clear()
    {
        lock (cacheLock)
        {
            foreach (var chunk in memoryCache.Values)
            {
                ReturnBuffer(chunk.Data);
            }
            memoryCache.Clear();
        }
    }

    /// <summary>
    /// Flush and clear cache on disposal.
    /// </summary>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        lock (cacheLock)
        {
            foreach (var chunk in memoryCache.Values)
            {
                ReturnBuffer(chunk.Data);
            }
            memoryCache.Clear();
        }
    }

    private void CompressChunk(byte[] data, string outputPath)
    {
        // No-op
    }

    private byte[] DecompressChunk(string filePath)
    {
        return Array.Empty<byte>(); // No-op
    }

    private string GetChunkPath(uint startFrame, uint endFrame)
    {
        return Path.Combine(chunksDirectory, $"chunk_{startFrame}_{endFrame}.gz");
    }

    // Removed ComputeHash to prevent 300ms CPU spikes on the background thread

    public void DeleteChunk(uint startFrame, uint endFrame)
    {
        string chunkPath = GetChunkPath(startFrame, endFrame);
        if (File.Exists(chunkPath))
            File.Delete(chunkPath);
    }

    public long MemoryUsage
    {
        get
        {
            lock (cacheLock)
            {
                return memoryCache.Values.Sum(c => (long)c.Data.Length);
            }
        }
    }
}
