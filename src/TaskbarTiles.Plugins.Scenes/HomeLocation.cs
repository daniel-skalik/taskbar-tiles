using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace TaskbarTiles.Plugins.Scenes;

/// <summary>
/// Where the farm's sun is computed when its settings give no place: the middle of the country or region chosen in
/// Windows (Settings › Time &amp; language › Region). No location permission or network is involved.
/// </summary>
public static class HomeLocation
{
    const int GeoClassNation = 16, GeoLatitude = 2, GeoLongitude = 3;

    [DllImport("kernel32.dll")]
    static extern int GetUserGeoID(int geoClass);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern int GetGeoInfoW(int location, int geoType, StringBuilder data, int size, int langId);

    /// <summary>Latitude and longitude in degrees, or null when Windows has no region set or the call fails.</summary>
    public static (double Latitude, double Longitude)? FromWindowsRegion()
    {
        try
        {
            var geo = GetUserGeoID(GeoClassNation);
            if (geo <= 0) return null;
            return Read(geo, GeoLatitude) is { } lat && Read(geo, GeoLongitude) is { } lon ? (lat, lon) : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    static double? Read(int geo, int type)
    {
        var buffer = new StringBuilder(32);
        return GetGeoInfoW(geo, type, buffer, buffer.Capacity, 0) > 0
               && double.TryParse(buffer.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }
}
