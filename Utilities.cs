using System;

namespace Visive;

public struct Pixel
{
    private uint rgb;      // 10-bit R, G, B packed
    public byte alpha;     // 8-bit alpha

    public ushort R
    {
        get => (ushort)((rgb >> 20) & 0x3FF);
        set => rgb = (rgb & ~(0x3FFu << 20)) | (((uint)value & 0x3FF) << 20);
    }
    public ushort G
    {
        get => (ushort)((rgb >> 10) & 0x3FF);
        set => rgb = (rgb & ~(0x3FFu << 10)) | (((uint)value & 0x3FF) << 10);
    }
    public ushort B
    {
        get => (ushort)(rgb & 0x3FF);
        set => rgb = (rgb & ~0x3FFu) | ((uint)value & 0x3FF);
    }
}

