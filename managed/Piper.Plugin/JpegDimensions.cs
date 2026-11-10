namespace LuckyRobots.Piper.Plugin;

public static class JpegDimensions
{
    public static (int Width, int Height) Read(ReadOnlySpan<byte> jpeg)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xff || jpeg[1] != 0xd8) throw new InvalidDataException("Invalid JPEG header.");
        int offset = 2;
        while (offset + 3 < jpeg.Length)
        {
            if (jpeg[offset++] != 0xff) break;
            while (offset < jpeg.Length && jpeg[offset] == 0xff) offset++;
            if (offset >= jpeg.Length) break;
            int marker = jpeg[offset++];
            if (marker is 0xd9 or 0xda) break;
            if (marker == 0x01 || marker is >= 0xd0 and <= 0xd7) continue;
            if (offset + 2 > jpeg.Length) break;
            int length = (jpeg[offset] << 8) | jpeg[offset + 1];
            if (length < 2 || offset + length > jpeg.Length) break;
            if (marker is >= 0xc0 and <= 0xcf && marker is not (0xc4 or 0xc8 or 0xcc))
            {
                if (length < 8) break;
                int height = (jpeg[offset + 3] << 8) | jpeg[offset + 4];
                int width = (jpeg[offset + 5] << 8) | jpeg[offset + 6];
                if (width is < 1 or > 2048 || height is < 1 or > 2048) break;
                return (width, height);
            }
            offset += length;
        }
        throw new InvalidDataException("JPEG is missing valid frame dimensions.");
    }
}
