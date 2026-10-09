using ControleTecnico.Models;

namespace ControleTecnico.Logic;

/// <summary>Um bloco da agenda de UM técnico. Não é gravado: é calculado a partir de
/// trechos, atendimentos e indisponibilidades — a fonte única que mantém mapa,
/// agenda e ficha sincronizados sem lançamentos duplicados.</summary>
public sealed record Evento
{
    public string Id { get; init; } = "";
    /// <summary>trecho | atend | ind</summary>
    public string Fonte { get; init; } = "";
    public string FonteId { get; init; } = "";
    public string TecnicoId { get; init; } = "";
    public string Tipo { get; init; } = "";
    public DateTime Ini { get; init; }
    public DateTime Fim { get; init; }
    /// <summary>provisorio | planejado | confirmado | cancelado</summary>
    public string Status { get; init; } = "confirmado";
    public string ViagemId { get; init; } = "";
    public string PlantaId { get; init; } = "";
    public string ClienteId { get; init; } = "";
    public string Titulo { get; init; } = "";
    public string LocalIni { get; init; } = "";
    public string LocalFim { get; init; } = "";
    public double? LatIni { get; init; }
    public double? LonIni { get; init; }
    public double? LatFim { get; init; }
    public double? LonFim { get; init; }
    public string PlantaIniId { get; init; } = "";
    public string PlantaFimId { get; init; } = "";
    public string Fuso { get; init; } = "";
    public string Obs { get; init; } = "";
    public bool Bloqueia => Status is "planejado" or "confirmado";
    public bool Provisorio => Status == "provisorio";
    public bool EhViagem => Tipo is Vocab.EvIda or Vocab.EvRetorno or Vocab.EvEntre;
    public bool EhIndisp => Tipo is Vocab.EvFerias or Vocab.EvFolga or Vocab.EvAfastamento or Vocab.EvBloqueio;
    public TimeSpan Duracao => Fim - Ini;
    public bool Cruza(DateTime ini, DateTime fim) => Ini < fim && Fim > ini;
}

public sealed record Conflito(string TecnicoId, Evento A, Evento B, string Tipo, string Severidade, string Motivo)
{
    public bool Erro => Severidade == "erro";
}

public sealed record Estado
{
    public Tecnico Tecnico { get; init; } = new();
    public string Status { get; init; } = Vocab.OpSemProg;
    public Evento? Atual { get; init; }
    public Evento? Provisorio { get; init; }
    public Evento? Proximo { get; init; }
    public Evento? Anterior { get; init; }
    public string LocalTexto { get; init; } = "";
    /// <summary>atendimento | viagem | ultimo | nenhum</summary>
    public string LocalFonte { get; init; } = "nenhum";
    public Planta? Planta { get; init; }
    public double? Lat { get; init; }
    public double? Lon { get; init; }
    public ConfirmacaoLocal? Confirmacao { get; init; }
    /// <summary>Confirmação recente e posterior ao último evento: vale como "localização confirmada".</summary>
    public bool ConfirmacaoVigente { get; init; }
    public bool ForaJornada { get; init; }
    public bool AgendaMantida { get; init; }
    public string Motivo { get; init; } = "";
    public bool TemLocalMapa => Lat.HasValue && Lon.HasValue;
}

public sealed record Livre(DateTime Dia, string Situacao);

public static class AgendaEngine
{
    // ------------------------------------------------------------------ eventos
    public static string StatusEvento(string statusViagem) => statusViagem switch
    {
        Vocab.ViagemRascunho => "provisorio",
        Vocab.ViagemPlanejada => "planejado",
        Vocab.ViagemCancelada => "cancelado",
        _ => "confirmado",
    };

    public static (DateTime? ini, DateTime? fim) Janela(Trecho t)
    {
        var ini = t.PartidaReal ?? t.PartidaPrev;
        var fim = t.ChegadaReal ?? t.ChegadaPrev;
        var dur = t.DuracaoMin is > 0 ? TimeSpan.FromMinutes(t.DuracaoMin.Value) : (TimeSpan?)null;
        if (ini is null && fim is not null && dur is not null) ini = fim - dur;
        if (fim is null && ini is not null && dur is not null) fim = ini + dur;
        return (ini, fim);
    }

    public static List<Evento> Eventos(Snapshot s, bool incluirCanceladas = false, string? tecnicoId = null)
    {
        var lista = new List<Evento>();

        foreach (var t in s.Trechos)
        {
            if (!s.Viagens.TryGetValue(t.ViagemId, out var v)) continue;
            var st = StatusEvento(v.Status);
            if (st == "cancelado" && !incluirCanceladas) continue;
            var (ini, fim) = Janela(t);
            if (ini is null || fim is null || fim <= ini) continue;
            var tecs = Snapshot.Ids(t.TecnicoIds);
            if (tecs.Length == 0) tecs = Snapshot.Ids(v.TecnicoIds);
            var tipo = t.Tipo switch { Vocab.TrechoRetorno => Vocab.EvRetorno, Vocab.TrechoEntre => Vocab.EvEntre, _ => Vocab.EvIda };
            var pDest = s.Planta(t.DestPlantaId);
            var pOri = s.Planta(t.OrigemPlantaId);
            foreach (var tec in tecs)
            {
                if (tecnicoId is not null && tec != tecnicoId) continue;
                lista.Add(new Evento
                {
                    Id = $"t:{t.Id}:{tec}", Fonte = "trecho", FonteId = t.Id, TecnicoId = tec, Tipo = tipo,
                    Ini = ini.Value, Fim = fim.Value, Status = st, ViagemId = v.Id,
                    PlantaId = t.DestPlantaId, ClienteId = pDest?.ClienteId ?? v.ClienteId,
                    Titulo = $"{(t.Modal == Vocab.ModalAviao ? "Voo" : "Viagem")} {t.Origem} → {t.Destino}",
                    LocalIni = t.Origem, LocalFim = t.Destino,
                    LatIni = t.OrigemLat ?? pOri?.Lat, LonIni = t.OrigemLon ?? pOri?.Lon,
                    LatFim = t.DestLat ?? pDest?.Lat, LonFim = t.DestLon ?? pDest?.Lon,
                    PlantaIniId = t.OrigemPlantaId, PlantaFimId = t.DestPlantaId,
                    Fuso = !string.IsNullOrEmpty(t.FusoDestino) ? t.FusoDestino : (pDest?.FusoHorario ?? ""),
                    Obs = t.Obs,
                });
            }
        }

        foreach (var a in s.Atendimentos)
        {
            string st; Viagem? v = null;
            if (!string.IsNullOrEmpty(a.ViagemId))
            {
                if (!s.Viagens.TryGetValue(a.ViagemId, out v)) continue;
                st = StatusEvento(v.Status);
            }
            else st = StatusEvento(a.Status);
            if (st == "cancelado" && !incluirCanceladas) continue;
            var ini = a.IniReal ?? a.IniPrev;
            var fim = a.FimReal ?? a.FimPrev;
            if (fim <= ini) continue;
            var p = s.Planta(a.PlantaId);
            var tecs = Snapshot.Ids(a.TecnicoIds);
            if (tecs.Length == 0 && v is not null) tecs = Snapshot.Ids(v.TecnicoIds);
            var serv = s.Servico(string.IsNullOrEmpty(a.ServicoId) ? v?.ServicoId : a.ServicoId)?.Nome ?? "Atendimento";
            foreach (var tec in tecs)
            {
                if (tecnicoId is not null && tec != tecnicoId) continue;
                lista.Add(new Evento
                {
                    Id = $"a:{a.Id}:{tec}", Fonte = "atend", FonteId = a.Id, TecnicoId = tec, Tipo = Vocab.EvAtendimento,
                    Ini = ini, Fim = fim, Status = st, ViagemId = a.ViagemId, PlantaId = a.PlantaId,
                    ClienteId = string.IsNullOrEmpty(a.ClienteId) ? (p?.ClienteId ?? "") : a.ClienteId,
                    Titulo = serv, LocalIni = s.NomePlanta(a.PlantaId), LocalFim = s.NomePlanta(a.PlantaId),
                    LatIni = p?.Lat, LonIni = p?.Lon, LatFim = p?.Lat, LonFim = p?.Lon,
                    PlantaIniId = a.PlantaId, PlantaFimId = a.PlantaId,
                    Fuso = p?.FusoHorario ?? "", Obs = a.Obs,
                });
            }
        }

        foreach (var i in s.Indisp)
        {
            if (tecnicoId is not null && i.TecnicoId != tecnicoId) continue;
            if (i.Fim <= i.Ini) continue;
            var tipo = i.Tipo switch { Vocab.IndFerias => Vocab.EvFerias, Vocab.IndFolga => Vocab.EvFolga, Vocab.IndAfastamento => Vocab.EvAfastamento, _ => Vocab.EvBloqueio };
            lista.Add(new Evento
            {
                Id = $"i:{i.Id}", Fonte = "ind", FonteId = i.Id, TecnicoId = i.TecnicoId, Tipo = tipo, Ini = i.Ini, Fim = i.Fim,
                Status = i.Provisoria ? "provisorio" : "confirmado",
                Titulo = Vocab.Get(Vocab.TiposEvento, tipo).Label + (string.IsNullOrWhiteSpace(i.Motivo) ? "" : $" — {i.Motivo}"),
                Obs = i.Motivo,
                Fuso = s.Tecnico(i.TecnicoId)?.FusoHorario ?? "",
            });
        }

        return lista.OrderBy(e => e.Ini).ThenBy(e => e.Fim).ToList();
    }

    // ---------------------------------------------------------------- conflitos
    public static List<Conflito> Conflitos(Snapshot s, IReadOnlyList<Evento> eventos, string? tecnicoId = null)
    {
        var res = new List<Conflito>();
        foreach (var grupo in eventos.Where(e => e.Status != "cancelado" && (tecnicoId is null || e.TecnicoId == tecnicoId))
                                     .GroupBy(e => e.TecnicoId))
        {
            var ev = grupo.OrderBy(e => e.Ini).ThenBy(e => e.Fim).ToList();
            for (var i = 0; i < ev.Count; i++)
            {
                for (var j = i + 1; j < ev.Count && ev[j].Ini < ev[i].Fim; j++)
                {
                    var a = ev[i]; var b = ev[j];
                    var provisorio = a.Provisorio || b.Provisorio;
                    res.Add(new Conflito(grupo.Key, a, b, "sobreposicao", provisorio ? "aviso" : "erro",
                        $"{Vocab.Get(Vocab.TiposEvento, a.Tipo).Label} e {Vocab.Get(Vocab.TiposEvento, b.Tipo).Label} " +
                        $"se sobrepõem em {Tempo.Hora(Max(a.Ini, b.Ini), s.FusoPadrao)} – {Tempo.Hora(Min(a.Fim, b.Fim), s.FusoPadrao)}" +
                        (provisorio ? " (envolve alocação provisória)" : "")));
                }
            }

            // deslocamento necessário entre localidades diferentes
            var seq = ev.Where(e => !e.EhIndisp).ToList();
            for (var i = 0; i + 1 < seq.Count; i++)
            {
                var a = seq[i]; var b = seq[i + 1];
                if (b.Ini < a.Fim) continue;                                     // sobreposição já reportada
                if (!string.IsNullOrEmpty(a.ViagemId) && a.ViagemId == b.ViagemId) continue;   // a própria viagem liga os pontos
                if (a.EhViagem || b.EhViagem) continue;                          // viagem entre eles: itinerário explícito
                if (!MesmoLugar(a, b, fimDeA: true))
                {
                    var gap = b.Ini - a.Fim;
                    if (gap > TimeSpan.FromHours(48)) continue;
                    var dist = a.LatFim.HasValue && a.LonFim.HasValue && b.LatIni.HasValue && b.LonIni.HasValue
                        ? Geo.Haversine(a.LatFim.Value, a.LonFim.Value, b.LatIni.Value, b.LonIni.Value) : (double?)null;
                    var provisorio = a.Provisorio || b.Provisorio;
                    res.Add(new Conflito(grupo.Key, a, b, "deslocamento", "aviso",
                        $"Há troca de localidade ({Rot(a.LocalFim)} → {Rot(b.LocalIni)}) com intervalo de {Tempo.Duracao(gap)} " +
                        "e sem deslocamento programado: a duração do trajeto não foi informada, verifique a viabilidade" +
                        (dist is { } d ? $" (≈ {d:0} km em linha reta)." : ".") +
                        (provisorio ? " Envolve alocação provisória." : "")));
                }
            }
        }
        return res.OrderBy(c => c.A.Ini).ToList();
    }

    private static string Rot(string s) => string.IsNullOrWhiteSpace(s) ? "local não informado" : s;
    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;

    private static bool MesmoLugar(Evento a, Evento b, bool fimDeA)
    {
        var pa = a.PlantaFimId; var pb = b.PlantaIniId;
        if (!string.IsNullOrEmpty(pa) && !string.IsNullOrEmpty(pb)) return pa == pb;
        if (a.LatFim.HasValue && a.LonFim.HasValue && b.LatIni.HasValue && b.LonIni.HasValue)
            return Geo.Haversine(a.LatFim.Value, a.LonFim.Value, b.LatIni.Value, b.LonIni.Value) < 2;
        return string.Equals(a.LocalFim.Trim(), b.LocalIni.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Conflitos que um candidato (ainda não gravado) teria com a agenda atual.</summary>
    public static List<Conflito> ConflitosCom(Snapshot s, IReadOnlyList<Evento> existentes, IEnumerable<Evento> candidatos)
    {
        var todos = existentes.Concat(candidatos).ToList();
        var ids = candidatos.Select(c => c.Id).ToHashSet();
        return Conflitos(s, todos).Where(c => ids.Contains(c.A.Id) || ids.Contains(c.B.Id)).ToList();
    }

    // ------------------------------------------------------- estado do técnico
    public static Estado EstadoEm(Snapshot s, IReadOnlyList<Evento> eventos, Tecnico t, DateTime instante, DateTime agora)
    {
        var meus = eventos.Where(e => e.TecnicoId == t.Id && e.Status != "cancelado").OrderBy(e => e.Ini).ToList();
        var bloq = meus.Where(e => e.Bloqueia).ToList();
        var ativos = bloq.Where(e => e.Ini <= instante && instante < e.Fim).ToList();
        var prov = meus.FirstOrDefault(e => e.Provisorio && e.Ini <= instante && instante < e.Fim);
        var proximo = bloq.FirstOrDefault(e => e.Ini > instante);
        var anterior = bloq.LastOrDefault(e => e.Fim <= instante);

        var ind = ativos.FirstOrDefault(e => e.EhIndisp);
        var atend = ativos.FirstOrDefault(e => e.Tipo == Vocab.EvAtendimento);
        var viag = ativos.FirstOrDefault(e => e.EhViagem);

        string status; Evento? atual; var mantida = false;
        if (ind is not null) { status = ind.Tipo == Vocab.EvFerias ? Vocab.OpFerias : Vocab.OpIndisponivel; atual = ind; }
        else if (atend is not null) { status = Vocab.OpAtendimento; atual = atend; }
        else if (viag is not null) { status = Vocab.OpViagem; atual = viag; }
        else
        {
            atual = null;
            // Sem compromisso = disponível. "AgendaMantida" só indica se há movimento recente/futuro cadastrado (usado como aviso).
            var janela = TimeSpan.FromDays(90);
            mantida = meus.Any(e => e.Fim > instante - janela && e.Ini < instante + janela);
            status = Vocab.OpDisponivel;
        }
        var mantidaFinal = status != Vocab.OpDisponivel || mantida;

        // ---- localização prevista (derivada da agenda) ----
        string texto = ""; string fonte = "nenhum"; Planta? planta = null; double? lat = null, lon = null;
        if (status == Vocab.OpAtendimento && atual is not null)
        {
            planta = s.Planta(atual.PlantaId); texto = atual.LocalFim; fonte = "atendimento"; lat = atual.LatFim; lon = atual.LonFim;
        }
        else if (status == Vocab.OpViagem && atual is not null)
        {
            texto = $"{Rot(atual.LocalIni)} → {Rot(atual.LocalFim)}"; fonte = "viagem"; lat = atual.LatFim; lon = atual.LonFim;
            planta = s.Planta(atual.PlantaFimId);
        }
        else
        {
            // Última localização conhecida pela agenda: onde terminou o último compromisso com local.
            var ultimo = bloq.LastOrDefault(e => !e.EhIndisp && e.Fim <= instante);
            if (ultimo is not null)
            {
                texto = ultimo.LocalFim; fonte = "ultimo"; lat = ultimo.LatFim; lon = ultimo.LonFim;
                planta = s.Planta(ultimo.PlantaFimId);
            }
        }
        if (string.IsNullOrWhiteSpace(texto) && !lat.HasValue) fonte = "nenhum";

        // ---- localização confirmada ----
        ConfirmacaoLocal? conf = s.Confirmacoes.Where(c => c.TecnicoId == t.Id && c.Quando <= (instante > agora ? agora : instante))
            .OrderByDescending(c => c.Quando).FirstOrDefault();
        var vigente = false;
        var visaoAtual = Math.Abs((instante - agora).TotalMinutes) <= 5;
        if (conf is not null && visaoAtual)
        {
            var recente = agora - conf.Quando <= TimeSpan.FromHours(s.ConfirmacaoHoras);
            var inicioContexto = atual?.Ini ?? anterior?.Fim ?? DateTime.MinValue;
            vigente = recente && conf.Quando >= inicioContexto;
        }

        // ---- jornada ----
        var local = Tempo.ParaLocal(instante, t.FusoHorario);
        var dias = Snapshot.Ids(t.DiasUteis);
        var forJ = !dias.Contains(((int)local.DayOfWeek).ToString())
                   || local.TimeOfDay < Tempo.ParseHora(t.JornadaInicio, TimeSpan.FromHours(8))
                   || local.TimeOfDay >= Tempo.ParseHora(t.JornadaFim, TimeSpan.FromHours(17));

        var motivo = status switch
        {
            Vocab.OpSemProg => "Nenhum compromisso cadastrado perto desta data: não há informação para afirmar que está livre.",
            Vocab.OpDisponivel when !mantidaFinal => "Sem compromissos neste momento (sem programação cadastrada perto desta data): considerado disponível.",
            Vocab.OpDisponivel => "Sem compromissos neste momento, com agenda mantida.",
            _ => atual?.Titulo ?? "",
        };

        return new Estado
        {
            Tecnico = t, Status = status, Atual = atual, Provisorio = prov, Proximo = proximo, Anterior = anterior,
            LocalTexto = texto, LocalFonte = fonte, Planta = planta, Lat = lat, Lon = lon,
            Confirmacao = conf, ConfirmacaoVigente = vigente, ForaJornada = forJ, AgendaMantida = mantidaFinal, Motivo = motivo,
        };
    }

    // ---------------------------------------------------------- disponibilidade
    private static (DateTime ini, DateTime fim)? JanelaDia(Tecnico t, DateTime diaLocal)
    {
        var dias = Snapshot.Ids(t.DiasUteis);
        if (!dias.Contains(((int)diaLocal.DayOfWeek).ToString())) return null;
        var i = Tempo.ParaUtc(diaLocal.Date + Tempo.ParseHora(t.JornadaInicio, TimeSpan.FromHours(8)), t.FusoHorario);
        var f = Tempo.ParaUtc(diaLocal.Date + Tempo.ParseHora(t.JornadaFim, TimeSpan.FromHours(17)), t.FusoHorario);
        return f > i ? (i, f) : null;
    }

    /// <summary>Situação de cada dia: livre | parcial | ocupado | provisorio | indisponivel | naoutil.</summary>
    public static List<Livre> Dias(Snapshot s, IReadOnlyList<Evento> eventos, Tecnico t, DateTime deLocal, DateTime ateLocal)
    {
        var meus = eventos.Where(e => e.TecnicoId == t.Id && e.Status != "cancelado").ToList();
        var res = new List<Livre>();
        for (var d = deLocal.Date; d <= ateLocal.Date; d = d.AddDays(1))
        {
            var j = JanelaDia(t, d);
            if (j is null) { res.Add(new Livre(d, "naoutil")); continue; }
            var (ini, fim) = j.Value;
            var dentro = meus.Where(e => e.Cruza(ini, fim)).ToList();
            var bloq = dentro.Where(e => e.Bloqueia).ToList();
            if (bloq.Any(e => e.EhIndisp && e.Ini <= ini && e.Fim >= fim)) { res.Add(new Livre(d, "indisponivel")); continue; }
            // cobertura do dia pelos blocos que bloqueiam
            var cobertos = Cobertura(bloq, ini, fim);
            if (cobertos >= (fim - ini) - TimeSpan.FromMinutes(1)) res.Add(new Livre(d, "ocupado"));
            else if (cobertos > TimeSpan.Zero) res.Add(new Livre(d, "parcial"));
            else if (dentro.Any(e => e.Provisorio)) res.Add(new Livre(d, "provisorio"));
            else res.Add(new Livre(d, "livre"));
        }
        return res;
    }

    private static TimeSpan Cobertura(List<Evento> bloq, DateTime ini, DateTime fim)
    {
        var total = TimeSpan.Zero; var cursor = ini;
        foreach (var e in bloq.OrderBy(e => e.Ini))
        {
            var a = e.Ini < cursor ? cursor : e.Ini;
            var b = e.Fim > fim ? fim : e.Fim;
            if (b > a) { total += b - a; cursor = b; }
        }
        return total;
    }

    /// <summary>Próximo período em que o técnico está inteiramente livre na jornada
    /// (dias úteis consecutivos até o próximo compromisso que bloqueia).</summary>
    public static (DateTime ini, DateTime? fim)? ProximoLivre(Snapshot s, IReadOnlyList<Evento> eventos, Tecnico t, DateTime aPartirUtc, int horizonteDias = 240)
    {
        var inicioLocal = Tempo.ParaLocal(aPartirUtc, t.FusoHorario).Date;
        var dias = Dias(s, eventos, t, inicioLocal, inicioLocal.AddDays(horizonteDias));
        DateTime? ini = null; DateTime? ultimo = null;
        foreach (var d in dias)
        {
            if (d.Situacao is "livre" or "provisorio")
            {
                ini ??= d.Dia; ultimo = d.Dia;
            }
            else if (d.Situacao == "naoutil") continue;
            else if (ini is not null) break;
        }
        if (ini is null) return null;
        var fimAberto = ultimo!.Value >= inicioLocal.AddDays(horizonteDias).AddDays(-1);
        return (ini.Value, fimAberto ? null : ultimo);
    }
}
