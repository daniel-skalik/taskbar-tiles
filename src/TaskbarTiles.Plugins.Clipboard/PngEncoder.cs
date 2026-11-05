using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace TaskbarTiles.Plugins.Clipboard;

/// <summary>BMP file bytes to PNG through Windows' own imaging (WinRT), so the plugin needs neither WPF nor WinForms.</summary>
public static class PngEncoder
{
    /// <summary>Decodes BMP file bytes and re-encodes them as PNG; an all-zero alpha channel becomes opaque. Throws when Windows cannot decode them.</summary>
    public static async Task<byte[]> FromBmpAsync(byte[] bmp)
    {
        using var input = new InMemoryRandomAccessStream();
        await input.WriteAsync(bmp.AsBuffer());
        input.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(input);
        var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        var bgra = pixels.DetachPixelData();
        ClipboardSave.OpaqueIfNoAlpha(bgra);

        using var output = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, decoder.PixelWidth, decoder.PixelHeight, 96, 96, bgra);
        await encoder.FlushAsync();

        var bytes = new byte[output.Size];
        using var reader = new DataReader(output.GetInputStreamAt(0));
        await reader.LoadAsync((uint)output.Size);
        reader.ReadBytes(bytes);
        return bytes;
    }
}
