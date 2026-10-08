using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using ControleTecnico.Data;
using ControleTecnico.Models;

namespace ControleTecnico.Logic;

public sealed record CampoImport(string Key, string Label, bool Obrigatorio, string[] Sinonimos, string Exemplo);

public sealed class ArquivoLido
{
    public string Nome { get; init; } = "";
    public List<string> Cabecalhos { get; init; } = new();
    public List<string[]> Linhas { get; init; } = new();
    /// <summary>Número da linha na planilha original para cada linha lida.</summary>
    public List<int> Numeros { get; init; } = new();
}

public enum AcaoImport { Criar, Atualizar, Ignorar }

public sealed class LinhaImport
{
    public int Numero { get; set; }
    public Dictionary<string, string> V { get; set; } = new();
    public List<string> Erros { get; } = new();
    public List<string> Avisos { get; } = new();
    public List<string> Diferencas { get; } = new();
    public string? PlantaExistenteId { get; set; }
    public string? ClienteExistenteId { get; set; }
    public bool Existente => PlantaExistenteId is not null;
    public AcaoImport Acao { get; set; } = AcaoImport.Criar;
    public bool Rejeitada => Erros.Count > 0;
    public string Cliente => V.GetValueOrDefault("cliente", "");
    public string Planta => V.GetValueOrDefault("planta", "");
}

public sealed class ResultadoImport
{
    public int ClientesCriados { get; set; }
    public int Importadas { get; set; }
    public int Atualizadas { get; set; }
    public int Ignoradas { get; set; }
    public List<(int linha, string cliente, string planta, string motivo)> Rejeitadas { get; } = new();
    public List<(int linha, string detalhe)> Avisos { get; } = new();
}

public sealed class Importador
{
    private readonly Db _db;
    private readonly Servicos _svc;
    public const int MaxLinhas = 5000;
    public const long MaxBytes = 10 * 1024 * 1024;

    public Importador(Db db, Servicos svc) { _db = db; _svc = svc; }

    /// <summary>Planilha de plantas: SOMENTE 5 colunas — Account (nome da planta, o que se seleciona ao enviar o técnico),
    /// Country, City, State e Address1. Só o nome é obrigatório; sem cidade a planta entra e a cidade é pedida quando
    /// ela for selecionada numa viagem. Cliente, CEP, fuso etc. não fazem parte da importação (ficam em branco/padrão).</summary>
    public static readonly CampoImport[] Campos =
    {
        new("planta", "Account", true, new[] { "account", "planta", "nome da planta", "planta/unidade", "unidade", "plant", "plant name", "site", "name", "nome", "filial" }, "Planta Campinas"),
        new("pais", "Country", false, new[] { "country", "pais", "país" }, "Brasil"),
        new("cidade", "City", false, new[] { "city", "cidade", "municipio" }, "Campinas"),
        new("estado", "State", false, new[] { "state", "estado", "uf", "provincia", "region", "regiao" }, "SP"),
        new("rua", "Address1", false, new[] { "address1", "address 1", "address", "endereco", "endereço", "rua", "logradouro", "street" }, "Av. das Indústrias, 1500"),
    };

    /// <summary>Chaves internas que não vêm da planilha (sempre vazias) mas são usadas pelas regras de importação.</summary>
    private static readonly string[] Ocultos =
        { "cliente", "numero", "complemento", "cep", "endereco", "fuso", "lat", "lon", "razao", "documento", "contato", "telefone", "email", "contato_local", "acesso", "obs" };

    private static readonly Dictionary<string, string> FusoPorPais = new(StringComparer.OrdinalIgnoreCase)
    {
        ["brasil"] = "America/Sao_Paulo", ["argentina"] = "America/Argentina/Buenos_Aires", ["chile"] = "America/Santiago",
        ["colombia"] = "America/Bogota", ["mexico"] = "America/Mexico_City", ["peru"] = "America/Lima", ["uruguai"] = "America/Montevideo",
        ["portugal"] = "Europe/Lisbon", ["espanha"] = "Europe/Madrid", ["alemanha"] = "Europe/Berlin", ["reino unido"] = "Europe/London",
        ["india"] = "Asia/Kolkata",
    };

    // ---------------------------------------------------------------- modelo
    public static byte[] Modelo()
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Plantas");
        string[] cab = { "Account", "Country", "City", "State", "Address1" };
        string[][] ex =
        {
            new[] { "Planta Campinas", "Brasil", "Campinas", "SP", "Av. das Indústrias, 1500" },
            new[] { "Planta Monterrey", "México", "Monterrey", "Nuevo León", "Av. Constitución 2100" },
            new[] { "Planta Calama", "Chile", "Calama", "Antofagasta", "" },
        };
        for (var i = 0; i < cab.Length; i++)
        {
            var c = ws.Cell(1, i + 1); c.Value = cab[i]; c.Style.Font.Bold = true; c.Style.Font.FontColor = XLColor.White;
            c.Style.Fill.BackgroundColor = i == 0 ? XLColor.FromHtml("#004785") : XLColor.FromHtml("#6b7a90");
            for (var r = 0; r < ex.Length; r++) ws.Cell(r + 2, i + 1).SetValue(ex[r][i]).Style.NumberFormat.Format = "@";
        }
        ws.Columns().AdjustToContents();
        var info = wb.AddWorksheet("Instruções");
        var linhas = new[]
        {
            "Uma linha por planta. Colunas, nesta ordem: 1) Account (nome da planta, que será selecionado ao enviar o técnico), 2) Country, 3) City, 4) State, 5) Address1.",
            "Só o nome da planta é obrigatório. A localização no mapa é buscada em cascata: país → estado → cidade → endereço.",
            "Se o endereço não for encontrado, a planta fica marcada na cidade; se a cidade não for encontrada, no estado; se o estado não for encontrado, no país.",
            "Planta sem cidade pode ser importada: quando for selecionada numa viagem, o sistema pede a cidade e já grava no cadastro.",
            "Apague as linhas de exemplo (2 a 4) antes de importar. Não há outras colunas: o restante (contatos, acesso, ajuste manual da posição) é preenchido no sistema.",
        };
        for (var i = 0; i < linhas.Length; i++) info.Cell(i + 1, 1).Value = linhas[i];
        info.Column(1).Width = 130;
        using var ms = new MemoryStream(); wb.SaveAs(ms); return ms.ToArray();
    }

    // ---------------------------------------------------------------- leitura
    public static ArquivoLido Ler(string nome, Stream conteudo)
    {
        using var ms = new MemoryStream();
        conteudo.CopyTo(ms);
        if (ms.Length > MaxBytes) throw new ValidacaoException("Arquivo acima de 10 MB.");
        if (ms.Length == 0) throw new ValidacaoException("O arquivo está vazio.");
        var ext = Path.GetExtension(nome).ToLowerInvariant();
        ms.Position = 0;
        return ext switch
        {
            ".xlsx" => LerXlsx(nome, ms),
            ".csv" or ".txt" => LerCsv(nome, ms.ToArray()),
            _ => throw new ValidacaoException("Formato não suportado. Envie um arquivo .xlsx ou .csv."),
        };
    }

    private static ArquivoLido LerXlsx(string nome, Stream s)
    {
        XLWorkbook wb;
        try { wb = new XLWorkbook(s); }
        catch { throw new ValidacaoException("Não foi possível abrir a planilha: o arquivo está corrompido ou não é um .xlsx válido."); }
        using (wb)
        {
            var ws = wb.Worksheets.FirstOrDefault(w => w.Name != "Instruções" && !w.IsEmpty()) ?? throw new ValidacaoException("A planilha não tem dados.");
            var usado = ws.RangeUsed() ?? throw new ValidacaoException("A planilha não tem dados.");
            var primeira = usado.FirstRow().RowNumber(); var ultima = usado.LastRow().RowNumber();
            var ultCol = usado.LastColumn().ColumnNumber(); var priCol = usado.FirstColumn().ColumnNumber();
            var cab = primeira;
            for (var r = primeira; r <= Math.Min(ultima, primeira + 9); r++)
                if (Enumerable.Range(priCol, ultCol - priCol + 1).Count(c => !string.IsNullOrWhiteSpace(ws.Cell(r, c).GetFormattedString())) >= 2) { cab = r; break; }
            if (ultCol == priCol) cab = primeira;      // uma coluna só: o cabeçalho é a primeira linha preenchida
            var a = new ArquivoLido { Nome = nome };
            for (var c = priCol; c <= ultCol; c++) a.Cabecalhos.Add(ws.Cell(cab, c).GetFormattedString().Trim());
            for (var r = cab + 1; r <= ultima; r++)
            {
                var vals = Enumerable.Range(priCol, ultCol - priCol + 1).Select(c => ws.Cell(r, c).GetFormattedString().Trim()).ToArray();
                if (vals.All(string.IsNullOrWhiteSpace)) continue;
                a.Linhas.Add(vals); a.Numeros.Add(r);
                if (a.Linhas.Count > MaxLinhas) throw new ValidacaoException($"A planilha excede {MaxLinhas} linhas.");
            }
            if (a.Linhas.Count == 0) throw new ValidacaoException("A planilha só tem o cabeçalho, sem registros.");
            return a;
        }
    }

    private static ArquivoLido LerCsv(string nome, byte[] bytes)
    {
        string texto;
        try { texto = new UTF8Encoding(false, true).GetString(bytes).TrimStart('﻿'); }
        catch { texto = Encoding.Latin1.GetString(bytes); }
        var primeira = texto.Split('\n')[0];
        var delim = new[] { ';', ',', '\t' }.OrderByDescending(d => primeira.Count(c => c == d)).First();
        var linhas = new List<string[]>();
        var campo = new StringBuilder(); var atual = new List<string>(); var aspas = false;
        for (var i = 0; i < texto.Length; i++)
        {
            var ch = texto[i];
            if (aspas)
            {
                if (ch == '"') { if (i + 1 < texto.Length && texto[i + 1] == '"') { campo.Append('"'); i++; } else aspas = false; }
                else campo.Append(ch);
            }
            else if (ch == '"') aspas = true;
            else if (ch == delim) { atual.Add(campo.ToString().Trim()); campo.Clear(); }
            else if (ch == '\n' || ch == '\r')
            {
                if (ch == '\r' && i + 1 < texto.Length && texto[i + 1] == '\n') i++;
                atual.Add(campo.ToString().Trim()); campo.Clear();
                linhas.Add(atual.ToArray()); atual = new();
            }
            else campo.Append(ch);
        }
        if (campo.Length > 0 || atual.Count > 0) { atual.Add(campo.ToString().Trim()); linhas.Add(atual.ToArray()); }
        if (aspas) throw new ValidacaoException("CSV inválido: aspas não fechadas.");
        var a = new ArquivoLido { Nome = nome };
        var idx = linhas.FindIndex(l => l.Count(x => !string.IsNullOrWhiteSpace(x)) >= 2);
        if (idx < 0) idx = linhas.FindIndex(l => l.Any(x => !string.IsNullOrWhiteSpace(x)));     // arquivo de uma coluna só (ex.: lista de nomes)
        if (idx < 0) throw new ValidacaoException("Não foi possível identificar o cabeçalho do CSV.");
        a.Cabecalhos.AddRange(linhas[idx].Select(x => x.Trim()));
        for (var i = idx + 1; i < linhas.Count; i++)
        {
            if (linhas[i].All(string.IsNullOrWhiteSpace)) continue;
            a.Linhas.Add(linhas[i]); a.Numeros.Add(i + 1);
            if (a.Linhas.Count > MaxLinhas) throw new ValidacaoException($"O arquivo excede {MaxLinhas} linhas.");
        }
        if (a.Linhas.Count == 0) throw new ValidacaoException("O arquivo só tem o cabeçalho, sem registros.");
        return a;
    }

    // ------------------------------------------------------------ mapeamento
    public static Dictionary<string, int> SugerirMapeamento(List<string> cabecalhos)
    {
        var map = new Dictionary<string, int>();
        var usados = new HashSet<int>();
        foreach (var c in Campos)
        {
            for (var i = 0; i < cabecalhos.Count; i++)
            {
                if (usados.Contains(i)) continue;
                var h = DocEngine.Norm(cabecalhos[i]);
                if (h == DocEngine.Norm(c.Label) || c.Sinonimos.Any(s => DocEngine.Norm(s) == h)) { map[c.Key] = i; usados.Add(i); break; }
            }
        }
        // planilha sem títulos reconhecíveis: vale a ordem combinada (1 planta, 2 país, 3 cidade, 4 estado, 5 endereço)
        if (!map.ContainsKey("planta") && cabecalhos.Count >= 1)
        {
            map.Clear();
            var ordem = new[] { "planta", "pais", "cidade", "estado", "rua" };
            for (var i = 0; i < Math.Min(ordem.Length, cabecalhos.Count); i++) map[ordem[i]] = i;
        }
        return map;
    }

    // ---------------------------------------------------------------- análise
    public List<LinhaImport> Analisar(ArquivoLido a, Dictionary<string, int> map)
    {
        var faltando = Campos.Where(c => c.Obrigatorio && !map.ContainsKey(c.Key)).Select(c => c.Label).ToList();
        if (faltando.Count > 0) throw new ValidacaoException($"Mapeie as colunas obrigatórias: {string.Join(", ", faltando)}.");

        var clientes = _db.Clientes.Todos();
        var plantas = _db.Plantas.Todos();
        var porCliente = clientes.GroupBy(c => DocEngine.Norm(c.Nome)).ToDictionary(g => g.Key, g => g.First());
        var plantaPorChave = plantas.GroupBy(p => $"{p.ClienteId}|{DocEngine.Norm(p.Nome)}").ToDictionary(g => g.Key, g => g.First());
        var vistos = new Dictionary<string, int>();
        var res = new List<LinhaImport>();

        for (var i = 0; i < a.Linhas.Count; i++)
        {
            var l = new LinhaImport { Numero = a.Numeros[i] };
            foreach (var k in Ocultos) l.V[k] = "";
            foreach (var c in Campos)
                l.V[c.Key] = map.TryGetValue(c.Key, out var col) && col < a.Linhas[i].Length ? a.Linhas[i][col].Trim() : "";

            if (string.IsNullOrWhiteSpace(l.V["planta"])) l.Erros.Add("Nome da planta não informado.");
            if (string.IsNullOrWhiteSpace(l.V["cidade"]) && string.IsNullOrWhiteSpace(l.V["endereco"]))
                l.Avisos.Add("Sem cidade: a planta será importada, mas a cidade será pedida quando ela for selecionada para enviar um técnico (e a localização no mapa fica pendente).");

            if (string.IsNullOrWhiteSpace(l.V["pais"]))
            {
                l.V["pais"] = "Brasil"; l.Avisos.Add("País não informado: foi considerado \"Brasil\".");
            }
            var br = DocEngine.Norm(l.V["pais"]) == "brasil";
            var cep = l.V["cep"];
            if (br && cep.Length > 0 && cep.All(char.IsDigit) && cep.Length is >= 5 and < 8)
            {
                l.V["cep"] = cep.PadLeft(8, '0'); l.Avisos.Add($"CEP completado com zeros à esquerda ({cep} → {l.V["cep"]}).");
            }
            if (br && l.V["cep"].Length > 0 && l.V["cep"].Count(char.IsDigit) != 8) l.Avisos.Add("CEP fora do padrão brasileiro (8 dígitos); mantido como informado.");

            if (!string.IsNullOrWhiteSpace(l.V["email"]) && !l.V["email"].Contains('@')) l.Erros.Add("E-mail inválido.");

            // coordenadas
            var temLat = !string.IsNullOrWhiteSpace(l.V["lat"]); var temLon = !string.IsNullOrWhiteSpace(l.V["lon"]);
            if (temLat != temLon) l.Erros.Add("Informe latitude e longitude juntas.");
            else if (temLat)
            {
                if (!TryNum(l.V["lat"], out var la) || !TryNum(l.V["lon"], out var lo)) l.Erros.Add("Latitude/longitude não numéricas.");
                else if (!Geo.CoordValida(la, lo)) l.Erros.Add("Latitude/longitude fora do intervalo válido.");
            }

            // fuso
            if (!string.IsNullOrWhiteSpace(l.V["fuso"]))
            {
                if (!Tempo.FusoValido(l.V["fuso"])) l.Erros.Add($"Fuso horário \"{l.V["fuso"]}\" inválido (use o formato IANA, ex.: America/Sao_Paulo).");
            }
            else
            {
                if (FusoPorPais.TryGetValue(DocEngine.Norm(l.V["pais"]), out var f)) l.V["fuso"] = f;
                else { l.V["fuso"] = _db.FusoPadrao; l.Avisos.Add($"Fuso horário não informado para {l.V["pais"]}: usado o padrão do sistema ({_db.FusoPadrao}). Revise na planta."); }
            }

            var kc = DocEngine.Norm(l.V["cliente"]); var kp = DocEngine.Norm(l.V["planta"]);
            if (kp.Length > 0)
            {
                var chave = $"{kc}|{kp}";
                if (vistos.TryGetValue(chave, out var primeira)) l.Erros.Add($"Duplicada no arquivo (mesma planta da linha {primeira}).");
                else vistos[chave] = l.Numero;
                Planta? pl = null; Cliente? cli = null;
                if (kc.Length > 0)
                {
                    if (porCliente.TryGetValue(kc, out cli)) { l.ClienteExistenteId = cli.Id; plantaPorChave.TryGetValue($"{cli.Id}|{kp}", out pl); }
                }
                else
                {
                    // sem cliente na planilha: o nome da planta é a chave (é o que se seleciona ao enviar o técnico)
                    pl = plantas.Where(x => DocEngine.Norm(x.Nome) == kp).OrderBy(x => x.ClienteId == "" ? 0 : 1).FirstOrDefault();
                    if (pl is not null && pl.ClienteId != "") cli = clientes.FirstOrDefault(c => c.Id == pl.ClienteId);
                }
                if (pl is not null) { l.PlantaExistenteId = pl.Id; if (!l.Rejeitada) MontarDiferencas(l, cli, pl); }
            }
            l.Acao = l.Rejeitada ? AcaoImport.Ignorar : l.Existente ? AcaoImport.Atualizar : AcaoImport.Criar;
            if (l.Existente && !l.Rejeitada && l.Diferencas.Count == 0) { l.Avisos.Add("Idêntica ao cadastro existente: nada a atualizar."); l.Acao = AcaoImport.Ignorar; }
            res.Add(l);
        }
        return res;
    }

    private static bool TryNum(string s, out double d) =>
        double.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out d);

    private static void MontarDiferencas(LinhaImport l, Cliente? c, Planta p)
    {
        void Dif(string campo, string atual, string novo)
        {
            if (!string.IsNullOrWhiteSpace(novo) && !string.Equals(atual?.Trim(), novo.Trim(), StringComparison.Ordinal))
                l.Diferencas.Add($"{campo}: \"{(string.IsNullOrWhiteSpace(atual) ? "vazio" : atual)}\" → \"{novo}\"");
        }
        Dif("Rua", p.Rua, l.V["rua"]); Dif("Número", p.Numero, l.V["numero"]); Dif("Complemento", p.Complemento, l.V["complemento"]);
        Dif("CEP", p.Cep, l.V["cep"]); Dif("Cidade", p.Cidade, l.V["cidade"]); Dif("Estado", p.Estado, l.V["estado"]);
        Dif("País", p.Pais, l.V["pais"]); Dif("Fuso", p.FusoHorario, l.V["fuso"]);
        Dif("Contato local", p.ContatoLocal, l.V["contato_local"]); Dif("Acesso", p.Acesso, l.V["acesso"]); Dif("Observações", p.Obs, l.V["obs"]);
        if (c is not null)
        {
            Dif("Razão social", c.RazaoSocial, l.V["razao"]); Dif("Documento", c.Documento, l.V["documento"]);
            Dif("Contato do cliente", c.Contato, l.V["contato"]); Dif("Telefone", c.Telefone, l.V["telefone"]); Dif("E-mail", c.Email, l.V["email"]);
        }
        if (TryNum(l.V["lat"], out var la) && TryNum(l.V["lon"], out var lo) && (p.Lat != la || p.Lon != lo))
            l.Diferencas.Add($"Coordenadas: {(p.TemCoord ? $"{p.Lat:0.#####}, {p.Lon:0.#####}" : "vazio")} → {la:0.#####}, {lo:0.#####}");
    }

    // ---------------------------------------------------------------- aplicação
    public ResultadoImport Aplicar(Ator ator, List<LinhaImport> linhas)
    {
        ator.Exigir(Perm.EditarClientes);
        var r = new ResultadoImport();
        var clientes = _db.Clientes.Todos().GroupBy(c => DocEngine.Norm(c.Nome)).ToDictionary(g => g.Key, g => g.First());
        var plantasPorId = _db.Plantas.Todos().ToDictionary(p => p.Id);
        var novosClientes = new Dictionary<string, Cliente>();
        var clientesParaSalvar = new Dictionary<string, Cliente>();
        var plantasParaSalvar = new List<Planta>();
        var nomesUsados = new HashSet<string>(plantasPorId.Values.Select(p => $"{p.ClienteId}|{DocEngine.Norm(p.Nome)}"));

        foreach (var l in linhas)
        {
            foreach (var av in l.Avisos) r.Avisos.Add((l.Numero, av));
            if (l.Rejeitada) { r.Rejeitadas.Add((l.Numero, l.Cliente, l.Planta, string.Join(" ", l.Erros))); continue; }
            if (l.Acao == AcaoImport.Ignorar) { r.Ignoradas++; continue; }

            var kc = DocEngine.Norm(l.V["cliente"]);
            Cliente? cli = null;
            if (kc.Length == 0) { /* sem cliente na planilha: a planta fica sem cliente (ou mantém o que já tem) */ }
            else if (!clientes.TryGetValue(kc, out cli))
            {
                if (!novosClientes.TryGetValue(kc, out cli))
                {
                    cli = new Cliente
                    {
                        Id = Repo<Cliente>.NovoId(), Nome = l.V["cliente"], RazaoSocial = l.V["razao"], Documento = l.V["documento"],
                        Contato = l.V["contato"], Telefone = l.V["telefone"], Email = l.V["email"], Ativo = true,
                    };
                    novosClientes[kc] = cli; clientesParaSalvar[cli.Id] = cli; r.ClientesCriados++;
                }
            }
            else
            {
                // cliente existente: só completa/atualiza com o que veio preenchido na planilha
                var mudou = false;
                void Set(string novo, Func<string> get, Action<string> set) { if (!string.IsNullOrWhiteSpace(novo) && get() != novo) { set(novo); mudou = true; } }
                Set(l.V["razao"], () => cli.RazaoSocial, v => cli.RazaoSocial = v);
                Set(l.V["documento"], () => cli.Documento, v => cli.Documento = v);
                Set(l.V["contato"], () => cli.Contato, v => cli.Contato = v);
                Set(l.V["telefone"], () => cli.Telefone, v => cli.Telefone = v);
                Set(l.V["email"], () => cli.Email, v => cli.Email = v);
                if (mudou && l.Acao == AcaoImport.Atualizar) clientesParaSalvar[cli.Id] = cli;
            }

            Planta p;
            var ehAtualizacao = l.Acao == AcaoImport.Atualizar && l.PlantaExistenteId is not null;
            var nomePlanta = l.V["planta"];
            var cliId = cli?.Id ?? "";
            if (ehAtualizacao) { p = plantasPorId[l.PlantaExistenteId!]; if (cli is not null) p.ClienteId = cli.Id; cliId = p.ClienteId; }
            else
            {
                // "criar mesmo assim" para algo que já existe: nome diferenciado, visível no resultado
                if (nomesUsados.Contains($"{cliId}|{DocEngine.Norm(nomePlanta)}"))
                {
                    var n = 2; while (nomesUsados.Contains($"{cliId}|{DocEngine.Norm(nomePlanta + " (" + n + ")")}")) n++;
                    r.Avisos.Add((l.Numero, $"Já existia \"{nomePlanta}\": criada como \"{nomePlanta} ({n})\"."));
                    nomePlanta = $"{nomePlanta} ({n})";
                }
                p = new Planta { Id = Repo<Planta>.NovoId(), ClienteId = cliId, Nome = nomePlanta, Ativo = true };
            }
            var enderecoAntes = $"{p.Rua}|{p.Numero}|{p.Cep}|{p.Cidade}|{p.Estado}|{p.Pais}";
            void S(string novo, Func<string> get, Action<string> set) { if (!string.IsNullOrWhiteSpace(novo)) set(novo); else if (!ehAtualizacao) set(""); }
            S(l.V["rua"], () => p.Rua, v => p.Rua = v); S(l.V["numero"], () => p.Numero, v => p.Numero = v);
            S(l.V["complemento"], () => p.Complemento, v => p.Complemento = v); S(l.V["cep"], () => p.Cep, v => p.Cep = v);
            S(l.V["cidade"], () => p.Cidade, v => p.Cidade = v); S(l.V["estado"], () => p.Estado, v => p.Estado = v);
            S(l.V["pais"], () => p.Pais, v => p.Pais = v); S(l.V["fuso"], () => p.FusoHorario, v => p.FusoHorario = v);
            S(l.V["contato_local"], () => p.ContatoLocal, v => p.ContatoLocal = v); S(l.V["acesso"], () => p.Acesso, v => p.Acesso = v);
            S(l.V["obs"], () => p.Obs, v => p.Obs = v);
            if (!string.IsNullOrWhiteSpace(l.V["endereco"])) p.EnderecoCompleto = l.V["endereco"];
            else p.EnderecoCompleto = Servicos.MontarEndereco(p);

            if (TryNum(l.V["lat"], out var la) && TryNum(l.V["lon"], out var lo))
            { p.Lat = la; p.Lon = lo; p.GeoStatus = "manual"; p.GeoFonte = "Coordenadas da planilha"; p.GeoEm = DateTime.UtcNow; }
            else if (!ehAtualizacao) { p.GeoStatus = "pendente"; }
            else if (enderecoAntes != $"{p.Rua}|{p.Numero}|{p.Cep}|{p.Cidade}|{p.Estado}|{p.Pais}" && p.GeoStatus != "manual")
            { p.Lat = null; p.Lon = null; p.GeoStatus = "pendente"; p.GeoFonte = ""; r.Avisos.Add((l.Numero, "Endereço alterado: a localização será consultada novamente.")); }

            nomesUsados.Add($"{p.ClienteId}|{DocEngine.Norm(p.Nome)}");
            plantasParaSalvar.Add(p);
            if (ehAtualizacao) r.Atualizadas++; else r.Importadas++;
        }

        if (clientesParaSalvar.Count > 0) _db.Clientes.SalvarVarios(clientesParaSalvar.Values, ator.Login);
        if (plantasParaSalvar.Count > 0) _db.Plantas.SalvarVarios(plantasParaSalvar, ator.Login);
        _svc.Auditar(ator, "importacao.plantas", "planta", "",
            $"Importação: {r.Importadas} criada(s), {r.Atualizadas} atualizada(s), {r.Ignoradas} ignorada(s), {r.Rejeitadas.Count} rejeitada(s), {r.ClientesCriados} cliente(s) novo(s)");
        return r;
    }

    public static string ErrosCsvTexto(ResultadoImport r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Linha;Cliente;Planta;Motivo");
        static string Q(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
        foreach (var e in r.Rejeitadas) sb.AppendLine(e.linha + ";" + Q(e.cliente) + ";" + Q(e.planta) + ";" + Q(e.motivo));
        return sb.ToString();
    }
}
