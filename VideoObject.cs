using System;
using System.Numerics;
using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Visive;

// TODO: Memory-Efficient Chunked Video Architecture
// Goal: Minimal RAM footprint with lazy-loaded, disk-compressed chunks
// 
// PHASE 1: Infrastructure
// - [ ] Create ChunkManifest.cs
//   - Serialize/deserialize chunk metadata (frame ranges, compression ratios, dirty flags)
//   - JSON-based: name.manifest.json in chunks/ directory
//   - Track: startFrame, endFrame, compressed size, uncompressed size, hash, isDirty
// 
// - [ ] Create ChunkCache.cs
//   - Single chunk in memory max (decompress on demand)
//   - Compress on evict (using System.IO.Compression.GzipStream)
//   - Methods: GetChunk(index), SetChunk(index, data), Flush(), Clear()
//   - Memory cap: ~50-100MB per VideoObject
//
// PHASE 2: Refactor VideoObject
// - [ ] Replace MakeIntermediary to create manifest only (no full .seq extraction)
// - [ ] Replace ReadBytes/WriteBytes to intercept I/O via ChunkCache
// - [ ] Update Slice() to create new manifest pointing to chunk ranges
// - [ ] Update Append() to chain manifests (virtual concatenation)
// - [ ] Update SaveOutVideo() to stream-reconstruct from compressed chunks
//
// PHASE 3: Chunk Extraction Strategy
// - [ ] Add ExtractChunk(chunkIndex) using ffmpeg -ss/-t
//   - Seek to frame start time: (frame / fps)
//   - Extract N frames: ffmpeg -ss {startTime} -t {duration} -f rawvideo -pix_fmt rgba ...
//   - Compress and store in chunks/ directory
// - [ ] Make extraction lazy: only on first ReadBytes/WriteBytes access
//
// PHASE 4: Disposal & Cleanup
// - [ ] Flush dirty chunks on Dispose (compress and save)
// - [ ] Delete chunks/ directory on ownsStore disposal
// - [ ] Preserve manifest for reload (optional, for caching)
//
// Expected Results:
// - Memory: Constant ~50-100MB regardless of video length
// - Disk (chunks/): ~10-20% of raw .seq size (compressed)
// - No monolithic .seq file on disk

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

    public VideoObject(string Name, string Source)
    {
        name = Name;
        source = Path.Combine(Directory.GetCurrentDirectory(), "tmp", name);
        store = MakeIntermediary(Source);
        var (res, f, len) = LoadVideoMetadata(Source);
        resolution = res;
        fps = f;
        length = len;
        ownsStore = true;   // created here, owned here
    }
    private VideoObject(string name, string intermediaryPath, Vector2 resolution, uint fps, float length, bool ownsStore)
    {
        this.name = name;
        source = string.Empty;
        store = intermediaryPath;
        this.resolution = resolution;
        this.fps = fps;
        this.length = length;
        this.ownsStore = ownsStore;
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

        if (ownsStore && File.Exists(store))
            File.Delete(store);

        GC.SuppressFinalize(this);
    }
    ~VideoObject()
    {
        if (!disposed && ownsStore && File.Exists(store))
            File.Delete(store);
    }
    public void SaveOutVideo(string ExportPath)
    {
        Console.WriteLine($"FFmpeg input store: {store}");
        Console.WriteLine($"Store exists: {File.Exists(store)}");
        if (File.Exists(store))
            Console.WriteLine($"Store size: {new FileInfo(store).Length} bytes");

        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = $"-y -f rawvideo -pix_fmt rgba -s {(int)resolution.X}x{(int)resolution.Y} -r {fps} -i \"{store}\" -c:v libx264 -preset medium \"{ExportPath}\"",
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
    public uint getFramefromTimecode(float timecode)
    {
        if (timecode > length) return 0;

        if (timecode <= 0) return 0;
        if (fps <= 0) return 0;

        return (uint)(timecode * fps);

    }
    private string MakeIntermediary(string videoPath)
    {
        string realpath = Path.Combine(Directory.GetCurrentDirectory(), videoPath);
        string tmpDir = Path.Combine(Directory.GetCurrentDirectory(), "tmp");
        string intermediaryPath = Path.Combine(
            tmpDir,
            Path.GetFileNameWithoutExtension(realpath) + ".seq"
        );

        if (File.Exists(intermediaryPath))
            return intermediaryPath;

        // Use FFmpeg to extract raw pixel data
        var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = $"-i \"{realpath}\" -f rawvideo -pix_fmt rgba -",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();

        // Write raw bytes to intermediary file
        using (var fs = File.Create(intermediaryPath))
        {
            process.StandardOutput.BaseStream.CopyTo(fs);
        }

        process.WaitForExit();

        return intermediaryPath;
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
        }

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
    private VideoObject(string name, string intermediaryPath, Vector2 resolution, uint fps, float length)
    {
        this.name = name;
        source = string.Empty;
        store = intermediaryPath;
        this.resolution = resolution;
        this.fps = fps;
        this.length = length;
    }
    public static VideoObject MakeFromIntermediary(string intermediaryPath, Vector2 Resolution, uint fps)
    {
        int width = (int)Resolution.X;
        int height = (int)Resolution.Y;
        long fileBytes = new FileInfo(intermediaryPath).Length;

        float length = fps > 0
            ? fileBytes / (width * height * 4f * fps)
            : 0f;

        string name = Path.GetFileNameWithoutExtension(intermediaryPath);

        return new VideoObject(name, intermediaryPath, Resolution, fps, length);
    }
    public void Append(VideoObject other)
    {
        if (resolution != other.resolution)
            throw new InvalidOperationException("Videos must have the same resolution to append.");

        if (fps != other.fps)
            throw new InvalidOperationException("Videos must have the same fps to append.");

        using var source = File.OpenRead(other.store);
        using var output = new FileStream(store, FileMode.Append, FileAccess.Write);

        CopyExactly(source, output, source.Length);

        length += other.length;
    }
    public VideoObject Slice(float startTime, float endTime)
    {
        if (startTime < 0 || endTime <= startTime || endTime > length)
            throw new ArgumentOutOfRangeException();

        uint startFrame = getFramefromTimecode(startTime);
        uint endFrame = getFramefromTimecode(endTime);
        if (endFrame <= startFrame) throw new InvalidOperationException("Slice is empty.");

        long bytesPerFrame = (long)resolution.X * (long)resolution.Y * 4;
        long startByte = startFrame * bytesPerFrame;
        long bytesToCopy = (endFrame - startFrame) * bytesPerFrame;

        string tmpDir = Path.Combine(Directory.GetCurrentDirectory(), "tmp");
        Directory.CreateDirectory(tmpDir);
        string outPath = Path.Combine(tmpDir, $"{name}_{startFrame}_{endFrame}.seq");

        using var input = File.OpenRead(store);
        using var output = new FileStream(outPath, FileMode.Create, FileAccess.Write);

        input.Seek(startByte, SeekOrigin.Begin);
        CopyExactly(input, output, bytesToCopy);

        return MakeFromIntermediary(outPath, resolution, (uint)fps, ownsStore: true);
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

    public void WriteBytes(long offset, byte[] buffer)
    {
        if (buffer == null || buffer.Length == 0)
            return;

        using var stream = new FileStream(store, FileMode.OpenOrCreate, FileAccess.Write);
        stream.Seek(offset, SeekOrigin.Begin);
        stream.Write(buffer, 0, buffer.Length);
    }
}
