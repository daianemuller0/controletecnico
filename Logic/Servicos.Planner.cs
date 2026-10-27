using ControleTecnico.Data;
using ControleTecnico.Models;

namespace ControleTecnico.Logic;

/// <summary>Planner de Manutenção: todas as gravações passam por aqui (permissões no servidor, regras e histórico).</summary>
public sealed partial class Servicos
{
    private const string EntPl = "plprojeto";

    // ------------------------------------------------------------------ leitura (com escopo e sigilo financeiro)
    /// <summary>Dados do Planner visíveis para a pessoa. Técnico: só projetos onde participa. Sem permissão financeira: custos zerados.</summary>
    public PlDados PlannerDados(Ator ator)
    {
        ator.Exigir(Perm.PlannerVer);
        var d = PlannerEngine.Carregar(Db, Foto());
        IEnumerable<PlProjeto> projetos = d.Projetos;
        if (!ator.Pode(Perm.PlannerEditar) && ator.Papel == Roles.Tecnico)
        {
            var eu = PlannerEngine.PessoaDe(ator);
            var meus = d.Atividades.Where(a => a.Responsavel == eu).Select(a => a.ProjetoId).ToHashSet();
            projetos = projetos.Where(p => p.Responsavel == eu || Snapshot.Ids(p.Equipe).Contains(eu) || meus.Contains(p.Id));
        }
        var lista = projetos.ToList();
        if (!ator.Pode(Perm.PlannerCustos))
            foreach (var p in lista) { p.CustoPlanejado = p.CustoMateriais = p.CustoMaoObra = p.CustoTerceiros = p.CustoOutros = null; p.CustoObs = ""; }
        var ids = lista.Select(p => p.Id).ToHashSet();
        return new PlDados
        {
            Projetos = lista, Atividades = d.Atividades.Where(a => ids.Contains(a.ProjetoId)).ToList(),
            Materiais = d.Materiais.Where(m => ids.Contains(m.ProjetoId)).ToList(), Pendencias = d.Pendencias.Where(x => ids.Contains(x.ProjetoId)).ToList(),
            Comentarios = d.Comentarios.Where(c => ids.Contains(c.ProjetoId)).ToList(), Hoje = d.Hoje, Snap = d.Snap, Pessoas = d.Pessoas,
        };
    }

    /// <summary>Pessoas que podem ser responsáveis: técnicos ativos e usuários ativos (não técnicos).</summary>
    public List<(string Chave, string Nome, string Tipo)> PlannerPessoas()
    {
        var res = Db.Tecnicos.Onde(t => t.Ativo).Select(t => ("t:" + t.Id, t.Nome, Vocab.PerfilRotulo(t.Perfil))).ToList();
        res.AddRange(Db.Usuarios.Onde(u => u.Ativo && u.Papel != Roles.Tecnico).Select(u => ("u:" + u.Id, string.IsNullOrWhiteSpace(u.Nome) ? u.Id : u.Nome, Roles.Label(u.Papel))));
        return res.OrderBy(x => x.Item2).ToList();
    }

    public bool PodeAtualizarAtividade(Ator ator, PlAtividade a) =>
        ator.Pode(Perm.PlannerEditar) || (ator.Pode(Perm.PlannerVer) && ator.Papel == Roles.Tecnico && a.Responsavel == PlannerEngine.PessoaDe(ator));

    private bool Participa(Ator ator, PlProjeto p)
    {
        if (ator.Pode(Perm.PlannerEditar)) return true;
        var eu = PlannerEngine.PessoaDe(ator);
        return p.Responsavel == eu || Snapshot.Ids(p.Equipe).Contains(eu) || Db.PlAtividades.Onde(a => a.ProjetoId == p.Id && a.Responsavel == eu).Count > 0;
    }

    public List<Auditoria> PlannerHistorico(Ator ator, string projetoId)
    {
        ator.Exigir(Perm.PlannerVer);
        var p = Db.PlProjetos.Obter(projetoId) ?? throw new ValidacaoException("Projeto não encontrado.");
        if (!Participa(ator, p) && ator.Papel == Roles.Tecnico) throw new UnauthorizedAccessException();
        return Db.Auditorias.Onde(a => a.Entidade == EntPl && a.EntidadeId == projetoId).OrderByDescending(a => a.Quando).ToList();
    }

    private void ConferirVersao(Entity atual, DateTime? versao, string oque)
    {
        // dois usuários editando o mesmo registro: quem salva por último precisa recarregar antes, para não apagar o outro
        if (versao is { } v && Math.Abs((atual.AtualizadoEm - v).TotalSeconds) > 1)
            throw new ValidacaoException($"{oque} foi alterado por {atual.AtualizadoPor} às {Tempo.ParaLocal(atual.AtualizadoEm, Db.FusoPadrao):HH:mm} enquanto você editava. Recarregue para ver a versão atual e refaça a alteração.");
    }

    // ------------------------------------------------------------------ projetos
    public PlProjeto SalvarProjeto(Ator ator, PlProjeto p, DateTime? versao = null)
    {
        ator.Exigir(Perm.PlannerEditar);
        var antes = string.IsNullOrEmpty(p.Id) ? null : Db.PlProjetos.Obter(p.Id);
        if (antes is not null) ConferirVersao(antes, versao, "Este projeto");
        var erros = new List<string>();
        p.Nome = (p.Nome ?? "").Trim();
        if (p.Nome.Length < 3) erros.Add("Informe o nome do projeto.");
        p.IniPrev = PlannerEngine.Dia(p.IniPrev); p.FimPrev = PlannerEngine.Dia(p.FimPrev); p.IniReal = PlannerEngine.Dia(p.IniReal); p.FimReal = PlannerEngine.Dia(p.FimReal);
        if (p.IniPrev is { } a && p.FimPrev is { } b && b < a) erros.Add("A conclusão prevista não pode ser antes do início previsto.");
        if (p.IniReal is { } c && p.FimReal is { } e && e < c) erros.Add("A conclusão real não pode ser antes do início real.");
        if (p.ProgressoManual is < 0 or > 100) erros.Add("O percentual de conclusão deve estar entre 0 e 100.");
        if (!PlVocab.StatusProjeto.Any(s => s.Key == p.Status)) erros.Add("Status inválido.");
        if (!PlVocab.Prioridades.Any(s => s.Key == p.Prioridade)) erros.Add("Prioridade inválida.");
        if (new[] { p.CustoPlanejado, p.CustoMateriais, p.CustoMaoObra, p.CustoTerceiros, p.CustoOutros }.Any(x => x is < 0)) erros.Add("Custos não podem ser negativos.");
        if (!string.IsNullOrEmpty(p.PlantaId) && Db.Plantas.Obter(p.PlantaId) is null) erros.Add("Unidade/planta não encontrada.");
        if (erros.Count > 0) throw new ValidacaoException(erros);
        if (!string.IsNullOrEmpty(p.PlantaId)) p.ClienteId = Db.Plantas.Obter(p.PlantaId)!.ClienteId;
        // custos: quem não tem permissão financeira não altera (nem apaga) o que já existe
        if (!ator.Pode(Perm.PlannerCustos))
        {
            p.CustoPlanejado = antes?.CustoPlanejado; p.CustoMateriais = antes?.CustoMateriais; p.CustoMaoObra = antes?.CustoMaoObra;
            p.CustoTerceiros = antes?.CustoTerceiros; p.CustoOutros = antes?.CustoOutros; p.CustoObs = antes?.CustoObs ?? "";
        }
        var hoje = DocEngine.Hoje(Db.FusoPadrao);
        var statusMudou = antes is null || antes.Status != p.Status;
        if (statusMudou)
        {
            if (p.Status == PlVocab.PConcluido && Db.PlAtividades.Onde(x => x.ProjetoId == p.Id && !PlVocab.AtivEncerrada(x.Status)).Count is > 0 and var n)
                throw new ValidacaoException($"Há {n} atividade(s) sem concluir. Conclua ou cancele as atividades antes de concluir o projeto.");
            p.StatusEm = DateTime.UtcNow; p.StatusPor = ator.Nome;
            if (p.Status == PlVocab.PAndamento && p.IniReal is null) p.IniReal = hoje;
            if (p.Status == PlVocab.PConcluido && p.FimReal is null) p.FimReal = hoje;
        }
        else { p.StatusEm = antes!.StatusEm; p.StatusPor = antes.StatusPor; }
        var novo = antes is null;
        if (novo)
        {
            if (string.IsNullOrEmpty(p.Id)) p.Id = Repo<PlProjeto>.NovoId();
            Db.PlProjetos.Recarregar();
            var ano = DateTime.UtcNow.Year;
            var seq = Db.PlProjetos.Onde(x => x.Codigo.StartsWith($"PM-{ano}-")).Select(x => int.TryParse(x.Codigo[^4..], out var k) ? k : 0).DefaultIfEmpty(0).Max() + 1;
            p.Codigo = $"PM-{ano}-{seq:0000}";
        }
        else p.Codigo = antes!.Codigo;
        Db.PlProjetos.Salvar(p, ator.Login);
        if (novo) Auditar(ator, "planner.projeto.criar", EntPl, p.Id, $"Projeto {p.Codigo} · {p.Nome} criado ({PlVocab.Get(PlVocab.StatusProjeto, p.Status).Label})");
        else
        {
            var mud = new List<string>();
            if (statusMudou) mud.Add($"status: {PlVocab.Get(PlVocab.StatusProjeto, antes!.Status).Label} → {PlVocab.Get(PlVocab.StatusProjeto, p.Status).Label}");
            if (antes!.IniPrev != p.IniPrev || antes.FimPrev != p.FimPrev) mud.Add($"prazo: {antes.IniPrev:dd/MM/yy}–{antes.FimPrev:dd/MM/yy} → {p.IniPrev:dd/MM/yy}–{p.FimPrev:dd/MM/yy}");
            if (antes.Prioridade != p.Prioridade) mud.Add($"prioridade: {PlVocab.Get(PlVocab.Prioridades, antes.Prioridade).Label} → {PlVocab.Get(PlVocab.Prioridades, p.Prioridade).Label}");
            if (antes.Responsavel != p.Responsavel) mud.Add("responsável alterado");
            Auditar(ator, statusMudou ? "planner.projeto.status" : "planner.projeto.alterar", EntPl, p.Id, $"{p.Codigo}: " + (mud.Count == 0 ? "dados atualizados" : string.Join("; ", mud)));
        }
        return p;
    }

    /// <summary>Cria o projeto e, se escolhido um modelo, as etapas em sequência a partir do início previsto.</summary>
    public PlProjeto CriarProjeto(Ator ator, PlProjeto p, string? modeloId)
    {
        var etapas = string.IsNullOrEmpty(modeloId) ? new List<PlannerEngine.EtapaModelo>() : EtapasDoModelo(modeloId);
        if (etapas.Count > 0 && p.IniPrev is null) throw new ValidacaoException("Informe o início previsto para montar as etapas do modelo.");
        if (etapas.Count > 0 && p.FimPrev is null) p.FimPrev = PlannerEngine.Dia(p.IniPrev!.Value.AddDays(etapas.Sum(e => Math.Max(1, e.Dias)) - 1));
        var salvo = SalvarProjeto(ator, p);
        var ini = salvo.IniPrev; var ordem = 0;
        var novas = new List<PlAtividade>();
        foreach (var e in etapas)
        {
            var dias = Math.Max(1, e.Dias);
            novas.Add(new PlAtividade { Id = Repo<PlAtividade>.NovoId(), ProjetoId = salvo.Id, Nome = e.Nome, Ordem = ++ordem, HorasEst = e.Horas, Peso = e.Peso > 0 ? e.Peso : 1,
                IniPrev = ini, FimPrev = ini?.AddDays(dias - 1), DependeDe = novas.Count == 0 ? "" : novas[^1].Id });
            ini = ini?.AddDays(dias);
        }
        if (novas.Count > 0) { Db.PlAtividades.SalvarVarios(novas, ator.Login); Auditar(ator, "planner.atividade.criar", EntPl, salvo.Id, $"{novas.Count} etapa(s) criadas pelo modelo"); }
        return salvo;
    }

    public void ExcluirProjeto(Ator ator, string id)
    {
        ator.Exigir(Perm.PlannerEditar);
        var p = Db.PlProjetos.Obter(id) ?? throw new ValidacaoException("Projeto não encontrado.");
        foreach (var a in Db.PlAtividades.Onde(x => x.ProjetoId == id)) Db.PlAtividades.Apagar(a.Id);
        foreach (var m in Db.PlMateriais.Onde(x => x.ProjetoId == id)) Db.PlMateriais.Apagar(m.Id);
        foreach (var x in Db.PlPendencias.Onde(x => x.ProjetoId == id)) Db.PlPendencias.Apagar(x.Id);
        foreach (var c in Db.PlComentarios.Onde(x => x.ProjetoId == id)) Db.PlComentarios.Apagar(c.Id);
        foreach (var an in Db.Anexos.Onde(a => a.DonoTipo == EntPl && a.DonoId == id)) Arquivos.Remover(an);
        Db.PlProjetos.Apagar(id);
        Auditar(ator, "planner.projeto.excluir", EntPl, id, $"Projeto {p.Codigo} · {p.Nome} excluído com atividades, materiais, pendências, comentários e anexos");
    }

    public PlProjeto AlterarStatusProjeto(Ator ator, string id, string status)
    {
        var p = Db.PlProjetos.Obter(id) ?? throw new ValidacaoException("Projeto não encontrado.");
        if (p.Status == status) return p;
        p.Status = status; return SalvarProjeto(ator, p);
    }

    // ------------------------------------------------------------------ atividades
    /// <summary>Grava a atividade. Técnico só altera o andamento das atividades atribuídas a ele (status, %, horas reais, datas reais, checklist, observações).</summary>
    public PlAtividade SalvarAtividade(Ator ator, PlAtividade a, DateTime? versao = null)
    {
        var antes = string.IsNullOrEmpty(a.Id) ? null : Db.PlAtividades.Obter(a.Id);
        var p = Db.PlProjetos.Obter(a.ProjetoId) ?? throw new ValidacaoException("Projeto não encontrado.");
        if (!ator.Pode(Perm.PlannerEditar))
        {
            if (antes is null || !PodeAtualizarAtividade(ator, antes)) throw new UnauthorizedAccessException("Você só pode atualizar as atividades atribuídas a você.");
            // copia SOMENTE os campos de execução sobre a versão gravada
            var x = antes.Copia<PlAtividade>();
            x.Status = a.Status; x.Progresso = a.Progresso; x.HorasReal = a.HorasReal; x.IniReal = a.IniReal; x.FimReal = a.FimReal; x.Obs = a.Obs;
            var ck = PlannerEngine.LerChecklist(antes.Checklist); var novo = PlannerEngine.LerChecklist(a.Checklist);
            for (var i = 0; i < ck.Count && i < novo.Count; i++) if (ck[i].Feito != novo[i].Feito) { ck[i].Feito = novo[i].Feito; ck[i].Por = ator.Nome; ck[i].Em = DateTime.UtcNow; }
            x.Checklist = PlannerEngine.GravarChecklist(ck);
            a = x;
        }
        if (antes is not null) ConferirVersao(antes, versao, "Esta atividade");
        var erros = new List<string>();
        a.Nome = (a.Nome ?? "").Trim();
        if (a.Nome.Length < 2) erros.Add("Informe o nome da atividade.");
        a.IniPrev = PlannerEngine.Dia(a.IniPrev); a.FimPrev = PlannerEngine.Dia(a.FimPrev); a.IniReal = PlannerEngine.Dia(a.IniReal); a.FimReal = PlannerEngine.Dia(a.FimReal);
        if (a.IniPrev is { } i1 && a.FimPrev is { } f1 && f1 < i1) erros.Add("O fim previsto não pode ser antes do início previsto.");
        if (a.IniReal is { } i2 && a.FimReal is { } f2 && f2 < i2) erros.Add("O fim real não pode ser antes do início real.");
        if (a.Progresso is < 0 or > 100) erros.Add("O percentual deve estar entre 0 e 100.");
        if (a.Peso <= 0) a.Peso = 1;
        if (a.HorasEst is < 0 || a.HorasReal is < 0) erros.Add("Horas não podem ser negativas.");
        if (!PlVocab.StatusAtividade.Any(s => s.Key == a.Status)) erros.Add("Status inválido.");
        var irmas = Db.PlAtividades.Onde(x => x.ProjetoId == a.ProjetoId);
        if (!string.IsNullOrEmpty(a.ParentId))
        {
            if (a.ParentId == a.Id || irmas.All(x => x.Id != a.ParentId)) erros.Add("Etapa mãe inválida.");
            else if (!string.IsNullOrEmpty(a.Id)) { var cur = irmas.FirstOrDefault(x => x.Id == a.ParentId); var guard = 0; while (cur is not null && guard++ < 50) { if (cur.Id == a.Id) { erros.Add("Uma atividade não pode ficar dentro dela mesma."); break; } cur = irmas.FirstOrDefault(x => x.Id == cur.ParentId); } }
        }
        // dependências: mesmo projeto, sem ciclo, e não começar antes do fim da anterior
        var deps = Snapshot.Ids(a.DependeDe).Where(x => x != a.Id).Distinct().ToList();
        foreach (var dId in deps)
        {
            var dep = irmas.FirstOrDefault(x => x.Id == dId);
            if (dep is null) { erros.Add("Dependência inválida (precisa ser uma atividade do mesmo projeto)."); continue; }
            if (DependeDe(irmas, dId, a.Id)) erros.Add($"Dependência circular com \"{dep.Nome}\".");
            if (a.IniPrev is { } ini && dep.FimPrev is { } fimDep && ini < fimDep && dep.Status != PlVocab.ACancelada)
                erros.Add($"\"{a.Nome}\" depende de \"{dep.Nome}\", que termina em {fimDep:dd/MM}: o início previsto precisa ser a partir dessa data.");
        }
        a.DependeDe = Csv(deps);
        var hoje = DocEngine.Hoje(Db.FusoPadrao);
        var statusMudou = antes is null ? a.Status != PlVocab.AAfazer : antes.Status != a.Status;
        if (statusMudou && a.Status is PlVocab.AAndamento or PlVocab.AConcluida)
        {
            var pend = deps.Select(id => irmas.FirstOrDefault(x => x.Id == id)).Where(x => x is not null && !PlVocab.AtivEncerrada(x.Status)).Select(x => x!.Nome).ToList();
            if (pend.Count > 0) erros.Add($"Antes, é preciso concluir: {string.Join(", ", pend)}.");
        }
        if (statusMudou && a.Status == PlVocab.AConcluida)
        {
            var obrig = PlannerEngine.LerChecklist(a.Checklist).Where(c => c.Obrigatorio && !c.Feito).Select(c => c.Texto).ToList();
            if (obrig.Count > 0) erros.Add($"Itens obrigatórios do checklist pendentes: {string.Join(", ", obrig)}.");
            if (irmas.Any(x => x.ParentId == a.Id && !PlVocab.AtivEncerrada(x.Status))) erros.Add("Conclua ou cancele as subtarefas antes.");
            var dados = new PlDados { Materiais = Db.PlMateriais.Onde(m => m.ProjetoId == a.ProjetoId), Pendencias = Db.PlPendencias.Onde(x => x.ProjetoId == a.ProjetoId) };
            if (PlannerEngine.MotivoBloqueio(dados, new PlAtividade { Id = a.Id, Status = PlVocab.AAfazer }) is { } mot) erros.Add($"A atividade está bloqueada: {mot}.");
        }
        if (erros.Count > 0) throw new ValidacaoException(erros);

        if (statusMudou)
        {
            a.StatusEm = DateTime.UtcNow; a.StatusPor = ator.Nome;
            if (a.Status == PlVocab.AAndamento && a.IniReal is null) a.IniReal = hoje;
            if (a.Status == PlVocab.AConcluida) { a.FimReal ??= hoje; a.IniReal ??= a.FimReal; a.Progresso = 100; }
        }
        else if (antes is not null) { a.StatusEm = antes.StatusEm; a.StatusPor = antes.StatusPor; }
        var eu = PlannerEngine.PessoaDe(ator);
        if (antes is null ? !string.IsNullOrEmpty(a.Responsavel) : antes.Responsavel != a.Responsavel) { a.ResponsavelEm = DateTime.UtcNow; a.ResponsavelPor = eu; }
        else if (antes is not null) { a.ResponsavelEm = antes.ResponsavelEm; a.ResponsavelPor = antes.ResponsavelPor; }
        if (antes is not null && (antes.IniPrev != a.IniPrev || antes.FimPrev != a.FimPrev)) { a.DatasAlteradasEm = DateTime.UtcNow; a.DatasAlteradasPor = eu; }
        else if (antes is not null) { a.DatasAlteradasEm = antes.DatasAlteradasEm; a.DatasAlteradasPor = antes.DatasAlteradasPor; }
        if (antes is null)
        {
            if (string.IsNullOrEmpty(a.Id)) a.Id = Repo<PlAtividade>.NovoId();
            if (a.Ordem == 0) a.Ordem = irmas.Where(x => x.ParentId == a.ParentId).Select(x => x.Ordem).DefaultIfEmpty(0).Max() + 1;
        }
        Db.PlAtividades.Salvar(a, ator.Login);

        var res = new List<string>();
        if (antes is null) res.Add("criada");
        if (statusMudou && antes is not null) res.Add($"status {PlVocab.Get(PlVocab.StatusAtividade, antes.Status).Label} → {PlVocab.Get(PlVocab.StatusAtividade, a.Status).Label}");
        if (antes is not null && (antes.IniPrev != a.IniPrev || antes.FimPrev != a.FimPrev)) res.Add($"datas {antes.IniPrev:dd/MM}–{antes.FimPrev:dd/MM} → {a.IniPrev:dd/MM}–{a.FimPrev:dd/MM}");
        if (antes is not null && antes.Responsavel != a.Responsavel) res.Add("responsável alterado");
        if (antes is not null && antes.HorasReal != a.HorasReal) res.Add($"horas realizadas {a.HorasReal:0.#}");
        if (antes is not null && antes.Progresso != a.Progresso && !statusMudou) res.Add($"{a.Progresso}%");
        Auditar(ator, statusMudou ? "planner.atividade.status" : antes is null ? "planner.atividade.criar" : "planner.atividade.alterar", EntPl, a.ProjetoId,
            $"Atividade \"{a.Nome}\": {(res.Count == 0 ? "atualizada" : string.Join("; ", res))}");

        // a primeira atividade iniciada coloca o projeto em andamento
        if (statusMudou && a.Status is PlVocab.AAndamento or PlVocab.AConcluida && p.Status is PlVocab.PNaoIniciado or PlVocab.PPlanejamento && ator.Pode(Perm.PlannerEditar))
            AlterarStatusProjeto(ator, p.Id, PlVocab.PAndamento);
        else if (statusMudou && a.Status is PlVocab.AAndamento or PlVocab.AConcluida && p.Status is PlVocab.PNaoIniciado or PlVocab.PPlanejamento)
        { p.Status = PlVocab.PAndamento; p.StatusEm = DateTime.UtcNow; p.StatusPor = ator.Nome; p.IniReal ??= hoje; Db.PlProjetos.Salvar(p, ator.Login); Auditar(ator, "planner.projeto.status", EntPl, p.Id, $"{p.Codigo}: status → Em andamento (primeira atividade iniciada)"); }
        return a;
    }

    private static bool DependeDe(List<PlAtividade> todas, string de, string alvo)
    {
        var vistos = new HashSet<string>(); var fila = new Queue<string>(); fila.Enqueue(de);
        while (fila.Count > 0)
        {
            var id = fila.Dequeue(); if (!vistos.Add(id)) continue;
            var x = todas.FirstOrDefault(t => t.Id == id); if (x is null) continue;
            foreach (var d in Snapshot.Ids(x.DependeDe)) { if (d == alvo) return true; fila.Enqueue(d); }
        }
        return false;
    }

    /// <summary>Reprograma (cronograma/calendário): move ou redimensiona. Etapa com subtarefas move as subtarefas junto.</summary>
    public PlAtividade ReprogramarAtividade(Ator ator, string id, DateTime novoIni, DateTime novoFim)
    {
        ator.Exigir(Perm.PlannerEditar);
        var a = Db.PlAtividades.Obter(id) ?? throw new ValidacaoException("Atividade não encontrada.");
        novoIni = PlannerEngine.Dia(novoIni); novoFim = PlannerEngine.Dia(novoFim);
        if (novoFim < novoIni) throw new ValidacaoException("O fim não pode ser antes do início.");
        var todas = Db.PlAtividades.Onde(x => x.ProjetoId == a.ProjetoId);
        var filhos = todas.ToLookup(x => x.ParentId);
        var (iniAtual, _) = PlannerEngine.Periodo(a, filhos);
        if (filhos[a.Id].Any() && iniAtual is { } ia)
        {
            var delta = novoIni - ia.Date;
            var desc = new List<PlAtividade>(); var fila = new Queue<string>(new[] { a.Id });
            while (fila.Count > 0) foreach (var f in filhos[fila.Dequeue()]) { desc.Add(f); fila.Enqueue(f.Id); }
            foreach (var f in desc.Where(f => f.IniPrev is not null).OrderBy(f => f.IniPrev))
            {
                var c = Db.PlAtividades.Obter(f.Id)!;
                c.IniPrev = c.IniPrev + delta; c.FimPrev = c.FimPrev + delta;
                SalvarAtividade(ator, c);
            }
            a = Db.PlAtividades.Obter(id)!;
            if (a.IniPrev is not null) { a.IniPrev += delta; a.FimPrev += delta; return SalvarAtividade(ator, a); }
            return a;
        }
        a.IniPrev = novoIni; a.FimPrev = novoFim;
        return SalvarAtividade(ator, a);
    }

    public PlAtividade AlterarStatusAtividade(Ator ator, string id, string status)
    {
        var a = Db.PlAtividades.Obter(id) ?? throw new ValidacaoException("Atividade não encontrada.");
        if (a.Status == status) return a;
        a.Status = status; return SalvarAtividade(ator, a);
    }

    public void ExcluirAtividade(Ator ator, string id)
    {
        ator.Exigir(Perm.PlannerEditar);
        var a = Db.PlAtividades.Obter(id) ?? throw new ValidacaoException("Atividade não encontrada.");
        var todas = Db.PlAtividades.Onde(x => x.ProjetoId == a.ProjetoId);
        var apagar = new HashSet<string> { id }; bool mais = true;
        while (mais) { mais = false; foreach (var x in todas.Where(x => apagar.Contains(x.ParentId) && apagar.Add(x.Id))) mais = true; }
        foreach (var x in apagar) Db.PlAtividades.Apagar(x);
        var ajustar = todas.Where(x => !apagar.Contains(x.Id) && Snapshot.Ids(x.DependeDe).Any(apagar.Contains)).ToList();
        foreach (var x in ajustar) x.DependeDe = Csv(Snapshot.Ids(x.DependeDe).Where(d => !apagar.Contains(d)));
        if (ajustar.Count > 0) Db.PlAtividades.SalvarVarios(ajustar, ator.Login);
        foreach (var m in Db.PlMateriais.Onde(m => apagar.Contains(m.AtividadeId))) { m.AtividadeId = ""; Db.PlMateriais.Salvar(m, ator.Login); }
        foreach (var x in Db.PlPendencias.Onde(x => apagar.Contains(x.AtividadeId))) { x.AtividadeId = ""; Db.PlPendencias.Salvar(x, ator.Login); }
        Auditar(ator, "planner.atividade.excluir", EntPl, a.ProjetoId, $"Atividade \"{a.Nome}\" excluída" + (apagar.Count > 1 ? $" com {apagar.Count - 1} subtarefa(s)" : ""));
    }

    public PlAtividade DuplicarAtividade(Ator ator, string id)
    {
        ator.Exigir(Perm.PlannerEditar);
        var a = Db.PlAtividades.Obter(id) ?? throw new ValidacaoException("Atividade não encontrada.");
        var c = a.Copia<PlAtividade>();
        c.Id = ""; c.Nome = a.Nome + " (cópia)"; c.Status = PlVocab.AAfazer; c.Progresso = 0; c.IniReal = c.FimReal = null; c.HorasReal = null; c.Ordem = a.Ordem;
        c.Checklist = PlannerEngine.GravarChecklist(PlannerEngine.LerChecklist(a.Checklist).Select(i => new PlannerEngine.ItemChecklist { Texto = i.Texto, Obrigatorio = i.Obrigatorio }));
        // abre espaço logo depois da original
        var depois = Db.PlAtividades.Onde(x => x.ProjetoId == a.ProjetoId && x.ParentId == a.ParentId && x.Ordem > a.Ordem);
        foreach (var x in depois) x.Ordem++;
        if (depois.Count > 0) Db.PlAtividades.SalvarVarios(depois, ator.Login);
        c.Ordem = a.Ordem + 1;
        return SalvarAtividade(ator, c);
    }

    /// <summary>Sobe/desce a atividade entre as irmãs.</summary>
    public void MoverAtividade(Ator ator, string id, int direcao)
    {
        ator.Exigir(Perm.PlannerEditar);
        var a = Db.PlAtividades.Obter(id) ?? throw new ValidacaoException("Atividade não encontrada.");
        var irmas = Db.PlAtividades.Onde(x => x.ProjetoId == a.ProjetoId && x.ParentId == a.ParentId).OrderBy(x => x.Ordem).ThenBy(x => x.CriadoEm).ToList();
        for (var i = 0; i < irmas.Count; i++) irmas[i].Ordem = i + 1;
        var idx = irmas.FindIndex(x => x.Id == id); var alvo = idx + Math.Sign(direcao);
        if (alvo < 0 || alvo >= irmas.Count) return;
        (irmas[idx].Ordem, irmas[alvo].Ordem) = (irmas[alvo].Ordem, irmas[idx].Ordem);
        Db.PlAtividades.SalvarVarios(irmas, ator.Login);
    }

    // ------------------------------------------------------------------ materiais, pendências, comentários
    public PlMaterial SalvarMaterial(Ator ator, PlMaterial m)
    {
        ator.Exigir(Perm.PlannerEditar);
        if (!Db.PlProjetos.Existe(m.ProjetoId)) throw new ValidacaoException("Projeto não encontrado.");
        m.Descricao = (m.Descricao ?? "").Trim();
        if (m.Descricao.Length < 2) throw new ValidacaoException("Informe a descrição do material.");
        if (m.Quantidade <= 0) throw new ValidacaoException("A quantidade deve ser maior que zero.");
        if (!PlVocab.StatusMaterial.Any(s => s.Key == m.Status)) throw new ValidacaoException("Status do material inválido.");
        if (!string.IsNullOrEmpty(m.AtividadeId) && Db.PlAtividades.Obter(m.AtividadeId)?.ProjetoId != m.ProjetoId) throw new ValidacaoException("Atividade inválida.");
        m.DataNecessaria = PlannerEngine.Dia(m.DataNecessaria);
        var antes = string.IsNullOrEmpty(m.Id) ? null : Db.PlMateriais.Obter(m.Id);
        Db.PlMateriais.Salvar(m, ator.Login);
        Auditar(ator, "planner.material", EntPl, m.ProjetoId, antes is null ? $"Material \"{m.Descricao}\" incluído ({PlVocab.Get(PlVocab.StatusMaterial, m.Status).Label})"
            : antes.Status != m.Status ? $"Material \"{m.Descricao}\": {PlVocab.Get(PlVocab.StatusMaterial, antes.Status).Label} → {PlVocab.Get(PlVocab.StatusMaterial, m.Status).Label}" : $"Material \"{m.Descricao}\" atualizado");
        return m;
    }
    public void ExcluirMaterial(Ator ator, string id)
    {
        ator.Exigir(Perm.PlannerEditar);
        var m = Db.PlMateriais.Obter(id) ?? throw new ValidacaoException("Material não encontrado.");
        Db.PlMateriais.Apagar(id); Auditar(ator, "planner.material", EntPl, m.ProjetoId, $"Material \"{m.Descricao}\" excluído");
    }

    public PlPendencia SalvarPendencia(Ator ator, PlPendencia x)
    {
        ator.Exigir(Perm.PlannerEditar);
        if (!Db.PlProjetos.Existe(x.ProjetoId)) throw new ValidacaoException("Projeto não encontrado.");
        x.Descricao = (x.Descricao ?? "").Trim();
        if (x.Descricao.Length < 3) throw new ValidacaoException("Descreva a pendência.");
        if (!string.IsNullOrEmpty(x.AtividadeId) && Db.PlAtividades.Obter(x.AtividadeId)?.ProjetoId != x.ProjetoId) throw new ValidacaoException("Atividade inválida.");
        var antes = string.IsNullOrEmpty(x.Id) ? null : Db.PlPendencias.Obter(x.Id);
        if (x.Abertura == default) x.Abertura = DateTime.UtcNow;
        x.Prazo = PlannerEngine.Dia(x.Prazo);
        if (x.Situacao == "resolvida" && antes?.Situacao != "resolvida") x.ResolvidaEm = DateTime.UtcNow;
        if (x.Situacao != "resolvida") x.ResolvidaEm = null;
        Db.PlPendencias.Salvar(x, ator.Login);
        Auditar(ator, "planner.pendencia", EntPl, x.ProjetoId, antes is null ? $"Pendência aberta ({PlVocab.Get(PlVocab.Impactos, x.Impacto).Label}): {x.Descricao}"
            : antes.Situacao != x.Situacao ? $"Pendência \"{x.Descricao}\": {PlVocab.Get(PlVocab.SituacoesPendencia, x.Situacao).Label}" : $"Pendência \"{x.Descricao}\" atualizada");
        return x;
    }
    public void ExcluirPendencia(Ator ator, string id)
    {
        ator.Exigir(Perm.PlannerEditar);
        var x = Db.PlPendencias.Obter(id) ?? throw new ValidacaoException("Pendência não encontrada.");
        Db.PlPendencias.Apagar(id); Auditar(ator, "planner.pendencia", EntPl, x.ProjetoId, $"Pendência \"{x.Descricao}\" excluída");
    }

    public PlComentario Comentar(Ator ator, string projetoId, string atividadeId, string texto)
    {
        ator.Exigir(Perm.PlannerVer);
        var p = Db.PlProjetos.Obter(projetoId) ?? throw new ValidacaoException("Projeto não encontrado.");
        if (ator.Papel == Roles.Consulta || !Participa(ator, p)) throw new UnauthorizedAccessException("Você não participa deste projeto.");
        texto = (texto ?? "").Trim();
        if (texto.Length == 0) throw new ValidacaoException("Escreva o comentário.");
        if (texto.Length > 4000) throw new ValidacaoException("Comentário muito longo (máx. 4000 caracteres).");
        var c = Db.PlComentarios.Salvar(new PlComentario { ProjetoId = projetoId, AtividadeId = atividadeId ?? "", Texto = texto, Autor = ator.Login, AutorNome = ator.Nome, Quando = DateTime.UtcNow }, ator.Login);
        return c;
    }
    public void ExcluirComentario(Ator ator, string id)
    {
        var c = Db.PlComentarios.Obter(id) ?? throw new ValidacaoException("Comentário não encontrado.");
        if (c.Autor != ator.Login && !ator.Pode(Perm.PlannerEditar)) throw new UnauthorizedAccessException("Só o autor pode excluir o comentário.");
        Db.PlComentarios.Apagar(id);
    }

    // ------------------------------------------------------------------ modelos, visões, alertas vistos
    public List<(string Id, string Nome, string Tipo, int Etapas, bool Padrao)> PlannerModelos()
    {
        var res = PlannerEngine.ModelosPadrao.Select(m => (m.Id, m.Nome, m.Tipo, m.Etapas.Length, true)).ToList();
        res.AddRange(Db.PlModelos.Todos().OrderBy(m => m.Nome).Select(m => (m.Id, m.Nome, m.Tipo, PlannerEngine.LerEtapas(m.Etapas).Count, false)));
        return res;
    }
    public List<PlannerEngine.EtapaModelo> EtapasDoModelo(string id) =>
        PlannerEngine.ModelosPadrao.FirstOrDefault(m => m.Id == id).Etapas?.ToList() ?? PlannerEngine.LerEtapas(Db.PlModelos.Obter(id)?.Etapas);

    /// <summary>Salva as etapas (1º nível) do projeto como modelo reutilizável, com a duração e o peso de cada uma.</summary>
    public PlModelo SalvarModeloDoProjeto(Ator ator, string projetoId, string nome)
    {
        ator.Exigir(Perm.PlannerEditar);
        var p = Db.PlProjetos.Obter(projetoId) ?? throw new ValidacaoException("Projeto não encontrado.");
        nome = (nome ?? "").Trim(); if (nome.Length < 3) throw new ValidacaoException("Informe o nome do modelo.");
        var etapas = Db.PlAtividades.Onde(a => a.ProjetoId == projetoId && a.ParentId == "" && a.Status != PlVocab.ACancelada).OrderBy(a => a.Ordem)
            .Select(a => new PlannerEngine.EtapaModelo { Nome = a.Nome, Dias = a.IniPrev is { } i && a.FimPrev is { } f ? (int)(f - i).TotalDays + 1 : 1, Horas = a.HorasEst, Peso = a.Peso }).ToList();
        if (etapas.Count == 0) throw new ValidacaoException("O projeto não tem etapas para virar modelo.");
        var m = Db.PlModelos.Salvar(new PlModelo { Nome = nome, Tipo = p.Tipo, Etapas = PlannerEngine.GravarEtapas(etapas) }, ator.Login);
        Auditar(ator, "planner.modelo", EntPl, projetoId, $"Modelo \"{nome}\" criado a partir de {p.Codigo} ({etapas.Count} etapas)");
        return m;
    }
    public void ExcluirModelo(Ator ator, string id) { ator.Exigir(Perm.PlannerEditar); if (Db.PlModelos.Existe(id)) Db.PlModelos.Apagar(id); }

    public List<PlVisao> PlannerVisoes(Ator ator) => Db.PlVisoes.Onde(v => v.Dono == ator.Login).OrderBy(v => v.Nome).ToList();
    public PlVisao SalvarVisao(Ator ator, string nome, string filtros)
    {
        ator.Exigir(Perm.PlannerVer);
        nome = (nome ?? "").Trim(); if (nome.Length is < 2 or > 40) throw new ValidacaoException("Dê um nome à visão (2 a 40 caracteres).");
        var v = Db.PlVisoes.Onde(x => x.Dono == ator.Login && DocEngine.Norm(x.Nome) == DocEngine.Norm(nome)).FirstOrDefault() ?? new PlVisao { Dono = ator.Login };
        v.Nome = nome; v.Filtros = filtros;
        return Db.PlVisoes.Salvar(v, ator.Login);
    }
    public void ExcluirVisao(Ator ator, string id) { var v = Db.PlVisoes.Obter(id); if (v is not null && v.Dono == ator.Login) Db.PlVisoes.Apagar(id); }

    public HashSet<string> AlertasVistos(Ator ator) => Db.PlAlertasLidos.Onde(x => x.Usuario == ator.Login).Select(x => x.Chave).ToHashSet();
    public void MarcarAlertaVisto(Ator ator, string chave)
    {
        if (Db.PlAlertasLidos.Onde(x => x.Usuario == ator.Login && x.Chave == chave).Count == 0)
            Db.PlAlertasLidos.Salvar(new PlAlertaLido { Usuario = ator.Login, Chave = chave }, ator.Login);
    }

    // ------------------------------------------------------------------ anexos (evidências) do projeto
    public bool PodeAnexarPlanner(Ator ator, string projetoId) =>
        Db.PlProjetos.Obter(projetoId) is { } p && ator.Pode(Perm.PlannerVer) && ator.Papel != Roles.Consulta && Participa(ator, p);
    public bool PodeVerPlanner(Ator ator, string projetoId) =>
        Db.PlProjetos.Obter(projetoId) is { } p && ator.Pode(Perm.PlannerVer) && (ator.Papel != Roles.Tecnico || Participa(ator, p));
}
