using System.Globalization;
using System.Text.Json;

namespace ControleTecnico.Logic;

/// <summary>Nivel: endereco | cidade | estado | pais — até onde a busca em cascata conseguiu chegar.</summary>
public sealed record GeoResultado(bool Ok, double? Lat, double? Lon, string Fonte, string Mensagem, string Nivel = "", bool ErroServico = false);
public sealed record RotaResultado(bool Ok, double? Km, int? Minutos, string Mensagem);

/// <summary>Geocodificação por Nominatim/OpenStreetMap (ou instância compatível).
/// Configurada por ambiente: Geocoding:Contato (e-mail exigido pela política de uso)
/// e, opcionalmente, Geocoding:BaseUrl. Sem configuração, NÃO simula: devolve "não configurado".</summary>
public sealed class Geocoder
{
    private readonly HttpClient _http;
    private readonly string _base;
    private readonly string _contatoCfg;
    private readonly Data.Db? _db;
    private readonly int _intervaloMs;
    private readonly bool _ativo;
    private readonly SemaphoreSlim _ritmo = new(1, 1);
    private DateTime _ultimo = DateTime.MinValue;
    // Muitas plantas dividem cidade/estado/país: a mesma consulta (inclusive "não encontrado") nunca é repetida.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (bool ok, double lat, double lon, string nome, string? erro, bool falhaDeRede)> _cache = new();
    public int ConsultasRealizadas { get; private set; }
    public int ConsultasEmCache { get; private set; }

    public Geocoder(IHttpClientFactory f, IConfiguration cfg, Data.Db? db = null)
    {
        _http = f.CreateClient("geo");
        _base = (cfg["Geocoding:BaseUrl"] ?? "https://nominatim.openstreetmap.org").TrimEnd('/');
        _contatoCfg = cfg["Geocoding:Contato"] ?? "";
        _intervaloMs = int.TryParse(cfg["Geocoding:IntervaloMs"], out var ms) ? ms : 1100;
        _ativo = !string.Equals(cfg["Geocoding:Ativo"], "false", StringComparison.OrdinalIgnoreCase);
        _db = db;
    }

    /// <summary>E-mail de contato exigido pela política do serviço: do appsettings/ambiente ou definido em Administração.</summary>
    private string Contato => !string.IsNullOrWhiteSpace(_contatoCfg) ? _contatoCfg : _db?.Cfg("geocoding_contato") ?? "";
    /// <summary>Ligada por padrão (o Nominatim público não exige chave). O e-mail de contato é recomendado pela política de uso,
    /// mas opcional. Pode ser desligada com Geocoding:Ativo=false ou apontada para outro serviço em Geocoding:BaseUrl.</summary>
    public bool Configurado => _ativo && !string.IsNullOrWhiteSpace(_base);
    public string Pendencia => "Geocodificação desativada (Geocoding:Ativo=false). Enquanto isso as plantas ficam como \"localização pendente\" ou podem ser posicionadas manualmente no mapa.";

    /// <summary>
    /// Localiza uma planta em CASCATA, do mais preciso ao mais genérico, parando no último nível que existir:
    /// endereço (rua + cidade + estado + país) → cidade (+ estado + país) → estado (+ país) → país.
    /// Cada consulta é estruturada (campos separados), então a hierarquia é respeitada: o endereço só vale dentro da
    /// cidade, a cidade dentro do estado e o estado dentro do país. Níveis sem dado informado são pulados.
    /// </summary>
    public async Task<GeoResultado> BuscarHierarquicoAsync(string? pais, string? estado, string? cidade, string? endereco,
        string? textoLivre = null, CancellationToken ct = default)
    {
        if (!Configurado) return new(false, null, null, "", Pendencia);
        pais = (pais ?? "").Trim(); estado = (estado ?? "").Trim(); cidade = (cidade ?? "").Trim(); endereco = (endereco ?? "").Trim();
        // "MG" não é reconhecido de forma confiável como estado pelo serviço de mapas: expande a sigla brasileira para o nome
        if (estado.Length == 2 && DocEngine.Norm(pais) is "brasil" or "brazil" && UfBrasil.TryGetValue(estado.ToUpperInvariant(), out var nomeUf)) estado = nomeUf;
        var niveis = new List<(string nivel, Dictionary<string, string> q)>();
        if (endereco != "" && cidade != "") niveis.Add(("endereco", Q(pais, estado, cidade, endereco)));
        if (cidade != "") niveis.Add(("cidade", Q(pais, estado, cidade, null)));
        if (estado != "") niveis.Add(("estado", Q(pais, estado, null, null)));
        if (pais != "") niveis.Add(("pais", Q(pais, null, null, null)));
        if (niveis.Count == 0)
        {
            if (string.IsNullOrWhiteSpace(textoLivre)) return new(false, null, null, "", "Informe ao menos o país, o estado ou a cidade.");
            var livre = await ConsultarAsync(new Dictionary<string, string> { ["q"] = textoLivre! }, ct);
            return livre.ok ? new(true, livre.lat, livre.lon, "OpenStreetMap/Nominatim", livre.nome, "endereco") : new(false, null, null, "", livre.erro ?? "Endereço não localizado.");
        }
        string? ultimoErro = null; var tentados = new List<string>();
        foreach (var (nivel, q) in niveis)
        {
            var r = await ConsultarAsync(q, ct);
            if (r.erro is not null && !r.ok && r.falhaDeRede) return new(false, null, null, "", r.erro, "", true);   // rede/serviço fora: não conclui "não existe"
            if (r.ok)
            {
                var msg = tentados.Count == 0 ? r.nome : $"{r.nome} — não encontrado: {string.Join(", ", tentados)}; posição aproximada ao nível de {nivel}.";
                return new(true, r.lat, r.lon, "OpenStreetMap/Nominatim", msg, nivel);
            }
            tentados.Add(nivel); ultimoErro = r.erro;
        }
        return new(false, null, null, "", ultimoErro ?? "Local não encontrado (país, estado, cidade e endereço).");
    }

    private static readonly Dictionary<string, string> UfBrasil = new()
    {
        ["AC"] = "Acre", ["AL"] = "Alagoas", ["AP"] = "Amapá", ["AM"] = "Amazonas", ["BA"] = "Bahia", ["CE"] = "Ceará", ["DF"] = "Distrito Federal",
        ["ES"] = "Espírito Santo", ["GO"] = "Goiás", ["MA"] = "Maranhão", ["MT"] = "Mato Grosso", ["MS"] = "Mato Grosso do Sul", ["MG"] = "Minas Gerais",
        ["PA"] = "Pará", ["PB"] = "Paraíba", ["PR"] = "Paraná", ["PE"] = "Pernambuco", ["PI"] = "Piauí", ["RJ"] = "Rio de Janeiro", ["RN"] = "Rio Grande do Norte",
        ["RS"] = "Rio Grande do Sul", ["RO"] = "Rondônia", ["RR"] = "Roraima", ["SC"] = "Santa Catarina", ["SP"] = "São Paulo", ["SE"] = "Sergipe", ["TO"] = "Tocantins",
    };

    private static Dictionary<string, string> Q(string pais, string estado, string? cidade, string? rua)
    {
        var d = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(rua)) d["street"] = rua;
        if (!string.IsNullOrEmpty(cidade)) d["city"] = cidade;
        if (!string.IsNullOrEmpty(estado)) d["state"] = estado;
        if (!string.IsNullOrEmpty(pais)) d["country"] = pais;
        return d;
    }

    private async Task<(bool ok, double lat, double lon, string nome, string? erro, bool falhaDeRede)> ConsultarAsync(Dictionary<string, string> parametros, CancellationToken ct)
    {
        var chave = string.Join("&", parametros.OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + kv.Value.Trim().ToLowerInvariant()));
        if (_cache.TryGetValue(chave, out var guardado)) { ConsultasEmCache++; return guardado; }
        var res = await ConsultarSemCacheAsync(parametros, ct);
        if (!res.falhaDeRede) _cache[chave] = res;           // falha de rede/serviço não é guardada: tenta de novo depois
        return res;
    }

    private async Task<(bool ok, double lat, double lon, string nome, string? erro, bool falhaDeRede)> ConsultarSemCacheAsync(Dictionary<string, string> parametros, CancellationToken ct)
    {
        await _ritmo.WaitAsync(ct);
        ConsultasRealizadas++;
        try
        {
            // política do Nominatim público: no máximo 1 requisição por segundo
            var espera = TimeSpan.FromMilliseconds(_intervaloMs) - (DateTime.UtcNow - _ultimo);
            if (espera > TimeSpan.Zero) await Task.Delay(espera, ct);
            var qs = string.Join("&", parametros.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
            var url = $"{_base}/search?format=jsonv2&limit=1&accept-language=pt-BR&{qs}" + (string.IsNullOrWhiteSpace(Contato) ? "" : $"&email={Uri.EscapeDataString(Contato)}");
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd(string.IsNullOrWhiteSpace(Contato) ? "ControleTecnico/1.0" : $"ControleTecnico/1.0 ({Contato})");
            using var resp = await _http.SendAsync(req, ct);
            _ultimo = DateTime.UtcNow;
            if (!resp.IsSuccessStatusCode) return (false, 0, 0, "", $"Serviço de geocodificação respondeu {(int)resp.StatusCode}.", true);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.GetArrayLength() == 0) return (false, 0, 0, "", "Não encontrado.", false);
            var e = doc.RootElement[0];
            return (true, double.Parse(e.GetProperty("lat").GetString()!, CultureInfo.InvariantCulture), double.Parse(e.GetProperty("lon").GetString()!, CultureInfo.InvariantCulture),
                e.TryGetProperty("display_name", out var dn) ? dn.GetString() ?? "" : "", null, false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return (false, 0, 0, "", "Falha ao consultar o serviço: " + ex.Message, true); }
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
