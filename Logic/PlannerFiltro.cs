using System.Text.Json;
using ControleTecnico.Models;

namespace ControleTecnico.Logic;

/// <summary>Filtros combináveis do Planner. O mesmo filtro vale para todas as abas (dashboard, lista, Kanban,
/// cronograma, calendário, equipe e relatórios), então todas mostram os mesmos registros.</summary>
public sealed class PlFiltro
{
    public string Busca { get; set; } = "";
    public string ClienteId { get; set; } = "";
    public string PlantaId { get; set; } = "";
    public string Responsavel { get; set; } = "";
    public string Status { get; set; } = "";
    public string Prioridade { get; set; } = "";
    public string Tipo { get; set; } = "";
    public DateTime? De { get; set; }
    public DateTime? Ate { get; set; }
    public bool Atrasados { get; set; }
    public bool Vencendo { get; set; }
    public bool Meus { get; set; }
    public bool AguardandoMateriais { get; set; }
    /// <summary>Por padrão concluídos e cancelados ficam de fora das telas de acompanhamento.</summary>
    public bool Encerrados { get; set; }

    public PlFiltro Copia() => (PlFiltro)MemberwiseClone();
    public string Serializar() => JsonSerializer.Serialize(this);
    public static PlFiltro Ler(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<PlFiltro>(json) ?? new(); } catch { return new(); }
    }

    public bool Vazio => Chips(null).Count == 0;

    /// <summary>Visões prontas (além das salvas por cada pessoa).</summary>
    public static readonly (string Id, string Nome, Func<DateTime, PlFiltro> Criar)[] Prontas =
    {
        ("meus", "Meus projetos", _ => new PlFiltro { Meus = true }),
        ("criticos", "Críticos", _ => new PlFiltro { Prioridade = "critica" }),
        ("atrasados", "Atrasados", _ => new PlFiltro { Atrasados = true }),
        ("mes", "Deste mês", h => new PlFiltro { De = new DateTime(h.Year, h.Month, 1), Ate = new DateTime(h.Year, h.Month, 1).AddMonths(1).AddDays(-1) }),
        ("materiais", "Aguardando materiais", _ => new PlFiltro { AguardandoMateriais = true }),
    };

    public bool AguardaMateriais(PlDados d, PlProjeto p) =>
        p.Status == PlVocab.PMateriais || d.Materiais.Any(m => m.ProjetoId == p.Id && !PlVocab.MaterialOk(m.Status) && (m.AtividadeId == "" || d.Atividades.Any(a => a.Id == m.AtividadeId && !PlVocab.AtivEncerrada(a.Status))));

    /// <summary>Projetos que passam no filtro.</summary>
    public List<PlProjeto> Projetos(PlDados d, string? eu)
    {
        var hoje = d.Hoje;
        return d.Projetos.Where(p =>
        {
            if (!Encerrados && Status == "" && PlVocab.Encerrado(p.Status)) return false;
            if (Status != "" && p.Status != Status) return false;
            if (Prioridade != "" && p.Prioridade != Prioridade) return false;
            if (Tipo != "" && p.Tipo != Tipo) return false;
            if (ClienteId != "" && p.ClienteId != ClienteId) return false;
            if (PlantaId != "" && p.PlantaId != PlantaId) return false;
            if (Responsavel != "" && !Envolve(d, p, Responsavel)) return false;
            if (Meus && (eu is null || !Envolve(d, p, eu))) return false;
            if (Atrasados && !PlannerEngine.Atrasado(p, hoje) && !d.AtivDe(p.Id).Any(a => PlannerEngine.Atrasada(a, hoje))) return false;
            if (Vencendo && !PlannerEngine.VenceEm(p, hoje, 7)) return false;
            if (AguardandoMateriais && !AguardaMateriais(d, p)) return false;
            if (De is { } de && (p.FimReal ?? p.FimPrev) is { } fim && fim.Date < de.Date) return false;
            if (Ate is { } ate && (p.IniReal ?? p.IniPrev) is { } ini && ini.Date > ate.Date) return false;
            if (!string.IsNullOrWhiteSpace(Busca))
            {
                var texto = $"{p.Codigo} {p.Nome} {p.Descricao} {p.Area} {p.Equipamento} {d.NomeUnidade(p)} {d.Snap.Cliente(p.ClienteId)?.Nome} {d.NomePessoa(p.Responsavel)} "
                    + string.Join(" ", d.AtivDe(p.Id).Select(a => a.Nome));
                if (!DocEngine.Norm(texto).Contains(DocEngine.Norm(Busca))) return false;
            }
            return true;
        }).ToList();
    }

    /// <summary>Atividades dos projetos filtrados (com responsável, se filtrado, e sem as encerradas quando pedido).</summary>
    public List<PlAtividade> Atividades(PlDados d, IEnumerable<PlProjeto> projetos)
    {
        var ids = projetos.Select(p => p.Id).ToHashSet();
        return d.Atividades.Where(a => ids.Contains(a.ProjetoId) && (Responsavel == "" || a.Responsavel == Responsavel)
            && (!Atrasados || PlannerEngine.Atrasada(a, d.Hoje) || PlannerEngine.Atrasado(d.Projeto(a.ProjetoId)!, d.Hoje))).ToList();
    }

    private static bool Envolve(PlDados d, PlProjeto p, string pessoa) =>
        p.Responsavel == pessoa || Snapshot.Ids(p.Equipe).Contains(pessoa) || d.AtivDe(p.Id).Any(a => a.Responsavel == pessoa);

    /// <summary>Filtros ativos em texto (chave para remover, rótulo).</summary>
    public List<(string Chave, string Rotulo)> Chips(PlDados? d)
    {
        var r = new List<(string, string)>();
        if (!string.IsNullOrWhiteSpace(Busca)) r.Add(("busca", $"Busca: {Busca}"));
        if (ClienteId != "") r.Add(("cliente", "Cliente: " + (d?.Snap.Cliente(ClienteId)?.Nome ?? "?")));
        if (PlantaId != "") r.Add(("planta", "Unidade: " + (d?.Snap.NomePlanta(PlantaId) ?? "?")));
        if (Responsavel != "") r.Add(("resp", "Responsável: " + (d?.NomePessoa(Responsavel) ?? "?")));
        if (Status != "") r.Add(("status", "Status: " + PlVocab.Get(PlVocab.StatusProjeto, Status).Label));
        if (Prioridade != "") r.Add(("prio", "Prioridade: " + PlVocab.Get(PlVocab.Prioridades, Prioridade).Label));
        if (Tipo != "") r.Add(("tipo", "Tipo: " + PlVocab.TipoLabel(Tipo)));
        if (De is not null || Ate is not null) r.Add(("periodo", $"Período: {De:dd/MM/yy} – {Ate:dd/MM/yy}"));
        if (Atrasados) r.Add(("atrasados", "Somente atrasados"));
        if (Vencendo) r.Add(("vencendo", "Vencendo em 7 dias"));
        if (Meus) r.Add(("meus", "Meus projetos"));
        if (AguardandoMateriais) r.Add(("materiais", "Aguardando materiais"));
        if (Encerrados) r.Add(("encerrados", "Inclui concluídos/cancelados"));
        return r;
    }

    public void Remover(string chave)
    {
        switch (chave)
        {
            case "busca": Busca = ""; break;
            case "cliente": ClienteId = ""; break;
            case "planta": PlantaId = ""; break;
            case "resp": Responsavel = ""; break;
            case "status": Status = ""; break;
            case "prio": Prioridade = ""; break;
            case "tipo": Tipo = ""; break;
            case "periodo": De = Ate = null; break;
            case "atrasados": Atrasados = false; break;
            case "vencendo": Vencendo = false; break;
            case "meus": Meus = false; break;
            case "materiais": AguardandoMateriais = false; break;
            case "encerrados": Encerrados = false; break;
        }
    }
}
