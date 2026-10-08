namespace ControleTecnico.Logic;

public static class Geo
{
    /// <summary>Distância em linha reta (km) — não é distância de rota.</summary>
    public static double Haversine(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371.0088;
        static double Rad(double d) => d * Math.PI / 180;
        var dLat = Rad(lat2 - lat1); var dLon = Rad(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * R * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }

    public static bool CoordValida(double? lat, double? lon) =>
        lat is >= -90 and <= 90 && lon is >= -180 and <= 180;
}
