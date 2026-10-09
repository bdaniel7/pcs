namespace PlexCompatibleServer.Infrastructure.Media;

/// <summary>
/// Reads pixel dimensions straight from the image header so the transcode endpoint can size its
/// output without decoding the picture. PNG (IHDR) and JPEG (SOF0..15) cover every file the art
/// pipeline writes or downloads; anything else - webp, truncated payloads, files that only look
/// like images - reports false and lets the caller serve the original untouched, which is always
/// a safe answer for an endpoint that must never 404 or mis-size.
/// </summary>
internal static class ImageSize
{
    public static bool TryGet(string path, out int width, out int height)
    {
        width = 0;
        height = 0;

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < 26 || info.Length > 64L * 1024 * 1024) return false;

            var bytes = File.ReadAllBytes(path);

            // PNG: 8-byte signature, then IHDR whose length field must be 13.
            if (bytes.Length >= 24 &&
                bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47 &&
                bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A)
            {
                width = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
                height = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];

                return width > 0 && height > 0;
            }

            if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
                return tryJpeg(bytes, out width, out height);

            return false;
        }
        catch
        {
            // A concurrent rewrite or a permissions hiccup must not take the image route down.
            return false;
        }
    }

    private static bool tryJpeg(byte[] bytes, out int width, out int height)
    {
        width = 0;
        height = 0;

        var i = 2;
        while (i + 8 < bytes.Length)
        {
            if (bytes[i] != 0xFF) { i++; continue; }

            var marker = bytes[i + 1];
            if (marker == 0xFF || marker == 0x00) { i++; continue; }   // fill / stuffed byte

            // Markers that carry no payload: TEM and the RST/SOI/EOI block.
            if (marker == 0x01 || marker is >= 0xD0 and <= 0xD9) { i += 2; continue; }

            // SOF0..SOF15 except DHT (C4), JPG (C8) and DAC (CC) hold the frame dimensions.
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                height = (bytes[i + 5] << 8) | bytes[i + 6];
                width = (bytes[i + 7] << 8) | bytes[i + 8];

                return width > 0 && height > 0;
            }

            var length = (bytes[i + 2] << 8) | bytes[i + 3];
            if (length < 2) return false;   // corrupt length: no point scanning further

            i += 2 + length;
        }

        return false;
    }
}
