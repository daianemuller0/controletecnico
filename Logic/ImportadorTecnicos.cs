using ClosedXML.Excel;
using ControleTecnico.Data;
using ControleTecnico.Models;

namespace ControleTecnico.Logic;

/// <summary>Importação em massa de técnicos (.xlsx/.csv): SOMENTE O NOME. Cidade, contatos, especialidades, jornada etc.
/// são preenchidos depois, direto na ficha de cada técnico. Mesmo fluxo da importação de plantas:
/// modelo → arquivo → mapeamento → prévia com validação → criar/ignorar → resultado.</summary>
public sealed class ImportadorTecnicos
{
    private readonly Db _db; private readonly Servicos _svc;
    public ImportadorTecnicos(Db db, Servicos svc) { _db = db; _svc = svc; }

    public static readonly CampoImport[] Campos =
    {
        new("nome", "Nome", true, new[] { "nome", "tecnico", "técnico", "nome do tecnico", "nome completo", "name", "colaborador" }, "Carlos Mendes"),
    };

    public static byte[] Modelo()
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Técnicos");
        var c = ws.Cell(1, 1); c.Value = "Nome"; c.Style.Font.Bold = true; c.Style.Font.FontColor = XLColor.White; c.Style.Fill.BackgroundColor = XLColor.FromHtml("#004785");
        ws.Cell(2, 1).Value = "Carlos Mendes"; ws.Cell(3, 1).Value = "Fernanda Lima";
        ws.Column(1).Width = 40;
        var info = wb.AddWorksheet("Instruções");
        info.Cell(1, 1).Value = "Uma linha por técnico, só com o NOME (coluna A). Cidade, telefone, especialidades, jornada e demais dados são preenchidos depois, na ficha de cada técnico.";
        info.Cell(2, 1).Value = "Apague as linhas de exemplo (2 e 3) antes de importar. Nomes já cadastrados são reconhecidos e não duplicam.";
        info.Column(1).Width = 130;
        using var ms = new MemoryStream(); wb.SaveAs(ms); return ms.ToArray();
    }

    public static Dictionary<string, int> SugerirMapeamento(List<string> cab)
    {
        var map = new Dictionary<string, int>();
        for (var i = 0; i < cab.Count; i++)
        {
            var h = DocEngine.Norm(cab[i]);
            if (Campos[0].Sinonimos.Any(s => DocEngine.Norm(s) == h)) { map["nome"] = i; break; }
        }
        if (!map.ContainsKey("nome") && cab.Count >= 1) map["nome"] = 0;     // sem título reconhecível: a primeira coluna é o nome
        return map;
    }

    public List<LinhaImport> Analisar(ArquivoLido a, Dictionary<string, int> map)
    {
        if (!map.ContainsKey("nome")) throw new ValidacaoException("Mapeie a coluna Nome.");
        var porNome = _db.Tecnicos.Todos().GroupBy(t => DocEngine.Norm(t.Nome)).ToDictionary(g => g.Key, g => g.First());
        var vistos = new Dictionary<string, int>(); var res = new List<LinhaImport>();
        for (var i = 0; i < a.Linhas.Count; i++)
        {
            var l = new LinhaImport { Numero = a.Numeros[i] };
            var nome = map["nome"] < a.Linhas[i].Length ? string.Join(" ", a.Linhas[i][map["nome"]].Split(' ', StringSplitOptions.RemoveEmptyEntries)) : "";
            l.V["nome"] = nome; l.V["cliente"] = nome; l.V["planta"] = "";
            var k = DocEngine.Norm(nome);
            if (k == "") l.Erros.Add("Nome não informado.");
            else if (nome.Length > 120) l.Erros.Add("Nome muito longo (máx. 120 caracteres).");
            else
            {
                if (vistos.TryGetValue(k, out var pn)) l.Erros.Add($"Nome repetido no arquivo (linha {pn}).");
                else vistos[k] = l.Numero;
                if (porNome.TryGetValue(k, out var ex)) l.PlantaExistenteId = ex.Id;
            }
            // nome já cadastrado: não há mais nada para atualizar (só o nome é importado)
            l.Acao = l.Rejeitada || l.Existente ? AcaoImport.Ignorar : AcaoImport.Criar;
            if (l.Existente && !l.Rejeitada) l.Avisos.Add("Já cadastrado: ignorado. Para criar um homônimo, escolha \"Criar novo\".");
            res.Add(l);
        }
        return res;
    }

    public ResultadoImport Aplicar(Ator ator, List<LinhaImport> linhas)
    {
        ator.Exigir(Perm.EditarTecnicos);
        var r = new ResultadoImport(); var novos = new List<Tecnico>();
        foreach (var l in linhas)
        {
            if (l.Rejeitada) { r.Rejeitadas.Add((l.Numero, l.V["nome"], "", string.Join(" ", l.Erros))); continue; }
            if (l.Acao is AcaoImport.Ignorar or AcaoImport.Atualizar) { r.Ignoradas++; continue; }
            novos.Add(new Tecnico { Id = Repo<Tecnico>.NovoId(), Nome = l.V["nome"], Pais = "Brasil", FusoHorario = _db.FusoPadrao, Ativo = true });
            if (l.Existente) r.Avisos.Add((l.Numero, "Criado como homônimo de um técnico já cadastrado."));
            r.Importadas++;
        }
        if (novos.Count > 0) _db.Tecnicos.SalvarVarios(novos, ator.Login);
        _svc.Auditar(ator, "importacao.tecnicos", "tecnico", "", $"Importação de técnicos (somente nomes): {r.Importadas} criado(s), {r.Ignoradas} ignorado(s), {r.Rejeitadas.Count} rejeitado(s)");
        return r;
    }
}
