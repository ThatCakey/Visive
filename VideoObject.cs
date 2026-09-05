using System;
using System.Numerics;
using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Visive;

public class VideoObject : IDisposable
{
    public readonly string name;
    public readonly string source;
    public readonly string store;
    public readonly Vector2 resolution;
    public readonly float fps;
    public float length;
    public readonly string id = Random.Shared.GetHexString(16, false);
    private readonly bool ownsStore;
    private bool disposed;
    
    private ChunkManifest? manifest;
    private ChunkCache? cache;
    private readonly string chunksDirectory;
    internal string? audioPath;  // Path to extracted audio file (AAC, re-muxable) - internal for Slice/Append
    private bool ownsAudio = true;  // Track if we created this audio or received it from parent
    private uint calculatedChunkSize;  // Dynamically calculated based on video resolution

    public VideoObject(string Name, string Source)
    {
        name = Name;
        // Make source path absolute so it works from any working directory (e.g., FFmpeg subprocesses)
        source = Path.IsPathRooted(Source) ? Source : Path.Combine(Directory.GetCurrentDirectory(), Source);
        Console.WriteLine($"[Constructor] Created {name} with source: {source}");
        store = MakeIntermediary(Source);
        var (res, f, len) = LoadVideoMetadata(Source);
        resolution = res;
        fps = f;
        length = len;
        ownsStore = true;   // created here, owned here
        
        // Phase 2: Initialize chunk system
        chunksDirectory = Path.Combine(Path.GetDirectoryName(store)!, $"{name}_chunks");
        InitializeChunks();
        
        // Audio Extraction: Extract audio from source for re-muxing on export
        ExtractAudio(Source);
    }
    private VideoObject(string name, string intermediaryPath, Vector2 resolution, uint fps, float length, bool ownsStore)
    {
        this.name = name;
        source = string.Empty;
        Console.WriteLine($"[Constructor] Created {name} from intermediary (sliced/empty source)");
        store = intermediaryPath;
        this.resolution = resolution;
        this.fps = fps;
        this.length = length;
        this.ownsStore = ownsStore;
        
        // Phase 2: Initialize chunk system
        chunksDirectory = Path.Combine(Path.GetDirectoryName(store)!, $"{name}_chunks");
        InitializeChunks();
    }
    public static VideoObject MakeFromIntermediary(string intermediaryPath, Vector2 resolution, uint fps, bool ownsStore = false)
    {
        int width = (int)resolution.X;
        int height = (int)resolution.Y;
        long fileBytes = new FileInfo(intermediaryPath).Length;
        float length = fps > 0 ? fileBytes / (width * height * 4f * fps) : 0f;
        string name = Path.GetFileNameWithoutExtension(intermediaryPath);
        return new VideoObject(name, intermediaryPath, resolution, fps, length, ownsStore);
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        // Phase 4: Flush dirty chunks and save manifest before cleanup
        if (cache != null && manifest != null)
        {
            Console.WriteLine($"[Phase 4] Flushing dirty chunks...");
            FlushAllDirtyChunks();
            
            // Save manifest with updated compression ratios
            string manifestPath = Path.Combine(chunksDirectory, $"{name}.manifest.json");
            Console.WriteLine($"[Phase 4] Saving manifest to {manifestPath}");
            manifest.Save(manifestPath);
            Console.WriteLine($"[Phase 4] Manifest saved");
        }

        if (cache != null)
        {
            cache.Dispose();
            cache = null;
        }

        if (ownsStore)
        {
            if (File.Exists(store))
                File.Delete(store);
            
            // Clean up audio file only if we own it (not transferred from parent)
            if (ownsAudio && !string.IsNullOrEmpty(audioPath) && File.Exists(audioPath))
            {
                Console.WriteLine($"[Audio] Deleting audio file: {audioPath}");
                File.Delete(audioPath);
            }
            
            // Clean up chunks directory if this VideoObject owns the store
            if (Directory.Exists(chunksDirectory))
            {
                Console.WriteLine($"[Phase 4] Cleaning up chunks directory: {chunksDirectory}");
                Directory.Delete(chunksDirectory, recursive: true);
                Console.WriteLine($"[Phase 4] Chunks directory removed");
            }
        }
        else
        {
            // If not owning store, preserve manifest for reload scenarios
            Console.WriteLine($"[Phase 4] Preserving chunks for potential reload (ownsStore={ownsStore})");
        }

        GC.SuppressFinalize(this);
    }
    ~VideoObject()
    {
        if (!disposed)
        {
            Dispose();
        }
    }
    private void FlushAllDirtyChunks()
    {
        if (manifest == null || cache == null)
            return;

        Console.WriteLine($"[Phase 4] Checking {manifest.Chunks.Count} chunks for dirty status...");
        int dirtyCount = 0;

        foreach (var chunk in manifest.Chunks)
        {
            if (chunk.IsDirty)
            {
                Console.WriteLine($"[Phase 4] Flushing dirty chunk {chunk.StartFrame}-{chunk.EndFrame}");
                cache.FlushChunk(chunk.StartFrame, chunk.EndFrame);
                dirtyCount++;
            }
        }

        Console.WriteLine($"[Phase 4] Flushed {dirtyCount} dirty chunks");
    }
    public void SaveOutVideo(string ExportPath)
    {
        Console.WriteLine($"FFmpeg input store: {store}");
        Console.WriteLine($"Store exists: {File.Exists(store)}");
        if (File.Exists(store))
            Console.WriteLine($"Store size: {new FileInfo(store).Length} bytes");

        // Build FFmpeg command with optional audio re-muxing
        string ffmpegArgs;
        if (!string.IsNullOrEmpty(audioPath) && File.Exists(audioPath))
        {
            Console.WriteLine($"[Audio] Re-muxing with audio from: {audioPath}");
            // Video + audio: encode video, add audio, use AAC codec
            ffmpegArgs = $"-y -f rawvideo -pix_fmt rgba -s {(int)resolution.X}x{(int)resolution.Y} -r {fps} -i \"{store}\" -i \"{audioPath}\" -c:v libx264 -preset medium -c:a aac -b:a 128k -shortest \"{ExportPath}\"";
        }
        else
        {
            Console.WriteLine($"[Audio] No audio found, encoding video only");
            // Video only
            ffmpegArgs = $"-y -f rawvideo -pix_fmt rgba -s {(int)resolution.X}x{(int)resolution.Y} -r {fps} -i \"{store}\" -c:v libx264 -preset medium \"{ExportPath}\"";
        }

        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = ffmpegArgs,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };

        using var process = new System.Diagnostics.Process { StartInfo = startInfo };

        process.Start();

        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();

        process.WaitForExit();

        Console.WriteLine("--- FFmpeg stdout ---");
        Console.WriteLine(stdout);
        Console.WriteLine("--- FFmpeg stderr ---");
        Console.WriteLine(stderr);

        if (process.ExitCode == 0)
            Console.WriteLine($"Video saved to {ExportPath}");
        else
            Console.WriteLine($"FFmpeg failed with code {process.ExitCode}");
    }

    /// <summary>
    /// Trim audio to a specific time range [startTime, endTime].
    /// Replaces current audio with trimmed version.
    /// </summary>
    private void TrimAudio(float startTime, float endTime)
    {
        if (string.IsNullOrEmpty(audioPath) || !File.Exists(audioPath))
        {
            Console.WriteLine($"[Audio] No audio to trim");
            return;
        }

        float duration = endTime - startTime;
        string audioDir = Path.Combine(Directory.GetCurrentDirectory(), "tmp");
        Directory.CreateDirectory(audioDir);
        string trimmedPath = Path.Combine(audioDir, $"{name}_audio_trimmed.aac");

        Console.WriteLine($"[Audio] Trimming audio from {startTime}s to {endTime}s (duration: {duration}s)");

        var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = $"-y -i \"{audioPath}\" -ss {startTime:F2} -t {duration:F2} -q:a 9 \"{trimmedPath}\" -hide_banner -loglevel error",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = false,
                RedirectStandardOutput = false
            }
        };

        process.Start();
        process.WaitForExit();

        if (process.ExitCode == 0 && File.Exists(trimmedPath))
        {
            // Delete old audio only if we own it (not transferred from parent)
            if (ownsAudio && File.Exists(audioPath))
                File.Delete(audioPath);
            
            audioPath = trimmedPath;
            ownsAudio = true;  // We own the trimmed file
            long audioSize = new FileInfo(audioPath).Length;
            Console.WriteLine($"[Audio] Trimmed audio: {audioSize} bytes");
        }
        else
        {
            Console.WriteLine($"[Audio] Trim failed");
        }
    }

    /// <summary>
    /// Concatenate audio from another VideoObject.
    /// Uses FFmpeg concat demuxer to join audio files.
    /// </summary>
    private void ConcatenateAudio(string otherAudioPath)
    {
        if (string.IsNullOrEmpty(audioPath) || !File.Exists(audioPath))
        {
            Console.WriteLine($"[Audio] No audio to concatenate to");
            return;
        }

        if (string.IsNullOrEmpty(otherAudioPath) || !File.Exists(otherAudioPath))
        {
            Console.WriteLine($"[Audio] Other audio not found");
            return;
        }

        string audioDir = Path.Combine(Directory.GetCurrentDirectory(), "tmp");
        Directory.CreateDirectory(audioDir);
        
        // Create concat demuxer file
        string concatFile = Path.Combine(audioDir, $"{name}_audio_concat.txt");
        string concatenatedPath = Path.Combine(audioDir, $"{name}_audio_concat.aac");

        Console.WriteLine($"[Audio] Concatenating audio from {otherAudioPath}");

        // Write FFmpeg concat demuxer format
        using (var writer = new System.IO.StreamWriter(concatFile))
        {
            writer.WriteLine($"file '{audioPath}'");
            writer.WriteLine($"file '{otherAudioPath}'");
        }

        var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = $"-y -f concat -safe 0 -i \"{concatFile}\" -c copy \"{concatenatedPath}\" -hide_banner -loglevel error",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = false,
                RedirectStandardOutput = false
            }
        };

        process.Start();
        process.WaitForExit();

        if (process.ExitCode == 0 && File.Exists(concatenatedPath))
        {
            // Delete old audio only if we own it
            if (ownsAudio && File.Exists(audioPath))
                File.Delete(audioPath);
            
            audioPath = concatenatedPath;
            ownsAudio = true;  // We own the concatenated file
            long audioSize = new FileInfo(audioPath).Length;
            Console.WriteLine($"[Audio] Concatenated audio: {audioSize} bytes");
            
            // Clean up concat file
            if (File.Exists(concatFile))
                File.Delete(concatFile);
        }
        else
        {
            Console.WriteLine($"[Audio] Concatenation failed");
            if (File.Exists(concatFile))
                File.Delete(concatFile);
        }
    }

    /// <summary>
    /// Extract audio from source video and store for re-muxing on export.
    /// Stores as AAC for compatibility and small file size.
    /// </summary>
    private void ExtractAudio(string sourceVideoPath)
    {
        if (string.IsNullOrEmpty(sourceVideoPath) || !File.Exists(sourceVideoPath))
        {
            Console.WriteLine($"[Audio] Source not found, skipping audio extraction");
            return;
        }

        string audioDir = Path.Combine(Directory.GetCurrentDirectory(), "tmp");
        Directory.CreateDirectory(audioDir);
        audioPath = Path.Combine(audioDir, $"{name}_audio.aac");

        Console.WriteLine($"[Audio] Extracting audio to {audioPath}");

        var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = $"-y -i \"{sourceVideoPath}\" -q:a 9 -map a \"{audioPath}\" -hide_banner -loglevel error",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = false,
                RedirectStandardOutput = false
            }
        };

        process.Start();
        process.WaitForExit();

        if (process.ExitCode == 0 && File.Exists(audioPath))
        {
            long audioSize = new FileInfo(audioPath).Length;
            Console.WriteLine($"[Audio] Extracted {audioSize} bytes to {audioPath}");
        }
        else
        {
            Console.WriteLine($"[Audio] Extraction failed or no audio stream found");
            audioPath = null;
        }
    }
    public uint getFramefromTimecode(float timecode)
    {
        if (timecode > length) return 0;

        if (timecode <= 0) return 0;
        if (fps <= 0) return 0;

        return (uint)(timecode * fps);

    }
    private string MakeIntermediary(string videoPath)
    {
        // Phase 3: Don't extract entire video upfront. Just return a path.
        // Chunks will be extracted on-demand via ExtractChunk().
        string realpath = Path.Combine(Directory.GetCurrentDirectory(), videoPath);
        string tmpDir = Path.Combine(Directory.GetCurrentDirectory(), "tmp");
        string intermediaryPath = Path.Combine(
            tmpDir,
            Path.GetFileNameWithoutExtension(realpath) + ".seq"
        );

        // Phase 3: We don't create .seq anymore, just return the path for compatibility
        // The actual video data will be lazily extracted from chunks.
        return intermediaryPath;
    }
    private void ExtractChunk(uint startFrame, uint endFrame)
    {
        if (manifest == null || cache == null)
            return;

        // If no source video, skip extraction (data comes from store for sliced videos)
        if (string.IsNullOrEmpty(source))
        {
            Console.WriteLine($"[Phase 3] Skipping extraction - no source video (sliced/intermediary object)");
            return;
        }

        // Check if chunk already exists
        var existingChunk = manifest.FindChunkForFrame(startFrame);
        if (existingChunk != null && existingChunk.StartFrame == startFrame && existingChunk.EndFrame == endFrame)
        {
            return;  // Already extracted
        }

        // Calculate time range for FFmpeg
        float startTime = startFrame / fps;
        float duration = (endFrame - startFrame) / fps;

        Console.WriteLine($"[Phase 3] Extracting chunk frames {startFrame}-{endFrame} (time {startTime:F2}s - {startTime + duration:F2}s)");

        // Use temporary file instead of pipe to avoid deadlock
        string tmpChunkFile = Path.Combine(chunksDirectory, $"chunk_{startFrame}_{endFrame}.tmp");
        Directory.CreateDirectory(chunksDirectory);

        var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = $"-y -ss {startTime:F2} -t {duration:F2} -i \"{source}\" -f rawvideo -pix_fmt rgba \"{tmpChunkFile}\" -hide_banner -loglevel warning",
                RedirectStandardOutput = false,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        Console.WriteLine($"[Phase 3] Starting FFmpeg extraction to {tmpChunkFile}");
        process.Start();
        Console.WriteLine($"[Phase 3] Waiting for FFmpeg to exit...");
        process.WaitForExit();
        Console.WriteLine($"[Phase 3] FFmpeg exited with code {process.ExitCode}");

        if (process.ExitCode != 0)
        {
            string stderr = process.StandardError.ReadToEnd();
            Console.WriteLine($"FFmpeg extraction failed: {stderr}");
            if (File.Exists(tmpChunkFile)) File.Delete(tmpChunkFile);
            return;
        }

        // Read chunk from temp file
        Console.WriteLine($"[Phase 3] Checking for output file: {tmpChunkFile}");
        if (!File.Exists(tmpChunkFile))
        {
            Console.WriteLine($"[Phase 3] Extraction produced no output file");
            return;
        }

        Console.WriteLine($"[Phase 3] Reading chunk file into memory...");
        byte[] chunkData = ReadFileStreaming(tmpChunkFile);
        Console.WriteLine($"[Phase 3] Read {chunkData.Length} bytes, deleting temp file...");
        File.Delete(tmpChunkFile);

        Console.WriteLine($"[Phase 3] Extracted {chunkData.Length} bytes for chunk {startFrame}-{endFrame}");

        // Store chunk in cache
        Console.WriteLine($"[Phase 3] Storing chunk in cache...");
        cache.SetChunk(startFrame, endFrame, chunkData);

        // Create manifest entry
        string hash = ChunkCache.ComputeHash(chunkData);
        var entry = new ChunkEntry
        {
            StartFrame = startFrame,
            EndFrame = endFrame,
            UncompressedBytes = chunkData.Length,
            CompressedBytes = 0,  // Will be set when flushed
            Hash = hash,
            IsDirty = true
        };

        Console.WriteLine($"[Phase 3] Adding chunk to manifest");
        manifest.AddOrUpdateChunk(entry);
        Console.WriteLine($"[Phase 3] Chunk extraction complete");

    }

    /// <summary>
    /// Read a file into memory using streaming to support files larger than 2GB.
    /// File.ReadAllBytes() has a 2GB limit; this method bypasses that.
    /// </summary>
    private byte[] ReadFileStreaming(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920);
        
        long fileSize = stream.Length;
        
        // Chunk size is now calculated to stay under 500MB, so this should not trigger
        if (fileSize > int.MaxValue)
        {
            throw new InvalidOperationException($"Chunk file too large: {fileSize} bytes (max {int.MaxValue}). Check calculated chunk size.");
        }

        byte[] buffer = new byte[fileSize];
        int totalRead = 0;
        int bytesRead;

        while (totalRead < fileSize && (bytesRead = stream.Read(buffer, totalRead, (int)(fileSize - totalRead))) > 0)
        {
            totalRead += bytesRead;
        }

        if (totalRead != fileSize)
            throw new EndOfStreamException($"Only read {totalRead} of {fileSize} bytes from {filePath}");

        return buffer;
    }

    private (Vector2 res, float fps, float len) LoadVideoMetadata(string videoPath)
    {
        string realpath = Path.Combine(Directory.GetCurrentDirectory(), videoPath);

        var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ffprobe",
                Arguments = $"-v error -select_streams v:0 -show_entries stream=width,height,r_frame_rate,duration -of default=noprint_wrappers=1:nokey=1 \"{realpath}\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        var lines = output.Trim().Split('\n');
        Vector2 res = new(255, 255);
        float fps = 24, len = 0;
        
        Console.WriteLine($"[LoadVideoMetadata] ffprobe output ({lines.Length} lines):");
        for (int i = 0; i < lines.Length; i++)
        {
            Console.WriteLine($"  Line {i}: '{lines[i]}'");
        }

        if (lines.Length >= 4)
        {
            int.TryParse(lines[0], out var w);
            int.TryParse(lines[1], out var h);
            var fpsStr = lines[2].Split('/');
            if (fpsStr.Length == 2 && float.TryParse(fpsStr[0], out var num) && float.TryParse(fpsStr[1], out var den))
            {
                fps = num / den;
            }
            float.TryParse(lines[3], out len);
            res = new Vector2(w, h);
            
            Console.WriteLine($"[LoadVideoMetadata] Parsed: res={w}x{h}, fps={fps}, len={len}");
        }

        Console.WriteLine($"[LoadVideoMetadata] Returning: ({res}, {fps}, {len})");
        return (res, fps, len);
    }
    public FrameObject getFrame(uint frame)
    {
        return new FrameObject(this, frame);
    }
    public FrameObject getFrame(float timecode)
    {
        return new FrameObject(this, timecode);
    }
    public void saveOutFrame(float timecode)
    {
        FrameObject frame = getFrame(timecode);

        int width = (int)resolution.X;
        int height = (int)resolution.Y;

        var image = new Image<Rgba32>(width, height);

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                var pixel = frame.pixels[y * width + x];

                byte r = (byte)(pixel.R >> 2);
                byte g = (byte)(pixel.G >> 2);
                byte b = (byte)(pixel.B >> 2);

                image[x, y] = new Rgba32(r, g, b, pixel.alpha);
            }
        }

        string tmpDir = Path.Combine(Directory.GetCurrentDirectory(), "tmp");
        string filename = Path.Combine(tmpDir, $"{name}_frame_{timecode:F2}.png");

        Console.WriteLine($"CWD: {AppDomain.CurrentDomain.BaseDirectory}");
        Console.WriteLine($"Saving to: {filename}");
        Console.WriteLine($"Tmp dir exists: {Directory.Exists(tmpDir)}");
        Console.WriteLine($"Image size: {image.Width}x{image.Height}");
        Console.WriteLine($"First pixel R: {image[0, 0].R}");

        Directory.CreateDirectory(tmpDir);
        image.SaveAsPng(filename);

        Console.WriteLine($"Saved frame to {filename}");
    }

    public void Append(VideoObject other)
    {
        if (resolution != other.resolution)
            throw new InvalidOperationException("Videos must have the same resolution to append.");

        if (fps != other.fps)
            throw new InvalidOperationException("Videos must have the same fps to append.");

        // Calculate total frames from other video
        long bytesPerFrame = (long)resolution.X * (long)resolution.Y * 4;
        uint otherTotalFrames = (uint)(other.length * other.fps);
        long totalBytes = bytesPerFrame * otherTotalFrames;

        // Read all data from other video via chunks (triggers lazy extraction if needed)
        byte[] sourceData = other.ReadBytes(0, (int)Math.Min(int.MaxValue, totalBytes));

        // Append our current size and write the data
        uint currentTotalFrames = (uint)(length * fps);
        long currentLength = bytesPerFrame * currentTotalFrames;
        WriteBytes(currentLength, sourceData);

        length += other.length;
        
        // Ensure this object has audio before trying to concatenate
        if ((string.IsNullOrEmpty(audioPath) || !File.Exists(audioPath)) && 
            !string.IsNullOrEmpty(other.audioPath) && File.Exists(other.audioPath))
        {
            // If we don't have audio but other does, just use other's audio
            Console.WriteLine($"[Audio] Using audio from appended video (no existing audio)");
            audioPath = other.audioPath;
            ownsAudio = false;  // Don't delete other's audio when we dispose
        }
        else if (!string.IsNullOrEmpty(audioPath) && File.Exists(audioPath) &&
                 !string.IsNullOrEmpty(other.audioPath) && File.Exists(other.audioPath))
        {
            // Both have audio, concatenate them
            ConcatenateAudio(other.audioPath);
        }
    }
    public VideoObject Slice(float startTime, float endTime)
    {
        Console.WriteLine($"[Slice] Starting Slice({startTime}, {endTime})");
        
        if (startTime < 0 || endTime <= startTime || endTime > length)
        {
            Console.WriteLine($"[Slice] Validation failed: startTime={startTime}, endTime={endTime}, length={length}");
            throw new ArgumentOutOfRangeException();
        }

        uint startFrame = getFramefromTimecode(startTime);
        uint endFrame = getFramefromTimecode(endTime);
        Console.WriteLine($"[Slice] Frames: {startFrame}-{endFrame}");
        
        if (endFrame <= startFrame)
        {
            Console.WriteLine($"[Slice] Empty slice: startFrame={startFrame}, endFrame={endFrame}");
            throw new InvalidOperationException("Slice is empty.");
        }

        long bytesPerFrame = (long)resolution.X * (long)resolution.Y * 4;
        long startByte = startFrame * bytesPerFrame;
        long bytesToCopy = (endFrame - startFrame) * bytesPerFrame;

        string tmpDir = Path.Combine(Directory.GetCurrentDirectory(), "tmp");
        Directory.CreateDirectory(tmpDir);
        string outPath = Path.Combine(tmpDir, $"{name}_{startFrame}_{endFrame}.seq");

        Console.WriteLine($"[Slice] Reading frames {startFrame}-{endFrame} from parent");
        Console.WriteLine($"[Slice] Copying {bytesToCopy} bytes ({endFrame - startFrame} frames) to {outPath}");
        
        try
        {
            // Stream data directly to file instead of buffering in memory
            // This avoids the 2GB array size limit
            using var outputStream = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920);
            
            long bytesCopied = 0;
            long currentOffset = startByte;
            
            while (bytesCopied < bytesToCopy)
            {
                int readSize = (int)Math.Min(int.MaxValue, bytesToCopy - bytesCopied);
                Console.WriteLine($"[Slice] Reading chunk: offset={currentOffset}, size={readSize}, total progress: {bytesCopied}/{bytesToCopy}");
                
                byte[] chunk = ReadBytes(currentOffset, readSize);
                if (chunk.Length == 0)
                {
                    Console.WriteLine($"[Slice] Hit end of data at {bytesCopied}/{bytesToCopy} bytes");
                    break;
                }
                
                outputStream.Write(chunk, 0, chunk.Length);
                bytesCopied += chunk.Length;
                currentOffset += chunk.Length;
                
                Console.WriteLine($"[Slice] Wrote {chunk.Length} bytes, total: {bytesCopied}/{bytesToCopy}");
            }
            
            outputStream.Flush();
            Console.WriteLine($"[Slice] Finished streaming {bytesCopied} bytes");

            var sliced = MakeFromIntermediary(outPath, resolution, (uint)fps, ownsStore: true);
            Console.WriteLine($"[Slice] Created sliced object");
            
            // Transfer parent audio to sliced object if parent has audio but slice doesn't
            // (slice tries to extract from .seq file which fails)
            if ((string.IsNullOrEmpty(sliced.audioPath) || !File.Exists(sliced.audioPath)) && 
                !string.IsNullOrEmpty(audioPath) && File.Exists(audioPath))
            {
                Console.WriteLine($"[Audio] Transferring parent audio to slice");
                sliced.audioPath = audioPath;
                sliced.ownsAudio = false;  // Don't delete parent's audio
            }
            
            // Trim audio to match slice time range
            if (!string.IsNullOrEmpty(sliced.audioPath) && File.Exists(sliced.audioPath))
            {
                sliced.TrimAudio(startTime, endTime);
            }
            
            Console.WriteLine($"[Slice] Slice complete, returning object");
            return sliced;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Slice] ERROR: {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine($"[Slice] Stack: {ex.StackTrace}");
            throw;
        }
    }

    private static void CopyExactly(Stream input, Stream output, long bytesToCopy)
    {
        byte[] buffer = new byte[81920];
        while (bytesToCopy > 0)
        {
            int read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, bytesToCopy));
            if (read <= 0) throw new EndOfStreamException();
            output.Write(buffer, 0, read);
            bytesToCopy -= read;
        }
    }

    public byte[] ReadBytes(long offset, int count)
    {
        if (count <= 0 || cache == null || manifest == null)
        {
            Console.WriteLine($"[ReadBytes] Early return: count={count}, cache={cache!=null}, manifest={manifest!=null}");
            return Array.Empty<byte>();
        }

        // Cap allocation to 500MB to avoid OutOfMemoryException with int.MaxValue arrays
        // Callers should loop if they need more data
        const int MAX_BUFFER_SIZE = 500_000_000; // 500MB
        int actualCount = Math.Min(count, MAX_BUFFER_SIZE);
        
        Console.WriteLine($"[ReadBytes] Allocating {actualCount} bytes (requested {count})");
        byte[] result = new byte[actualCount];
        long currentOffset = offset;
        int bytesRead = 0;

        while (bytesRead < actualCount)
        {
            // Find chunk containing current offset
            long loopFrameIndex = currentOffset / ((long)resolution.X * (long)resolution.Y * 4);
            var loopChunk = manifest.FindChunkForFrame((uint)loopFrameIndex);

            Console.WriteLine($"[ReadBytes] Frame {(uint)loopFrameIndex}: chunk found = {loopChunk != null}");

            // If no chunk found, try to extract one
            if (loopChunk == null)
            {
                uint chunkSize = calculatedChunkSize;
                uint chunkStart = ((uint)loopFrameIndex / chunkSize) * chunkSize;
                uint chunkEnd = Math.Min(chunkStart + chunkSize, (uint)(length * fps));

                if (chunkEnd > chunkStart)
                {
                    Console.WriteLine($"[ReadBytes] Extracting chunk({chunkStart}, {chunkEnd}) for frame {(uint)loopFrameIndex}");
                    ExtractChunk(chunkStart, chunkEnd);
                    loopChunk = manifest.FindChunkForFrame((uint)loopFrameIndex);
                }
            }

            // If still no chunk, stop (reached end of video or extraction failed)
            if (loopChunk == null)
            {
                Console.WriteLine($"[ReadBytes] Still no chunk for frame {(uint)loopFrameIndex}, stopping");
                break;
            }

            try
            {
                // Load chunk via cache (decompresses if needed)
                byte[] chunkData = cache.GetChunk(loopChunk);
                
                // Calculate position within chunk
                long chunkStartByte = loopChunk.StartFrame * (long)resolution.X * (long)resolution.Y * 4;
                long posInChunk = currentOffset - chunkStartByte;
                long bytesAvailableInChunk = chunkData.Length - posInChunk;

                if (bytesAvailableInChunk <= 0)
                {
                    Console.WriteLine($"[ReadBytes] No data left in chunk {loopChunk.StartFrame}-{loopChunk.EndFrame}");
                    break;  // No more data in this chunk
                }

                int toCopy = (int)Math.Min(bytesAvailableInChunk, actualCount - bytesRead);
                Array.Copy(chunkData, posInChunk, result, bytesRead, toCopy);

                bytesRead += toCopy;
                currentOffset += toCopy;
                Console.WriteLine($"[ReadBytes] Read {toCopy} bytes from chunk, total: {bytesRead}/{actualCount}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ReadBytes] Error reading chunk: {ex.Message}");
                break;
            }
        }

        // Trim result if less data was read than requested
        if (bytesRead < actualCount)
            Array.Resize(ref result, bytesRead);

        Console.WriteLine($"[ReadBytes] Final: returning {bytesRead}/{actualCount} bytes (requested {count})");
        return result;
    }

    public void WriteBytes(long offset, byte[] buffer)
    {
        if (buffer == null || buffer.Length == 0 || cache == null || manifest == null)
            return;

        long frameIndex = offset / ((long)resolution.X * (long)resolution.Y * 4);
        uint frameIdxUint = (uint)frameIndex;

        // Phase 3: Check if chunk exists; if not, extract it
        var chunk = manifest.FindChunkForFrame(frameIdxUint);
        if (chunk == null)
        {
            // No chunk for this frame, try to extract a chunk based on calculated size
            uint chunkSize = calculatedChunkSize;
            uint chunkStart = (frameIdxUint / chunkSize) * chunkSize;
            uint chunkEnd = Math.Min(chunkStart + chunkSize, (uint)(length * fps));
            
            if (chunkEnd > chunkStart)
            {
                ExtractChunk(chunkStart, chunkEnd);
                chunk = manifest.FindChunkForFrame(frameIdxUint);
            }
        }

        if (manifest.Chunks.Count == 0)
        {
            // Still no chunks, fall back to raw store
            WriteBytesToRawStore(offset, buffer);
            return;
        }

        long currentOffset = offset;
        int bytesWritten = 0;

        while (bytesWritten < buffer.Length)
        {
            // Find or create chunk for current offset
            long loopFrameIndex = currentOffset / ((long)resolution.X * (long)resolution.Y * 4);
            var loopChunk = manifest.FindChunkForFrame((uint)loopFrameIndex);

            if (loopChunk == null)
            {
                // No chunk found, stop writing
                break;
            }

            try
            {
                // Load chunk via cache
                byte[] chunkData = cache.GetChunk(loopChunk);
                
                // Calculate position within chunk
                long chunkStartByte = loopChunk.StartFrame * (long)resolution.X * (long)resolution.Y * 4;
                long posInChunk = currentOffset - chunkStartByte;
                long bytesAvailableInChunk = chunkData.Length - posInChunk;

                if (bytesAvailableInChunk <= 0)
                {
                    break;  // No space in this chunk
                }

                int toCopy = (int)Math.Min(bytesAvailableInChunk, buffer.Length - bytesWritten);
                Array.Copy(buffer, bytesWritten, chunkData, posInChunk, toCopy);

                // Mark chunk as dirty (needs compression on flush)
                cache.SetChunk(loopChunk.StartFrame, loopChunk.EndFrame, chunkData);

                bytesWritten += toCopy;
                currentOffset += toCopy;
            }
            catch
            {
                // If chunk write fails, stop
                break;
            }
        }
    }

    private byte[] ReadBytesFromCachedChunksOnly(long offset, int count)
    {
        // Read only from chunks that already exist on disk (extracted previously)
        // without triggering new extractions from source. Used by Slice() to avoid
        // memory pressure from extracting multiple chunks.
        if (count <= 0 || cache == null || manifest == null)
            return Array.Empty<byte>();

        long frameIndex = offset / ((long)resolution.X * (long)resolution.Y * 4);
        uint frameIdxUint = (uint)frameIndex;
        byte[] result = new byte[count];
        long currentOffset = offset;
        int bytesRead = 0;

        while (bytesRead < count)
        {
            long loopFrameIndex = currentOffset / ((long)resolution.X * (long)resolution.Y * 4);
            var loopChunk = manifest.FindChunkForFrame((uint)loopFrameIndex);

            if (loopChunk == null)
            {
                // No chunk for this frame - skip it (don't extract)
                Console.WriteLine($"[Slice] Frame {(uint)loopFrameIndex} not in cache, skipping");
                break;
            }

            try
            {
                byte[] chunkData = cache.GetChunk(loopChunk);
                
                // Calculate position within chunk
                long chunkStartByte = (long)loopChunk.StartFrame * (long)resolution.X * (long)resolution.Y * 4;
                long offsetInChunk = currentOffset - chunkStartByte;
                
                // Calculate how much we can read from this chunk
                int bytesToReadFromChunk = (int)Math.Min(
                    count - bytesRead,
                    chunkData.Length - offsetInChunk
                );

                if (bytesToReadFromChunk > 0)
                {
                    Array.Copy(chunkData, offsetInChunk, result, bytesRead, bytesToReadFromChunk);
                    bytesRead += bytesToReadFromChunk;
                    currentOffset += bytesToReadFromChunk;
                }
                else
                {
                    break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Slice] Error reading chunk: {ex.Message}");
                break;
            }
        }

        if (bytesRead < count)
        {
            // Resize result to match actual bytes read
            Array.Resize(ref result, bytesRead);
        }

        Console.WriteLine($"[Slice] Read {bytesRead} bytes from cached chunks (requested {count})");
        return result;
    }

    private byte[] ReadBytesFromRawStore(long offset, int count)
    {
        if (count <= 0)
            return Array.Empty<byte>();

        byte[] buffer = new byte[count];

        using var stream = File.OpenRead(store);
        stream.Seek(offset, SeekOrigin.Begin);

        int totalRead = 0;
        while (totalRead < count)
        {
            int read = stream.Read(buffer, totalRead, count - totalRead);
            if (read <= 0)
                break;

            totalRead += read;
        }

        if (totalRead == count)
            return buffer;

        Array.Resize(ref buffer, totalRead);
        return buffer;
    }

    private void WriteBytesToRawStore(long offset, byte[] buffer)
    {
        if (buffer == null || buffer.Length == 0)
            return;

        using var stream = new FileStream(store, FileMode.OpenOrCreate, FileAccess.Write);
        stream.Seek(offset, SeekOrigin.Begin);
        stream.Write(buffer, 0, buffer.Length);
    }

    private void InitializeChunks()
    {
        cache = new ChunkCache(chunksDirectory);
        
        // Calculate dynamic chunk size based on video resolution
        CalculateChunkSize();
        
        string manifestPath = Path.Combine(chunksDirectory, $"{name}.manifest.json");
        
        if (File.Exists(manifestPath))
        {
            // Load existing manifest
            manifest = ChunkManifest.Load(manifestPath);
        }
        else
        {
            // Create new manifest (chunks will be created later on demand)
            manifest = ChunkManifest.Create(name, resolution, fps, (uint)(length * fps));
        }
    }

    /// <summary>
    /// Calculate chunk size dynamically based on video resolution and target memory budget.
    /// Ensures chunks stay under ~500MB in RAM to avoid exceeding byte array limits.
    /// </summary>
    private void CalculateChunkSize()
    {
        const long targetMaxBytes = 100_000_000;  // 100MB target chunk size in RAM (accounts for multiple objects)
        long bytesPerFrame = (long)resolution.X * (long)resolution.Y * 4;  // RGBA = 4 bytes
        
        if (bytesPerFrame <= 0)
        {
            calculatedChunkSize = 300;  // Fallback
            return;
        }

        // Calculate how many frames fit in 500MB
        uint framesInBudget = (uint)(targetMaxBytes / bytesPerFrame);
        
        // Ensure at least 1 frame per chunk, and cap to total frames
        calculatedChunkSize = Math.Max(1, Math.Min(framesInBudget, (uint)(length * fps)));
        
        long chunkBytes = bytesPerFrame * calculatedChunkSize;
        Console.WriteLine($"[Phase 2] Calculated chunk size: {calculatedChunkSize} frames = {chunkBytes / 1_000_000}MB per chunk");
    }
}
