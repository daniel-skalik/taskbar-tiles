using System.Text.Json;

namespace TaskbarTiles.Plugins.Clipboard;

/// <summary>Where Save writes, the file name, and turning clipboard image bytes into a BMP file. Pure.</summary>
public static class ClipboardSave
{
    public const string SaveAction = "save";
    public const string ShowSavedAction = "show-saved";

    /// <summary>"saveFolder" from settings (environment variables expanded, quotes trimmed), else Pictures\Clipboard.</summary>
    public static string Folder(JsonElement settings)
    {
        if (settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty("saveFolder", out var value)
            && value.ValueKind == JsonValueKind.String && value.GetString()!.Trim().Trim('"') is { Length: > 0 } folder)
            return Environment.ExpandEnvironmentVariables(folder);
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Clipboard");
    }

    /// <summary>clipboard-2026-11-05-143012.png, or -2, -3, … before the extension while <paramref name="taken"/> says the name is used.</summary>
    public static string FileName(DateTime now, string extension, Func<string, bool> taken)
    {
        var stem = $"clipboard-{now:yyyy-MM-dd-HHmmss}";
        var name = stem + extension;
        for (var n = 2; taken(name); n++) name = $"{stem}-{n}{extension}";
        return name;
    }

    /// <summary>
    /// A BMP file from a packed DIB (CF_DIB: header, masks, palette, pixels): the 14-byte file header in front, pointing
    /// past the header, the three BI_BITFIELDS masks a 40-byte header leaves out, and the palette. Null when too short.
    /// </summary>
    public static byte[]? BmpFromDib(byte[] dib)
    {
        if (dib.Length < 40) return null;
        var headerSize = BitConverter.ToInt32(dib, 0);
        if (headerSize < 40 || headerSize > dib.Length) return null;
        var bitCount = BitConverter.ToUInt16(dib, 14);
        var compression = BitConverter.ToUInt32(dib, 16);
        var colorsUsed = BitConverter.ToUInt32(dib, 32);
        var masks = headerSize == 40 && compression == 3 ? 12 : headerSize == 40 && compression == 6 ? 16 : 0; // BI_BITFIELDS, BI_ALPHABITFIELDS
        var palette = colorsUsed != 0 ? colorsUsed : bitCount <= 8 ? 1u << bitCount : 0;
        var offset = 14 + headerSize + masks + (int)palette * 4;

        var bmp = new byte[14 + dib.Length];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BitConverter.GetBytes(bmp.Length).CopyTo(bmp, 2);
        BitConverter.GetBytes(offset).CopyTo(bmp, 10);
        dib.CopyTo(bmp, 14);
        return bmp;
    }

    /// <summary>
    /// Most apps copy 32-bit images with every alpha byte 0, meaning "no alpha", not "invisible": then all pixels become
    /// opaque. An image with any alpha set is left as it is. <paramref name="bgra"/> is B, G, R, A per pixel.
    /// </summary>
    public static void OpaqueIfNoAlpha(byte[] bgra)
    {
        for (var i = 3; i < bgra.Length; i += 4)
            if (bgra[i] != 0) return;
        for (var i = 3; i < bgra.Length; i += 4) bgra[i] = 255;
    }
}
