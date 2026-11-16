using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components.Authorization;

namespace ControleTecnico.Logic;

/// <summary>
/// Idiomas do sistema. O texto em português é a CHAVE: as telas escrevem <c>@L["Salvar"]</c> e o dicionário do
/// idioma (Idiomas/es/*.json, embutido no programa) diz a tradução. Sem tradução, o texto sai em português.
/// Entradas com {0}, {1}… valem como modelo para textos montados com valores ("{0} de {1} itens concluídos"):
/// a tradução recebe os mesmos valores (que também são traduzidos, quando houver tradução para eles).
/// </summary>
public static class Idiomas
{
    public static readonly (string Cod, string Nome)[] Lista = { ("pt", "Português"), ("es", "Español") };
    public static readonly CultureInfo Pt = new("pt-BR"), Es = new("es-CL");
    public static CultureInfo Cultura(string idioma) => idioma == "es" ? Es : Pt;

    private sealed class Dic
    {
        public readonly Dictionary<string, string> Exatos = new(StringComparer.Ordinal);
        public readonly List<(Regex re, string pt, string trad)> Modelos = new();
        public readonly ConcurrentDictionary<string, string> Cache = new(StringComparer.Ordinal);
    }

    private static readonly Regex _ph = new(@"\{(\d+)\}", RegexOptions.Compiled);
    private static readonly Dictionary<string, Dic> _dic = Carregar();

    private static Dictionary<string, Dic> Carregar()
    {
        var res = new Dictionary<string, Dic>();
        var asm = Assembly.GetExecutingAssembly();
        foreach (var nome in asm.GetManifestResourceNames().Where(n => n.Contains(".Idiomas.") && n.EndsWith(".json", StringComparison.OrdinalIgnoreCase)).OrderBy(n => n, StringComparer.Ordinal))
        {
            // ControleTecnico.Idiomas.es.telas.json -> "es"
            var partes = nome.Split('.'); var i = Array.IndexOf(partes, "Idiomas");
            if (i < 0 || i + 1 >= partes.Length) continue;
            var idioma = partes[i + 1];
            if (!res.TryGetValue(idioma, out var d)) res[idioma] = d = new Dic();
            using var st = asm.GetManifestResourceStream(nome)!;
            var itens = JsonSerializer.Deserialize<Dictionary<string, string>>(st, new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) ?? new();
            foreach (var (pt, trad) in itens)
            {
                if (string.IsNullOrEmpty(pt) || string.IsNullOrEmpty(trad)) continue;
                var chave = pt.Trim();
                if (!d.Exatos.TryAdd(chave, trad.Trim())) continue;      // chave repetida em outro arquivo: vale a primeira (ordem alfabética)
                if (_ph.IsMatch(chave))
                {
                    var padrao = "^" + _ph.Replace(Regex.Escape(chave).Replace(@"\{", "{"), m => $"(?<p{m.Groups[1].Value}>.+?)") + "$";
                    try { d.Modelos.Add((new Regex(padrao, RegexOptions.Singleline | RegexOptions.Compiled), chave, trad.Trim())); } catch { }
                }
            }
        }
        // modelos mais específicos (mais texto fixo) primeiro
        foreach (var d in res.Values) d.Modelos.Sort((a, b) => _ph.Replace(b.pt, "").Length.CompareTo(_ph.Replace(a.pt, "").Length));
        return res;
    }

    public static int Quantidade(string idioma) => _dic.TryGetValue(idioma, out var d) ? d.Exatos.Count : 0;

    public static string Traduzir(string? texto, string idioma)
    {
        if (string.IsNullOrEmpty(texto) || idioma == "pt" || !_dic.TryGetValue(idioma, out var d)) return texto ?? "";
        if (d.Cache.TryGetValue(texto, out var c)) return c;
        var r = TraduzirSemCache(texto, d);
        if (d.Cache.Count < 50_000) d.Cache[texto] = r;
        return r;
    }

    private static string TraduzirSemCache(string texto, Dic d, int nivel = 0)
    {
        // preserva espaços nas pontas (textos colados em outros elementos)
        var miolo = texto.Trim();
        if (miolo.Length == 0) return texto;
        var ini = texto[..texto.IndexOf(miolo, StringComparison.Ordinal)]; var fim = texto[(ini.Length + miolo.Length)..];
        if (d.Exatos.TryGetValue(miolo, out var t)) return ini + t + fim;
        foreach (var (re, _, trad) in d.Modelos)
        {
            var m = re.Match(miolo);
            if (!m.Success) continue;
            var saida = _ph.Replace(trad, x =>
            {
                var g = m.Groups["p" + x.Groups[1].Value];
                if (!g.Success) return x.Value;
                if (d.Exatos.TryGetValue(g.Value.Trim(), out var v)) return v;
                // valor que é ele mesmo uma frase montada (mensagem dentro de mensagem): tenta os modelos, até 3 níveis
                return nivel < 3 ? TraduzirSemCache(g.Value, d, nivel + 1) : g.Value;
            });
            return ini + saida + fim;
        }
        return texto;
    }
}

/// <summary>Tradução para a pessoa logada (um por circuito). Nas telas: <c>@L["texto"]</c>, <c>@L.F("{0} itens", n)</c> e <c>L.Cultura</c> para datas.</summary>
public sealed class Tr
{
    private readonly AuthenticationStateProvider _auth;
    private readonly IHttpContextAccessor _http;
    private string? _idioma;
    public Tr(AuthenticationStateProvider auth, IHttpContextAccessor http) { _auth = auth; _http = http; }
    /// <summary>Antes do login (tela de acesso): o idioma escolhido fica num cookie.</summary>
    public static string IdiomaDoCookie(HttpContext? h) => h?.Request.Cookies["ct-idioma"] is "es" ? "es" : "pt";

    public string Idioma
    {
        get
        {
            if (_idioma is not null) return _idioma;
            try
            {
                var t = _auth.GetAuthenticationStateAsync();
                if (!t.IsCompleted) return "pt";
                var u = t.Result.User;
                _idioma = u.Identity?.IsAuthenticated == true ? Ator.De(u).Idioma : IdiomaDoCookie(_http.HttpContext);
            }
            catch { _idioma = "pt"; }
            return _idioma;
        }
    }
    public bool Es => Idioma == "es";
    public CultureInfo Cultura => Idiomas.Cultura(Idioma);
    public string this[string? texto] => Idiomas.Traduzir(texto, Idioma);
    /// <summary>Traduz o modelo e preenche os valores: L.F("{0} de {1} itens", a, b).</summary>
    public string F(string modelo, params object?[] valores) => string.Format(Cultura, Idiomas.Traduzir(modelo, Idioma), valores);
}
