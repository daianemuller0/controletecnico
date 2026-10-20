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
    /// <summary>Versão da busca em cascata. Quando muda, a localização automática tenta de novo as plantas pendentes/aproximadas.</summary>
    public const int Versao = 3;
    private readonly HttpClient _http;
    private readonly string _base;
    private readonly string _contatoCfg;
    private readonly Data.Db? _db;
    private readonly int _intervaloMs;
    private readonly bool _ativo;
    private readonly string _photon;
    private readonly SemaphoreSlim _ritmo = new(1, 1);
    private DateTime _ultimo = DateTime.MinValue;
    // Nominatim recusou/não respondeu: fica 2 min sem ser consultado (insistir prolonga o bloqueio) e as buscas seguem pelo Photon
    private DateTime _nominatimPausaAte = DateTime.MinValue;
    private string _nominatimUltimoErro = "";
    // Muitas plantas dividem cidade/estado/país: a mesma consulta (inclusive "não encontrado") nunca é repetida.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (bool ok, double lat, double lon, string nome, string? erro, bool falhaDeRede, int rank, string tipo)> _cache = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _cachePhoton = new();
    public int ConsultasRealizadas { get; private set; }
    public int ConsultasEmCache { get; private set; }

    public Geocoder(IHttpClientFactory f, IConfiguration cfg, Data.Db? db = null)
    {
        _http = f.CreateClient("geo");
        _base = (cfg["Geocoding:BaseUrl"] ?? "https://nominatim.openstreetmap.org").TrimEnd('/');
        _contatoCfg = cfg["Geocoding:Contato"] ?? "";
        _intervaloMs = int.TryParse(cfg["Geocoding:IntervaloMs"], out var ms) ? ms : 1100;
        _ativo = !string.Equals(cfg["Geocoding:Ativo"], "false", StringComparison.OrdinalIgnoreCase);
        // segundo serviço (Photon, também OpenStreetMap): tolera texto "solto" e entra quando o Nominatim não acha ou não responde. "" desliga.
        _photon = (cfg["Geocoding:PhotonUrl"] ?? "https://photon.komoot.io").TrimEnd('/');
        _db = db;
    }

    /// <summary>E-mail de contato exigido pela política do serviço: do appsettings/ambiente ou definido em Administração.</summary>
    private string Contato => !string.IsNullOrWhiteSpace(_contatoCfg) ? _contatoCfg : _db?.Cfg("geocoding_contato") ?? "";
    /// <summary>Ligada por padrão (o Nominatim público não exige chave). O e-mail de contato é recomendado pela política de uso,
    /// mas opcional. Pode ser desligada com Geocoding:Ativo=false ou apontada para outro serviço em Geocoding:BaseUrl.</summary>
    public bool Configurado => _ativo && !string.IsNullOrWhiteSpace(_base);
    public string Pendencia => "Geocodificação desativada (Geocoding:Ativo=false). Enquanto isso as plantas ficam como \"localização pendente\" ou podem ser posicionadas manualmente no mapa.";

    /// <summary>
    /// Localiza um endereço em CASCATA, do mais preciso ao mais genérico, parando no primeiro nível encontrado:
    /// rua + número → rua → texto livre → (Photon) → CEP → bairro → cidade → estado → país.
    /// "Rua Paraíba, 244 Jardim Tarumã" é separado em logradouro, número e bairro: o serviço de mapas não acha a rua
    /// com o bairro grudado no número. As consultas por campos respeitam a hierarquia (a rua só vale dentro da cidade).
    /// Se o Nominatim não responder (rede, proxy, bloqueio), a busca continua pelo Photon antes de desistir.
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
        var (logradouro, numero, bairro) = Logradouro(rua);
        var cid = cidade; var est = estado;
        var niveis = new List<(string nivel, string fonte, Dictionary<string, string> q)>();
        if (logradouro != "" && cidade != "")
        {
            niveis.Add(("endereco", "n", Q(pais, estado, cidade, numero != "" ? $"{numero} {logradouro}" : logradouro)));
            // o número quase nunca está no mapa: a rua sozinha ainda é o endereço (melhor que cair na cidade)
            if (numero != "") niveis.Add(("endereco", "n", Q(pais, estado, cidade, logradouro)));
        }
        // texto livre com tudo o que foi informado, já separado: acha o que a busca por campos não acha (rodovia, km…)
        if (endereco != "")
        {
            var livre = string.Join(", ", new[] { numero != "" ? $"{logradouro} {numero}" : logradouro, bairro, cidade, estado, pais }.Where(x => x != "").Distinct(StringComparer.OrdinalIgnoreCase));
            niveis.Add(("endereco", "n", new Dictionary<string, string> { ["q"] = livre }));
        }
        // pelo NOME do local (fábricas, refinarias, a própria Howden costumam estar cadastradas no mapa pelo nome)
        // só quando não há endereço: com endereço, a busca pela rua já foi tentada (e o nome dobraria as consultas)
        if (!string.IsNullOrWhiteSpace(nomeLocal) && endereco == "" && (cidade != "" || estado != ""))
            niveis.Add(("endereco", "n", new Dictionary<string, string> { ["q"] = string.Join(", ", new[] { nomeLocal!.Trim(), cidade, estado, pais }.Where(x => x != "")) }));
        // segundo serviço para a rua: tolera abreviações e palavras a mais; o resultado só vale se a rua, a cidade e o estado conferirem
        if (PhotonAtivo && logradouro != "" && cidade != "")
            niveis.Add(("endereco", "p", new Dictionary<string, string> { ["q"] = string.Join(", ", new[] { numero != "" ? $"{logradouro} {numero}" : logradouro, cidade, estado }.Where(x => x != "")) }));
        if (cep != "" && brasil) niveis.Add(("cep", "n", new Dictionary<string, string> { ["postalcode"] = cep, ["country"] = "Brasil" }));
        if (bairro != "" && cidade != "")
        {
            niveis.Add(("bairro", "n", new Dictionary<string, string> { ["q"] = string.Join(", ", new[] { bairro, cidade, estado, pais }.Where(x => x != "")) }));
            if (PhotonAtivo) niveis.Add(("bairro", "p?", new Dictionary<string, string> { ["q"] = string.Join(", ", new[] { bairro, cidade, estado }.Where(x => x != "")) }));
        }
        if (cidade != "")
        {
            niveis.Add(("cidade", "n", Q(pais, estado, cidade, null)));
            if (PhotonAtivo) niveis.Add(("cidade", "p?", new Dictionary<string, string> { ["q"] = string.Join(", ", new[] { cidade, estado }.Where(x => x != "")) }));
        }
        if (estado != "") niveis.Add(("estado", "n", Q(pais, estado, null, null)));
        if (pais != "") niveis.Add(("pais", "n", Q(pais, null, null, null)));
        if (niveis.Count == 0)
        {
            if (string.IsNullOrWhiteSpace(textoLivre)) return new(false, null, null, "", "Informe ao menos o país, o estado ou a cidade.");
            var livre = await ConsultarComNivelAsync(new Dictionary<string, string> { ["q"] = textoLivre! }, "endereco", ct);
            return livre.ok ? new(true, livre.lat, livre.lon, "OpenStreetMap/Nominatim", livre.nome, livre.nivel)
                : new(false, null, null, "", livre.erro ?? "Endereço não localizado.", "", livre.falhaDeRede);
        }
        var nominatimFora = PhotonAtivo && DateTime.UtcNow < _nominatimPausaAte;
        string? ultimoErro = null, erroRede = nominatimFora ? _nominatimUltimoErro : null; var tentados = new List<string>(); var achouAlgo = false;
        foreach (var (nivel, fonte, q) in niveis)
        {
            if (fonte == "n" && nominatimFora) continue;
            if (fonte == "p?" && !nominatimFora) continue;                 // reservas do Photon para bairro/cidade: só com o Nominatim fora
            var r = fonte == "n" ? await ConsultarComNivelAsync(q, nivel, ct)
                : await ConsultarPhotonAsync(q["q"], nivel, cid, est, brasil, logradouro, ct);
            if (!r.ok && r.falhaDeRede)
            {
                erroRede ??= r.erro;
                if (fonte == "n")
                {
                    if (!PhotonAtivo) return new(false, null, null, "", $"Não foi possível acessar o serviço de mapas ({r.erro}). Verifique a internet/VPN/proxy ou use \"Definir a posição exata\" com o link do Google Maps.", "", true);
                    nominatimFora = true; _nominatimPausaAte = DateTime.UtcNow.AddMinutes(2); _nominatimUltimoErro = r.erro ?? "";
                }
                continue;                                                    // rede/serviço fora: não conclui "não existe", tenta o outro serviço
            }
            achouAlgo |= r.ok || r.erro is not null;
            // o serviço devolveu algo MAIS VAGO do que o pedido (ex.: pediu a rua e veio só "Brasil"): não vale para este nível
            if (r.ok && Precisao(r.nivel) < Precisao(nivel)) { tentados.Add(nivel); continue; }
            if (r.ok)
            {
                var fonteNome = fonte == "n" ? "OpenStreetMap/Nominatim" : "OpenStreetMap/Photon";
                var falhos = tentados.Distinct().ToList();
                var msg = falhos.Count == 0 ? r.nome : $"{r.nome} — não encontrado: {string.Join(", ", falhos)}; posição aproximada ao nível de {nivel}.";
                return new(true, r.lat, r.lon, fonteNome, msg, nivel);
            }
            tentados.Add(nivel); ultimoErro = r.erro;
        }
        // nenhum serviço respondeu: é problema de acesso (internet, VPN, proxy), não do endereço
        if (erroRede is not null && (nominatimFora || !achouAlgo))
            return new(false, null, null, "", $"Não foi possível acessar o serviço de mapas ({erroRede}). Verifique a internet/VPN/proxy ou use \"Definir a posição exata\" com o link do Google Maps.", "", true);
        return new(false, null, null, "", ultimoErro ?? "Local não encontrado (país, estado, cidade e endereço).");
    }

    /// <summary>Separa o logradouro, o número e o bairro do campo de endereço:
    /// "Rua Paraíba, 244 Jardim Tarumã", "Av. Brasil 1000 - Centro", "Rua 7, nº 120, apto 3, Vila Nova".
    /// "Rua 7" e "Rodovia SP 300, km 45" não perdem o número que faz parte do nome.</summary>
    public static (string logradouro, string numero, string bairro) Logradouro(string rua)
    {
        var partes = System.Text.RegularExpressions.Regex.Split(rua ?? "", @"\s*[,;]\s*|\s+[-–]\s+")
            .Select(x => x.Trim().Trim('-', '–').Trim()).Where(x => x != "").ToList();
        if (partes.Count == 0) return ("", "", "");
        string logr = partes[0], num = ""; var bairros = new List<string>();
        static bool EhNumero(string t) => System.Text.RegularExpressions.Regex.IsMatch(t, @"^\d{1,6}[A-Za-z]?$");
        static bool MarcaNumero(string t) => DocEngine.Norm(t).TrimEnd('.', ':') is "n" or "no" or "nº" or "n°" or "num" or "numero";
        static bool Complemento(string t) => System.Text.RegularExpressions.Regex.IsMatch(DocEngine.Norm(t),
            @"^(apto?\.?|apartamento|ap\.?|bloco|bl\.?|sala|sl\.?|conj\.?|conjunto|cj\.?|casa|fundos|lote|lt\.?|quadra|qd\.?|km|andar|loja|box|galpao|predio|torre)\b|^s\s*/?\s*n$");
        // número no meio do 1º pedaço: "Rua Paraíba 244 Jardim Tarumã" (precisa de ao menos 2 palavras antes: "Rua 7" é nome)
        var tk = logr.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 2; i < tk.Length; i++)
        {
            var antes = tk[i - 1];
            if (!EhNumero(tk[i].TrimStart('n', 'N', 'º', '°', '.'))) continue;
            if (DocEngine.Norm(antes) is "km" or "br" || System.Text.RegularExpressions.Regex.IsMatch(antes, @"^[A-Z]{2,3}$")) continue;   // "SP 300", "km 45"
            var fim = MarcaNumero(antes) ? i - 1 : i;
            if (fim < 2) continue;
            num = tk[i].TrimStart('n', 'N', 'º', '°', '.');
            var resto = string.Join(' ', tk.Skip(i + 1));
            logr = string.Join(' ', tk.Take(fim));
            if (resto != "") bairros.Add(resto);
            break;
        }
        foreach (var p in partes.Skip(1))
        {
            var m = System.Text.RegularExpressions.Regex.Match(p, @"^(?:n[º°o]?\.?\s*|n[uú]mero\s*)?(\d{1,6}[A-Za-z]?)\b\s*(.*)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (num == "" && m.Success)
            {
                num = m.Groups[1].Value;
                var resto = m.Groups[2].Value.Trim().Trim('-', '–').Trim();
                if (resto != "" && !Complemento(resto)) bairros.Add(resto);
                continue;
            }
            if (Complemento(p) || m.Success) continue;
            bairros.Add(p);
        }
        var bairro = bairros.FirstOrDefault() ?? "";
        bairro = System.Text.RegularExpressions.Regex.Replace(bairro, @"^bairro\s*:?\s*", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
        return (logr.Trim(), num, bairro);
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
    public static int Precisao(string nivel) => nivel switch { "manual" or "link" => 5, "endereco" => 4, "cep" or "bairro" => 3, "cidade" => 2, "estado" => 1, "pais" => 0, _ => -1 };
    /// <summary>Nome do nível para mensagens ("o bairro", "a cidade"…).</summary>
    public static string NomeNivel(string nivel) => nivel switch { "endereco" => "o endereço", "cep" => "o CEP", "bairro" => "o bairro", "cidade" => "a cidade", "estado" => "o estado", "pais" => "o país", _ => nivel };

    /// <summary>Nível que o resultado REALMENTE tem, pelo place_rank do Nominatim (rua 26+, bairro 19-25, cidade 13-18, estado 5-12, país até 4).</summary>
    public static string NivelDoRank(int rank, string tipo) =>
        tipo == "postcode" ? "cep" : rank >= 26 ? "endereco" : rank >= 19 ? "bairro" : rank >= 13 ? "cidade" : rank >= 5 ? "estado" : "pais";

    private async Task<(bool ok, double lat, double lon, string nome, string? erro, bool falhaDeRede, string nivel)> ConsultarComNivelAsync(Dictionary<string, string> q, string pedido, CancellationToken ct)
    {
        var r = await ConsultarAsync(q, ct);
        if (!r.ok) return (r.ok, r.lat, r.lon, r.nome, r.erro, r.falhaDeRede, "");
        var real = r.rank < 0 ? pedido : NivelDoRank(r.rank, r.tipo);           // sem rank (serviço compatível sem o campo): confia no pedido
        return (true, r.lat, r.lon, r.nome, null, false, real);
    }

    private bool PhotonAtivo => _photon != "";

    /// <summary>Consulta o Photon e só aceita o resultado que CONFERE com o pedido: mesmo país/estado/cidade e, para endereço, a mesma rua
    /// (o Photon sempre devolve "o mais parecido", que pode ser outra rua ou outra cidade).</summary>
    private async Task<(bool ok, double lat, double lon, string nome, string? erro, bool falhaDeRede, string nivel)> ConsultarPhotonAsync(
        string texto, string pedido, string cidade, string estado, bool brasil, string logradouro, CancellationToken ct)
    {
        var url = $"{_photon}/api/?q={Uri.EscapeDataString(texto)}&limit=8";
        if (!_cachePhoton.TryGetValue(url, out var corpo))
        {
            await _ritmo.WaitAsync(ct);
            ConsultasRealizadas++;
            try
            {
                var espera = TimeSpan.FromMilliseconds(_intervaloMs) - (DateTime.UtcNow - _ultimo);
                if (espera > TimeSpan.Zero) await Task.Delay(espera, ct);
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.UserAgent.ParseAdd(string.IsNullOrWhiteSpace(Contato) ? "ControleTecnico/1.0" : $"ControleTecnico/1.0 ({Contato})");
                using var resp = await _http.SendAsync(req, ct);
                _ultimo = DateTime.UtcNow;
                if (!resp.IsSuccessStatusCode) return (false, 0, 0, "", $"Photon respondeu {(int)resp.StatusCode}", true, "");
                corpo = await resp.Content.ReadAsStringAsync(ct);
                _cachePhoton[url] = corpo;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { return (false, 0, 0, "", "Photon: " + ex.Message, true, ""); }
            finally { _ritmo.Release(); }
        }
        else ConsultasEmCache++;
        try
        {
            using var doc = JsonDocument.Parse(corpo);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("features", out var fs) || fs.ValueKind != JsonValueKind.Array)
                return (false, 0, 0, "", "Não encontrado.", false, "");
            foreach (var f in fs.EnumerateArray())
            {
                if (!f.TryGetProperty("properties", out var p) || !f.TryGetProperty("geometry", out var g) || !g.TryGetProperty("coordinates", out var c) || c.GetArrayLength() < 2) continue;
                string S(string k) => p.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                bool Igual(string a, string b) => a != "" && DocEngine.Norm(a) == DocEngine.Norm(b);
                if (brasil && S("countrycode") is { Length: > 0 } cc && !cc.Equals("BR", StringComparison.OrdinalIgnoreCase)) continue;
                if (estado != "" && S("state") is { Length: > 0 } st && !Igual(st, estado)) continue;
                var tipo = S("type");
                string? nivel = null;
                if (pedido == "endereco")
                {
                    if (cidade == "" || !new[] { "city", "county", "locality", "district" }.Any(k => Igual(S(k), cidade))) continue;
                    var nomeRua = tipo == "street" ? S("name") : S("street");
                    if (tipo is "house" or "street" or "other" && MesmaRua(nomeRua, logradouro)) nivel = "endereco";
                }
                else if (pedido == "bairro")
                {
                    if (tipo is "district" or "locality" && new[] { "city", "county" }.Any(k => Igual(S(k), cidade))) nivel = "bairro";
                }
                else if (pedido == "cidade")
                {
                    if (tipo is "city" or "county" or "district" or "locality" && Igual(S("name"), cidade)) nivel = "cidade";
                }
                if (nivel is null) continue;
                var nome = string.Join(", ", new[] { S("name"), S("housenumber") != "" && tipo == "house" ? S("street") + " " + S("housenumber") : "", S("district"), S("city"), S("state") }.Where(x => x.Trim() != "").Distinct());
                return (true, c[1].GetDouble(), c[0].GetDouble(), nome, null, false, nivel);
            }
            return (false, 0, 0, "", "Não encontrado.", false, "");
        }
        catch (JsonException) { return (false, 0, 0, "", "Não encontrado.", false, ""); }
    }

    private static readonly HashSet<string> TiposLogradouro = new() { "rua", "r", "avenida", "av", "travessa", "tv", "trav", "alameda", "al", "estrada", "est", "rodovia", "rod", "praca", "pca", "pc", "largo", "lgo", "via", "viela", "vila", "beco", "servidao", "passagem", "ladeira", "rua.", "av." };
    private static readonly HashSet<string> Ligacoes = new() { "de", "da", "do", "das", "dos", "e" };
    private static readonly Dictionary<string, string> Abreviacoes = new()
    {
        ["dr"] = "doutor", ["dra"] = "doutora", ["prof"] = "professor", ["profa"] = "professora", ["eng"] = "engenheiro", ["cel"] = "coronel", ["gen"] = "general",
        ["pres"] = "presidente", ["gov"] = "governador", ["des"] = "desembargador", ["cap"] = "capitao", ["ten"] = "tenente", ["mal"] = "marechal",
        ["alm"] = "almirante", ["pe"] = "padre", ["sto"] = "santo", ["sta"] = "santa", ["dep"] = "deputado", ["ver"] = "vereador", ["sen"] = "senador",
        ["cmdte"] = "comandante", ["com"] = "comendador", ["mons"] = "monsenhor", ["visc"] = "visconde", ["bar"] = "barao", ["maj"] = "major", ["sgt"] = "sargento",
    };
    /// <summary>A rua devolvida é a digitada? Compara as palavras do nome sem o tipo (Rua/Av.), sem acentos e com abreviações expandidas.</summary>
    public static bool MesmaRua(string encontrada, string digitada)
    {
        static HashSet<string> Palavras(string s) => DocEngine.Norm(s).Replace(".", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => Abreviacoes.TryGetValue(w, out var x) ? x : w).Where(w => !Ligacoes.Contains(w)).ToHashSet();
        var a = Palavras(encontrada); var b = Palavras(digitada);
        a.RemoveWhere(TiposLogradouro.Contains); b.RemoveWhere(TiposLogradouro.Contains);
        return a.Count > 0 && a.SetEquals(b);
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
