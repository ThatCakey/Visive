using System.Numerics;

namespace Visive;

//Current Milestone: audio

public class VideoProcessor
{
    public void RunTestSuite()
    {
        Console.WriteLine("Test Suite Started: \n");
        var watch = new StopWatch();
        watch.StartWatch();

        using var obj = new VideoObject("test", "StreamingAssets/Test2.mp4");
        watch.Lap("load object");

        Console.WriteLine($"[TestSequence] Created VideoObject {obj.name} with id {obj.id}");

        uint framecount = (uint)(obj.length * obj.fps);

        using var part1 = obj.Slice(0f, 10f);

        watch.Lap("slice 1");

        using var part2 = obj.Slice(10f, obj.length);

        watch.Lap("slice 2");

        part2.Append(part1);

        watch.Lap("Append");

        // Add effect to the second clip (part2 originally had 1 clip, appending part1 adds a second)
        // Let's just add it to the first clip of part2
        part2.clips[0].AddEffect(new SwapRedAndGreen());

        part2.SaveOutVideo(Environment.CurrentDirectory + $"/tmp/{obj.name}_Export.mp4");

        watch.Lap("save");

        watch.Cancel();
    }

}