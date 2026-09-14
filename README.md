# Visive

Visive is a Non-Linear Editor (NLE) video processing engine written in C# that utilizes FFmpeg for decoding and encoding. 

It handles video manipulations and composition in memory as a virtual timeline to minimize intermediate disk usage.

## Building and Integration

To build Visive from source:
```bash
dotnet build Visive.csproj
```

To include Visive in another .NET project, add a reference to the `Visive.csproj` file:
```bash
dotnet add reference /path/to/Visive/Visive.csproj
```
*Note: Ensure `ffmpeg` and `ffprobe` are installed and accessible via your system's PATH.*

### Global Configuration
You can configure global engine limits (such as the background chunk cache size limit) at any point in your application lifecycle:

```csharp
using Visive;

// Change the chunk disk cache limit to 20 GB (default is 10 GB)
ChunkCache.MaxCacheSizeBytes = 20L * 1024 * 1024 * 1024;
```

## Usage and User-Facing Components

The primary component you will interact with is the `VideoObject`. This represents your virtual timeline.

### Initialization and Basic Editing
```csharp
// Load a video into a new timeline
using var mainVideo = new VideoObject("MyVideo", "source_file.mp4");

// Slice creates a new VideoObject timeline containing only the specified timecode range
using var clip1 = mainVideo.Slice(0f, 10f); // First 10 seconds
using var clip2 = mainVideo.Slice(20f, 30f); // 20s to 30s mark

// Append merges the virtual timeline of clip1 to the end of clip2
clip2.Append(clip1); 

// Export the composed timeline to a final MP4 file
clip2.SaveOutVideo("final_output.mp4");
```
*Important: Always wrap `VideoObject` in a `using` statement or call `.Dispose()` when finished to ensure temporary caches are cleaned up.*

## Building Custom Effects

You can build custom pixel manipulation effects by inheriting from the `VideoEffect` base class and overriding the `Process` method. 

The `Process` method receives a `FrameObject` which contains a `Pixel[] pixels` array. You can read and write to this array directly.

### 1. Define the Effect
```csharp
using Visive;

public class SwapRedAndGreen : VideoEffect
{
    public override FrameObject Process(FrameObject Frame)
    {
        Pixel[] pixels = Frame.pixels;

        // Manipulate pixels directly
        for (int i = 0; i < pixels.Length; i++)
        {
            ushort tempR = pixels[i].R;
            pixels[i].R = pixels[i].G;
            pixels[i].G = tempR;
        }

        return Frame;
    }
}
```

### 2. Apply the Effect
Effects are applied to individual `VideoClip` instances within your `VideoObject` timeline.

```csharp
// Access the underlying clips of your VideoObject timeline
VideoClip firstClip = myVideo.clips[0];

// Add the custom effect
firstClip.AddEffect(new SwapRedAndGreen());

// When Export() or GetFrameData() is called, the effect will automatically process over the raw frames.
myVideo.SaveOutVideo("output.mp4");
```

## Running the Test Suite

The project includes tests in the `Testing/` directory to verify the pipeline.

1. Navigate to the testing directory:
```bash
cd Testing/
```
2. Run the test suite:
```bash
dotnet run
```

This will run a test sequence (loading, slicing, appending, and applying a video effect) on the provided test footage and export the result to `Testing/tmp/test_Export.mp4`.
