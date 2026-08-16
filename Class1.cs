using System.Numerics;
using System.Runtime.Remoting;
using System.Xml.Linq;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Visive;

//Current Milestone: load and export full video

public class Class1
{
    public void RunTestSuite()
    {
        Console.WriteLine("Test Suite Started: \n");

        VideoObject obj = new("test", "StreamingAssets/test.mp4");

        Console.WriteLine($"Created VideoObject {obj.name} from {obj.source} with id {obj.id}");

        obj.saveOutFrame(1);
    }
}


public class VideoObject
{
    public readonly string name;
    public readonly string source;
    public readonly string store;
    public readonly Vector2 resolution;
    public readonly float fps;
    public readonly float length;
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
        var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = $"-f rawvideo -pix_fmt rgba -s {(int)resolution.X}x{(int)resolution.Y} -r {fps} -i \"{store}\" -c:v libx264 -preset medium \"{ExportPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        process.WaitForExit();

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
}
public class FrameObject
{
    public readonly Pixel[] pixels;

    public FrameObject(VideoObject video, float timecode)
    {
        int width = (int)video.resolution.X;
        int height = (int)video.resolution.Y;

        uint frame = video.getFramefromTimecode(timecode);

        pixels = loadFrameFromIntermediary(width, height, video, frame);
    }

    Pixel[] loadFrameFromIntermediary(int width, int height, VideoObject video, uint frame)
    {
        // Calculate bytes per frame and frame offset
        int bytesPerFrame = width * height * 4;  // RGBA: 4 bytes per pixel
        uint frameNumber = frame;
        long frameOffset = frameNumber * bytesPerFrame;

        Pixel[] pixels = new Pixel[width * height];

        using (var fs = File.OpenRead(video.store))  // Need to store this path
        {
            fs.Seek(frameOffset, SeekOrigin.Begin);

            // Read raw RGBA data
            byte[] buffer = new byte[bytesPerFrame];
            fs.Read(buffer, 0, bytesPerFrame);

            // Parse RGBA bytes into Pixel structs
            for (int i = 0; i < width * height; i++)
            {
                int offset = i * 4;
                byte r8 = buffer[offset];
                byte g8 = buffer[offset + 1];
                byte b8 = buffer[offset + 2];
                byte a8 = buffer[offset + 3];

                // Convert 8-bit to 10-bit
                pixels[i] = new Pixel
                {
                    R = (ushort)(r8 << 2),  // Scale 0-255 to 0-1023
                    G = (ushort)(g8 << 2),
                    B = (ushort)(b8 << 2),
                    alpha = a8
                };
            }
        }

        return pixels;
    }
}

public struct Pixel()
{
    private uint rgb;      // 10-bit R, G, B packed
    public byte alpha;     // 8-bit alpha

    public ushort R
    {
        get => (ushort)((rgb >> 20) & 0x3FF);
        set => rgb = (rgb & ~(0x3FFu << 20)) | ((uint)value & 0x3FF) << 20;
    }
    public ushort G
    {
        get => (ushort)((rgb >> 10) & 0x3FF);
        set => rgb = (rgb & ~(0x3FFu << 10)) | ((uint)value & 0x3FF) << 10;
    }
    public ushort B
    {
        get => (ushort)(rgb & 0x3FF);
        set => rgb = (rgb & ~0x3FFu) | ((uint)value & 0x3FF);
    }
}

