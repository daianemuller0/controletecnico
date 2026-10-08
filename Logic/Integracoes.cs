using System.Globalization;
using System.Text.Json;

namespace ControleTecnico.Logic;

public sealed record GeoResultado(bool Ok, double? Lat, double? Lon, string Fonte, string Mensagem);
public sealed record RotaResultado(bool Ok, double? Km, int? Minutos, string Mensagem);

/// <summary>Geocodificação por Nominatim/OpenStreetMap (ou instância compatível).
/// Configurada por ambiente: Geocoding:Contato (e-mail exigido pela política de uso)
/// e, opcionalmente, Geocoding:BaseUrl. Sem configuração, NÃO simula: devolve "não configurado".</summary>
public sealed class Geocoder
{
    private readonly HttpClient _http;
    private readonly string _base;
    private readonly string _contato;
    private readonly SemaphoreSlim _ritmo = new(1, 1);
    private DateTime _ultimo = DateTime.MinValue;

    public Geocoder(IHttpClientFactory f, IConfiguration cfg)
    {
        _http = f.CreateClient("geo");
        _base = (cfg["Geocoding:BaseUrl"] ?? "https://nominatim.openstreetmap.org").TrimEnd('/');
        _contato = cfg["Geocoding:Contato"] ?? "";
    }

    public bool Configurado => !string.IsNullOrWhiteSpace(_contato);
    public string Pendencia => "Geocodificação não configurada: defina Geocoding:Contato (e-mail de contato exigido pelo serviço) " +
                               "nas configurações do ambiente. Enquanto isso as plantas ficam como \"localização pendente\" ou podem ser posicionadas manualmente no mapa.";

    public async Task<GeoResultado> BuscarAsync(string endereco, string? pais = null, CancellationToken ct = default)
    {
        if (!Configurado) return new(false, null, null, "", Pendencia);
        if (string.IsNullOrWhiteSpace(endereco)) return new(false, null, null, "", "Endereço vazio.");
        await _ritmo.WaitAsync(ct);
        try
        {
            // política do Nominatim público: no máximo 1 requisição por segundo
            var espera = TimeSpan.FromMilliseconds(1100) - (DateTime.UtcNow - _ultimo);
            if (espera > TimeSpan.Zero) await Task.Delay(espera, ct);
            var url = $"{_base}/search?format=jsonv2&limit=1&q={Uri.EscapeDataString(endereco)}&email={Uri.EscapeDataString(_contato)}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd($"ControleTecnico/1.0 ({_contato})");
            req.Headers.AcceptLanguage.ParseAdd("pt-BR");
            using var resp = await _http.SendAsync(req, ct);
            _ultimo = DateTime.UtcNow;
            if (!resp.IsSuccessStatusCode) return new(false, null, null, "", $"Serviço de geocodificação respondeu {(int)resp.StatusCode}.");
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.GetArrayLength() == 0) return new(false, null, null, "", "Endereço não localizado.");
            var e = doc.RootElement[0];
            var lat = double.Parse(e.GetProperty("lat").GetString()!, CultureInfo.InvariantCulture);
            var lon = double.Parse(e.GetProperty("lon").GetString()!, CultureInfo.InvariantCulture);
            return new(true, lat, lon, "OpenStreetMap/Nominatim", e.TryGetProperty("display_name", out var dn) ? dn.GetString() ?? "" : "");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new(false, null, null, "", "Falha ao consultar o serviço: " + ex.Message); }
        finally { _ritmo.Release(); }
    }
}

/// <summary>Estimativa de rota por carro via OSRM (Routing:BaseUrl). Sem configuração,
/// a duração precisa ser informada manualmente — nada é inventado.</summary>
public sealed class Rota
{
    private readonly HttpClient _http;
    private readonly string _base;
    public Rota(IHttpClientFactory f, IConfiguration cfg)
    {
        _http = f.CreateClient("geo");
        _base = (cfg["Routing:BaseUrl"] ?? "").TrimEnd('/');
    }
    public bool Configurada => !string.IsNullOrEmpty(_base);
    public string Pendencia => "Estimativa de rota não configurada (Routing:BaseUrl, serviço compatível com OSRM). Informe distância e duração manualmente.";

    public async Task<RotaResultado> EstimarAsync(double lat1, double lon1, double lat2, double lon2, CancellationToken ct = default)
    {
        if (!Configurada) return new(false, null, null, Pendencia);
        try
        {
            var f = (double d) => d.ToString(CultureInfo.InvariantCulture);
            var url = $"{_base}/route/v1/driving/{f(lon1)},{f(lat1)};{f(lon2)},{f(lat2)}?overview=false";
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return new(false, null, null, $"Serviço de rotas respondeu {(int)resp.StatusCode}.");
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var r = doc.RootElement.GetProperty("routes")[0];
            return new(true, r.GetProperty("distance").GetDouble() / 1000, (int)Math.Round(r.GetProperty("duration").GetDouble() / 60), "");
        }
        catch (Exception ex) { return new(false, null, null, "Falha ao estimar a rota: " + ex.Message); }
    }
}
