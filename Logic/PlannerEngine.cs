using System.Text.Json;
using ControleTecnico.Data;
using ControleTecnico.Models;

namespace ControleTecnico.Logic;

/// <summary>Fotografia dos dados do Planner (uma leitura para todas as telas: dashboard, Kanban, cronograma e calendário
/// mostram SEMPRE os mesmos registros e a mesma regra de atraso).</summary>
public sealed class PlDados
{
    public List<PlProjeto> Projetos { get; init; } = new();
    public List<PlAtividade> Atividades { get; init; } = new();
    public List<PlMaterial> Materiais { get; init; } = new();
    public List<PlPendencia> Pendencias { get; init; } = new();
    public List<PlComentario> Comentarios { get; init; } = new();
    public DateTime Hoje { get; init; }
    public Snapshot Snap { get; init; } = new();
    /// <summary>Pessoa → nome (técnicos e usuários).</summary>
    public Dictionary<string, string> Pessoas { get; init; } = new();

    public IEnumerable<PlAtividade> AtivDe(string projetoId) => Atividades.Where(a => a.ProjetoId == projetoId);
    public PlProjeto? Projeto(string? id) => Projetos.FirstOrDefault(p => p.Id == id);
    public string NomePessoa(string? chave) => string.IsNullOrEmpty(chave) ? "" : Pessoas.TryGetValue(chave, out var n) ? n : "(removido)";
    public string NomeUnidade(PlProjeto p) => string.IsNullOrEmpty(p.PlantaId) ? "" : Snap.NomePlanta(p.PlantaId);
}

public sealed record PlAlerta(string Chave, string Nivel, string Texto, string ProjetoId, string AtividadeId, string Link, DateTime? Desde = null);

public sealed class PlCapacidade
{
    public string Pessoa { get; init; } = "";
    public string Nome { get; init; } = "";
    public string Funcao { get; init; } = "";
    public double HorasCapacidade { get; set; }
    public double HorasPlanejadas { get; set; }
    public double HorasRealizadas { get; set; }
    public int Atividades { get; set; }
    public int Atrasadas { get; set; }
    public List<string> Projetos { get; } = new();
    public List<string> Conflitos { get; } = new();
    public bool JornadaPadrao { get; set; }
    public double Uso => HorasCapacidade <= 0 ? (HorasPlanejadas > 0 ? 999 : 0) : HorasPlanejadas / HorasCapacidade * 100;
    public string Situacao => HorasCapacidade <= 0 && HorasPlanejadas <= 0 ? "indisponivel" : Uso > 100 ? "sobrecarga" : Uso >= 80 ? "alocado" : Uso > 0 ? "parcial" : "disponivel";
}

public static class PlannerEngine
{
    public sealed class ItemChecklist { public string Texto { get; set; } = ""; public bool Feito { get; set; } public bool Obrigatorio { get; set; } public string Por { get; set; } = ""; public DateTime? Em { get; set; } }
    public sealed class EtapaModelo { public string Nome { get; set; } = ""; public int Dias { get; set; } = 1; public double? Horas { get; set; } public double Peso { get; set; } = 1; }

    private static readonly JsonSerializerOptions Js = new() { PropertyNameCaseInsensitive = true };
    public static List<ItemChecklist> LerChecklist(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<List<ItemChecklist>>(json, Js) ?? new(); } catch { return new(); }
    }
    public static string GravarChecklist(IEnumerable<ItemChecklist> itens) => JsonSerializer.Serialize(itens.ToList());
    public static List<EtapaModelo> LerEtapas(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<List<EtapaModelo>>(json, Js) ?? new(); } catch { return new(); }
    }
    public static string GravarEtapas(IEnumerable<EtapaModelo> e) => JsonSerializer.Serialize(e.ToList());

    /// <summary>Modelos que já vêm no sistema (além dos criados pela equipe).</summary>
    public static readonly (string Id, string Nome, string Tipo, EtapaModelo[] Etapas)[] ModelosPadrao =
    {
        ("std_compressor", "Revisão geral de compressor", "preventiva", new[]
        {
            E("Planejamento e levantamento técnico", 2, 8), E("Separação de ferramentas", 1, 4), E("Solicitação de materiais", 1, 2),
            E("Desmontagem do equipamento", 2, 16, 2), E("Inspeção dos componentes", 2, 12, 2), E("Execução dos reparos", 4, 32, 3),
            E("Montagem", 2, 16, 2), E("Testes operacionais", 1, 8), E("Validação e entrega", 1, 4),
        }),
        ("std_ventilador", "Manutenção de ventilador industrial", "preventiva", new[]
        {
            E("Planejamento e liberação da área (LOTO)", 1, 4), E("Inspeção de rotor, mancais e acoplamento", 1, 8), E("Balanceamento e alinhamento", 1, 8, 2),
            E("Troca de rolamentos / lubrificação", 1, 8, 2), E("Testes de vibração", 1, 4), E("Relatório técnico e entrega", 1, 4),
        }),
        ("std_parada", "Parada programada", "parada", new[]
        {
            E("Planejamento da parada e escopo", 5, 24), E("Aprovação do escopo", 2, 4), E("Compra e recebimento de materiais", 10, 8),
            E("Mobilização da equipe e terceiros", 2, 8), E("Execução das frentes de serviço", 7, 120, 4), E("Comissionamento e partida", 2, 24, 2),
            E("Desmobilização e relatório final", 2, 8),
        }),
    };
    private static EtapaModelo E(string n, int d, double h, double p = 1) => new() { Nome = n, Dias = d, Horas = h, Peso = p };

    public static PlDados Carregar(Db db, Snapshot snap)
    {
        var pessoas = new Dictionary<string, string>();
        foreach (var t in snap.Tecnicos.Values) pessoas["t:" + t.Id] = t.Nome;
        foreach (var u in db.Usuarios.Todos()) if (u.Papel != Roles.Tecnico || string.IsNullOrEmpty(u.TecnicoId)) pessoas["u:" + u.Id] = string.IsNullOrWhiteSpace(u.Nome) ? u.Id : u.Nome;
        return new PlDados
        {
            Projetos = db.PlProjetos.Todos(), Atividades = db.PlAtividades.Todos(), Materiais = db.PlMateriais.Todos(),
            Pendencias = db.PlPendencias.Todos(), Comentarios = db.PlComentarios.Todos(),
            Hoje = DocEngine.Hoje(snap.FusoPadrao).Date, Snap = snap, Pessoas = pessoas,
        };
    }

    /// <summary>Chave de pessoa de quem está logado (técnico ligado à ficha = "t:", demais = "u:").</summary>
    public static string PessoaDe(Ator a) => !string.IsNullOrEmpty(a.TecnicoId) ? "t:" + a.TecnicoId : "u:" + a.Login;

    public static DateTime Dia(DateTime d) => DateTime.SpecifyKind(d.Date, DateTimeKind.Utc);
    public static DateTime? Dia(DateTime? d) => d is { } x ? Dia(x) : null;

    // ------------------------------------------------------------------ atraso (regra única)
    /// <summary>Atividade atrasada: não encerrada e o fim previsto já passou (o dia do fim ainda conta como no prazo).</summary>
    public static bool Atrasada(PlAtividade a, DateTime hoje) => !PlVocab.AtivEncerrada(a.Status) && a.FimPrev is { } f && f.Date < hoje.Date;
    /// <summary>Projeto atrasado: ativo (não concluído, cancelado nem pausado) e o fim previsto já passou.</summary>
    public static bool Atrasado(PlProjeto p, DateTime hoje) => !PlVocab.Encerrado(p.Status) && p.Status != PlVocab.PPausado && p.FimPrev is { } f && f.Date < hoje.Date;
    public static int DiasAtraso(DateTime? fimPrev, DateTime hoje) => fimPrev is { } f && f.Date < hoje.Date ? (int)(hoje.Date - f.Date).TotalDays : 0;
    public static bool VenceEm(PlProjeto p, DateTime hoje, int dias) => !PlVocab.Encerrado(p.Status) && p.FimPrev is { } f && f.Date >= hoje.Date && f.Date <= hoje.Date.AddDays(dias);

    /// <summary>Material da atividade ainda não disponível ou pendência aberta ligada a ela.</summary>
    public static string? MotivoBloqueio(PlDados d, PlAtividade a)
    {
        if (PlVocab.AtivEncerrada(a.Status)) return null;
        var m = d.Materiais.FirstOrDefault(x => x.AtividadeId == a.Id && !PlVocab.MaterialOk(x.Status));
        if (m is not null) return $"material \"{m.Descricao}\" {PlVocab.Get(PlVocab.StatusMaterial, m.Status).Label.ToLowerInvariant()}";
        var p = d.Pendencias.FirstOrDefault(x => x.AtividadeId == a.Id && x.Situacao != "resolvida");
        if (p is not null) return $"pendência de {PlVocab.TiposPendencia.First(t => t.Key == p.Tipo).Label.ToLowerInvariant()}: {p.Descricao}";
        return a.Status == PlVocab.ABloqueada ? "marcada como bloqueada" : null;
    }

    // ------------------------------------------------------------------ progresso
    /// <summary>Progresso de uma atividade: concluída = 100; com subtarefas = média ponderada delas; senão o % informado.</summary>
    public static double ProgressoAtividade(PlAtividade a, ILookup<string, PlAtividade> filhos)
    {
        if (a.Status == PlVocab.AConcluida) return 100;
        var fs = filhos[a.Id].Where(f => f.Status != PlVocab.ACancelada).ToList();
        if (fs.Count == 0) return Math.Clamp(a.Progresso, 0, 100);
        var tot = fs.Sum(f => Peso(f)); return tot <= 0 ? 0 : fs.Sum(f => ProgressoAtividade(f, filhos) * Peso(f)) / tot;
    }
    private static double Peso(PlAtividade a) => a.Peso > 0 ? a.Peso : 1;

    /// <summary>Progresso do projeto (0–100): ponderado pelas etapas do 1º nível; sem atividades usa o % manual.</summary>
    public static int Progresso(PlProjeto p, IEnumerable<PlAtividade> atividades)
    {
        if (p.Status == PlVocab.PConcluido) return 100;
        var ats = atividades.Where(a => a.ProjetoId == p.Id).ToList();
        var filhos = ats.ToLookup(a => a.ParentId);
        var topo = ats.Where(a => (a.ParentId == "" || ats.All(x => x.Id != a.ParentId)) && a.Status != PlVocab.ACancelada).ToList();
        if (topo.Count == 0) return Math.Clamp(p.ProgressoManual ?? 0, 0, 100);
        var tot = topo.Sum(Peso);
        return (int)Math.Round(tot <= 0 ? 0 : topo.Sum(a => ProgressoAtividade(a, filhos) * Peso(a)) / tot);
    }

    /// <summary>Progresso que DEVERIA ter na data (pelo cronograma previsto): linear entre início e fim previstos de cada atividade.</summary>
    public static int ProgressoPrevisto(PlProjeto p, IEnumerable<PlAtividade> atividades, DateTime data)
    {
        static double Frac(DateTime? i, DateTime? f, DateTime d)
        {
            if (i is null || f is null) return 0;
            if (d.Date >= f.Value.Date) return 1; if (d.Date < i.Value.Date) return 0;
            var tot = (f.Value.Date - i.Value.Date).TotalDays + 1; return Math.Clamp(((d.Date - i.Value.Date).TotalDays + 1) / tot, 0, 1);
        }
        var folhas = atividades.Where(a => a.ProjetoId == p.Id && a.Status != PlVocab.ACancelada).ToList();
        var comFilhos = folhas.Select(a => a.ParentId).ToHashSet();
        folhas = folhas.Where(a => !comFilhos.Contains(a.Id)).ToList();
        if (folhas.Count == 0) return (int)Math.Round(Frac(p.IniPrev, p.FimPrev, data) * 100);
        var tot = folhas.Sum(Peso); return (int)Math.Round(tot <= 0 ? 0 : folhas.Sum(a => Frac(a.IniPrev, a.FimPrev, data) * Peso(a)) / tot * 100);
    }

    /// <summary>Período efetivo de uma etapa com subtarefas (do início mais cedo ao fim mais tarde das filhas).</summary>
    public static (DateTime? ini, DateTime? fim) Periodo(PlAtividade a, ILookup<string, PlAtividade> filhos)
    {
        var fs = filhos[a.Id].ToList();
        if (fs.Count == 0) return (a.IniPrev, a.FimPrev);
        var per = fs.Select(f => Periodo(f, filhos)).ToList();
        var ini = per.Where(x => x.ini.HasValue).Select(x => x.ini).DefaultIfEmpty(a.IniPrev).Min();
        var fim = per.Where(x => x.fim.HasValue).Select(x => x.fim).DefaultIfEmpty(a.FimPrev).Max();
        return (ini ?? a.IniPrev, fim ?? a.FimPrev);
    }

    /// <summary>Última movimentação do projeto (ele, atividades, comentários, materiais, pendências).</summary>
    public static DateTime UltimaAtualizacao(PlDados d, PlProjeto p) =>
        new[] { p.AtualizadoEm }
            .Concat(d.AtivDe(p.Id).Select(a => a.AtualizadoEm))
            .Concat(d.Comentarios.Where(c => c.ProjetoId == p.Id).Select(c => c.Quando))
            .Concat(d.Materiais.Where(m => m.ProjetoId == p.Id).Select(m => m.AtualizadoEm))
            .Concat(d.Pendencias.Where(x => x.ProjetoId == p.Id).Select(x => x.AtualizadoEm)).Max();

    // ------------------------------------------------------------------ alertas / "Atenção necessária"
    public static List<PlAlerta> Alertas(PlDados d, IEnumerable<PlProjeto> projetos, string? pessoa = null, bool soMeus = false)
    {
        var res = new List<PlAlerta>(); var hoje = d.Hoje;
        var ids = projetos.Select(p => p.Id).ToHashSet();
        string Lk(string pid, string aid = "") => $"/planner/projetos/{pid}" + (aid == "" ? "" : $"?atividade={aid}");
        foreach (var p in projetos.Where(p => !PlVocab.Encerrado(p.Status)))
        {
            var meu = pessoa is null || p.Responsavel == pessoa || Snapshot.Ids(p.Equipe).Contains(pessoa);
            if (soMeus && !meu && !d.AtivDe(p.Id).Any(a => a.Responsavel == pessoa)) continue;
            if (Atrasado(p, hoje)) res.Add(new($"patraso:{p.Id}:{p.FimPrev:yyyyMMdd}", "critico", $"Projeto {p.Codigo} · {p.Nome} atrasado há {DiasAtraso(p.FimPrev, hoje)} dia(s)", p.Id, "", Lk(p.Id), p.FimPrev));
            else if (VenceEm(p, hoje, 3)) res.Add(new($"pprazo:{p.Id}:{p.FimPrev:yyyyMMdd}", "medio", $"Projeto {p.Codigo} · {p.Nome} vence em {(int)(p.FimPrev!.Value.Date - hoje).TotalDays} dia(s)", p.Id, "", Lk(p.Id), p.FimPrev));
            var ult = UltimaAtualizacao(d, p);
            if (DateTime.UtcNow - ult > TimeSpan.FromDays(7)) res.Add(new($"psemat:{p.Id}:{ult:yyyyMMdd}", "medio", $"Projeto {p.Codigo} · {p.Nome} sem atualização há {(int)(DateTime.UtcNow - ult).TotalDays} dias", p.Id, "", Lk(p.Id), ult));
            if (string.IsNullOrEmpty(p.Responsavel)) res.Add(new($"psemresp:{p.Id}", "alto", $"Projeto {p.Codigo} · {p.Nome} sem responsável principal", p.Id, "", Lk(p.Id)));
            foreach (var a in d.AtivDe(p.Id).Where(a => !PlVocab.AtivEncerrada(a.Status)))
            {
                if (soMeus && a.Responsavel != pessoa && !meu) continue;
                if (Atrasada(a, hoje)) res.Add(new($"aatraso:{a.Id}:{a.FimPrev:yyyyMMdd}", a.Prioridade == "critica" ? "critico" : "alto", $"Atividade \"{a.Nome}\" ({p.Codigo}) atrasada há {DiasAtraso(a.FimPrev, hoje)} dia(s)", p.Id, a.Id, Lk(p.Id, a.Id), a.FimPrev));
                if (a.Prioridade == "critica" && string.IsNullOrEmpty(a.Responsavel)) res.Add(new($"acritsem:{a.Id}", "critico", $"Atividade crítica \"{a.Nome}\" ({p.Codigo}) sem responsável", p.Id, a.Id, Lk(p.Id, a.Id)));
                if (MotivoBloqueio(d, a) is { } mot && (a.IniPrev is null || a.IniPrev.Value.Date <= hoje.AddDays(7)))
                    res.Add(new($"abloq:{a.Id}", "alto", $"Atividade \"{a.Nome}\" ({p.Codigo}) bloqueada: {mot}", p.Id, a.Id, Lk(p.Id, a.Id)));
            }
            foreach (var m in d.Materiais.Where(m => m.ProjetoId == p.Id && !PlVocab.MaterialOk(m.Status) && m.DataNecessaria is { } dn && dn.Date <= hoje.AddDays(5)))
                res.Add(new($"mat:{m.Id}:{m.Status}", m.DataNecessaria!.Value.Date < hoje ? "critico" : "alto", $"Material \"{m.Descricao}\" ({p.Codigo}) necessário em {m.DataNecessaria:dd/MM} e ainda {PlVocab.Get(PlVocab.StatusMaterial, m.Status).Label.ToLowerInvariant()}", p.Id, m.AtividadeId, Lk(p.Id)));
            foreach (var x in d.Pendencias.Where(x => x.ProjetoId == p.Id && x.Situacao != "resolvida" && (x.Impacto == "critico" || (x.Prazo is { } pz && pz.Date < hoje))))
                res.Add(new($"pend:{x.Id}:{x.Situacao}", x.Impacto == "critico" ? "critico" : "alto", $"Pendência {(x.Impacto == "critico" ? "crítica" : "vencida")}: {x.Descricao} ({p.Codigo})", p.Id, x.AtividadeId, Lk(p.Id)));
        }
        // eventos recentes para a pessoa: atribuição, mudança de datas, etapa concluída
        if (pessoa is not null)
        {
            var desde = DateTime.UtcNow.AddDays(-7);
            foreach (var a in d.Atividades.Where(a => ids.Contains(a.ProjetoId)))
            {
                var p = d.Projeto(a.ProjetoId)!;
                if (a.Responsavel == pessoa && a.ResponsavelEm > desde && a.ResponsavelPor != pessoa && !PlVocab.AtivEncerrada(a.Status))
                    res.Add(new($"atrib:{a.Id}:{a.ResponsavelEm:yyyyMMddHHmm}", "info", $"Nova atividade atribuída a você: \"{a.Nome}\" ({p.Codigo})", p.Id, a.Id, Lk(p.Id, a.Id), a.ResponsavelEm));
                var envolvido = p.Responsavel == pessoa || a.Responsavel == pessoa;
                if (envolvido && a.DatasAlteradasEm > desde && a.DatasAlteradasPor != pessoa)
                    res.Add(new($"datas:{a.Id}:{a.DatasAlteradasEm:yyyyMMddHHmm}", "info", $"Cronograma alterado: \"{a.Nome}\" ({p.Codigo}) agora {a.IniPrev:dd/MM}–{a.FimPrev:dd/MM}", p.Id, a.Id, Lk(p.Id, a.Id), a.DatasAlteradasEm));
                if (p.Responsavel == pessoa && a.ParentId == "" && a.Status == PlVocab.AConcluida && a.StatusEm > desde && a.StatusPor != d.NomePessoa(pessoa))
                    res.Add(new($"etapa:{a.Id}", "info", $"Etapa concluída: \"{a.Nome}\" ({p.Codigo})", p.Id, a.Id, Lk(p.Id, a.Id), a.StatusEm));
            }
        }
        var ordem = new Dictionary<string, int> { ["critico"] = 0, ["alto"] = 1, ["medio"] = 2, ["info"] = 3 };
        return res.GroupBy(r => r.Chave).Select(g => g.First()).OrderBy(r => ordem.GetValueOrDefault(r.Nivel, 9)).ThenBy(r => r.Desde ?? DateTime.MaxValue).ToList();
    }

    /// <summary>Pontuação de atenção do projeto (ranking): atraso, prioridade, bloqueios e desvio previsto × realizado.</summary>
    public static double PontosAtencao(PlDados d, PlProjeto p)
    {
        if (PlVocab.Encerrado(p.Status)) return 0;
        var pts = DiasAtraso(p.FimPrev, d.Hoje) * 3.0 + PlVocab.PesoPrioridade(p.Prioridade) * 2;
        var ats = d.AtivDe(p.Id).ToList();
        pts += ats.Count(a => Atrasada(a, d.Hoje)) * 4 + ats.Count(a => MotivoBloqueio(d, a) is not null) * 3;
        pts += d.Pendencias.Count(x => x.ProjetoId == p.Id && x.Situacao != "resolvida" && x.Impacto is "alto" or "critico") * 3;
        var desvio = ProgressoPrevisto(p, ats, d.Hoje) - Progresso(p, ats);
        if (desvio > 0) pts += desvio / 5.0;
        if (PlVocab.EmEspera(p.Status)) pts += 2;
        return pts;
    }

    // ------------------------------------------------------------------ capacidade da equipe
    /// <summary>Capacidade x alocação no período. Técnicos usam jornada, dias úteis, indisponibilidades e feriados da ficha;
    /// compromissos da Agenda (viagens/atendimentos) também ocupam. Usuários sem ficha usam 8 h em dias úteis (jornada padrão).</summary>
    public static List<PlCapacidade> Capacidade(PlDados d, IEnumerable<PlAtividade> atividades, DateTime de, DateTime ate, IReadOnlyList<Evento>? agenda = null)
    {
        de = de.Date; ate = ate.Date;
        var feriados = Feriados.DoPeriodo(d.Snap.FeriadosCfg, de, ate);
        var ats = atividades.Where(a => !string.IsNullOrEmpty(a.Responsavel) && a.Status != PlVocab.ACancelada).ToList();
        var pessoas = ats.Select(a => a.Responsavel).Concat(d.Projetos.Where(p => !PlVocab.Encerrado(p.Status)).SelectMany(p => Snapshot.Ids(p.Equipe).Append(p.Responsavel)))
            .Where(x => !string.IsNullOrEmpty(x)).Distinct().ToList();
        var res = new List<PlCapacidade>();
        foreach (var pk in pessoas)
        {
            var tec = pk.StartsWith("t:") ? d.Snap.Tecnico(pk[2..]) : null;
            var horasDia = 8.0; var dias = new HashSet<int> { 1, 2, 3, 4, 5 };
            if (tec is not null)
            {
                if (TimeSpan.TryParse(tec.JornadaInicio, out var ji) && TimeSpan.TryParse(tec.JornadaFim, out var jf) && jf > ji) horasDia = (jf - ji).TotalHours;
                dias = Snapshot.Ids(tec.DiasUteis).Select(int.Parse).ToHashSet();
            }
            var cap = new PlCapacidade { Pessoa = pk, Nome = d.NomePessoa(pk), Funcao = tec is null ? "Usuário" : Vocab.PerfilRotulo(tec.Perfil), JornadaPadrao = tec is null };
            var diasUteis = new List<DateTime>();
            for (var dia = de; dia <= ate; dia = dia.AddDays(1))
            {
                if (!dias.Contains((int)dia.DayOfWeek) || feriados.ContainsKey(dia)) continue;
                if (tec is not null && d.Snap.Indisp.Any(i => i.TecnicoId == tec.Id && i.Ini < Tempo.ParaUtc(dia.AddDays(1), tec.FusoHorario) && i.Fim > Tempo.ParaUtc(dia, tec.FusoHorario))) continue;
                diasUteis.Add(dia);
            }
            cap.HorasCapacidade = diasUteis.Count * horasDia;
            // viagens e atendimentos da Agenda também ocupam o técnico
            if (tec is not null && agenda is not null)
                foreach (var ev in agenda.Where(e => e.TecnicoId == tec.Id && !e.EhIndisp && e.Status != "cancelado"))
                {
                    var ocup = diasUteis.Count(dia => ev.Ini < Tempo.ParaUtc(dia.AddDays(1), tec.FusoHorario) && ev.Fim > Tempo.ParaUtc(dia, tec.FusoHorario));
                    if (ocup > 0) { cap.HorasPlanejadas += ocup * horasDia; cap.Conflitos.Add($"Agenda: {ev.Titulo} ({Tempo.ParaLocal(ev.Ini, tec.FusoHorario):dd/MM}–{Tempo.ParaLocal(ev.Fim, tec.FusoHorario):dd/MM})"); }
                }
            var minhas = ats.Where(a => a.Responsavel == pk).ToList();
            foreach (var a in minhas)
            {
                if (a.IniPrev is null || a.FimPrev is null) continue;
                var ini = a.IniPrev.Value.Date; var fim = a.FimPrev.Value.Date;
                if (fim < de || ini > ate) continue;
                if (PlVocab.AtivEncerrada(a.Status)) { cap.HorasRealizadas += a.HorasReal ?? 0; continue; }
                var uteisAtiv = Enumerable.Range(0, (int)(fim - ini).TotalDays + 1).Select(i => ini.AddDays(i)).Count(x => dias.Contains((int)x.DayOfWeek) && !feriados.ContainsKey(x));
                var noPeriodo = diasUteis.Count(x => x >= ini && x <= fim);
                var horas = a.HorasEst is > 0 ? a.HorasEst.Value : uteisAtiv * horasDia;          // sem estimativa: ocupa a jornada inteira
                cap.HorasPlanejadas += uteisAtiv == 0 ? 0 : horas * noPeriodo / uteisAtiv;
                cap.HorasRealizadas += a.HorasReal ?? 0;
                cap.Atividades++; if (Atrasada(a, d.Hoje)) cap.Atrasadas++;
                var proj = d.Projeto(a.ProjetoId); if (proj is not null && !cap.Projetos.Contains(proj.Codigo)) cap.Projetos.Add(proj.Codigo);
            }
            // atividades da mesma pessoa que se sobrepõem
            var ord = minhas.Where(a => !PlVocab.AtivEncerrada(a.Status) && a.IniPrev is not null && a.FimPrev is not null && a.FimPrev.Value.Date >= de && a.IniPrev.Value.Date <= ate)
                .OrderBy(a => a.IniPrev).ToList();
            for (var i = 0; i < ord.Count; i++)
                for (var j = i + 1; j < ord.Count && ord[j].IniPrev <= ord[i].FimPrev; j++)
                    if (ord[i].HorasEst is null || ord[j].HorasEst is null)
                        cap.Conflitos.Add($"\"{ord[i].Nome}\" e \"{ord[j].Nome}\" no mesmo período ({ord[j].IniPrev:dd/MM}–{(ord[i].FimPrev < ord[j].FimPrev ? ord[i].FimPrev : ord[j].FimPrev):dd/MM})");
            res.Add(cap);
        }
        return res.OrderByDescending(c => c.Uso).ThenBy(c => c.Nome).ToList();
    }
}
