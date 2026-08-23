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

    public VideoObject(string Name, String Source)
    {
        name = Name;
        source = Environment.CurrentDirectory + $"/tmp/{name}/";
        store = MakeIntermediary(Source);

        var (res, f, len) = LoadVideoMetadata(Source);
        resolution = res;
        fps = f;
        length = len;
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

    private bool disposed;
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        if (File.Exists(store))
            File.Delete(store);

        GC.SuppressFinalize(this);
    }
    ~VideoObject()
    {
        if (!disposed && File.Exists(store))
            File.Delete(store);
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

        string inputPath = other.store;

        if (ReferenceEquals(this, other))
        {
            inputPath = Path.Combine(Path.GetTempPath(), $"{id}_snapshot.seq");
            File.Copy(other.store, inputPath, true);
        }

        using var output = new FileStream(store, FileMode.Append, FileAccess.Write);
        using var input = new FileStream(inputPath, FileMode.Open, FileAccess.Read);

        input.CopyTo(output);

        if (!ReferenceEquals(this, other))
            File.Delete(inputPath);

        length += other.length;
    }
}
