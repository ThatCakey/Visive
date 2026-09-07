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

        using var obj = new VideoObject("test", "StreamingAssets/Test2.mp4"); // 1920x1080 @ 25fps
        watch.Lap("load object 1");
        using var obj2 = new VideoObject("test2", "StreamingAssets/test.mp4"); // 640x360 @ 30fps
        watch.Lap("load object 2");

        Console.WriteLine($"[TestSequence] Created VideoObject {obj.name}");

        // Slice first 10 seconds of 1080p video
        using var part1 = obj.Slice(0f, 10f);
        watch.Lap("slice 1");

        // Append the entire 720p 30fps video to the 1080p 25fps timeline!
        part1.Append(obj2);
        watch.Lap("Append");

        // Let's modify the appended clip's resolution to make it double size and position it!
        // part1.clips[1] is the appended test.mp4 clip
        part1.clips[1].Resolution = new Vector2(1280, 720); // Scale it up
        part1.clips[1].Position = new Vector2(100, 100);    // Offset it from top-left

        // Add a visual effect to prove it works
        part1.clips[1].AddEffect(new SwapRedAndGreen());

        part1.SaveOutVideo(Environment.CurrentDirectory + $"/tmp/composite_Export.mp4");

        watch.Lap("save");

        watch.Cancel();
    }

}