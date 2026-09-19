using System;
using System.IO;
using System.Numerics;

namespace Visive;

public class FrameObject : IDisposable
{
    private Pixel[]? _pixels;
    public Pixel[] pixels 
    {
        get 
        {
            if (_pixels == null && RawBuffer != null)
                _pixels = LoadFrameFromBuffer(width, height, RawBuffer);
            return _pixels ?? Array.Empty<Pixel>();
        }
        set { _pixels = value; }
    }
    
    public byte[]? RawBuffer { get; private set; }
    
    public int width { get; private set; }
    public int height { get; private set; }
    private uint frame;
    private VideoObject? videoParent;
    public readonly bool loaded = false;
    private readonly bool isPooled = false;

    public void Dispose()
    {
        if (isPooled && RawBuffer != null)
        {
            ChunkCache.ReturnBuffer(RawBuffer);
            RawBuffer = null;
        }
    }

    public FrameObject(VideoObject video, float timecode)
    {
        width = (int)video.resolution.X;
        height = (int)video.resolution.Y;

        this.frame = video.getFramefromTimecode(timecode);
        videoParent = video;

        RawBuffer = LoadFrameBuffer(width, height, video, this.frame);

        loaded = true;
    }

    public FrameObject(VideoObject video, uint frame)
    {
        width = (int)video.resolution.X;
        height = (int)video.resolution.Y;

        this.frame = frame;
        videoParent = video;

        RawBuffer = LoadFrameBuffer(width, height, video, this.frame);

        loaded = true;
    }

    public FrameObject(int width, int height, byte[] frameBuffer, bool isPooled = false)
    {
        this.width = width;
        this.height = height;
        this.videoParent = null;
        this.frame = 0;
        
        this.RawBuffer = frameBuffer;
        this.loaded = true;
        this.isPooled = isPooled;
    }

    byte[] LoadFrameBuffer(int width, int height, VideoObject video, uint frame)
    {
        int bytesPerFrame = width * height * 4;
        byte[] buffer = new byte[bytesPerFrame];
        
        video.GetFrameData(frame, buffer);

        return buffer;
    }

    Pixel[] LoadFrameFromBuffer(int width, int height, byte[] buffer)
    {
        Pixel[] pixels = new Pixel[width * height];

        for (int i = 0; i < width * height; i++)
        {
            int offset = i * 4;
            byte r8 = buffer[offset];
            byte g8 = buffer[offset + 1];
            byte b8 = buffer[offset + 2];
            byte a8 = buffer[offset + 3];

            pixels[i] = new Pixel
            {
                R = (ushort)(r8 << 2),
                G = (ushort)(g8 << 2),
                B = (ushort)(b8 << 2),
                alpha = a8
            };
        }
        return pixels;
    }

    public void SaveFrame()
    {
        if (!loaded || pixels.Length <= 0 || videoParent == null) return;

        int bytesPerFrame = width * height * 4;
        byte[] buffer = new byte[bytesPerFrame];
        WriteToBuffer(buffer);

        videoParent.SetFrameData(frame, buffer);
    }

    public void WriteToBuffer(byte[] buffer)
    {
        WriteToBuffer(buffer, 0);
    }

    public void WriteToBuffer(byte[] buffer, int targetOffset)
    {
        if (!loaded) return;

        // Fast path: if pixels haven't been parsed/modified, just copy the raw buffer
        if (_pixels == null && RawBuffer != null)
        {
            int copyLen = Math.Min(buffer.Length - targetOffset, RawBuffer.Length);
            Array.Copy(RawBuffer, 0, buffer, targetOffset, copyLen);
            return;
        }

        if (pixels.Length <= 0) return;

        for (int i = 0; i < pixels.Length; i++)
        {
            int offset = targetOffset + (i * 4);
            buffer[offset] = (byte)(pixels[i].R >> 2);
            buffer[offset + 1] = (byte)(pixels[i].G >> 2);
            buffer[offset + 2] = (byte)(pixels[i].B >> 2);
            buffer[offset + 3] = pixels[i].alpha;
        }
    }

    public void WriteToBuffer(IntPtr destPtr, int destLength)
    {
        if (!loaded) return;

        if (_pixels == null && RawBuffer != null)
        {
            int copyLen = Math.Min(destLength, RawBuffer.Length);
            System.Runtime.InteropServices.Marshal.Copy(RawBuffer, 0, destPtr, copyLen);
            return;
        }

        if (pixels.Length <= 0) return;

        byte[] temp = new byte[destLength];
        WriteToBuffer(temp);
        System.Runtime.InteropServices.Marshal.Copy(temp, 0, destPtr, destLength);
    }

    public FrameObject Resize(int newWidth, int newHeight)
    {
        byte[] newBuffer = new byte[newWidth * newHeight * 4];
        float ratioX = (float)width / newWidth;
        float ratioY = (float)height / newHeight;

        for (int y = 0; y < newHeight; y++)
        {
            for (int x = 0; x < newWidth; x++)
            {
                int srcX = (int)(x * ratioX);
                int srcY = (int)(y * ratioY);
                int srcIndex = srcY * width + srcX;
                int dstIndex = (y * newWidth + x) * 4;

                newBuffer[dstIndex] = (byte)(pixels[srcIndex].R >> 2);
                newBuffer[dstIndex + 1] = (byte)(pixels[srcIndex].G >> 2);
                newBuffer[dstIndex + 2] = (byte)(pixels[srcIndex].B >> 2);
                newBuffer[dstIndex + 3] = pixels[srcIndex].alpha;
            }
        }

        return new FrameObject(newWidth, newHeight, newBuffer);
    }

    public FrameObject Blend(FrameObject overlay)
    {
        if (width != overlay.width || height != overlay.height) return this;

        int bytesPerFrame = width * height * 4;
        byte[] baseBuffer = new byte[bytesPerFrame];
        WriteToBuffer(baseBuffer);

        byte[] overlayBuffer = new byte[bytesPerFrame];
        overlay.WriteToBuffer(overlayBuffer);

        VideoObject.BlendBuffers(baseBuffer, overlayBuffer);

        return new FrameObject(width, height, baseBuffer);
    }

    public void ExportToPng(string path)
    {
        if (!loaded) return;

        int bytesPerFrame = width * height * 4;
        byte[] buffer = new byte[bytesPerFrame];
        WriteToBuffer(buffer);

        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = $"-y -f rawvideo -pix_fmt rgba -s {width}x{height} -i pipe:0 -vframes 1 \"{path}\" -hide_banner -loglevel error",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true
        };

        using var process = new System.Diagnostics.Process { StartInfo = startInfo };
        process.Start();
        
        using (var stdin = process.StandardInput.BaseStream)
        {
            stdin.Write(buffer, 0, buffer.Length);
        }
        
        process.WaitForExit();
    }
}
