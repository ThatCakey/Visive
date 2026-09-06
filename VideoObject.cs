using System;
using System.Collections.Generic;
using System.Numerics;
using System.IO;
using System.Linq;

namespace Visive;

public class VideoClip
{
    public string SourcePath;
    public uint SourceStartFrame;
    public uint SourceEndFrame;
    public uint TimelineStartFrame;
    public uint Length => SourceEndFrame - SourceStartFrame;

    public List<VideoEffect> Effects { get; } = new List<VideoEffect>();
    public Action? OnEffectsChanged;

    public void AddEffect(VideoEffect effect)
    {
        Effects.Add(effect);
        OnEffectsChanged?.Invoke();
    }

    public void RemoveEffect(VideoEffect effect)
    {
        Effects.Remove(effect);
        OnEffectsChanged?.Invoke();
    }
}

public class AudioClip
{
    public string SourcePath;
    public float SourceStartTime;
    public float SourceEndTime;
    public float TimelineStartTime;
    public float Length => SourceEndTime - SourceStartTime;
}

public class VideoObject : IDisposable
{
    public readonly string name;
    public readonly Vector2 resolution;
    public readonly float fps;
    public float length;
    public readonly string id = Random.Shared.GetHexString(16, false);

    private bool disposed;

    public List<VideoClip> clips = new List<VideoClip>();
    public List<AudioClip> audioClips = new List<AudioClip>();

    private ChunkManifest? manifest;
    private ChunkCache? cache;
    private readonly string chunksDirectory;
    private uint calculatedChunkSize;

    public VideoObject(string Name, string Source)
    {
        name = Name;
        string absSource = Path.IsPathRooted(Source) ? Source : Path.Combine(Directory.GetCurrentDirectory(), Source);

        var (res, f, len) = LoadVideoMetadata(absSource);
        resolution = res;
        fps = f;
        length = len;
        uint totalFrames = (uint)(len * f);

        var initialClip = new VideoClip
        {
            SourcePath = absSource,
            SourceStartFrame = 0,
            SourceEndFrame = totalFrames,
            TimelineStartFrame = 0
        };
        initialClip.OnEffectsChanged += () => cache?.Clear();
        clips.Add(initialClip);

        // Audio Extraction
        string audioDir = Path.Combine(Directory.GetCurrentDirectory(), "tmp");
        Directory.CreateDirectory(audioDir);
        string audioPath = Path.Combine(audioDir, $"{name}_audio.aac");

        var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = $"-y -i \"{absSource}\" -q:a 9 -map 0:a? \"{audioPath}\" -hide_banner -loglevel error",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            }
        };
        process.Start();
        process.WaitForExit();

        if (process.ExitCode == 0 && File.Exists(audioPath))
        {
            // If ffmpeg created an empty file because there's no audio, delete it
            if (new FileInfo(audioPath).Length > 0)
            {
                audioClips.Add(new AudioClip
                {
                    SourcePath = audioPath,
                    SourceStartTime = 0f,
                    SourceEndTime = len,
                    TimelineStartTime = 0f
                });
            }
            else
            {
                File.Delete(audioPath);
            }
        }

        chunksDirectory = Path.Combine(audioDir, $"{name}_chunks");
        InitializeChunks();
    }

    public void AddEffect(VideoEffect effect)
    {
        foreach (var clip in clips)
        {
            clip.AddEffect(effect);
        }
    }
    public void RemoveEffect(VideoEffect effect)
    {
        foreach (var clip in clips)
        {
            clip.RemoveEffect(effect);
        }
    }

    private VideoObject(string name, Vector2 resolution, float fps)
    {
        this.name = name;
        this.resolution = resolution;
        this.fps = fps;
        this.length = 0f;

        string tmpDir = Path.Combine(Directory.GetCurrentDirectory(), "tmp");
        chunksDirectory = Path.Combine(tmpDir, $"{name}_chunks");
        InitializeChunks();
    }

    private void InitializeChunks()
    {
        Directory.CreateDirectory(chunksDirectory);
        string manifestPath = Path.Combine(chunksDirectory, $"{name}.manifest.json");

        uint totalFrames = (uint)(length * fps);
        if (File.Exists(manifestPath))
            manifest = ChunkManifest.Load(manifestPath);
        else
            manifest = ChunkManifest.Create(name, resolution, fps, totalFrames);

        cache = new ChunkCache(chunksDirectory);

        long bytesPerFrame = (long)resolution.X * (long)resolution.Y * 4;
        long targetChunkBytes = 100 * 1024 * 1024; // Target 100MB per chunk uncompressed
        calculatedChunkSize = (uint)Math.Max(1, targetChunkBytes / bytesPerFrame);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        if (cache != null && manifest != null)
        {
            FlushAllDirtyChunks();
            string manifestPath = Path.Combine(chunksDirectory, $"{name}.manifest.json");
            manifest.Save(manifestPath);
        }

        cache?.Dispose();
        cache = null;

        // Clean up audio files originally extracted by this instance
        foreach (var clip in audioClips)
        {
            if (File.Exists(clip.SourcePath) && Path.GetFileName(clip.SourcePath).StartsWith(name + "_audio"))
            {
                try { File.Delete(clip.SourcePath); } catch { }
            }
        }

        // Clean up chunks directory
        try
        {
            if (Directory.Exists(chunksDirectory))
                Directory.Delete(chunksDirectory, true);
        }
        catch { }
    }

    private void FlushAllDirtyChunks()
    {
        if (manifest == null || cache == null) return;
        foreach (var chunk in manifest.Chunks)
        {
            if (chunk.IsDirty)
                cache.FlushChunk(chunk.StartFrame, chunk.EndFrame);
        }
    }

    public VideoObject Slice(float startTime, float endTime)
    {
        if (startTime < 0 || endTime <= startTime || endTime > length)
            throw new ArgumentOutOfRangeException();

        uint startFrame = getFramefromTimecode(startTime);
        uint endFrame = getFramefromTimecode(endTime);
        if (endFrame <= startFrame)
            throw new InvalidOperationException("Slice is empty.");

        string outName = $"{name}_{startFrame}_{endFrame}";
        var sliced = new VideoObject(outName, resolution, fps);
        sliced.length = endTime - startTime;

        // Map video clips
        uint currentTimelineFrame = 0;
        foreach (var clip in clips)
        {
            uint clipEnd = clip.TimelineStartFrame + clip.Length;
            if (startFrame < clipEnd && endFrame > clip.TimelineStartFrame)
            {
                uint overlapStart = Math.Max(startFrame, clip.TimelineStartFrame);
                uint overlapEnd = Math.Min(endFrame, clipEnd);

                uint offsetInClip = overlapStart - clip.TimelineStartFrame;

                var newClip = new VideoClip
                {
                    SourcePath = clip.SourcePath,
                    SourceStartFrame = clip.SourceStartFrame + offsetInClip,
                    SourceEndFrame = clip.SourceStartFrame + offsetInClip + (overlapEnd - overlapStart),
                    TimelineStartFrame = currentTimelineFrame
                };
                foreach (var effect in clip.Effects) newClip.Effects.Add(effect);
                newClip.OnEffectsChanged += () => sliced.cache?.Clear();
                sliced.clips.Add(newClip);

                currentTimelineFrame += (overlapEnd - overlapStart);
            }
        }

        // Map audio clips
        float currentTimelineTime = 0f;
        foreach (var clip in audioClips)
        {
            float clipEnd = clip.TimelineStartTime + clip.Length;
            if (startTime < clipEnd && endTime > clip.TimelineStartTime)
            {
                float overlapStart = Math.Max(startTime, clip.TimelineStartTime);
                float overlapEnd = Math.Min(endTime, clipEnd);

                float offsetInClip = overlapStart - clip.TimelineStartTime;

                sliced.audioClips.Add(new AudioClip
                {
                    SourcePath = clip.SourcePath,
                    SourceStartTime = clip.SourceStartTime + offsetInClip,
                    SourceEndTime = clip.SourceStartTime + offsetInClip + (overlapEnd - overlapStart),
                    TimelineStartTime = currentTimelineTime
                });

                currentTimelineTime += (overlapEnd - overlapStart);
            }
        }

        return sliced;
    }

    public void Append(VideoObject other)
    {
        if (resolution != other.resolution || fps != other.fps)
            throw new InvalidOperationException("Videos must have same resolution and fps.");

        uint currentTotalFrames = (uint)(length * fps);

        foreach (var clip in other.clips)
        {
            var newClip = new VideoClip
            {
                SourcePath = clip.SourcePath,
                SourceStartFrame = clip.SourceStartFrame,
                SourceEndFrame = clip.SourceEndFrame,
                TimelineStartFrame = currentTotalFrames + clip.TimelineStartFrame
            };
            foreach (var effect in clip.Effects) newClip.Effects.Add(effect);
            newClip.OnEffectsChanged += () => cache?.Clear();
            clips.Add(newClip);
        }

        float currentTotalTime = length;
        foreach (var clip in other.audioClips)
        {
            audioClips.Add(new AudioClip
            {
                SourcePath = clip.SourcePath,
                SourceStartTime = clip.SourceStartTime,
                SourceEndTime = clip.SourceEndTime,
                TimelineStartTime = currentTotalTime + clip.TimelineStartTime
            });
        }

        length += other.length;

        // Wipe local chunk manifest since timeline changed
        cache?.Clear();
        manifest = ChunkManifest.Create(name, resolution, fps, (uint)(length * fps));
    }

    public void GetFrameData(uint frame, byte[] buffer)
    {
        long bytesPerFrame = (long)resolution.X * (long)resolution.Y * 4;
        long currentOffset = frame * bytesPerFrame;

        var loopChunk = manifest?.FindChunkForFrame(frame);
        if (loopChunk == null)
        {
            uint chunkSize = calculatedChunkSize;
            uint chunkStart = (frame / chunkSize) * chunkSize;
            uint chunkEnd = Math.Min(chunkStart + chunkSize, (uint)(length * fps));
            if (chunkEnd > chunkStart)
            {
                ExtractChunk(chunkStart, chunkEnd);
                loopChunk = manifest?.FindChunkForFrame(frame);
            }
        }

        if (loopChunk == null || cache == null) return;

        byte[] chunkData = cache.GetChunk(loopChunk);
        long chunkStartByte = (long)loopChunk.StartFrame * bytesPerFrame;
        long posInChunk = currentOffset - chunkStartByte;

        Array.Copy(chunkData, posInChunk, buffer, 0, bytesPerFrame);
    }

    public void SetFrameData(uint frame, byte[] buffer)
    {
        long bytesPerFrame = (long)resolution.X * (long)resolution.Y * 4;
        long currentOffset = frame * bytesPerFrame;

        var loopChunk = manifest?.FindChunkForFrame(frame);
        if (loopChunk == null)
        {
            uint chunkSize = calculatedChunkSize;
            uint chunkStart = (frame / chunkSize) * chunkSize;
            uint chunkEnd = Math.Min(chunkStart + chunkSize, (uint)(length * fps));
            if (chunkEnd > chunkStart)
            {
                ExtractChunk(chunkStart, chunkEnd);
                loopChunk = manifest?.FindChunkForFrame(frame);
            }
        }

        if (loopChunk == null || cache == null) return;

        byte[] chunkData = cache.GetChunk(loopChunk);
        long chunkStartByte = (long)loopChunk.StartFrame * bytesPerFrame;
        long posInChunk = currentOffset - chunkStartByte;

        Array.Copy(buffer, 0, chunkData, posInChunk, bytesPerFrame);
        cache.SetChunk(loopChunk.StartFrame, loopChunk.EndFrame, chunkData, isDirty: true);
    }

    private void ExtractChunk(uint startFrame, uint endFrame)
    {
        if (manifest == null || cache == null) return;

        long bytesPerFrame = (long)resolution.X * (long)resolution.Y * 4;
        long totalBytes = (endFrame - startFrame) * bytesPerFrame;
        byte[] chunkData = new byte[totalBytes];

        foreach (var clip in clips)
        {
            uint clipEnd = clip.TimelineStartFrame + clip.Length;
            if (startFrame < clipEnd && endFrame > clip.TimelineStartFrame)
            {
                uint overlapStart = Math.Max(startFrame, clip.TimelineStartFrame);
                uint overlapEnd = Math.Min(endFrame, clipEnd);

                uint sourceStart = clip.SourceStartFrame + (overlapStart - clip.TimelineStartFrame);
                float startTimeSec = sourceStart / fps;
                float durationSec = (overlapEnd - overlapStart) / fps;

                string tmpDir = Path.Combine(Directory.GetCurrentDirectory(), "tmp");
                Directory.CreateDirectory(tmpDir);
                string tmpRaw = Path.Combine(tmpDir, $"extract_{overlapStart}_{overlapEnd}.raw");

                var process = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "ffmpeg",
                        Arguments = $"-y -ss {startTimeSec:F3} -t {durationSec:F3} -i \"{clip.SourcePath}\" -f rawvideo -pix_fmt rgba -s {(int)resolution.X}x{(int)resolution.Y} -r {fps} \"{tmpRaw}\" -hide_banner -loglevel error",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                process.Start();
                process.WaitForExit();

                if (File.Exists(tmpRaw))
                {
                    byte[] rawData = File.ReadAllBytes(tmpRaw);

                    if (clip.Effects.Count > 0)
                    {
                        int bytesPerSingleFrame = (int)resolution.X * (int)resolution.Y * 4;
                        int framesExtracted = rawData.Length / bytesPerSingleFrame;

                        for (int i = 0; i < framesExtracted; i++)
                        {
                            byte[] frameBuffer = new byte[bytesPerSingleFrame];
                            Array.Copy(rawData, i * bytesPerSingleFrame, frameBuffer, 0, bytesPerSingleFrame);

                            var frameObj = new FrameObject((int)resolution.X, (int)resolution.Y, frameBuffer);

                            foreach (var effect in clip.Effects)
                            {
                                frameObj = effect.Process(frameObj);
                            }

                            frameObj.WriteToBuffer(frameBuffer);
                            Array.Copy(frameBuffer, 0, rawData, i * bytesPerSingleFrame, bytesPerSingleFrame);
                        }
                    }

                    long offsetInChunk = (overlapStart - startFrame) * bytesPerFrame;
                    long copyLen = Math.Min(rawData.Length, chunkData.Length - offsetInChunk);
                    Array.Copy(rawData, 0, chunkData, offsetInChunk, copyLen);
                    File.Delete(tmpRaw);
                }
            }
        }

        cache.SetChunk(startFrame, endFrame, chunkData, false);

        string hash = ChunkCache.ComputeHash(chunkData);
        var entry = new ChunkEntry
        {
            StartFrame = startFrame,
            EndFrame = endFrame,
            UncompressedBytes = chunkData.Length,
            CompressedBytes = 0,
            Hash = hash,
            IsDirty = false
        };
        manifest.AddOrUpdateChunk(entry);
    }

    public byte[] ReadBytes(long offset, int count)
    {
        long bytesPerFrame = (long)resolution.X * (long)resolution.Y * 4;
        byte[] result = new byte[count];
        long bytesRead = 0;

        while (bytesRead < count)
        {
            long currentOffset = offset + bytesRead;
            uint frame = (uint)(currentOffset / bytesPerFrame);

            var loopChunk = manifest?.FindChunkForFrame(frame);
            if (loopChunk == null)
            {
                uint chunkSize = calculatedChunkSize;
                uint chunkStart = (frame / chunkSize) * chunkSize;
                uint chunkEnd = Math.Min(chunkStart + chunkSize, (uint)(length * fps));
                if (chunkEnd > chunkStart)
                {
                    ExtractChunk(chunkStart, chunkEnd);
                    loopChunk = manifest?.FindChunkForFrame(frame);
                }
            }

            if (loopChunk == null || cache == null) break;

            byte[] chunkData = cache.GetChunk(loopChunk);
            long chunkStartByte = (long)loopChunk.StartFrame * bytesPerFrame;
            long chunkEndByte = (long)loopChunk.EndFrame * bytesPerFrame;

            long posInChunk = currentOffset - chunkStartByte;
            long availableInChunk = chunkEndByte - currentOffset;
            long bytesToCopy = Math.Min(availableInChunk, count - bytesRead);

            Array.Copy(chunkData, posInChunk, result, bytesRead, bytesToCopy);
            bytesRead += bytesToCopy;
        }

        if (bytesRead < count)
        {
            byte[] trimmed = new byte[bytesRead];
            Array.Copy(result, trimmed, bytesRead);
            return trimmed;
        }
        return result;
    }

    public void SaveOutVideo(string ExportPath)
    {
        FlushAllDirtyChunks();
        string tmpDir = Path.Combine(Directory.GetCurrentDirectory(), "tmp");
        Directory.CreateDirectory(tmpDir);

        string finalAudioPath = null;
        if (audioClips.Count > 0)
        {
            finalAudioPath = Path.Combine(tmpDir, $"{name}_export_audio.aac");
            string filterComplex = "";
            string inputs = "";

            for (int i = 0; i < audioClips.Count; i++)
            {
                inputs += $"-i \"{audioClips[i].SourcePath}\" ";
                // Use atrim and asetpts to correctly place the audio clips
                filterComplex += $"[{i}:a]atrim=start={audioClips[i].SourceStartTime:F3}:end={audioClips[i].SourceEndTime:F3},asetpts=PTS-STARTPTS[a{i}];";
            }

            for (int i = 0; i < audioClips.Count; i++)
                filterComplex += $"[a{i}]";

            filterComplex += $"concat=n={audioClips.Count}:v=0:a=1[outa]";

            var audioProc = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = $"-y {inputs}-filter_complex \"{filterComplex}\" -map \"[outa]\" -c:a aac -b:a 128k \"{finalAudioPath}\" -hide_banner -loglevel error",
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            audioProc.Start();
            audioProc.WaitForExit();
        }

        string ffmpegArgs;
        if (!string.IsNullOrEmpty(finalAudioPath) && File.Exists(finalAudioPath))
        {
            ffmpegArgs = $"-y -f rawvideo -pix_fmt rgba -s {(int)resolution.X}x{(int)resolution.Y} -r {fps} -i pipe:0 -i \"{finalAudioPath}\" -c:v libx264 -preset medium -c:a copy -shortest \"{ExportPath}\"";
        }
        else
        {
            ffmpegArgs = $"-y -f rawvideo -pix_fmt rgba -s {(int)resolution.X}x{(int)resolution.Y} -r {fps} -i pipe:0 -c:v libx264 -preset medium \"{ExportPath}\"";
        }

        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = ffmpegArgs,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            RedirectStandardInput = true
        };

        using var process = new System.Diagnostics.Process { StartInfo = startInfo };
        process.Start();

        long totalBytes = (long)(length * fps) * (long)resolution.X * (long)resolution.Y * 4;
        long bytesCopied = 0;

        using (var stdin = process.StandardInput.BaseStream)
        {
            while (bytesCopied < totalBytes)
            {
                int readSize = (int)Math.Min(81920, totalBytes - bytesCopied);
                byte[] chunk = ReadBytes(bytesCopied, readSize);
                if (chunk.Length == 0) break;
                stdin.Write(chunk, 0, chunk.Length);
                bytesCopied += chunk.Length;
            }
        }

        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode == 0)
            Console.WriteLine($"Video saved to {ExportPath}");
        else
            Console.WriteLine($"FFmpeg failed with code {process.ExitCode}\n{stderr}");

        // Clean up the temporary mixed audio file
        if (!string.IsNullOrEmpty(finalAudioPath) && File.Exists(finalAudioPath))
        {
            try { File.Delete(finalAudioPath); } catch { }
        }
    }

    public FrameObject getFrame(uint frameNumber)
    {
        return new FrameObject(this, frameNumber);
    }

    public uint getFramefromTimecode(float timecode)
    {
        if (timecode > length || timecode <= 0 || fps <= 0) return 0;
        return (uint)(timecode * fps);
    }

    private (Vector2 resolution, float fps, float length) LoadVideoMetadata(string filePath)
    {
        var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ffprobe",
                Arguments = $"-v error -select_streams v:0 -show_entries stream=width,height,r_frame_rate,duration -of default=noprint_wrappers=1:nokey=1 \"{filePath}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            }
        };

        process.Start();
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        string[] lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        int width = 1920;
        int height = 1080;
        float fps = 25f;
        float len = 0f;

        if (lines.Length >= 4)
        {
            int.TryParse(lines[0], out width);
            int.TryParse(lines[1], out height);

            string[] fpsParts = lines[2].Split('/');
            if (fpsParts.Length == 2 && float.TryParse(fpsParts[0], out float num) && float.TryParse(fpsParts[1], out float den) && den > 0)
                fps = num / den;
            else
                float.TryParse(lines[2], out fps);

            float.TryParse(lines[3], out len);
        }

        return (new Vector2(width, height), fps, len);
    }
}
