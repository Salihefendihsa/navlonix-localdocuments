namespace Navlonix.LocalDocuments;

/// <summary>
/// PNG/JPEG/WEBP başlığından yalnız boyutları okur (görüntüyü çözmeden). OCR motoruna gitmeden önce piksel sayısı
/// sınırını zorlamak (sıkıştırma bombası koruması) ve bozuk başlıkları tanımak için kullanılır. Tüm döngüler sınırlıdır.
/// </summary>
public static class ImageHeaderInspector
{
    public readonly record struct Dimensions(string Format, long Width, long Height)
    {
        public long Pixels => Width * Height;
    }

    /// <summary>Boyutlar güvenilir biçimde okunamazsa null (bozuk/kesik/desteklenmeyen).</summary>
    public static Dimensions? TryRead(ReadOnlySpan<byte> b)
    {
        try
        {
            if (b.Length >= 24 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return Png(b);
            if (b.Length >= 4 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return Jpeg(b);
            if (b.Length >= 30 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P') return Webp(b);
        }
        catch (ArgumentOutOfRangeException)
        {
            // Kesik başlık: bozuk sayılır.
        }
        return null;
    }

    private static Dimensions? Png(ReadOnlySpan<byte> b)
    {
        // 8 imza + 4 uzunluk + "IHDR" + 4 genişlik + 4 yükseklik
        if (b[12] != 'I' || b[13] != 'H' || b[14] != 'D' || b[15] != 'R') return null;
        long w = (long)b[16] << 24 | (long)b[17] << 16 | (long)b[18] << 8 | b[19];
        long h = (long)b[20] << 24 | (long)b[21] << 16 | (long)b[22] << 8 | b[23];
        return w > 0 && h > 0 ? new Dimensions("png", w, h) : null;
    }

    private static Dimensions? Jpeg(ReadOnlySpan<byte> b)
    {
        var i = 2;
        for (var guard = 0; guard < 512 && i + 4 <= b.Length; guard++)
        {
            if (b[i] != 0xFF) return null;
            while (i < b.Length && b[i] == 0xFF) i++; // dolgu baytları
            if (i >= b.Length) return null;
            var marker = b[i++];
            if (marker is 0xD8 or 0x01 || marker is >= 0xD0 and <= 0xD7) continue; // uzunluksuz işaretler
            if (marker == 0xD9 || marker == 0xDA) return null; // SOF görülmeden veri başladı
            if (i + 2 > b.Length) return null;
            var len = b[i] << 8 | b[i + 1];
            if (len < 2) return null;
            if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC))
            {
                if (i + 7 > b.Length) return null;
                long h = b[i + 3] << 8 | b[i + 4];
                long w = b[i + 5] << 8 | b[i + 6];
                return w > 0 && h > 0 ? new Dimensions("jpeg", w, h) : null;
            }
            i += len;
        }
        return null;
    }

    private static Dimensions? Webp(ReadOnlySpan<byte> b)
    {
        if (b[12] == 'V' && b[13] == 'P' && b[14] == '8' && b[15] == ' ')
        {
            if (b.Length < 30 || b[23] != 0x9D || b[24] != 0x01 || b[25] != 0x2A) return null;
            long w = (b[26] | b[27] << 8) & 0x3FFF;
            long h = (b[28] | b[29] << 8) & 0x3FFF;
            return w > 0 && h > 0 ? new Dimensions("webp", w, h) : null;
        }
        if (b[12] == 'V' && b[13] == 'P' && b[14] == '8' && b[15] == 'L')
        {
            if (b[20] != 0x2F) return null;
            var bits = (uint)(b[21] | b[22] << 8 | b[23] << 16 | b[24] << 24);
            long w = (bits & 0x3FFF) + 1;
            long h = ((bits >> 14) & 0x3FFF) + 1;
            return new Dimensions("webp", w, h);
        }
        if (b[12] == 'V' && b[13] == 'P' && b[14] == '8' && b[15] == 'X')
        {
            long w = (b[24] | b[25] << 8 | b[26] << 16) + 1L;
            long h = (b[27] | b[28] << 8 | b[29] << 16) + 1L;
            return new Dimensions("webp", w, h);
        }
        return null;
    }
}

