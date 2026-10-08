using System.Globalization;
using ClosedXML.Excel;
using ControleTecnico.Data;
using ControleTecnico.Models;

namespace ControleTecnico.Logic;

/// <summary>Importação em massa de técnicos (.xlsx/.csv), no mesmo fluxo da importação de plantas:
/// modelo → arquivo → mapeamento → prévia com validação por linha → criar/atualizar/ignorar → resultado.
/// A leitura do arquivo e as classes de linha/resultado são as mesmas do <see cref="Importador"/>.</summary>
public sealed class ImportadorTecnicos
{
    private readonly Db _db; private readonly Servicos _svc;
    public ImportadorTecnicos(Db db, Servicos svc) { _db = db; _svc = svc; }

    public static readonly CampoImport[] Campos =
    {
        new("nome", "Nome", true, new[] { "nome", "tecnico", "técnico", "nome do tecnico", "name" }, "Carlos Mendes"),
        new("cidade", "Cidade onde mora", true, new[] { "cidade", "cidade onde mora", "municipio", "city" }, "São Paulo"),
        new("matricula", "Identificador interno", false, new[] { "identificador interno", "matricula", "id", "codigo", "chapa" }, "T-001"),
        new("estado", "Estado", false, new[] { "estado", "uf", "state" }, "SP"),
        new("pais", "País", false, new[] { "pais", "country" }, "Brasil"),
        new("telefone", "Telefone", false, new[] { "telefone", "celular", "fone", "phone" }, "+55 11 95555-0001"),
        new("email", "E-mail", false, new[] { "email", "e-mail" }, "carlos@empresa.com"),
        new("origem", "Origem habitual para viagens", false, new[] { "origem habitual para viagens", "origem habitual", "origem" }, "São Paulo/SP"),
        new("fuso", "Fuso horário", false, new[] { "fuso", "fuso horario", "timezone", "tz" }, "America/Sao_Paulo"),
        new("especialidades", "Especialidades (separadas por ; ou ,)", false, new[] { "especialidades", "especialidade" }, "Vibração e balanceamento; Mecânica de rotativos"),
        new("servicos", "Serviços (separados por ; ou ,)", false, new[] { "servicos", "serviços", "servico" }, "Manutenção preventiva; Partida assistida"),
        new("jornada_ini", "Jornada — início", false, new[] { "jornada inicio", "jornada - inicio", "jornada — início", "inicio da jornada", "entrada" }, "08:00"),
        new("jornada_fim", "Jornada — fim", false, new[] { "jornada fim", "jornada - fim", "jornada — fim", "fim da jornada", "saida" }, "17:00"),
        new("dias", "Dias úteis", false, new[] { "dias uteis", "dias úteis", "dias" }, "seg-sex"),
        new("ativo", "Ativo (sim/não)", false, new[] { "ativo", "situacao", "status" }, "sim"),
        new("obs", "Observações", false, new[] { "observacoes", "obs", "notes" }, ""),
    };

    public static byte[] Modelo()
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Técnicos");
        for (var i = 0; i < Campos.Length; i++)
        {
            var c = ws.Cell(1, i + 1); c.Value = Campos[i].Label; c.Style.Font.Bold = true; c.Style.Font.FontColor = XLColor.White;
            c.Style.Fill.BackgroundColor = Campos[i].Obrigatorio ? XLColor.FromHtml("#004785") : XLColor.FromHtml("#6b7a90");
            ws.Cell(2, i + 1).SetValue(Campos[i].Exemplo).Style.NumberFormat.Format = "@";
        }
        ws.Columns().AdjustToContents();
        var info = wb.AddWorksheet("Instruções");
        var linhas = new[]
        {
            "Uma linha por técnico. Obrigatórios (azul): Nome e Cidade onde mora. As demais colunas (cinza) são opcionais.",
            "Especialidades e serviços: separe por ponto e vírgula. Nomes que ainda não existem serão CRIADOS (a prévia avisa).",
            "Dias úteis: 'seg-sex', 'seg,ter,qua' ou números 0-6 (0 = domingo). Vazio = segunda a sexta.",
            "Jornada no formato HH:mm (vazio = 08:00 às 17:00). Fuso no formato IANA (ex.: America/Sao_Paulo); vazio = deduzido do país.",
            "Técnico já cadastrado é reconhecido pelo Identificador interno ou, sem ele, pelo nome. Na prévia você escolhe atualizar, ignorar ou criar novo.",
            "Apague a linha de exemplo (linha 2) antes de importar.",
        };
        for (var i = 0; i < linhas.Length; i++) info.Cell(i + 1, 1).Value = linhas[i];
        info.Column(1).Width = 120;
        using var ms = new MemoryStream(); wb.SaveAs(ms); return ms.ToArray();
    }

    public static Dictionary<string, int> SugerirMapeamento(List<string> cab)
    {
        var map = new Dictionary<string, int>(); var usados = new HashSet<int>();
        foreach (var c in Campos)
            for (var i = 0; i < cab.Count; i++)
            {
                if (usados.Contains(i)) continue;
                var h = DocEngine.Norm(cab[i]);
                if (h == DocEngine.Norm(c.Label) || c.Sinonimos.Any(s => DocEngine.Norm(s) == h)) { map[c.Key] = i; usados.Add(i); break; }
            }
        return map;
    }

    private static readonly Dictionary<string, int> NomesDias = new()
    {
        ["dom"] = 0, ["domingo"] = 0, ["seg"] = 1, ["segunda"] = 1, ["ter"] = 2, ["terca"] = 2, ["qua"] = 3, ["quarta"] = 3,
        ["qui"] = 4, ["quinta"] = 4, ["sex"] = 5, ["sexta"] = 5, ["sab"] = 6, ["sabado"] = 6,
    };

    /// <summary>"seg-sex", "seg,qua,sex", "1,2,3" → conjunto de dias (0=dom). null = inválido.</summary>
    public static HashSet<int>? ParseDias(string texto)
    {
        var res = new HashSet<int>();
        foreach (var parte in texto.Split(new[] { ',', ';', '/', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var p = DocEngine.Norm(parte.Replace('–', '-').Replace('—', '-'));
            int Dia(string x) => int.TryParse(x, out var n) && n is >= 0 and <= 6 ? n : NomesDias.GetValueOrDefault(x.TrimEnd('.'), -1);
            var ab = p.Split('-', StringSplitOptions.RemoveEmptyEntries);
            if (ab.Length == 2)
            {
                int a = Dia(ab[0]), b = Dia(ab[1]); if (a < 0 || b < 0) return null;
                for (var d = a; ; d = (d + 1) % 7) { res.Add(d); if (d == b) break; }
            }
            else if (ab.Length == 1) { var d = Dia(ab[0]); if (d < 0) return null; res.Add(d); }
            else return null;
        }
        return res.Count == 0 ? null : res;
    }

    private static List<string> Lista(string s) =>
        s.Split(new[] { ';', '|', ',', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static bool? Bool(string s) => DocEngine.Norm(s) switch
    {
        "sim" or "s" or "true" or "1" or "ativo" or "yes" => true,
        "nao" or "n" or "false" or "0" or "inativo" or "no" => false,
        _ => null,
    };

    private static readonly Dictionary<string, string> FusoPorPais = new(StringComparer.OrdinalIgnoreCase)
    { ["brasil"] = "America/Sao_Paulo", ["argentina"] = "America/Argentina/Buenos_Aires", ["chile"] = "America/Santiago", ["colombia"] = "America/Bogota",
      ["mexico"] = "America/Mexico_City", ["peru"] = "America/Lima", ["uruguai"] = "America/Montevideo", ["portugal"] = "Europe/Lisbon" };

    public List<LinhaImport> Analisar(ArquivoLido a, Dictionary<string, int> map)
    {
        var falta = Campos.Where(c => c.Obrigatorio && !map.ContainsKey(c.Key)).Select(c => c.Label).ToList();
        if (falta.Count > 0) throw new ValidacaoException($"Mapeie as colunas obrigatórias: {string.Join(", ", falta)}.");

        var existentes = _db.Tecnicos.Todos();
        var porMat = existentes.Where(t => t.Matricula != "").GroupBy(t => DocEngine.Norm(t.Matricula)).ToDictionary(g => g.Key, g => g.First());
        var porNome = existentes.GroupBy(t => DocEngine.Norm(t.Nome)).ToDictionary(g => g.Key, g => g.First());
        var esp = _db.Especialidades.Todos().Select(e => DocEngine.Norm(e.Nome)).ToHashSet();
        var srv = _db.Servicos.Todos().Select(e => DocEngine.Norm(e.Nome)).ToHashSet();
        var vistosMat = new Dictionary<string, int>(); var vistosNome = new Dictionary<string, int>();
        var res = new List<LinhaImport>();

        for (var i = 0; i < a.Linhas.Count; i++)
        {
            var l = new LinhaImport { Numero = a.Numeros[i] };
            foreach (var c in Campos) l.V[c.Key] = map.TryGetValue(c.Key, out var col) && col < a.Linhas[i].Length ? a.Linhas[i][col].Trim() : "";
            // reaproveita as propriedades de exibição da prévia (Cliente/Planta) para nome/cidade
            l.V["cliente"] = l.V["nome"]; l.V["planta"] = l.V["matricula"]; l.V["estado"] = l.V["estado"]; l.V["pais"] = l.V["pais"];

            if (l.V["nome"] == "") l.Erros.Add("Nome não informado.");
            if (l.V["cidade"] == "") l.Erros.Add("Cidade onde mora não informada.");
            if (l.V["email"] != "" && !l.V["email"].Contains('@')) l.Erros.Add("E-mail inválido.");

            if (l.V["pais"] == "") { l.V["pais"] = "Brasil"; l.Avisos.Add("País não informado: considerado \"Brasil\"."); }
            if (l.V["fuso"] == "")
            {
                if (FusoPorPais.TryGetValue(DocEngine.Norm(l.V["pais"]), out var f)) l.V["fuso"] = f;
                else { l.V["fuso"] = _db.FusoPadrao; l.Avisos.Add($"Fuso não informado para {l.V["pais"]}: usado o padrão do sistema ({_db.FusoPadrao})."); }
            }
            else if (!Tempo.FusoValido(l.V["fuso"])) l.Erros.Add($"Fuso horário \"{l.V["fuso"]}\" inválido (formato IANA, ex.: America/Sao_Paulo).");

            var ji = l.V["jornada_ini"] == "" ? "08:00" : l.V["jornada_ini"]; var jf = l.V["jornada_fim"] == "" ? "17:00" : l.V["jornada_fim"];
            if (!TimeSpan.TryParse(ji, CultureInfo.InvariantCulture, out var t1) || !TimeSpan.TryParse(jf, CultureInfo.InvariantCulture, out var t2)) l.Erros.Add("Jornada inválida (use HH:mm).");
            else if (t2 <= t1) l.Erros.Add("Jornada inválida: o fim deve ser posterior ao início.");
            else { l.V["jornada_ini"] = t1.ToString(@"hh\:mm"); l.V["jornada_fim"] = t2.ToString(@"hh\:mm"); }

            if (l.V["dias"] != "") { var d = ParseDias(l.V["dias"]); if (d is null) l.Erros.Add($"Dias úteis \"{l.V["dias"]}\" não reconhecidos (ex.: seg-sex)."); else l.V["dias_norm"] = string.Join(",", d.OrderBy(x => x)); }
            if (l.V["ativo"] != "" && Bool(l.V["ativo"]) is null) l.Erros.Add($"Valor de \"Ativo\" não reconhecido: \"{l.V["ativo"]}\" (use sim/não).");

            foreach (var e in Lista(l.V["especialidades"])) if (!esp.Contains(DocEngine.Norm(e))) l.Avisos.Add($"Especialidade nova será criada: \"{e}\".");
            foreach (var s in Lista(l.V["servicos"])) if (!srv.Contains(DocEngine.Norm(s))) l.Avisos.Add($"Serviço novo será criado: \"{s}\".");

            // duplicidades no arquivo e no cadastro
            var km = DocEngine.Norm(l.V["matricula"]); var kn = DocEngine.Norm(l.V["nome"]);
            if (km != "") { if (vistosMat.TryGetValue(km, out var pm)) l.Erros.Add($"Identificador interno repetido no arquivo (linha {pm})."); else vistosMat[km] = l.Numero; }
            if (kn != "") { if (vistosNome.TryGetValue(kn, out var pn) && km == "") l.Erros.Add($"Nome repetido no arquivo (linha {pn}); informe o identificador interno para diferenciar homônimos."); else vistosNome.TryAdd(kn, l.Numero); }
            Tecnico? ex = null;
            if (km != "" && porMat.TryGetValue(km, out var tm)) ex = tm;
            else if (km == "" && kn != "" && porNome.TryGetValue(kn, out var tn)) ex = tn;
            else if (km != "" && kn != "" && porNome.TryGetValue(kn, out var tn2) && tn2.Matricula != "" && DocEngine.Norm(tn2.Matricula) != km)
                l.Avisos.Add($"Já existe \"{tn2.Nome}\" com outro identificador ({tn2.Matricula}); será criado como novo técnico.");
            if (ex is not null) { l.PlantaExistenteId = ex.Id; if (!l.Rejeitada) Diferencas(l, ex); }

            l.Acao = l.Rejeitada ? AcaoImport.Ignorar : l.Existente ? AcaoImport.Atualizar : AcaoImport.Criar;
            if (l.Existente && !l.Rejeitada && l.Diferencas.Count == 0) { l.Avisos.Add("Idêntico ao cadastro existente: nada a atualizar."); l.Acao = AcaoImport.Ignorar; }
            res.Add(l);
        }
        return res;
    }

    private void Diferencas(LinhaImport l, Tecnico t)
    {
        void D(string campo, string atual, string novo) { if (novo != "" && !string.Equals((atual ?? "").Trim(), novo.Trim(), StringComparison.Ordinal)) l.Diferencas.Add($"{campo}: \"{(string.IsNullOrWhiteSpace(atual) ? "vazio" : atual)}\" → \"{novo}\""); }
        D("Nome", t.Nome, l.V["nome"]); D("Identificador", t.Matricula, l.V["matricula"]); D("Cidade", t.Cidade, l.V["cidade"]); D("Estado", t.Estado, l.V["estado"]);
        D("País", t.Pais, l.V["pais"]); D("Telefone", t.Telefone, l.V["telefone"]); D("E-mail", t.Email, l.V["email"]); D("Origem habitual", t.OrigemHabitual, l.V["origem"]);
        D("Fuso", t.FusoHorario, l.V["fuso"]); D("Jornada início", t.JornadaInicio, l.V["jornada_ini"]); D("Jornada fim", t.JornadaFim, l.V["jornada_fim"]);
        if (l.V.GetValueOrDefault("dias_norm", "") != "") D("Dias úteis", t.DiasUteis, l.V["dias_norm"]);
        if (l.V["especialidades"] != "")
            D("Especialidades", string.Join("; ", Snapshot.Ids(t.EspecialidadeIds).Select(id => _db.Especialidades.Obter(id)?.Nome).Where(n => n != null)), string.Join("; ", Lista(l.V["especialidades"])));
        if (l.V["servicos"] != "")
            D("Serviços", string.Join("; ", Snapshot.Ids(t.ServicoIds).Select(id => _db.Servicos.Obter(id)?.Nome).Where(n => n != null)), string.Join("; ", Lista(l.V["servicos"])));
        if (l.V["ativo"] != "" && Bool(l.V["ativo"]) is { } at && at != t.Ativo) l.Diferencas.Add($"Situação: {(t.Ativo ? "ativo" : "inativo")} → {(at ? "ativo" : "inativo")}");
        D("Observações", t.Obs, l.V["obs"]);
    }

    public ResultadoImport Aplicar(Ator ator, List<LinhaImport> linhas)
    {
        ator.Exigir(Perm.EditarTecnicos);
        var r = new ResultadoImport();
        var esp = _db.Especialidades.Todos().GroupBy(e => DocEngine.Norm(e.Nome)).ToDictionary(g => g.Key, g => g.First());
        var srv = _db.Servicos.Todos().GroupBy(e => DocEngine.Norm(e.Nome)).ToDictionary(g => g.Key, g => g.First());
        var novasEsp = new List<Especialidade>(); var novosSrv = new List<Servico>();
        string IdEsp(string nome) { var k = DocEngine.Norm(nome); if (!esp.TryGetValue(k, out var e)) { e = new Especialidade { Id = Repo<Especialidade>.NovoId(), Nome = nome }; esp[k] = e; novasEsp.Add(e); } return e.Id; }
        string IdSrv(string nome) { var k = DocEngine.Norm(nome); if (!srv.TryGetValue(k, out var e)) { e = new Servico { Id = Repo<Servico>.NovoId(), Nome = nome }; srv[k] = e; novosSrv.Add(e); } return e.Id; }
        var salvar = new List<Tecnico>();

        foreach (var l in linhas)
        {
            foreach (var av in l.Avisos) r.Avisos.Add((l.Numero, av));
            if (l.Rejeitada) { r.Rejeitadas.Add((l.Numero, l.V["nome"], "", string.Join(" ", l.Erros))); continue; }
            if (l.Acao == AcaoImport.Ignorar) { r.Ignoradas++; continue; }
            var atualiza = l.Acao == AcaoImport.Atualizar && l.PlantaExistenteId is not null;
            var t = atualiza ? _db.Tecnicos.Obter(l.PlantaExistenteId)! : new Tecnico { Id = Repo<Tecnico>.NovoId() };
            if (!atualiza && l.PlantaExistenteId is not null) r.Avisos.Add((l.Numero, "Criado como novo técnico, mantendo o cadastro anterior."));
            void S(string novo, Action<string> set, string padrao = "") { if (novo != "") set(novo); else if (!atualiza) set(padrao); }
            S(l.V["nome"], v => t.Nome = v); S(l.V["matricula"], v => t.Matricula = v); S(l.V["cidade"], v => t.Cidade = v); S(l.V["estado"], v => t.Estado = v);
            S(l.V["pais"], v => t.Pais = v, "Brasil"); S(l.V["telefone"], v => t.Telefone = v); S(l.V["email"], v => t.Email = v);
            S(l.V["fuso"], v => t.FusoHorario = v, _db.FusoPadrao); S(l.V["jornada_ini"], v => t.JornadaInicio = v, "08:00"); S(l.V["jornada_fim"], v => t.JornadaFim = v, "17:00");
            S(l.V.GetValueOrDefault("dias_norm", ""), v => t.DiasUteis = v, "1,2,3,4,5"); S(l.V["obs"], v => t.Obs = v);
            if (l.V["origem"] != "") { if (t.OrigemHabitual != l.V["origem"]) { t.OrigemLat = null; t.OrigemLon = null; } t.OrigemHabitual = l.V["origem"]; }
            else if (!atualiza || string.IsNullOrWhiteSpace(t.OrigemHabitual)) t.OrigemHabitual = $"{t.Cidade}{(string.IsNullOrWhiteSpace(t.Estado) ? "" : "/" + t.Estado)}";
            if (l.V["especialidades"] != "") t.EspecialidadeIds = Servicos.Csv(Lista(l.V["especialidades"]).Select(IdEsp));
            if (l.V["servicos"] != "") t.ServicoIds = Servicos.Csv(Lista(l.V["servicos"]).Select(IdSrv));
            if (l.V["ativo"] != "" && Bool(l.V["ativo"]) is { } at) t.Ativo = at; else if (!atualiza) t.Ativo = true;
            salvar.Add(t);
            if (atualiza) r.Atualizadas++; else r.Importadas++;
        }
        if (novasEsp.Count > 0) { _db.Especialidades.SalvarVarios(novasEsp, ator.Login); r.Avisos.Add((0, $"{novasEsp.Count} especialidade(s) criada(s): {string.Join(", ", novasEsp.Select(e => e.Nome))}.")); }
        if (novosSrv.Count > 0) { _db.Servicos.SalvarVarios(novosSrv, ator.Login); r.Avisos.Add((0, $"{novosSrv.Count} serviço(s) criado(s): {string.Join(", ", novosSrv.Select(e => e.Nome))}.")); }
        if (salvar.Count > 0) _db.Tecnicos.SalvarVarios(salvar, ator.Login);
        _svc.Auditar(ator, "importacao.tecnicos", "tecnico", "",
            $"Importação de técnicos: {r.Importadas} criado(s), {r.Atualizadas} atualizado(s), {r.Ignoradas} ignorado(s), {r.Rejeitadas.Count} rejeitado(s)");
        return r;
    }
}
