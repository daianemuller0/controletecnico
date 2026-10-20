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

    /// <summary>
    /// Lê coordenadas de um link do Google Maps (ou OpenStreetMap/Waze) ou de um texto "lat, lon".
    /// Aceita: "-23.5505, -46.6333" · ".../@-23.55,-46.63,17z" · "...!3d-23.55!4d-46.63" · "?q=-23.55,-46.63" · "ll=" · "query=" · "#map=17/-23.55/-46.63".
    /// </summary>
    public static (double lat, double lon)? ExtrairCoordenadas(string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto)) return null;
        var t = Uri.UnescapeDataString(texto.Trim());
        static (double, double)? Par(string a, string b)
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture; var nf = System.Globalization.NumberStyles.Float;
            return double.TryParse(a, nf, ci, out var la) && double.TryParse(b, nf, ci, out var lo) && CoordValida(la, lo) && !(la == 0 && lo == 0) ? (la, lo) : null;
        }
        const string N = @"(-?\d{1,3}\.\d+)";
        foreach (var padrao in new[]
        {
            $@"!3d{N}!4d{N}",                       // marcador exato do lugar (Google)
            $@"[?&](?:q|query|ll|destination|daddr|center)={N}\s*,\s*{N}",
            $@"#map=\d+/{N}/{N}",                   // OpenStreetMap
            $@"@{N},{N}",                            // centro da tela do Google Maps
            $@"^\s*{N}\s*[,; ]\s*{N}\s*$",       // texto "lat, lon"
        })
        {
            var m = System.Text.RegularExpressions.Regex.Match(t, padrao);
            if (m.Success && Par(m.Groups[1].Value, m.Groups[2].Value) is { } r) return r;
        }
        // "-23,5505 -46,6333" (vírgula decimal, como o Excel em português)
        var br = System.Text.RegularExpressions.Regex.Match(t, @"^\s*(-?\d{1,3},\d+)\s*[; ]\s*(-?\d{1,3},\d+)\s*$");
        if (br.Success) return Par(br.Groups[1].Value.Replace(',', '.'), br.Groups[2].Value.Replace(',', '.'));
        return null;
    }

    public static bool EhLinkCurto(string? texto) =>
        !string.IsNullOrWhiteSpace(texto) && System.Text.RegularExpressions.Regex.IsMatch(texto, @"^https?://(maps\.app\.goo\.gl|goo\.gl/maps|g\.co/kgs)/", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
}
