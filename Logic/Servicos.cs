using System.Security.Cryptography;
using ControleTecnico.Data;
using ControleTecnico.Models;

namespace ControleTecnico.Logic;

public sealed class ResultadoViagem
{
    public List<string> Erros { get; } = new();
    public List<string> Avisos { get; } = new();
    public List<Conflito> Conflitos { get; set; } = new();
    public List<Aptidao> Aptidoes { get; } = new();
    /// <summary>Plantas usadas na viagem que não têm cidade: a tela pede a cidade e grava no cadastro.</summary>
    public List<string> PlantasSemCidade { get; } = new();
    public bool ConflitoErro => Conflitos.Any(c => c.Erro);
    public bool RequisitoBloqueante => Aptidoes.Any(a => a.Bloqueado);
    public bool PodeConfirmar => Erros.Count == 0 && !ConflitoErro && !RequisitoBloqueante;
}

/// <summary>Operações de escrita e regras de negócio. TODA permissão é verificada aqui,
/// no servidor — a interface apenas esconde botões por conveniência.</summary>
public sealed partial class Servicos
{
    public Db Db { get; }
    public Armazenamento Arquivos { get; }

    public Servicos(Db db, Armazenamento arq) { Db = db; Arquivos = arq; }

    // ------------------------------------------------------------ auditoria
    public void Auditar(Ator a, string acao, string entidade, string id, string resumo) =>
        Db.Auditorias.Salvar(new Auditoria
        {
            Quando = DateTime.UtcNow, Usuario = string.IsNullOrEmpty(a.Login) ? a.Nome : a.Login, Papel = a.Papel,
            Acao = acao, Entidade = entidade, EntidadeId = id, Resumo = resumo,
        });

    public Snapshot Foto() => Snapshot.Carregar(Db);

    // ------------------------------------------------------------- viagens
    public static string Csv(IEnumerable<string> ids) => string.Join(",", ids.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct());

    /// <summary>Valida uma viagem ainda não gravada: erros de preenchimento, avisos,
    /// conflitos de agenda e aptidão documental de cada técnico.</summary>
    public ResultadoViagem Validar(Viagem v, List<Trecho> trechos, List<Atendimento> atends, bool simularConfirmada = false)
    {
        var r = new ResultadoViagem();
        var snap = Foto();
        var tecs = Snapshot.Ids(v.TecnicoIds);
        var rascunho = v.Status == Vocab.ViagemRascunho && !simularConfirmada;

        if (tecs.Length == 0) r.Erros.Add("Selecione ao menos um técnico participante.");
        foreach (var id in tecs)
            if (!snap.Tecnicos.TryGetValue(id, out var t)) r.Erros.Add($"Técnico {id} não encontrado.");
            else if (!t.Ativo) r.Erros.Add($"{t.Nome} está inativo.");
        if (string.IsNullOrWhiteSpace(v.PlantaId) && atends.Count == 0 && !rascunho)
            r.Erros.Add("Informe a planta de destino ou ao menos um atendimento.");
        if (!string.IsNullOrEmpty(v.PlantaId) && snap.Planta(v.PlantaId) is null) r.Erros.Add("Planta de destino não encontrada.");

        // planta sem cidade: não dá para localizar no mapa nem planejar o deslocamento; a tela pede a cidade
        var usadas = new[] { v.PlantaId }.Concat(atends.Select(x => x.PlantaId)).Concat(trechos.Select(x => x.DestPlantaId)).Concat(trechos.Select(x => x.OrigemPlantaId))
            .Where(x => !string.IsNullOrEmpty(x)).Distinct();
        foreach (var pid in usadas)
            if (snap.Planta(pid) is { } pl && string.IsNullOrWhiteSpace(pl.Cidade))
            {
                r.PlantasSemCidade.Add(pid);
                (rascunho ? r.Avisos : r.Erros).Add($"A planta \"{snap.NomePlanta(pid)}\" não tem cidade cadastrada: informe a cidade para localizá-la no mapa.");
            }

        var n = 0;
        foreach (var t in trechos)
        {
            n++;
            var rot = $"Trecho {n}";
            if (string.IsNullOrWhiteSpace(t.Origem) || string.IsNullOrWhiteSpace(t.Destino))
                (rascunho ? r.Avisos : r.Erros).Add($"{rot}: informe origem e destino.");
            var (ini, fim) = AgendaEngine.Janela(t);
            if (ini is null || fim is null)
                (rascunho ? r.Avisos : r.Erros).Add($"{rot}: informe saída e chegada (ou saída/chegada e a duração). Sem horários o trecho não aparece na agenda.");
            else if (fim <= ini) r.Erros.Add($"{rot}: a chegada deve ser posterior à saída.");
            if (t.Modal == Vocab.ModalAviao && string.IsNullOrWhiteSpace(t.Voo) && !rascunho) r.Avisos.Add($"{rot}: número do voo não informado.");
            if (t.DuracaoMin is < 0) r.Erros.Add($"{rot}: duração inválida.");
        }
        var ord = trechos.Where(t => t.PartidaPrev.HasValue).OrderBy(t => t.PartidaPrev).ToList();
        if (!trechos.Any(t => t.Tipo == Vocab.TrechoIda)) r.Avisos.Add("Nenhum trecho de ida programado.");
        if (!trechos.Any(t => t.Tipo == Vocab.TrechoRetorno))
            r.Avisos.Add("Retorno não programado: a localização do técnico após o atendimento ficará sem destino definido.");

        foreach (var a in atends)
        {
            var rot = "Atendimento " + (snap.NomePlanta(a.PlantaId) is { Length: > 0 } np ? $"({np})" : "");
            if (string.IsNullOrEmpty(a.PlantaId) || snap.Planta(a.PlantaId) is null) r.Erros.Add($"{rot}: selecione a planta.");
            if (a.FimPrev <= a.IniPrev) r.Erros.Add($"{rot}: o término deve ser posterior ao início.");
            if (a.IniReal.HasValue && a.FimReal.HasValue && a.FimReal <= a.IniReal) r.Erros.Add($"{rot}: término realizado anterior ao início realizado.");
            if (a.HorasPrev is < 0 || a.HorasReal is < 0) r.Erros.Add($"{rot}: horas não podem ser negativas.");
            foreach (var id in Snapshot.Ids(a.TecnicoIds))
                if (!tecs.Contains(id)) r.Erros.Add($"{rot}: o técnico {snap.Tecnico(id)?.Nome ?? id} não participa da viagem.");
        }
        foreach (var t in trechos)
            foreach (var id in Snapshot.Ids(t.TecnicoIds))
                if (!tecs.Contains(id)) r.Erros.Add($"Trecho {t.Origem}→{t.Destino}: o técnico {snap.Tecnico(id)?.Nome ?? id} não participa da viagem.");
        if (atends.Count == 0 && !rascunho) r.Avisos.Add("Nenhum atendimento programado (período de permanência na planta).");

        // ---- conflitos: remove a versão gravada da viagem e coloca a do formulário ----
        var vv = v.Copia<Viagem>();
        if (simularConfirmada && vv.Status is Vocab.ViagemRascunho or Vocab.ViagemPlanejada) vv.Status = Vocab.ViagemConfirmada;
        if (string.IsNullOrEmpty(vv.Id)) vv.Id = "_novo";
        snap.Trechos.RemoveAll(t => t.ViagemId == vv.Id);
        snap.Atendimentos.RemoveAll(a => a.ViagemId == vv.Id);
        snap.Viagens[vv.Id] = vv;
        foreach (var t in trechos) { var c = t.Copia<Trecho>(); c.ViagemId = vv.Id; if (string.IsNullOrEmpty(c.Id)) c.Id = "_t" + snap.Trechos.Count; snap.Trechos.Add(c); }
        foreach (var a in atends) { var c = a.Copia<Atendimento>(); c.ViagemId = vv.Id; if (string.IsNullOrEmpty(c.Id)) c.Id = "_a" + snap.Atendimentos.Count; snap.Atendimentos.Add(c); }
        if (vv.Status != Vocab.ViagemCancelada)
        {
            var eventos = AgendaEngine.Eventos(snap);
            var meus = eventos.Where(e => e.ViagemId == vv.Id).Select(e => e.Id).ToHashSet();
            r.Conflitos = AgendaEngine.Conflitos(snap, eventos).Where(c => meus.Contains(c.A.Id) || meus.Contains(c.B.Id)).ToList();

            // ---- aptidão documental ----
            var hoje = DocEngine.Hoje(snap.FusoPadrao);
            var linhas = atends.Count > 0
                ? atends.Select(a => (a, tecs: Snapshot.Ids(a.TecnicoIds).Length > 0 ? Snapshot.Ids(a.TecnicoIds) : tecs)).ToList()
                : new List<(Atendimento a, string[] tecs)>();
            var porTec = new Dictionary<string, List<Pendencia>>();
            foreach (var (a, ts) in linhas)
            {
                var serv = string.IsNullOrEmpty(a.ServicoId) ? v.ServicoId : a.ServicoId;
                var reqs = DocEngine.RequisitosDe(snap, new[] { serv }, new[] { a.PlantaId });
                var fuso = snap.Planta(a.PlantaId)?.FusoHorario;
                foreach (var tid in ts)
                {
                    if (!snap.Tecnicos.TryGetValue(tid, out var tec)) continue;
                    var ap = DocEngine.Avaliar(snap, tec, reqs, a.IniPrev, a.FimPrev, hoje, fuso);
                    if (!porTec.TryGetValue(tid, out var lst)) porTec[tid] = lst = new();
                    foreach (var p in ap.Pendencias)
                        if (!lst.Any(x => x.Requisito.Id == p.Requisito.Id && x.Tipo == p.Tipo)) lst.Add(p);
                }
            }
            foreach (var (tid, lst) in porTec)
                r.Aptidoes.Add(new Aptidao(snap.Tecnicos[tid], lst.Count == 0, lst.Any(p => p.Bloqueia), lst));
        }
        return r;
    }

    public Viagem SalvarViagem(Ator ator, Viagem v, List<Trecho> trechos, List<Atendimento> atends)
    {
        ator.Exigir(Perm.EditarViagens);
        var anterior = Db.Viagens.Obter(v.Id);
        v.TecnicoIds = Csv(Snapshot.Ids(v.TecnicoIds));
        var res = Validar(v, trechos, atends);
        if (res.Erros.Count > 0) throw new ValidacaoException(res.Erros);

        var exigeGate = v.Status is Vocab.ViagemConfirmada or Vocab.ViagemAndamento;
        if (exigeGate && !res.PodeConfirmar)
        {
            var motivos = new List<string>();
            motivos.AddRange(res.Conflitos.Where(c => c.Erro).Select(c => "Conflito: " + c.Motivo));
            foreach (var ap in res.Aptidoes.Where(a => a.Bloqueado))
                motivos.AddRange(ap.Pendencias.Where(p => p.Bloqueia).Select(p => $"{ap.Tecnico.Nome} — {p.Motivo}"));
            throw new ValidacaoException(new[] { "Não é possível confirmar a viagem:" }.Concat(motivos));
        }

        if (string.IsNullOrEmpty(v.Id) || anterior is null)
        {
            if (string.IsNullOrEmpty(v.Id)) v.Id = Repo<Viagem>.NovoId();
            if (string.IsNullOrEmpty(v.Codigo))
            {
                var ano = DateTime.UtcNow.Year;
                Db.Viagens.Recarregar();   // base compartilhada: outra máquina pode ter criado viagens agora
                var seq = Db.Viagens.Onde(x => x.Codigo.StartsWith($"V-{ano}-")).Select(x => int.TryParse(x.Codigo[^4..], out var n) ? n : 0).DefaultIfEmpty(0).Max() + 1;
                v.Codigo = $"V-{ano}-{seq:0000}";
            }
        }
        if (string.IsNullOrWhiteSpace(v.Responsavel)) v.Responsavel = ator.Nome;
        v.Origem = string.IsNullOrWhiteSpace(v.Origem) ? (trechos.OrderBy(t => t.PartidaPrev).FirstOrDefault()?.Origem ?? "") : v.Origem;
        v.Destino = string.IsNullOrWhiteSpace(v.Destino) ? (Foto().NomePlanta(v.PlantaId)) : v.Destino;
        Db.Viagens.Salvar(v, ator.Login);

        // trechos
        var existentes = Db.Trechos.Onde(t => t.ViagemId == v.Id);
        foreach (var velho in existentes.Where(e => !trechos.Any(t => t.Id == e.Id))) Db.Trechos.Apagar(velho.Id);
        var ordem = 0;
        foreach (var t in trechos.OrderBy(t => t.PartidaPrev ?? DateTime.MaxValue).ThenBy(t => t.Ordem))
        {
            t.ViagemId = v.Id; t.Ordem = ++ordem;
            if (t.PartidaPrev.HasValue && t.ChegadaPrev.HasValue && t.DuracaoFonte != "informada" && t.DuracaoFonte != "estimada")
            { t.DuracaoMin = (int)(t.ChegadaPrev.Value - t.PartidaPrev.Value).TotalMinutes; t.DuracaoFonte = "horarios"; }
            if (string.IsNullOrEmpty(t.Id)) t.Id = Repo<Trecho>.NovoId();
            t.TecnicoIds = Csv(Snapshot.Ids(t.TecnicoIds));
        }
        Db.Trechos.SalvarVarios(trechos, ator.Login);

        var atExist = Db.Atendimentos.Onde(a => a.ViagemId == v.Id);
        foreach (var velho in atExist.Where(e => !atends.Any(a => a.Id == e.Id))) Db.Atendimentos.Apagar(velho.Id);
        foreach (var a in atends)
        {
            a.ViagemId = v.Id; a.TecnicoIds = Csv(Snapshot.Ids(a.TecnicoIds));
            if (string.IsNullOrEmpty(a.ClienteId)) a.ClienteId = v.ClienteId;
            if (string.IsNullOrEmpty(a.ServicoId)) a.ServicoId = v.ServicoId;
            if (string.IsNullOrEmpty(a.Id)) a.Id = Repo<Atendimento>.NovoId();
        }
        Db.Atendimentos.SalvarVarios(atends, ator.Login);

        var acao = anterior is null ? "viagem.criar" : anterior.Status != v.Status
            ? (v.Status == Vocab.ViagemCancelada ? "viagem.cancelar" : "viagem.status") : "viagem.alterar";
        Auditar(ator, acao, "viagem", v.Id,
            anterior is null ? $"{v.Codigo} criada ({Vocab.Get(Vocab.StatusViagem, v.Status).Label})"
            : $"{v.Codigo}: {Vocab.Get(Vocab.StatusViagem, anterior.Status).Label} → {Vocab.Get(Vocab.StatusViagem, v.Status).Label}; {trechos.Count} trecho(s), {atends.Count} atendimento(s)");
        return v;
    }

    /// <summary>Muda só o status (confirmar, iniciar, concluir, cancelar), reavaliando as regras.</summary>
    public Viagem AlterarStatusViagem(Ator ator, string viagemId, string novoStatus, string? motivo = null)
    {
        ator.Exigir(Perm.EditarViagens);
        var v = Db.Viagens.Obter(viagemId) ?? throw new ValidacaoException("Viagem não encontrada.");
        var trechos = Db.Trechos.Onde(t => t.ViagemId == viagemId).OrderBy(t => t.Ordem).ToList();
        var atends = Db.Atendimentos.Onde(a => a.ViagemId == viagemId);
        if (v.Status == Vocab.ViagemCancelada && novoStatus != Vocab.ViagemRascunho)
            throw new ValidacaoException("Uma viagem cancelada só pode voltar a rascunho.");
        v.Status = novoStatus;
        if (novoStatus == Vocab.ViagemCancelada && !string.IsNullOrWhiteSpace(motivo))
            v.Obs = string.IsNullOrWhiteSpace(v.Obs) ? $"Cancelada: {motivo}" : $"{v.Obs}\nCancelada: {motivo}";
        return SalvarViagem(ator, v, trechos, atends);
    }

    public void ExcluirRascunho(Ator ator, string viagemId)
    {
        ator.Exigir(Perm.EditarViagens);
        var v = Db.Viagens.Obter(viagemId) ?? throw new ValidacaoException("Viagem não encontrada.");
        if (v.Status != Vocab.ViagemRascunho) throw new ValidacaoException("Só rascunhos podem ser excluídos; cancele as demais viagens.");
        foreach (var t in Db.Trechos.Onde(t => t.ViagemId == viagemId)) Db.Trechos.Apagar(t.Id);
        foreach (var a in Db.Atendimentos.Onde(a => a.ViagemId == viagemId)) Db.Atendimentos.Apagar(a.Id);
        foreach (var an in Db.Anexos.Onde(a => a.DonoTipo == "viagem" && a.DonoId == viagemId)) Arquivos.Remover(an);
        Db.Viagens.Apagar(viagemId);
        Auditar(ator, "viagem.excluir", "viagem", viagemId, $"Rascunho {v.Codigo} excluído");
    }

    // ---------------------------------------------------- atendimento avulso
    public Atendimento SalvarAtendimentoAvulso(Ator ator, Atendimento a)
    {
        ator.Exigir(Perm.EditarAgenda);
        var erros = new List<string>();
        var snap = Foto();
        if (Snapshot.Ids(a.TecnicoIds).Length == 0) erros.Add("Selecione o técnico.");
        if (snap.Planta(a.PlantaId) is null) erros.Add("Selecione a planta.");
        if (a.FimPrev <= a.IniPrev) erros.Add("O término deve ser posterior ao início.");
        if (erros.Count > 0) throw new ValidacaoException(erros);
        a.ViagemId = "";
        if (string.IsNullOrEmpty(a.ClienteId)) a.ClienteId = snap.Planta(a.PlantaId)!.ClienteId;
        var novo = string.IsNullOrEmpty(a.Id);

        // o gate de aptidão vale também aqui, quando o compromisso é firme
        if (a.Status is Vocab.ViagemConfirmada or Vocab.ViagemAndamento)
        {
            var hoje = DocEngine.Hoje(snap.FusoPadrao);
            var reqs = DocEngine.RequisitosDe(snap, new[] { a.ServicoId }, new[] { a.PlantaId });
            var bloq = new List<string>();
            foreach (var tid in Snapshot.Ids(a.TecnicoIds))
                if (snap.Tecnico(tid) is { } tec)
                    bloq.AddRange(DocEngine.Avaliar(snap, tec, reqs, a.IniPrev, a.FimPrev, hoje, snap.Planta(a.PlantaId)?.FusoHorario)
                        .Pendencias.Where(p => p.Bloqueia).Select(p => $"{tec.Nome} — {p.Motivo}"));
            if (bloq.Count > 0) throw new ValidacaoException(new[] { "Não é possível confirmar o atendimento:" }.Concat(bloq));
        }
        Db.Atendimentos.Salvar(a, ator.Login);
        Auditar(ator, novo ? "atendimento.criar" : "atendimento.alterar", "atendimento", a.Id, $"Atendimento em {snap.NomePlanta(a.PlantaId)} ({Vocab.Get(Vocab.StatusViagem, a.Status).Label})");
        return a;
    }

    public void ExcluirAtendimentoAvulso(Ator ator, string id)
    {
        ator.Exigir(Perm.EditarAgenda);
        var a = Db.Atendimentos.Obter(id) ?? throw new ValidacaoException("Atendimento não encontrado.");
        if (!string.IsNullOrEmpty(a.ViagemId)) throw new ValidacaoException("Este atendimento pertence a uma viagem: edite a viagem.");
        Db.Atendimentos.Apagar(id);
        Auditar(ator, "atendimento.excluir", "atendimento", id, "Atendimento avulso excluído");
    }

    // ---------------------------------------------------- indisponibilidades
    public Indisponibilidade SalvarIndisponibilidade(Ator ator, Indisponibilidade i)
    {
        ator.Exigir(Perm.EditarAgenda);
        var erros = new List<string>();
        if (!Db.Tecnicos.Existe(i.TecnicoId)) erros.Add("Selecione o técnico.");
        if (i.Fim <= i.Ini) erros.Add("O término deve ser posterior ao início.");
        if (erros.Count > 0) throw new ValidacaoException(erros);
        var novo = string.IsNullOrEmpty(i.Id);
        Db.Indisponibilidades.Salvar(i, ator.Login);
        Auditar(ator, novo ? "indisponibilidade.criar" : "indisponibilidade.alterar", "indisponibilidade", i.Id,
            $"{Vocab.TiposInd.First(t => t.Key == i.Tipo).Label} de {Db.Tecnicos.Obter(i.TecnicoId)?.Nome}: {Tempo.Hora(i.Ini, Db.FusoPadrao, null, true)} a {Tempo.Hora(i.Fim, Db.FusoPadrao, null, true)}");
        return i;
    }

    public void ExcluirIndisponibilidade(Ator ator, string id)
    {
        ator.Exigir(Perm.EditarAgenda);
        var i = Db.Indisponibilidades.Obter(id) ?? throw new ValidacaoException("Registro não encontrado.");
        Db.Indisponibilidades.Apagar(id);
        Auditar(ator, "indisponibilidade.excluir", "indisponibilidade", id, $"{i.Tipo} de {Db.Tecnicos.Obter(i.TecnicoId)?.Nome} removida");
    }

    // --------------------------------------------------------- reagendamento
    public sealed record SimulacaoReagendar(List<Conflito> Conflitos, List<string> Impedimentos, int TecnicosAfetados, string Descricao)
    {
        public bool Bloqueado => Impedimentos.Count > 0 || Conflitos.Any(c => c.Erro);
    }

    /// <summary>Calcula o efeito de mover um evento em "delta" SEM gravar nada.</summary>
    public SimulacaoReagendar SimularReagendar(Ator ator, string fonte, string fonteId, TimeSpan delta, bool viagemInteira)
    {
        var snap = Foto();
        var imp = new List<string>();
        if (!ator.Pode(Perm.EditarAgenda)) imp.Add("Seu perfil não pode reagendar.");
        string desc = ""; int afetados = 0;
        string? viagemId = null;
        if (fonte == "ind")
        {
            var i = snap.Indisp.FirstOrDefault(x => x.Id == fonteId);
            if (i is null) imp.Add("Registro não encontrado."); else { i.Ini += delta; i.Fim += delta; desc = "Indisponibilidade"; afetados = 1; }
        }
        else if (fonte == "atend")
        {
            var a = snap.Atendimentos.FirstOrDefault(x => x.Id == fonteId);
            if (a is null) { imp.Add("Atendimento não encontrado."); }
            else
            {
                viagemId = a.ViagemId; desc = "Atendimento"; afetados = Snapshot.Ids(a.TecnicoIds).Length;
                if (!viagemInteira || string.IsNullOrEmpty(viagemId)) { a.IniPrev += delta; a.FimPrev += delta; }
            }
        }
        else if (fonte == "trecho")
        {
            var t = snap.Trechos.FirstOrDefault(x => x.Id == fonteId);
            if (t is null) imp.Add("Trecho não encontrado.");
            else
            {
                viagemId = t.ViagemId; desc = "Trecho de viagem"; afetados = Math.Max(1, Snapshot.Ids(t.TecnicoIds).Length);
                if (!viagemInteira) { Deslocar(t, delta); }
            }
        }
        if (viagemId is not null && snap.Viagens.TryGetValue(viagemId, out var v))
        {
            if (v.Status is Vocab.ViagemConcluida or Vocab.ViagemCancelada)
                imp.Add($"A viagem {v.Codigo} está {Vocab.Get(Vocab.StatusViagem, v.Status).Label.ToLowerInvariant()} e não pode ser reagendada.");
            if (viagemInteira)
            {
                foreach (var t in snap.Trechos.Where(t => t.ViagemId == viagemId)) Deslocar(t, delta);
                foreach (var a in snap.Atendimentos.Where(a => a.ViagemId == viagemId)) { a.IniPrev += delta; a.FimPrev += delta; }
                desc = $"Viagem {v.Codigo} inteira"; afetados = Snapshot.Ids(v.TecnicoIds).Length;
            }
        }
        var eventos = AgendaEngine.Eventos(snap);
        var original = AgendaEngine.Eventos(Foto());
        var antes = AgendaEngine.Conflitos(snap, original).Select(Chave).ToHashSet();
        var novos = AgendaEngine.Conflitos(snap, eventos).Where(c => !antes.Contains(Chave(c))).ToList();
        return new SimulacaoReagendar(novos, imp, afetados, desc);
    }

    private static string Chave(Conflito c) => $"{c.TecnicoId}|{c.Tipo}|{c.A.FonteId}|{c.B.FonteId}";

    private static void Deslocar(Trecho t, TimeSpan d)
    {
        if (t.PartidaPrev.HasValue) t.PartidaPrev += d;
        if (t.ChegadaPrev.HasValue) t.ChegadaPrev += d;
    }

    public void Reagendar(Ator ator, string fonte, string fonteId, TimeSpan delta, bool viagemInteira, bool aceitarAvisos)
    {
        ator.Exigir(Perm.EditarAgenda);
        var sim = SimularReagendar(ator, fonte, fonteId, delta, viagemInteira);
        if (sim.Impedimentos.Count > 0) throw new ValidacaoException(sim.Impedimentos);
        var erros = sim.Conflitos.Where(c => c.Erro).ToList();
        if (erros.Count > 0) throw new ValidacaoException(new[] { "O reagendamento gera conflito e não foi salvo:" }.Concat(erros.Select(c => c.Motivo)));
        if (sim.Conflitos.Count > 0 && !aceitarAvisos)
            throw new ValidacaoException(new[] { "O reagendamento gera avisos que precisam ser confirmados:" }.Concat(sim.Conflitos.Select(c => c.Motivo)));

        string resumo;
        if (fonte == "ind")
        {
            var i = Db.Indisponibilidades.Obter(fonteId)!; i.Ini += delta; i.Fim += delta;
            Db.Indisponibilidades.Salvar(i, ator.Login); resumo = $"Indisponibilidade de {Db.Tecnicos.Obter(i.TecnicoId)?.Nome} movida {Tempo.Duracao(delta)}";
        }
        else
        {
            string? viagemId = fonte == "atend" ? Db.Atendimentos.Obter(fonteId)?.ViagemId : Db.Trechos.Obter(fonteId)?.ViagemId;
            if (viagemInteira && !string.IsNullOrEmpty(viagemId))
            {
                var ts = Db.Trechos.Onde(t => t.ViagemId == viagemId); foreach (var t in ts) Deslocar(t, delta); Db.Trechos.SalvarVarios(ts, ator.Login);
                var ats = Db.Atendimentos.Onde(a => a.ViagemId == viagemId); foreach (var a in ats) { a.IniPrev += delta; a.FimPrev += delta; }
                Db.Atendimentos.SalvarVarios(ats, ator.Login);
                resumo = $"Viagem {Db.Viagens.Obter(viagemId)?.Codigo} reagendada ({(delta < TimeSpan.Zero ? "−" : "+")}{Tempo.Duracao(delta)})";
            }
            else if (fonte == "atend")
            {
                var a = Db.Atendimentos.Obter(fonteId)!; a.IniPrev += delta; a.FimPrev += delta; Db.Atendimentos.Salvar(a, ator.Login);
                resumo = $"Atendimento em {Foto().NomePlanta(a.PlantaId)} reagendado ({(delta < TimeSpan.Zero ? "−" : "+")}{Tempo.Duracao(delta)})";
            }
            else
            {
                var t = Db.Trechos.Obter(fonteId)!; Deslocar(t, delta); Db.Trechos.Salvar(t, ator.Login);
                resumo = $"Trecho {t.Origem}→{t.Destino} reagendado ({(delta < TimeSpan.Zero ? "−" : "+")}{Tempo.Duracao(delta)})";
            }
        }
        Auditar(ator, "agenda.reagendar", fonte, fonteId, resumo);
    }

    // ------------------------------------------------------ confirmar local
    public ConfirmacaoLocal ConfirmarLocal(Ator ator, string tecnicoId, string plantaId, string texto, double? lat, double? lon, string obs)
    {
        ator.Exigir(Perm.ConfirmarLocal, tecnicoId);
        var tec = Db.Tecnicos.Obter(tecnicoId) ?? throw new ValidacaoException("Técnico não encontrado.");
        var planta = Db.Plantas.Obter(plantaId);
        if (planta is null && string.IsNullOrWhiteSpace(texto)) throw new ValidacaoException("Informe a planta ou descreva o local.");
        if (planta is not null && string.IsNullOrWhiteSpace(texto)) texto = Foto().NomePlanta(planta.Id);
        if ((lat.HasValue ^ lon.HasValue) || !(lat is null || Geo.CoordValida(lat, lon))) throw new ValidacaoException("Coordenadas inválidas.");
        var c = Db.Confirmacoes.Salvar(new ConfirmacaoLocal
        {
            TecnicoId = tecnicoId, PlantaId = plantaId, Texto = texto, Lat = lat ?? planta?.Lat, Lon = lon ?? planta?.Lon,
            Quando = DateTime.UtcNow, Por = ator.Nome, Origem = ator.Papel == Roles.Tecnico ? "tecnico" : "gestao", Obs = obs,
        }, ator.Login);
        Auditar(ator, "local.confirmar", "tecnico", tecnicoId, $"Local de {tec.Nome} confirmado: {texto}");
        return c;
    }

    // ---------------------------------------------------------- hash de senha
    public static (string hash, string salt) HashSenha(string senha)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var h = Rfc2898DeriveBytes.Pbkdf2(senha, salt, 100_000, HashAlgorithmName.SHA256, 32);
        return (Convert.ToBase64String(h), Convert.ToBase64String(salt));
    }

    public static bool ConferirSenha(Usuario u, string senha)
    {
        if (string.IsNullOrEmpty(u.Hash) || string.IsNullOrEmpty(u.Salt)) return false;
        var h = Rfc2898DeriveBytes.Pbkdf2(senha, Convert.FromBase64String(u.Salt), 100_000, HashAlgorithmName.SHA256, 32);
        return CryptographicOperations.FixedTimeEquals(h, Convert.FromBase64String(u.Hash));
    }
}

public sealed partial class Servicos
{
    /// <summary>Prévia (sem gravar) de um atendimento avulso: conflitos e aptidão dos técnicos.</summary>
    public (List<Conflito> conflitos, List<Aptidao> aptidoes) PreviaAtendimento(Atendimento a)
    {
        var snap = Foto();
        var cand = a.Copia<Atendimento>();
        if (string.IsNullOrEmpty(cand.Id)) cand.Id = "_prev";
        snap.Atendimentos.RemoveAll(x => x.Id == cand.Id);
        if (cand.FimPrev > cand.IniPrev) snap.Atendimentos.Add(cand);
        var conflitos = new List<Conflito>();
        if (cand.Status != Vocab.ViagemCancelada)
        {
            var ev = AgendaEngine.Eventos(snap);
            var meus = ev.Where(e => e.FonteId == cand.Id).Select(e => e.Id).ToHashSet();
            conflitos = AgendaEngine.Conflitos(snap, ev).Where(c => meus.Contains(c.A.Id) || meus.Contains(c.B.Id)).ToList();
        }
        var aptidoes = new List<Aptidao>();
        var hoje = DocEngine.Hoje(snap.FusoPadrao);
        var reqs = DocEngine.RequisitosDe(snap, new[] { cand.ServicoId }, new[] { cand.PlantaId });
        foreach (var tid in Snapshot.Ids(cand.TecnicoIds))
            if (snap.Tecnico(tid) is { } t && cand.FimPrev > cand.IniPrev)
                aptidoes.Add(DocEngine.Avaliar(snap, t, reqs, cand.IniPrev, cand.FimPrev, hoje, snap.Planta(cand.PlantaId)?.FusoHorario));
        return (conflitos, aptidoes);
    }

    public List<Conflito> PreviaIndisp(Indisponibilidade i)
    {
        var snap = Foto();
        var cand = i.Copia<Indisponibilidade>();
        if (string.IsNullOrEmpty(cand.Id)) cand.Id = "_prev";
        snap.Indisp.RemoveAll(x => x.Id == cand.Id);
        if (cand.Fim > cand.Ini) snap.Indisp.Add(cand);
        var ev = AgendaEngine.Eventos(snap);
        var meus = ev.Where(e => e.FonteId == cand.Id).Select(e => e.Id).ToHashSet();
        return AgendaEngine.Conflitos(snap, ev).Where(c => meus.Contains(c.A.Id) || meus.Contains(c.B.Id)).ToList();
    }
}
