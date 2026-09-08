using System.Numerics;

namespace Visive;

public class VideoEffect
{
    public virtual FrameObject Process(FrameObject Frame)
    {
        return Frame;
    }
}

public class SwapRedAndGreen : VideoEffect
{
    public override FrameObject Process(FrameObject Frame)
    {
        Pixel[] pixels = Frame.pixels;

        for (int i = 0; i <= pixels.Length - 1; i++)
        {
            // Store the original Red value temporarily
            ushort tempR = pixels[i].R;

            // Swap them
            pixels[i].R = pixels[i].G;
            pixels[i].G = tempR;
        }

        return Frame;
    }
}
