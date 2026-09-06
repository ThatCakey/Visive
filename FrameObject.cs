using System;
using System.IO;
using System.Numerics;

namespace Visive;

public class FrameObject
{
    public Pixel[] pixels;
    public int width { get; private set; }
    public int height { get; private set; }
    private uint frame;
    private VideoObject? videoParent;
    public readonly bool loaded = false;

    public FrameObject(VideoObject video, float timecode)
    {
        width = (int)video.resolution.X;
        height = (int)video.resolution.Y;

        this.frame = video.getFramefromTimecode(timecode);
        videoParent = video;

        pixels = LoadFrame(width, height, video, this.frame);

        loaded = true;
    }

    public FrameObject(VideoObject video, uint frame)
    {
        width = (int)video.resolution.X;
        height = (int)video.resolution.Y;

        this.frame = frame;
        videoParent = video;

        pixels = LoadFrame(width, height, video, this.frame);

        loaded = true;
    }

    public FrameObject(int width, int height, byte[] frameBuffer)
    {
        this.width = width;
        this.height = height;
        this.videoParent = null;
        this.frame = 0;
        
        pixels = LoadFrameFromBuffer(width, height, frameBuffer);
        loaded = true;
    }

    Pixel[] LoadFrame(int width, int height, VideoObject video, uint frame)
    {
        int bytesPerFrame = width * height * 4;
        byte[] buffer = new byte[bytesPerFrame];
        
        video.GetFrameData(frame, buffer);

        return LoadFrameFromBuffer(width, height, buffer);
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
        if (!loaded || pixels.Length <= 0) return;

        for (int i = 0; i < pixels.Length; i++)
        {
            int offset = i * 4;
            buffer[offset] = (byte)(pixels[i].R >> 2);
            buffer[offset + 1] = (byte)(pixels[i].G >> 2);
            buffer[offset + 2] = (byte)(pixels[i].B >> 2);
            buffer[offset + 3] = pixels[i].alpha;
        }
    }
}
