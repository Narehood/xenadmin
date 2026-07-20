namespace XcpNgCenter.Rfb;

/// <summary>Opaque RGB color used by RFB fill operations.</summary>
public readonly record struct RfbColor(byte R, byte G, byte B)
{
    public static RfbColor Black { get; } = new(0, 0, 0);
    public static RfbColor White { get; } = new(255, 255, 255);

    public static RfbColor FromRgb(byte r, byte g, byte b) => new(r, g, b);

    /// <summary>Pack as BGRA32 little-endian uint (A = 255).</summary>
    public uint ToBgra32() => (uint)(B | (G << 8) | (R << 16) | (0xFF << 24));
}
