namespace TaskbarTiles.Plugins.Scenes;

/// <summary>
/// Sunrise and sunset for a place and day, from NOAA's solar position equations: within a few minutes of the published
/// times anywhere outside the polar circles. Times are hours of the local day (may fall outside 0..24 near the date line).
/// </summary>
public static class SunTimes
{
    /// <summary>
    /// Sunrise and sunset in hours; <see cref="Rises"/> false in a polar night (both are solar noon then) and
    /// <see cref="Sets"/> false in a polar day (sunrise and sunset 12 hours either side of solar noon).
    /// </summary>
    public readonly record struct Day(double Sunrise, double Sunset, bool Rises = true, bool Sets = true)
    {
        public double Noon => (Sunrise + Sunset) / 2;
    }

    /// <summary>
    /// The day at <paramref name="latitude"/> (north positive) and <paramref name="longitude"/> (east positive), in degrees,
    /// with clock times in <paramref name="utcOffset"/>.
    /// </summary>
    public static Day For(DateOnly date, double latitude, double longitude, TimeSpan utcOffset)
    {
        var days = DateTime.IsLeapYear(date.Year) ? 366 : 365;
        var g = 2 * Math.PI / days * (date.DayOfYear - 1); // the fractional year, in radians
        var equationOfTime = 229.18 * (0.000075 + 0.001868 * Math.Cos(g) - 0.032077 * Math.Sin(g)
                                       - 0.014615 * Math.Cos(2 * g) - 0.040849 * Math.Sin(2 * g)); // minutes
        var declination = 0.006918 - 0.399912 * Math.Cos(g) + 0.070257 * Math.Sin(g) - 0.006758 * Math.Cos(2 * g)
                          + 0.000907 * Math.Sin(2 * g) - 0.002697 * Math.Cos(3 * g) + 0.00148 * Math.Sin(3 * g);

        var noon = (720 - 4 * longitude - equationOfTime + utcOffset.TotalMinutes) / 60;
        var lat = latitude * Math.PI / 180;
        // 90.833°: the sun's upper edge on the horizon, with the usual allowance for refraction.
        var cosHourAngle = Math.Cos(90.833 * Math.PI / 180) / (Math.Cos(lat) * Math.Cos(declination)) - Math.Tan(lat) * Math.Tan(declination);
        if (cosHourAngle >= 1) return new Day(noon, noon, Rises: false);
        if (cosHourAngle <= -1) return new Day(noon - 12, noon + 12, Sets: false);
        var halfDay = Math.Acos(cosHourAngle) * 180 / Math.PI / 15; // hours
        return new Day(noon - halfDay, noon + halfDay);
    }
}
