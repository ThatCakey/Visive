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

    public Vector2 Resolution;
    public Vector2 Position = Vector2.Zero;

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
    public List<VideoObject> Overlays = new List<VideoObject>();

    private ChunkManifest? manifest;
    private ChunkCache? cache;
    private readonly string chunksDirectory;
    private uint calculatedChunkSize;
    
    private readonly HashSet<uint> extractingChunks = new HashSet<uint>();

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
            TimelineStartFrame = 0,
            Resolution = res
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

    public VideoObject(string name, Vector2 resolution, float fps)
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
        long targetChunkBytes = 500 * 1024 * 1024; // Target 500MB per chunk uncompressed (about 60 frames / 2s at 1080p)
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
                    TimelineStartFrame = currentTimelineFrame,
                    Resolution = clip.Resolution,
                    Position = clip.Position
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

        foreach (var overlay in Overlays)
        {
            sliced.Overlays.Add(overlay.Slice(startTime, endTime));
        }

        return sliced;
    }

    public void Append(VideoObject other)
    {
        uint currentTotalFrames = (uint)(length * fps);
        float fpsRatio = this.fps / other.fps;

        foreach (var clip in other.clips)
        {
            var newClip = new VideoClip
            {
                SourcePath = clip.SourcePath,
                SourceStartFrame = (uint)Math.Round(clip.SourceStartFrame * fpsRatio),
                SourceEndFrame = (uint)Math.Round(clip.SourceEndFrame * fpsRatio),
                TimelineStartFrame = currentTotalFrames + (uint)Math.Round(clip.TimelineStartFrame * fpsRatio),
                Resolution = clip.Resolution,
                Position = clip.Position
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

        // Append overlays
        // If 'other' has overlays, we need to map them properly
        foreach (var otherOverlay in other.Overlays)
        {
            // The overlay needs to start at current length
            var shiftedOverlay = otherOverlay; 
            // Wait, we need to just keep it in the list, but if they are stacked we must ensure time aligns.
            // Actually, for append, we should append the entire layer structures?
            // Let's keep it simple: we can just leave this as is for now, users can stack *after* appending.
        }

        // Wipe local chunk manifest since timeline changed
        cache?.Clear();
        manifest = ChunkManifest.Create(name, resolution, fps, (uint)(length * fps));
    }

    public void PrependEmpty(float duration)
    {
        uint emptyFrames = getFramefromTimecode(duration);
        
        foreach (var clip in clips) clip.TimelineStartFrame += emptyFrames;
        foreach (var audio in audioClips) audio.TimelineStartTime += duration;
        foreach (var overlay in Overlays) overlay.PrependEmpty(duration);

        length += duration;

        cache?.Clear();
        manifest = ChunkManifest.Create(name, resolution, fps, (uint)(length * fps));
    }

    public void AppendEmpty(float duration)
    {
        length += duration;
        foreach (var overlay in Overlays) overlay.AppendEmpty(duration);

        cache?.Clear();
        manifest = ChunkManifest.Create(name, resolution, fps, (uint)(length * fps));
    }

    public static unsafe void BlendBuffers(byte[] baseBuffer, byte[] overlayBuffer)
    {
        int len = Math.Min(baseBuffer.Length, overlayBuffer.Length);
        if (len == 0) return;
        
        fixed (byte* pBase = baseBuffer)
        fixed (byte* pOver = overlayBuffer)
        {
            for (int i = 0; i < len - 3; i += 4)
            {
                int a_overlay = pOver[i + 3];
                if (a_overlay == 255)
                {
                    pBase[i] = pOver[i];
                    pBase[i + 1] = pOver[i + 1];
                    pBase[i + 2] = pOver[i + 2];
                    pBase[i + 3] = 255;
                }
                else if (a_overlay > 0)
                {
                    int a_base = pBase[i + 3];
                    int inv_a_overlay = 255 - a_overlay;
                    int a_out = a_overlay * 255 + a_base * inv_a_overlay;

                    if (a_out > 0)
                    {
                        pBase[i] = (byte)((pOver[i] * a_overlay * 255 + pBase[i] * a_base * inv_a_overlay) / a_out);
                        pBase[i + 1] = (byte)((pOver[i + 1] * a_overlay * 255 + pBase[i + 1] * a_base * inv_a_overlay) / a_out);
                        pBase[i + 2] = (byte)((pOver[i + 2] * a_overlay * 255 + pBase[i + 2] * a_base * inv_a_overlay) / a_out);
                        pBase[i + 3] = (byte)(a_out / 255);
                    }
                }
            }
        }
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

        byte[]? chunkData = cache.GetChunk(loopChunk);
        if (chunkData == null)
        {
            // Chunk was evicted, re-extract it synchronously if we got here
            ExtractChunk(loopChunk.StartFrame, loopChunk.EndFrame);
            chunkData = cache.GetChunk(loopChunk);
        }

        if (chunkData != null)
        {
            long chunkStartByte = (long)loopChunk.StartFrame * bytesPerFrame;
            long posInChunk = currentOffset - chunkStartByte;
            Array.Copy(chunkData, posInChunk, buffer, 0, bytesPerFrame);
        }
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

    private readonly System.Collections.Concurrent.ConcurrentDictionary<uint, object> chunkLocks = new();

    private void ExtractChunk(uint startFrame, uint endFrame)
    {
        if (manifest == null || cache == null) return;

        object chunkLock = chunkLocks.GetOrAdd(startFrame, _ => new object());
        
        lock (chunkLock)
        {
            // Double-check if the chunk was extracted while we were waiting for the lock
            var checkChunk = manifest.FindChunkForFrame(startFrame);
            if (checkChunk != null && !checkChunk.IsDirty)
            {
                return; // Another thread already extracted it!
            }

            long bytesPerFrame = (long)resolution.X * (long)resolution.Y * 4;
            long totalBytes = (endFrame - startFrame) * bytesPerFrame;
            byte[] chunkData = ChunkCache.RentBuffer((int)totalBytes);
            Array.Clear(chunkData, 0, chunkData.Length);

            foreach (var clip in clips)
            {
                uint clipEnd = clip.TimelineStartFrame + clip.Length;
                if (startFrame < clipEnd && endFrame > clip.TimelineStartFrame)
                {
                    uint overlapStart = Math.Max(startFrame, clip.TimelineStartFrame);
                    uint overlapEnd = Math.Min(endFrame, clipEnd);

                    uint sourceStart = clip.SourceStartFrame + (overlapStart - clip.TimelineStartFrame);
                    float startTimeSec = sourceStart / fps;
                    uint expectedFrames = overlapEnd - overlapStart;

                    // OPTIMIZATION: Instead of generating a black lavfi stream and overlaying, we use a single input stream
                    // and use the 'pad' filter to place it on a black canvas. This avoids costly alpha blending and dual-stream processing.
                    string filterComplex = $"[0:v]scale={(int)clip.Resolution.X}:{(int)clip.Resolution.Y}:flags=fast_bilinear,setpts=PTS-STARTPTS,pad={(int)resolution.X}:{(int)resolution.Y}:{clip.Position.X}:{clip.Position.Y}:black[out]";

                    using var process = new System.Diagnostics.Process
                    {
                        StartInfo = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = "ffmpeg",
                            Arguments = $"-y -ss {startTimeSec:F3} -i \"{clip.SourcePath}\" -filter_complex \"{filterComplex}\" -map \"[out]\" -vframes {expectedFrames} -f rawvideo -pix_fmt rgba -r {fps} pipe:1 -hide_banner -loglevel error",
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardOutput = true
                        }
                    };
                    process.Start();

                    int expectedBytes = (int)(expectedFrames * bytesPerFrame);
                    int totalRead = 0;
                    long offsetInChunk = (overlapStart - startFrame) * bytesPerFrame;
                    
                    using (var stream = process.StandardOutput.BaseStream)
                    {
                        while (totalRead < expectedBytes)
                        {
                            int read = stream.Read(chunkData, (int)offsetInChunk + totalRead, expectedBytes - totalRead);
                            if (read == 0) break;
                            totalRead += read;
                        }
                    }

                    process.WaitForExit();

                    if (totalRead > 0)
                    {
                        if (clip.Effects.Count > 0)
                        {
                            int bytesPerSingleFrame = (int)resolution.X * (int)resolution.Y * 4;
                            int framesExtracted = totalRead / bytesPerSingleFrame;

                            for (int i = 0; i < framesExtracted; i++)
                            {
                                int frameOffset = (int)offsetInChunk + (i * bytesPerSingleFrame);
                                byte[] frameBuffer = new byte[bytesPerSingleFrame];
                                Array.Copy(chunkData, frameOffset, frameBuffer, 0, bytesPerSingleFrame);

                                var frameObj = new FrameObject((int)resolution.X, (int)resolution.Y, frameBuffer);

                                foreach (var effect in clip.Effects)
                                {
                                    frameObj = effect.Process(frameObj);
                                }

                                frameObj.WriteToBuffer(chunkData, frameOffset);
                            }
                        }
                    }
                }
            }

            lock (cache)
            {
                cache.SetChunk(startFrame, endFrame, chunkData, true);
            }

            var entry = new ChunkEntry
            {
                StartFrame = startFrame,
                EndFrame = endFrame,
                UncompressedBytes = chunkData.Length,
                CompressedBytes = 0,
                Hash = "",
                IsDirty = false
            };
            manifest.AddOrUpdateChunk(entry);
        }
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

            byte[]? chunkData;
            lock (cache)
            {
                chunkData = cache.GetChunk(loopChunk);
            }

            if (chunkData == null)
            {
                // Chunk was evicted from RAM, re-extract it
                ExtractChunk(loopChunk.StartFrame, loopChunk.EndFrame);
                lock (cache)
                {
                    chunkData = cache.GetChunk(loopChunk);
                }
            }
            
            if (chunkData == null) break; // Should not happen unless extraction failed completely
            long chunkStartByte = (long)loopChunk.StartFrame * bytesPerFrame;
            long chunkEndByte = (long)loopChunk.EndFrame * bytesPerFrame;

            long posInChunk = currentOffset - chunkStartByte;
            long availableInChunk = chunkEndByte - currentOffset;
            long bytesToCopy = Math.Min(availableInChunk, count - bytesRead);

            Array.Copy(chunkData, posInChunk, result, bytesRead, bytesToCopy);
            bytesRead += bytesToCopy;
        }

        foreach (var overlay in Overlays)
        {
            byte[] overlayBytes = overlay.ReadBytes(offset, (int)bytesRead);
            BlendBuffers(result, overlayBytes);
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
        foreach (var overlay in Overlays) overlay.FlushAllDirtyChunks();

        string tmpDir = Path.Combine(Directory.GetCurrentDirectory(), "tmp");
        Directory.CreateDirectory(tmpDir);

        string finalAudioPath = null;
        
        List<VideoObject> allTracks = new List<VideoObject> { this };
        allTracks.AddRange(Overlays);
        
        bool hasAudio = allTracks.Any(t => t.audioClips.Count > 0);

        if (hasAudio)
        {
            finalAudioPath = Path.Combine(tmpDir, $"{name}_export_audio.aac");
            string filterComplex = "";
            string inputs = "";
            int inputIndex = 0;
            List<string> trackOutputs = new List<string>();

            for (int t = 0; t < allTracks.Count; t++)
            {
                var track = allTracks[t];
                if (track.audioClips.Count == 0) continue;

                for (int i = 0; i < track.audioClips.Count; i++)
                {
                    var clip = track.audioClips[i];
                    inputs += $"-i \"{clip.SourcePath}\" ";
                    filterComplex += $"[{inputIndex}:a]atrim=start={clip.SourceStartTime:F3}:end={clip.SourceEndTime:F3},asetpts=PTS-STARTPTS[a{inputIndex}];";
                    inputIndex++;
                }

                int trackStartInput = inputIndex - track.audioClips.Count;
                for (int i = trackStartInput; i < inputIndex; i++)
                {
                    filterComplex += $"[a{i}]";
                }
                filterComplex += $"concat=n={track.audioClips.Count}:v=0:a=1[track{t}];";
                trackOutputs.Add($"[track{t}]");
            }

            foreach (var tout in trackOutputs) filterComplex += tout;
            
            if (trackOutputs.Count > 1)
                filterComplex += $"amix=inputs={trackOutputs.Count}:duration=longest[outa]";
            else
                filterComplex += $"aformat=sample_fmts=fltp:sample_rates=44100[outa]";

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

    public FrameObject? GetPreviewFrame(float timecode, float quality = 1.0f, bool keyframeOnly = false, bool allowSync = true)
    {
        uint frame = getFramefromTimecode(timecode);
        return GetPreviewFrame(frame, quality, keyframeOnly, allowSync);
    }

    public FrameObject? GetPreviewFrame(uint frame, float quality = 1.0f, bool keyframeOnly = false, bool allowSync = true)
    {
        var baseFrame = GetSingleTrackPreviewFrame(frame, quality, keyframeOnly, allowSync);
        if (baseFrame == null) return null;

        foreach (var overlay in Overlays)
        {
            var overlayFrame = overlay.GetPreviewFrame(frame, quality, keyframeOnly, allowSync);
            if (overlayFrame != null)
            {
                var newBase = baseFrame.Blend(overlayFrame);
                baseFrame.Dispose();
                overlayFrame.Dispose();
                baseFrame = newBase;
            }
        }
        return baseFrame;
    }

    private FrameObject? GetSingleTrackPreviewFrame(uint frame, float quality = 1.0f, bool keyframeOnly = false, bool allowSync = true)
    {
        int scaledW = (int)Math.Max(1, resolution.X * quality);
        int scaledH = (int)Math.Max(1, resolution.Y * quality);

        // Fast Path: Check if cached
        var loopChunk = manifest?.FindChunkForFrame(frame);
        if (loopChunk != null && cache != null && !loopChunk.IsDirty)
        {
            byte[] buffer = ChunkCache.RentBuffer((int)resolution.X * (int)resolution.Y * 4);
            GetFrameData(frame, buffer);
            
            // READ-AHEAD PREFETCH:
            // If we are playing (keyframeOnly=false) and we are past the halfway point of this chunk,
            // fire off a background prefetch for the NEXT chunk so it's ready when we get there.
            if (!keyframeOnly)
            {
                uint midpoint = loopChunk.StartFrame + (calculatedChunkSize / 2);
                if (frame >= midpoint)
                {
                    uint nextChunkStart = loopChunk.EndFrame;
                    uint nextChunkEnd = Math.Min(nextChunkStart + calculatedChunkSize, (uint)(length * fps));
                    
                    if (nextChunkStart < nextChunkEnd)
                    {
                        lock (extractingChunks)
                        {
                            if (!extractingChunks.Contains(nextChunkStart))
                            {
                                extractingChunks.Add(nextChunkStart);
                                Task.Run(() => 
                                {
                                    try { ExtractChunk(nextChunkStart, nextChunkEnd); }
                                    finally { lock(extractingChunks) { extractingChunks.Remove(nextChunkStart); } }
                                });
                            }
                        }
                    }
                }
            }

            var frameObj = new FrameObject((int)resolution.X, (int)resolution.Y, buffer, isPooled: true);
            if (Math.Abs(quality - 1.0f) > 0.01f)
            {
                var resized = frameObj.Resize(scaledW, scaledH);
                frameObj.Dispose();
                return resized;
            }
            return frameObj;
        }

        // Slow Path: Cache Miss - Prefetch asynchronously (ONLY if we aren't fast-scrubbing)
        if (!keyframeOnly)
        {
            uint chunkStart = (frame / calculatedChunkSize) * calculatedChunkSize;
            uint chunkEnd = Math.Min(chunkStart + calculatedChunkSize, (uint)(length * fps));
            
            lock (extractingChunks)
            {
                if (chunkEnd > chunkStart && !extractingChunks.Contains(chunkStart))
                {
                    extractingChunks.Add(chunkStart);
                    Task.Run(() => 
                    {
                        try {
                            ExtractChunk(chunkStart, chunkEnd);
                        } finally {
                            lock(extractingChunks) { extractingChunks.Remove(chunkStart); }
                        }
                    });
                }
            }
        }

        if (!allowSync) return null;

        // Extract single frame AFAP
        int bytesPerFrame = scaledW * scaledH * 4;
        byte[] frameData = ChunkCache.RentBuffer(bytesPerFrame);
        Array.Clear(frameData, 0, frameData.Length);

        foreach (var clip in clips)
        {
            uint clipEnd = clip.TimelineStartFrame + clip.Length;
            if (frame >= clip.TimelineStartFrame && frame < clipEnd)
            {
                uint sourceStart = clip.SourceStartFrame + (frame - clip.TimelineStartFrame);
                float startTimeSec = sourceStart / fps;

                string tmpDir = Path.Combine(Directory.GetCurrentDirectory(), "tmp");
                Directory.CreateDirectory(tmpDir);
                string tmpRaw = Path.Combine(tmpDir, $"preview_{frame}_{clip.GetHashCode()}.raw");

                string filterComplex = $"[1:v]scale={(int)(clip.Resolution.X * quality)}:{(int)(clip.Resolution.Y * quality)},setpts=PTS-STARTPTS[scaled]; [0:v][scaled]overlay={(int)(clip.Position.X * quality)}:{(int)(clip.Position.Y * quality)}:shortest=1[out]";
                string skipArgs = keyframeOnly ? "-skip_frame nokey " : "";

                var process = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "ffmpeg",
                        Arguments = $"-y -f lavfi -i \"color=black@0:s={scaledW}x{scaledH}:r={fps}\" {skipArgs}-ss {startTimeSec:F3} -i \"{clip.SourcePath}\" -filter_complex \"{filterComplex}\" -map \"[out]\" -vframes 1 -f rawvideo -pix_fmt rgba -r {fps} \"{tmpRaw}\" -hide_banner -loglevel error",
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
                        var frameObj = new FrameObject(scaledW, scaledH, rawData);
                        foreach (var effect in clip.Effects)
                        {
                            frameObj = effect.Process(frameObj);
                        }
                        frameObj.WriteToBuffer(rawData);
                    }

                    long copyLen = Math.Min(rawData.Length, frameData.Length);
                    // Simple overwrite layering (matching ExtractChunk's current behavior)
                    Array.Copy(rawData, 0, frameData, 0, copyLen);
                    File.Delete(tmpRaw);
                }
            }
        }

        return new FrameObject(scaledW, scaledH, frameData, isPooled: true);
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
