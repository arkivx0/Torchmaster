namespace Lanternly.Core;

/// <summary>Coordinates typed by the user (decimal degrees, north/east positive). Never from a location service.</summary>
public sealed record SunLocation(double Lat, double Lon)
{
    public static bool Valid(double lat, double lon) => !double.IsNaN(lat) && !double.IsNaN(lon) && lat is >= -90 and <= 90 && lon is >= -180 and <= 180;
    public static SunLocation? Parse(string lat, string lon) =>
        double.TryParse(lat.Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var a) &&
        double.TryParse(lon.Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var b) && Valid(a, b) ? new(a, b) : null;
}

/// <summary>Sunrise / sunset, offline, NOAA solar calculator algorithm (zenith 90.833°). Mirrors Sun.kt.</summary>
public static class Sun
{
    static double Rad(double d) => d * Math.PI / 180; static double Deg(double r) => r * 180 / Math.PI;
    static double Mod(double a, double m) => ((a % m) + m) % m;
    static double GeomMeanLong(double t) => Mod(280.46646 + t * (36000.76983 + 0.0003032 * t), 360);
    static double GeomMeanAnom(double t) => 357.52911 + t * (35999.05029 - 0.0001537 * t);
    static double Eccent(double t) => 0.016708634 - t * (0.000042037 + 0.0000001267 * t);
    static double EqOfCenter(double t) { var m = Rad(GeomMeanAnom(t)); return Math.Sin(m) * (1.914602 - t * (0.004817 + 0.000014 * t)) + Math.Sin(2 * m) * (0.019993 - 0.000101 * t) + Math.Sin(3 * m) * 0.000289; }
    static double AppLong(double t) => GeomMeanLong(t) + EqOfCenter(t) - 0.00569 - 0.00478 * Math.Sin(Rad(125.04 - 1934.136 * t));
    static double Obliq(double t) { var e0 = 23 + (26 + (21.448 - t * (46.815 + t * (0.00059 - t * 0.001813))) / 60) / 60; return e0 + 0.00256 * Math.Cos(Rad(125.04 - 1934.136 * t)); }
    static double Decl(double t) => Deg(Math.Asin(Math.Sin(Rad(Obliq(t))) * Math.Sin(Rad(AppLong(t)))));
    static double EqTime(double t)
    {
        var y = Math.Pow(Math.Tan(Rad(Obliq(t)) / 2), 2); var l0 = Rad(GeomMeanLong(t)); var e = Eccent(t); var m = Rad(GeomMeanAnom(t));
        return 4 * Deg(y * Math.Sin(2 * l0) - 2 * e * Math.Sin(m) + 4 * e * y * Math.Sin(m) * Math.Cos(2 * l0) - 0.5 * y * y * Math.Sin(4 * l0) - 1.25 * e * e * Math.Sin(2 * m));
    }
    static double HourAngle(double lat, double d) => Deg(Math.Acos(Math.Cos(Rad(90.833)) / (Math.Cos(Rad(lat)) * Math.Cos(Rad(d))) - Math.Tan(Rad(lat)) * Math.Tan(Rad(d))));

    /// <summary>Minutes after 00:00 UTC of <paramref name="date"/>, or null (polar day/night).</summary>
    public static double? UtcMinutes(DateOnly date, double lat, double lon, bool rise)
    {
        double jd = date.DayNumber - 719162 + 2440587.5; // DayNumber of 1970-01-01 is 719162
        double m = 720 - 4 * lon;
        for (int i = 0; i < 3; i++)
        {
            var t = (jd + m / 1440 - 2451545.0) / 36525;
            var ha = HourAngle(lat, Decl(t)); if (double.IsNaN(ha)) return null;
            m = 720 - 4 * (lon + (rise ? ha : -ha)) - EqTime(t);
        }
        return m;
    }

    /// <summary>The event on local date <paramref name="date"/> in <paramref name="tz"/>, or null.</summary>
    public static DateTimeOffset? Event(DateOnly date, SunLocation loc, bool rise, TimeZoneInfo tz)
    {
        foreach (var d in new[] { 0, -1, 1 })
        {
            var day = date.AddDays(d);
            if (UtcMinutes(day, loc.Lat, loc.Lon, rise) is not { } m) continue;
            var utc = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddSeconds(Math.Round(m * 60));
            var local = TimeZoneInfo.ConvertTime(utc, tz);
            if (DateOnly.FromDateTime(local.DateTime) == date) return local;
        }
        return null;
    }
}
