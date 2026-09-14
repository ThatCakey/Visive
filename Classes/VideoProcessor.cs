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

        // Stacking Test: Create a Picture-in-Picture Overlay
        var overlayTrack = obj2.Slice(2f, 5f); // 3s clip
        overlayTrack.PrependEmpty(2f); // Offset by 2 seconds so it starts at 2s
        
        // Make it a smaller PiP window in the top left corner
        overlayTrack.clips[0].Resolution = new System.Numerics.Vector2(640, 360);
        overlayTrack.clips[0].Position = new System.Numerics.Vector2(50, 50);
        
        // Add an effect exclusively to the overlay
        overlayTrack.clips[0].AddEffect(new SwapRedAndGreen());
        
        // Stack it on the main track
        part1.Overlays.Add(overlayTrack);

        // --- PREVIEW API BENCHMARKS ---
        Console.WriteLine("\n--- Preview API Benchmarks ---");
        
        // 1. Cache Miss + 50% Quality Downscaling (Includes Overlay Blending!)
        watch.Lap("start preview cache miss");
        var previewFrame = part1.GetPreviewFrame(4.0f, 0.5f); // 4.0s in (hits overlay!)
        watch.Lap("AFAP preview (cache miss)");
        Console.WriteLine($"Preview Frame Extracted: {previewFrame.width}x{previewFrame.height}");
        previewFrame.ExportToPng(Path.Combine(Directory.GetCurrentDirectory(), "tmp", "OverlayPreviewTest.png"));

        // Wait a brief moment to allow background cache prefetch to complete
        Console.WriteLine("Waiting 500ms for background prefetch to complete...");
        System.Threading.Thread.Sleep(500);

        // 2. Cache Hit + Nearest-Neighbor Downscaling (Includes Overlay Blending!)
        watch.Lap("start preview cache hit");
        var previewFrame2 = part1.GetPreviewFrame(4.04f, 0.5f); // 4.04s in
        watch.Lap("AFAP preview (cache hit)");
        Console.WriteLine($"Preview Frame Extracted: {previewFrame2.width}x{previewFrame2.height}");

        Console.WriteLine("------------------------------\n");

        part1.SaveOutVideo(Path.Combine(Directory.GetCurrentDirectory(), "tmp", "test_Export.mp4"));
        previewFrame2.ExportToPng(Environment.CurrentDirectory + $"/tmp/PreviewTest2.png");

        Console.WriteLine("------------------------------\n");

        //part1.SaveOutVideo(Environment.CurrentDirectory + $"/tmp/composite_Export.mp4");

        //watch.Lap("save");

        watch.Cancel();
    }

}