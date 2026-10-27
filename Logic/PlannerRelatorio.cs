using ClosedXML.Excel;
using ControleTecnico.Models;

namespace ControleTecnico.Logic;

/// <summary>Indicadores e exportação (Excel) do Planner, calculados sobre os mesmos registros filtrados das telas.</summary>
public static class PlannerRelatorio
{
    public sealed class Indicadores
    {
        public int Total, Ativos, Andamento, EmEspera, Atrasados, Concluidos, ConcluidosNoPrazo, Cancelados, VencendoSemana, ConcluidosMes;
        public double AtrasoMedio, ProgressoMedio, HorasEst, HorasReal;
        public int AtivTotal, AtivConcluidas, AtivAtrasadas, AtivBloqueadas, AtivSemResp, MateriaisPendentes, PendenciasAbertas;
        public double? CustoPlanejado, CustoRealizado;
        public double PctNoPrazo => Concluidos == 0 ? 0 : ConcluidosNoPrazo * 100.0 / Concluidos;
    }

    public sealed record Linha(string Chave, string Nome, int Projetos, int Ativos, int Atrasados, int Concluidos, int Atividades, int AtivAtrasadas, double HorasEst, double HorasReal);

    public static double? CustoReal(PlProjeto p)
    {
        var v = new[] { p.CustoMateriais, p.CustoMaoObra, p.CustoTerceiros, p.CustoOutros };
        return v.All(x => x is null) ? null : v.Sum(x => x ?? 0);
    }

    public static Indicadores Calcular(PlDados d, List<PlProjeto> ps, List<PlAtividade> ats)
    {
        var h = d.Hoje; var i = new Indicadores { Total = ps.Count };
        var ativos = ps.Where(p => !PlVocab.Encerrado(p.Status)).ToList();
        i.Ativos = ativos.Count;
        i.Andamento = ps.Count(p => p.Status == PlVocab.PAndamento);
        i.EmEspera = ps.Count(p => PlVocab.EmEspera(p.Status));
        i.Atrasados = ps.Count(p => PlannerEngine.Atrasado(p, h));
        i.VencendoSemana = ps.Count(p => PlannerEngine.VenceEm(p, h, 7));
        var concl = ps.Where(p => p.Status == PlVocab.PConcluido).ToList();
        i.Concluidos = concl.Count;
        i.ConcluidosNoPrazo = concl.Count(p => p.FimPrev is null || p.FimReal is null || p.FimReal.Value.Date <= p.FimPrev.Value.Date);
        i.ConcluidosMes = concl.Count(p => p.FimReal is { } f && f.Year == h.Year && f.Month == h.Month);
        i.Cancelados = ps.Count(p => p.Status == PlVocab.PCancelado);
        // atraso médio: concluídos depois do prazo (dias que passaram) + ativos atrasados (dias até hoje)
        var atrasos = concl.Where(p => p.FimPrev is not null && p.FimReal is not null && p.FimReal.Value.Date > p.FimPrev.Value.Date).Select(p => (p.FimReal!.Value.Date - p.FimPrev!.Value.Date).TotalDays)
            .Concat(ps.Where(p => PlannerEngine.Atrasado(p, h)).Select(p => (double)PlannerEngine.DiasAtraso(p.FimPrev, h))).ToList();
        i.AtrasoMedio = atrasos.Count == 0 ? 0 : atrasos.Average();
        i.ProgressoMedio = ativos.Count == 0 ? 0 : ativos.Average(p => PlannerEngine.Progresso(p, d.Atividades));
        i.AtivTotal = ats.Count(a => a.Status != PlVocab.ACancelada);
        i.AtivConcluidas = ats.Count(a => a.Status == PlVocab.AConcluida);
        i.AtivAtrasadas = ats.Count(a => PlannerEngine.Atrasada(a, h));
        i.AtivBloqueadas = ats.Count(a => PlannerEngine.MotivoBloqueio(d, a) is not null);
        i.AtivSemResp = ats.Count(a => !PlVocab.AtivEncerrada(a.Status) && string.IsNullOrEmpty(a.Responsavel));
        i.HorasEst = ats.Sum(a => a.HorasEst ?? 0); i.HorasReal = ats.Sum(a => a.HorasReal ?? 0);
        var ids = ps.Select(p => p.Id).ToHashSet();
        i.MateriaisPendentes = d.Materiais.Count(m => ids.Contains(m.ProjetoId) && !PlVocab.MaterialOk(m.Status));
        i.PendenciasAbertas = d.Pendencias.Count(x => ids.Contains(x.ProjetoId) && x.Situacao != "resolvida");
        if (ps.Any(p => p.CustoPlanejado is not null)) i.CustoPlanejado = ps.Sum(p => p.CustoPlanejado ?? 0);
        if (ps.Any(p => CustoReal(p) is not null)) i.CustoRealizado = ps.Sum(p => CustoReal(p) ?? 0);
        return i;
    }

    /// <summary>Quebra por responsável, unidade ou tipo.</summary>
    public static List<Linha> Agrupar(PlDados d, List<PlProjeto> ps, List<PlAtividade> ats, string por)
    {
        var h = d.Hoje;
        if (por == "responsavel")
        {
            var pessoas = ps.Select(p => p.Responsavel).Concat(ats.Select(a => a.Responsavel)).Distinct().ToList();
            return pessoas.Select(k =>
            {
                var meusP = ps.Where(p => p.Responsavel == k).ToList(); var minhas = ats.Where(a => a.Responsavel == k && a.Status != PlVocab.ACancelada).ToList();
                return new Linha(k, k == "" ? "(sem responsável)" : d.NomePessoa(k), meusP.Count, meusP.Count(p => !PlVocab.Encerrado(p.Status)), meusP.Count(p => PlannerEngine.Atrasado(p, h)),
                    meusP.Count(p => p.Status == PlVocab.PConcluido), minhas.Count, minhas.Count(a => PlannerEngine.Atrasada(a, h)), minhas.Sum(a => a.HorasEst ?? 0), minhas.Sum(a => a.HorasReal ?? 0));
            }).OrderByDescending(l => l.Atividades + l.Projetos).ToList();
        }
        Func<PlProjeto, (string, string)> chave = por switch
        {
            "unidade" => p => (p.PlantaId, p.PlantaId == "" ? "(sem unidade)" : d.NomeUnidade(p)),
            "cliente" => p => (p.ClienteId, d.Snap.Cliente(p.ClienteId)?.Nome ?? "(sem cliente)"),
            _ => p => (p.Tipo, PlVocab.TipoLabel(p.Tipo)),
        };
        return ps.GroupBy(chave).Select(g =>
        {
            var gIds = g.Select(p => p.Id).ToHashSet(); var gA = ats.Where(a => gIds.Contains(a.ProjetoId) && a.Status != PlVocab.ACancelada).ToList();
            return new Linha(g.Key.Item1, g.Key.Item2, g.Count(), g.Count(p => !PlVocab.Encerrado(p.Status)), g.Count(p => PlannerEngine.Atrasado(p, h)), g.Count(p => p.Status == PlVocab.PConcluido),
                gA.Count, gA.Count(a => PlannerEngine.Atrasada(a, h)), gA.Sum(a => a.HorasEst ?? 0), gA.Sum(a => a.HorasReal ?? 0));
        }).OrderByDescending(l => l.Projetos).ToList();
    }

    /// <summary>Concluídos por mês (fim real), dos últimos <paramref name="meses"/> meses.</summary>
    public static List<(DateTime Mes, int NoPrazo, int ComAtraso)> ConcluidosPorMes(PlDados d, List<PlProjeto> ps, int meses = 6)
    {
        var ini = new DateTime(d.Hoje.Year, d.Hoje.Month, 1).AddMonths(-(meses - 1));
        return Enumerable.Range(0, meses).Select(k =>
        {
            var m = ini.AddMonths(k);
            var c = ps.Where(p => p.Status == PlVocab.PConcluido && p.FimReal is { } f && f.Year == m.Year && f.Month == m.Month).ToList();
            var ok = c.Count(p => p.FimPrev is null || p.FimReal!.Value.Date <= p.FimPrev.Value.Date);
            return (m, ok, c.Count - ok);
        }).ToList();
    }

    // ------------------------------------------------------------------ Excel
    public static byte[] Excel(PlDados d, List<PlProjeto> ps, List<PlAtividade> ats, bool custos, string filtrosTexto)
    {
        using var wb = new XLWorkbook();
        var h = d.Hoje; var ids = ps.Select(p => p.Id).ToHashSet();
        var ind = Calcular(d, ps, ats);

        var wi = wb.AddWorksheet("Indicadores");
        wi.Cell(1, 1).Value = "Planner de Manutenção — indicadores"; wi.Cell(1, 1).Style.Font.Bold = true; wi.Cell(1, 1).Style.Font.FontSize = 14;
        wi.Cell(2, 1).Value = $"Gerado em {h:dd/MM/yyyy}" + (string.IsNullOrEmpty(filtrosTexto) ? " · sem filtros" : " · filtros: " + filtrosTexto);
        var linhas = new List<(string, object)>
        {
            ("Projetos no filtro", ind.Total), ("Ativos", ind.Ativos), ("Em andamento", ind.Andamento), ("Em espera (aprovação/materiais/terceiros/pausado)", ind.EmEspera),
            ("Atrasados", ind.Atrasados), ("Vencendo em 7 dias", ind.VencendoSemana), ("Concluídos", ind.Concluidos), ("Concluídos no prazo (%)", Math.Round(ind.PctNoPrazo, 1)),
            ("Atraso médio (dias)", Math.Round(ind.AtrasoMedio, 1)), ("Cancelados", ind.Cancelados), ("Progresso médio dos ativos (%)", Math.Round(ind.ProgressoMedio, 1)),
            ("Atividades", ind.AtivTotal), ("Atividades concluídas", ind.AtivConcluidas), ("Atividades atrasadas", ind.AtivAtrasadas), ("Atividades bloqueadas", ind.AtivBloqueadas),
            ("Atividades sem responsável", ind.AtivSemResp), ("Horas estimadas", Math.Round(ind.HorasEst, 1)), ("Horas realizadas", Math.Round(ind.HorasReal, 1)),
            ("Materiais pendentes", ind.MateriaisPendentes), ("Pendências abertas", ind.PendenciasAbertas),
        };
        if (custos) { linhas.Add(("Custo planejado (R$)", ind.CustoPlanejado ?? 0)); linhas.Add(("Custo realizado (R$)", ind.CustoRealizado ?? 0)); }
        var r = 4;
        foreach (var (k, v) in linhas) { wi.Cell(r, 1).Value = k; wi.Cell(r, 2).Value = XLCellValue.FromObject(v); r++; }
        wi.Columns().AdjustToContents();

        var wp = wb.AddWorksheet("Projetos");
        var cab = new List<string> { "Código", "Projeto", "Cliente", "Unidade", "Área", "Equipamento", "Tipo", "Responsável", "Status", "Prioridade", "Início previsto", "Conclusão prevista",
            "Início real", "Conclusão real", "Progresso (%)", "Previsto hoje (%)", "Dias de atraso", "Atividades", "Atividades atrasadas", "Última atualização" };
        if (custos) cab.AddRange(new[] { "Custo planejado", "Materiais", "Mão de obra", "Terceiros", "Outros", "Custo realizado" });
        Cabecalho(wp, cab);
        r = 2;
        foreach (var p in ps.OrderBy(p => p.Codigo))
        {
            var pa = d.AtivDe(p.Id).ToList(); var c = 1;
            wp.Cell(r, c++).Value = p.Codigo; wp.Cell(r, c++).Value = p.Nome; wp.Cell(r, c++).Value = d.Snap.Cliente(p.ClienteId)?.Nome ?? ""; wp.Cell(r, c++).Value = d.NomeUnidade(p);
            wp.Cell(r, c++).Value = p.Area; wp.Cell(r, c++).Value = p.Equipamento; wp.Cell(r, c++).Value = PlVocab.TipoLabel(p.Tipo); wp.Cell(r, c++).Value = d.NomePessoa(p.Responsavel);
            wp.Cell(r, c++).Value = PlVocab.Get(PlVocab.StatusProjeto, p.Status).Label; wp.Cell(r, c++).Value = PlVocab.Get(PlVocab.Prioridades, p.Prioridade).Label;
            Data(wp.Cell(r, c++), p.IniPrev); Data(wp.Cell(r, c++), p.FimPrev); Data(wp.Cell(r, c++), p.IniReal); Data(wp.Cell(r, c++), p.FimReal);
            wp.Cell(r, c++).Value = PlannerEngine.Progresso(p, pa); wp.Cell(r, c++).Value = PlVocab.Encerrado(p.Status) ? 100 : PlannerEngine.ProgressoPrevisto(p, pa, h);
            wp.Cell(r, c++).Value = PlannerEngine.Atrasado(p, h) ? PlannerEngine.DiasAtraso(p.FimPrev, h) : 0;
            wp.Cell(r, c++).Value = pa.Count; wp.Cell(r, c++).Value = pa.Count(a => PlannerEngine.Atrasada(a, h));
            wp.Cell(r, c++).Value = Tempo.ParaLocal(PlannerEngine.UltimaAtualizacao(d, p), d.Snap.FusoPadrao); wp.Cell(r, c - 1).Style.DateFormat.Format = "dd/MM/yyyy HH:mm";
            if (custos)
            {
                foreach (var v in new[] { p.CustoPlanejado, p.CustoMateriais, p.CustoMaoObra, p.CustoTerceiros, p.CustoOutros, CustoReal(p) })
                { if (v is { } x) { wp.Cell(r, c).Value = x; wp.Cell(r, c).Style.NumberFormat.Format = "#,##0.00"; } c++; }
            }
            r++;
        }
        Finalizar(wp);

        var wa = wb.AddWorksheet("Atividades");
        Cabecalho(wa, new[] { "Projeto", "Etapa / atividade", "Nível", "Responsável", "Status", "Prioridade", "Início previsto", "Fim previsto", "Início real", "Fim real",
            "Progresso (%)", "Peso", "Horas estimadas", "Horas realizadas", "Atrasada", "Dias de atraso", "Bloqueio", "Depende de", "Marco" });
        r = 2;
        var nomes = d.Atividades.ToDictionary(a => a.Id, a => a.Nome);
        foreach (var p in ps.OrderBy(p => p.Codigo))
        {
            var pa = ats.Where(a => a.ProjetoId == p.Id).ToList(); var filhos = d.AtivDe(p.Id).ToLookup(a => a.ParentId);
            foreach (var (a, nivel) in Arvore(pa))
            {
                var c = 1;
                wa.Cell(r, c++).Value = p.Codigo; wa.Cell(r, c++).Value = new string(' ', nivel * 4) + a.Nome; wa.Cell(r, c++).Value = nivel == 0 ? "Etapa" : "Subtarefa";
                wa.Cell(r, c++).Value = d.NomePessoa(a.Responsavel); wa.Cell(r, c++).Value = PlVocab.Get(PlVocab.StatusAtividade, a.Status).Label; wa.Cell(r, c++).Value = PlVocab.Get(PlVocab.Prioridades, a.Prioridade).Label;
                Data(wa.Cell(r, c++), a.IniPrev); Data(wa.Cell(r, c++), a.FimPrev); Data(wa.Cell(r, c++), a.IniReal); Data(wa.Cell(r, c++), a.FimReal);
                wa.Cell(r, c++).Value = Math.Round(PlannerEngine.ProgressoAtividade(a, filhos)); wa.Cell(r, c++).Value = a.Peso;
                if (a.HorasEst is { } he) wa.Cell(r, c).Value = he; c++;
                if (a.HorasReal is { } hr) wa.Cell(r, c).Value = hr; c++;
                wa.Cell(r, c++).Value = PlannerEngine.Atrasada(a, h) ? "Sim" : "Não"; wa.Cell(r, c++).Value = PlannerEngine.Atrasada(a, h) ? PlannerEngine.DiasAtraso(a.FimPrev, h) : 0;
                wa.Cell(r, c++).Value = PlannerEngine.MotivoBloqueio(d, a) ?? ""; wa.Cell(r, c++).Value = string.Join(", ", Snapshot.Ids(a.DependeDe).Select(x => nomes.GetValueOrDefault(x, "?")));
                wa.Cell(r, c++).Value = a.Marco ? "Sim" : "";
                r++;
            }
        }
        Finalizar(wa);

        var wm = wb.AddWorksheet("Materiais");
        Cabecalho(wm, new[] { "Projeto", "Material", "Código", "Quantidade", "Unidade", "Necessário em", "Responsável", "Status", "Atividade", "Observação" });
        r = 2;
        foreach (var m in d.Materiais.Where(m => ids.Contains(m.ProjetoId)).OrderBy(m => d.Projeto(m.ProjetoId)?.Codigo).ThenBy(m => m.DataNecessaria))
        {
            var c = 1;
            wm.Cell(r, c++).Value = d.Projeto(m.ProjetoId)?.Codigo ?? ""; wm.Cell(r, c++).Value = m.Descricao; wm.Cell(r, c++).Value = m.Codigo; wm.Cell(r, c++).Value = m.Quantidade; wm.Cell(r, c++).Value = m.Unidade;
            Data(wm.Cell(r, c++), m.DataNecessaria); wm.Cell(r, c++).Value = d.NomePessoa(m.Responsavel); wm.Cell(r, c++).Value = PlVocab.Get(PlVocab.StatusMaterial, m.Status).Label;
            wm.Cell(r, c++).Value = nomes.GetValueOrDefault(m.AtividadeId, ""); wm.Cell(r, c++).Value = m.Obs; r++;
        }
        Finalizar(wm);

        var wd = wb.AddWorksheet("Pendências");
        Cabecalho(wd, new[] { "Projeto", "Pendência", "Tipo", "Impacto", "Situação", "Responsável", "Abertura", "Prazo", "Ação", "Atividade" });
        r = 2;
        foreach (var x in d.Pendencias.Where(x => ids.Contains(x.ProjetoId)).OrderBy(x => x.Situacao == "resolvida").ThenBy(x => x.Prazo))
        {
            var c = 1;
            wd.Cell(r, c++).Value = d.Projeto(x.ProjetoId)?.Codigo ?? ""; wd.Cell(r, c++).Value = x.Descricao; wd.Cell(r, c++).Value = PlVocab.TiposPendencia.FirstOrDefault(t => t.Key == x.Tipo).Label ?? x.Tipo;
            wd.Cell(r, c++).Value = PlVocab.Get(PlVocab.Impactos, x.Impacto).Label; wd.Cell(r, c++).Value = PlVocab.Get(PlVocab.SituacoesPendencia, x.Situacao).Label; wd.Cell(r, c++).Value = d.NomePessoa(x.Responsavel);
            Data(wd.Cell(r, c++), Tempo.ParaLocal(x.Abertura, d.Snap.FusoPadrao)); Data(wd.Cell(r, c++), x.Prazo); wd.Cell(r, c++).Value = x.Acao; wd.Cell(r, c++).Value = nomes.GetValueOrDefault(x.AtividadeId, ""); r++;
        }
        Finalizar(wd);

        var wr = wb.AddWorksheet("Por responsável");
        Cabecalho(wr, new[] { "Responsável", "Projetos", "Ativos", "Atrasados", "Concluídos", "Atividades", "Atividades atrasadas", "Horas estimadas", "Horas realizadas" });
        r = 2;
        foreach (var l in Agrupar(d, ps, ats, "responsavel"))
        {
            var c = 1;
            wr.Cell(r, c++).Value = l.Nome; wr.Cell(r, c++).Value = l.Projetos; wr.Cell(r, c++).Value = l.Ativos; wr.Cell(r, c++).Value = l.Atrasados; wr.Cell(r, c++).Value = l.Concluidos;
            wr.Cell(r, c++).Value = l.Atividades; wr.Cell(r, c++).Value = l.AtivAtrasadas; wr.Cell(r, c++).Value = Math.Round(l.HorasEst, 1); wr.Cell(r, c++).Value = Math.Round(l.HorasReal, 1); r++;
        }
        Finalizar(wr);

        using var ms = new MemoryStream(); wb.SaveAs(ms); return ms.ToArray();
    }

    /// <summary>Atividades em ordem de árvore (etapa, subtarefas…) com o nível.</summary>
    public static List<(PlAtividade A, int Nivel)> Arvore(IEnumerable<PlAtividade> ats)
    {
        var lista = ats.ToList(); var ids = lista.Select(a => a.Id).ToHashSet();
        var filhos = lista.ToLookup(a => ids.Contains(a.ParentId) ? a.ParentId : "");
        var res = new List<(PlAtividade, int)>();
        void Add(string pai, int n) { foreach (var a in filhos[pai].OrderBy(a => a.Ordem).ThenBy(a => a.CriadoEm)) { res.Add((a, n)); if (n < 20) Add(a.Id, n + 1); } }
        Add("", 0);
        return res;
    }

    private static void Cabecalho(IXLWorksheet w, IEnumerable<string> cols)
    {
        var c = 1;
        foreach (var t in cols) { var cel = w.Cell(1, c++); cel.Value = t; cel.Style.Font.Bold = true; cel.Style.Font.FontColor = XLColor.White; cel.Style.Fill.BackgroundColor = XLColor.FromHtml("#004785"); }
        w.SheetView.FreezeRows(1);
    }
    private static void Finalizar(IXLWorksheet w) { if (w.LastRowUsed() is { } u && u.RowNumber() > 1) w.RangeUsed()!.SetAutoFilter(); w.Columns().AdjustToContents(8, 60); }
    private static void Data(IXLCell c, DateTime? d) { if (d is { } x) { c.Value = x.Date; c.Style.DateFormat.Format = "dd/MM/yyyy"; } }
}
