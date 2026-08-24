using System;
using System.IO;
using System.Numerics;

namespace Visive;

public class FrameObject
{
    public Pixel[] pixels;
    private uint frame;
    private VideoObject videoParent;
    public readonly bool loaded = false;
    public FrameObject(VideoObject video, float timecode)
    {
        int width = (int)video.resolution.X;
        int height = (int)video.resolution.Y;

        this.frame = video.getFramefromTimecode(timecode);
        videoParent = video;

        pixels = loadFrameFromIntermediary(width, height, video, this.frame);

        loaded = true;
    }
    public FrameObject(VideoObject video, uint frame)
    {
        int width = (int)video.resolution.X;
        int height = (int)video.resolution.Y;

        this.frame = frame;
        videoParent = video;

        pixels = loadFrameFromIntermediary(width, height, video, this.frame);

        loaded = true;
    }

    Pixel[] loadFrameFromIntermediary(int width, int height, VideoObject video, uint frame)
    {
        // Calculate bytes per frame and frame offset
        int bytesPerFrame = width * height * 4;  // RGBA: 4 bytes per pixel
        uint frameNumber = frame;
        long frameOffset = frameNumber * bytesPerFrame;

        Pixel[] pixels = new Pixel[width * height];

        byte[] buffer = video.ReadBytes(frameOffset, bytesPerFrame);
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

        return pixels;
    }
    public void SaveToIntermediary()
    {
        if (!loaded || pixels.Length <= 0) return;

        int bytesPerFrame = (int)videoParent.resolution.X * (int)videoParent.resolution.Y * 4;
        long frameOffset = (long)frame * bytesPerFrame;

        byte[] buffer = new byte[bytesPerFrame];

        for (int i = 0; i < pixels.Length; i++)
        {
            int offset = i * 4;
            buffer[offset] = (byte)(pixels[i].R >> 2);
            buffer[offset + 1] = (byte)(pixels[i].G >> 2);
            buffer[offset + 2] = (byte)(pixels[i].B >> 2);
            buffer[offset + 3] = pixels[i].alpha;
        }

        videoParent.WriteBytes(frameOffset, buffer);
    }
}
