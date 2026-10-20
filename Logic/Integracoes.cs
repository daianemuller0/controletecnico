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
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (bool ok, double lat, double lon, string nome, string? erro, bool falhaDeRede, int rank, string tipo)> _cache = new();
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
        string? textoLivre = null, CancellationToken ct = default, string? nomeLocal = null)
    {
        if (!Configurado) return new(false, null, null, "", Pendencia);
        pais = (pais ?? "").Trim(); estado = (estado ?? "").Trim(); cidade = (cidade ?? "").Trim(); endereco = (endereco ?? "").Trim();
        var brasil = pais == "" || DocEngine.Norm(pais) is "brasil" or "brazil";
        // endereço completo digitado num campo só ("Rua X, 100 - Bairro, Sorocaba - SP, 18000-000"): separa CEP, cidade e UF
        var (cep, cidadeEnd, ufEnd, rua) = Decompor(endereco);
        if (cidade == "" && cidadeEnd != "") cidade = cidadeEnd;
        if (estado == "" && ufEnd != "") estado = ufEnd;
        if (pais == "" && (cep != "" || ufEnd != "")) pais = "Brasil";
        // "MG" não é reconhecido de forma confiável como estado pelo serviço de mapas: expande a sigla brasileira para o nome
        if (estado.Length == 2 && brasil && UfBrasil.TryGetValue(estado.ToUpperInvariant(), out var nomeUf)) estado = nomeUf;
        var niveis = new List<(string nivel, Dictionary<string, string> q)>();
        if (rua != "" && cidade != "") niveis.Add(("endereco", Q(pais, estado, cidade, rua)));
        // texto livre com tudo o que foi informado: acha endereços que a busca por campos não acha (bairro, rodovia, km…)
        if (endereco != "")
        {
            var livre = string.Join(", ", new[] { rua, cidade, estado, pais }.Where(x => x != "").Distinct(StringComparer.OrdinalIgnoreCase));
            niveis.Add(("endereco", new Dictionary<string, string> { ["q"] = livre }));
        }
        // pelo NOME do local (fábricas, refinarias, a própria Howden costumam estar cadastradas no mapa pelo nome)
        // só quando não há endereço: com endereço, a busca pela rua já foi tentada (e o nome dobraria as consultas)
        if (!string.IsNullOrWhiteSpace(nomeLocal) && endereco == "" && (cidade != "" || estado != ""))
            niveis.Add(("endereco", new Dictionary<string, string> { ["q"] = string.Join(", ", new[] { nomeLocal!.Trim(), cidade, estado, pais }.Where(x => x != "")) }));
        if (cep != "" && brasil) niveis.Add(("cep", new Dictionary<string, string> { ["postalcode"] = cep, ["country"] = "Brasil" }));
        if (cidade != "") niveis.Add(("cidade", Q(pais, estado, cidade, null)));
        if (estado != "") niveis.Add(("estado", Q(pais, estado, null, null)));
        if (pais != "") niveis.Add(("pais", Q(pais, null, null, null)));
        if (niveis.Count == 0)
        {
            if (string.IsNullOrWhiteSpace(textoLivre)) return new(false, null, null, "", "Informe ao menos o país, o estado ou a cidade.");
            var livre = await ConsultarComNivelAsync(new Dictionary<string, string> { ["q"] = textoLivre! }, "endereco", ct);
            return livre.ok ? new(true, livre.lat, livre.lon, "OpenStreetMap/Nominatim", livre.nome, livre.nivel) : new(false, null, null, "", livre.erro ?? "Endereço não localizado.");
        }
        string? ultimoErro = null; var tentados = new List<string>();
        foreach (var (nivel, q) in niveis)
        {
            var r = await ConsultarComNivelAsync(q, nivel, ct);
            if (r.erro is not null && !r.ok && r.falhaDeRede) return new(false, null, null, "", r.erro, "", true);   // rede/serviço fora: não conclui "não existe"
            // o serviço devolveu algo MAIS VAGO do que o pedido (ex.: pediu a rua e veio só "Brasil"): não vale para este nível
            if (r.ok && Precisao(r.nivel) < Precisao(nivel)) { tentados.Add(nivel); continue; }
            if (r.ok)
            {
                var msg = tentados.Count == 0 ? r.nome : $"{r.nome} — não encontrado: {string.Join(", ", tentados)}; posição aproximada ao nível de {nivel}.";
                return new(true, r.lat, r.lon, "OpenStreetMap/Nominatim", msg, nivel);
            }
            tentados.Add(nivel); ultimoErro = r.erro;
        }
        return new(false, null, null, "", ultimoErro ?? "Local não encontrado (país, estado, cidade e endereço).");
    }

    /// <summary>Separa de um endereço digitado inteiro: CEP, cidade e UF ("Sorocaba - SP", "Sorocaba/SP") e o resto (rua, número, bairro).</summary>
    public static (string cep, string cidade, string uf, string rua) Decompor(string endereco)
    {
        var e = endereco ?? "";
        var mc = System.Text.RegularExpressions.Regex.Match(e, @"\b(\d{5})-?(\d{3})\b");
        var cep = mc.Success ? $"{mc.Groups[1].Value}-{mc.Groups[2].Value}" : "";
        if (mc.Success) e = e.Remove(mc.Index, mc.Length);
        e = System.Text.RegularExpressions.Regex.Replace(e, @"\bCEP\b:?", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        string cidade = "", uf = "";
        var mu = System.Text.RegularExpressions.Regex.Matches(e, @"([A-Za-zÀ-ÿ'.\s]{3,}?)\s*[-/–]\s*([A-Za-z]{2})\b");
        for (var i = mu.Count - 1; i >= 0; i--)
        {
            var m = mu[i];
            if (!UfBrasil.ContainsKey(m.Groups[2].Value.ToUpperInvariant())) continue;
            cidade = m.Groups[1].Value.Trim().Trim(',', '-').Trim(); uf = m.Groups[2].Value.ToUpperInvariant();
            // "Rua X, 100, Centro, Sorocaba - SP": a cidade é o último pedaço antes da UF
            if (cidade.Contains(',')) cidade = cidade[(cidade.LastIndexOf(',') + 1)..].Trim();
            e = e.Remove(m.Index, m.Length).Insert(m.Index, ",");
            break;
        }
        var rua = string.Join(", ", e.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(x => x.Trim('-', ' ') != ""));
        return (cep, cidade, uf, rua.Trim().Trim(',', '-').Trim());
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

    /// <summary>Link curto do Google Maps (maps.app.goo.gl): segue o redirecionamento e lê as coordenadas do endereço final.</summary>
    public async Task<(double lat, double lon)?> CoordenadasDoLinkAsync(string texto, CancellationToken ct = default)
    {
        if (Geo.ExtrairCoordenadas(texto) is { } direto) return direto;
        if (!Geo.EhLinkCurto(texto)) return null;
        try
        {
            var url = texto.Trim();
            for (var i = 0; i < 6; i++)
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.UserAgent.ParseAdd("Mozilla/5.0 ControleTecnico");
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                var destino = resp.Headers.Location?.ToString() ?? resp.RequestMessage?.RequestUri?.ToString() ?? "";
                if (Geo.ExtrairCoordenadas(destino) is { } c) return c;
                if (resp.Headers.Location is null) break;
                url = resp.Headers.Location.IsAbsoluteUri ? resp.Headers.Location.ToString() : new Uri(new Uri(url), resp.Headers.Location).ToString();
            }
        }
        catch { }
        return null;
    }

    /// <summary>Precisão de cada nível (maior = mais exato).</summary>
    public static int Precisao(string nivel) => nivel switch { "manual" or "link" => 5, "endereco" => 4, "cep" => 3, "cidade" => 2, "estado" => 1, "pais" => 0, _ => -1 };

    /// <summary>Nível que o resultado REALMENTE tem, pelo place_rank do Nominatim (rua 26+, bairro/cidade 13-25, estado 5-12, país até 4).</summary>
    public static string NivelDoRank(int rank, string tipo) =>
        tipo == "postcode" ? "cep" : rank >= 26 ? "endereco" : rank >= 13 ? "cidade" : rank >= 5 ? "estado" : "pais";

    private async Task<(bool ok, double lat, double lon, string nome, string? erro, bool falhaDeRede, string nivel)> ConsultarComNivelAsync(Dictionary<string, string> q, string pedido, CancellationToken ct)
    {
        var r = await ConsultarAsync(q, ct);
        if (!r.ok) return (r.ok, r.lat, r.lon, r.nome, r.erro, r.falhaDeRede, "");
        var real = r.rank < 0 ? pedido : NivelDoRank(r.rank, r.tipo);           // sem rank (serviço compatível sem o campo): confia no pedido
        return (true, r.lat, r.lon, r.nome, null, false, real);
    }

    private async Task<(bool ok, double lat, double lon, string nome, string? erro, bool falhaDeRede, int rank, string tipo)> ConsultarAsync(Dictionary<string, string> parametros, CancellationToken ct)
    {
        var chave = string.Join("&", parametros.OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + kv.Value.Trim().ToLowerInvariant()));
        if (_cache.TryGetValue(chave, out var guardado)) { ConsultasEmCache++; return guardado; }
        var res = await ConsultarSemCacheAsync(parametros, ct);
        if (!res.falhaDeRede) _cache[chave] = res;           // falha de rede/serviço não é guardada: tenta de novo depois
        return res;
    }

    private async Task<(bool ok, double lat, double lon, string nome, string? erro, bool falhaDeRede, int rank, string tipo)> ConsultarSemCacheAsync(Dictionary<string, string> parametros, CancellationToken ct)
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
            if (!resp.IsSuccessStatusCode) return (false, 0, 0, "", $"Serviço de geocodificação respondeu {(int)resp.StatusCode}.", true, -1, "");
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.GetArrayLength() == 0) return (false, 0, 0, "", "Não encontrado.", false, -1, "");
            var e = doc.RootElement[0];
            var rank = e.TryGetProperty("place_rank", out var pr) && pr.ValueKind == JsonValueKind.Number ? pr.GetInt32() : -1;
            var tipo = e.TryGetProperty("addresstype", out var at) ? at.GetString() ?? "" : e.TryGetProperty("type", out var ty) ? ty.GetString() ?? "" : "";
            return (true, double.Parse(e.GetProperty("lat").GetString()!, CultureInfo.InvariantCulture), double.Parse(e.GetProperty("lon").GetString()!, CultureInfo.InvariantCulture),
                e.TryGetProperty("display_name", out var dn) ? dn.GetString() ?? "" : "", null, false, rank, tipo);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return (false, 0, 0, "", "Falha ao consultar o serviço: " + ex.Message, true, -1, ""); }
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
