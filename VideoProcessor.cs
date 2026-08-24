using System.Numerics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Visive;

//Current Milestone: 

public class VideoProcessor
{
    public void RunTestSuite()
    {
        Console.WriteLine("Test Suite Started: \n");

        using var obj = new VideoObject("test", "StreamingAssets/test.mp4");

        Console.WriteLine($"Created VideoObject {obj.name} from {obj.source} with id {obj.id}");

        uint framecount = (uint)(obj.length * obj.fps);

        using var part1 = obj.Slice(0f, 2f);
        using var part2 = obj.Slice(2f, obj.length);

        part2.Append(part1);

        SwapRedAndGreenInPlace(part2);

        part2.SaveOutVideo(Environment.CurrentDirectory + $"/tmp/{obj.name}_Export.mp4");
    }

    public Pixel[] SwapRedAndGreenInPlace(Pixel[] pixels)
    {
        if (pixels == null) return null;

        for (int i = 0; i <= pixels.Length - 1; i++)
        {
            // Store the original Red value temporarily
            ushort tempR = pixels[i].R;

            // Swap them
            pixels[i].R = pixels[i].G;
            pixels[i].G = tempR;
        }

        return pixels;
    }

    public void SwapRedAndGreenInPlace(VideoObject video)
    {
        uint framecount = (uint)(video.length * video.fps);

        for (int i = 0; i < framecount; i++)
        {
            Console.WriteLine($"Swapping frame {i}");
            FrameObject frame = video.getFrame((uint)i);
            SwapRedAndGreenInPlace(frame.pixels);
            frame.SaveToIntermediary();
        }

        return;
    }

}

