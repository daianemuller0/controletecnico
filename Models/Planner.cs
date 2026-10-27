using ControleTecnico.Data;

namespace ControleTecnico.Models;

// ============================================================================================
// Planner de Manutenção: projetos, atividades (etapas e subtarefas), materiais, pendências,
// comentários, modelos e visões salvas. Mesma base (Parquet) e mesmo histórico (auditoria) do sistema.
// Datas de planejamento são DIAS (meia-noite, sem hora). Pessoas = "t:{técnico}" ou "u:{login}".
// ============================================================================================

public class PlProjeto : Entity
{
    /// <summary>PM-aaaa-0001 (gerado ao criar).</summary>
    public string Codigo { get; set; } = "";
    public string Nome { get; set; } = "";
    public string Descricao { get; set; } = "";
    /// <summary>Unidade industrial (planta). O cliente vem dela.</summary>
    public string PlantaId { get; set; } = "";
    public string ClienteId { get; set; } = "";
    /// <summary>Setor ou área dentro da planta.</summary>
    public string Area { get; set; } = "";
    public string Equipamento { get; set; } = "";
    /// <summary>preventiva | corretiva | preditiva | melhoria | retrofit | parada | outro.</summary>
    public string Tipo { get; set; } = "preventiva";
    /// <summary>Responsável principal (pessoa).</summary>
    public string Responsavel { get; set; } = "";
    /// <summary>Equipe envolvida (pessoas, csv).</summary>
    public string Equipe { get; set; } = "";
    public DateTime? IniPrev { get; set; }
    public DateTime? FimPrev { get; set; }
    public DateTime? IniReal { get; set; }
    public DateTime? FimReal { get; set; }
    /// <summary>baixa | media | alta | critica.</summary>
    public string Prioridade { get; set; } = "media";
    public string Status { get; set; } = "nao_iniciado";
    public DateTime? StatusEm { get; set; }
    public string StatusPor { get; set; } = "";
    /// <summary>Só vale quando o projeto não tem atividades (com atividades o progresso é calculado).</summary>
    public int? ProgressoManual { get; set; }
    public string Obs { get; set; } = "";
    // ---- custos (opcional; só para quem tem permissão financeira)
    public double? CustoPlanejado { get; set; }
    public double? CustoMateriais { get; set; }
    public double? CustoMaoObra { get; set; }
    public double? CustoTerceiros { get; set; }
    public double? CustoOutros { get; set; }
    public string CustoObs { get; set; } = "";
}

public class PlAtividade : Entity
{
    public string ProjetoId { get; set; } = "";
    /// <summary>Etapa/atividade mãe (vazio = etapa do primeiro nível). Subtarefas apontam para a mãe.</summary>
    public string ParentId { get; set; } = "";
    public int Ordem { get; set; }
    public string Nome { get; set; } = "";
    public string Descricao { get; set; } = "";
    public string Responsavel { get; set; } = "";
    public DateTime? ResponsavelEm { get; set; }
    public string ResponsavelPor { get; set; } = "";
    public DateTime? IniPrev { get; set; }
    public DateTime? FimPrev { get; set; }
    public DateTime? IniReal { get; set; }
    public DateTime? FimReal { get; set; }
    /// <summary>afazer | andamento | bloqueada | concluida | cancelada.</summary>
    public string Status { get; set; } = "afazer";
    public DateTime? StatusEm { get; set; }
    public string StatusPor { get; set; } = "";
    public string Prioridade { get; set; } = "media";
    public int Progresso { get; set; }
    /// <summary>Peso no progresso do projeto (padrão 1).</summary>
    public double Peso { get; set; } = 1;
    /// <summary>Atividades que precisam estar concluídas antes (csv de ids do mesmo projeto).</summary>
    public string DependeDe { get; set; } = "";
    /// <summary>JSON: [{ Texto, Feito, Obrigatorio, Por, Em }].</summary>
    public string Checklist { get; set; } = "";
    public string Obs { get; set; } = "";
    public double? HorasEst { get; set; }
    public double? HorasReal { get; set; }
    /// <summary>Marco (entrega) mostrado no calendário e no cronograma.</summary>
    public bool Marco { get; set; }
    public DateTime? DatasAlteradasEm { get; set; }
    public string DatasAlteradasPor { get; set; } = "";
}

public class PlMaterial : Entity
{
    public string ProjetoId { get; set; } = "";
    /// <summary>Atividade que depende do material (opcional): fica bloqueada enquanto o material não estiver disponível.</summary>
    public string AtividadeId { get; set; } = "";
    public string Descricao { get; set; } = "";
    public string Codigo { get; set; } = "";
    public double Quantidade { get; set; } = 1;
    public string Unidade { get; set; } = "un";
    public DateTime? DataNecessaria { get; set; }
    public string Responsavel { get; set; } = "";
    /// <summary>nao_solicitado | solicitado | comprado | disponivel | recebido.</summary>
    public string Status { get; set; } = "nao_solicitado";
    public string Obs { get; set; } = "";
}

public class PlPendencia : Entity
{
    public string ProjetoId { get; set; } = "";
    public string AtividadeId { get; set; } = "";
    public string Descricao { get; set; } = "";
    /// <summary>material | aprovacao | terceiros | outro.</summary>
    public string Tipo { get; set; } = "outro";
    public string Responsavel { get; set; } = "";
    public DateTime Abertura { get; set; }
    public DateTime? Prazo { get; set; }
    /// <summary>baixo | medio | alto | critico.</summary>
    public string Impacto { get; set; } = "medio";
    /// <summary>aberta | tratamento | resolvida.</summary>
    public string Situacao { get; set; } = "aberta";
    public string Acao { get; set; } = "";
    public DateTime? ResolvidaEm { get; set; }
}

public class PlComentario : Entity
{
    public string ProjetoId { get; set; } = "";
    public string AtividadeId { get; set; } = "";
    public string Texto { get; set; } = "";
    public string Autor { get; set; } = "";
    public string AutorNome { get; set; } = "";
    public DateTime Quando { get; set; }
}

/// <summary>Modelo reutilizável de projeto: etapas com duração, horas e peso.</summary>
public class PlModelo : Entity
{
    public string Nome { get; set; } = "";
    public string Tipo { get; set; } = "preventiva";
    /// <summary>JSON: [{ Nome, Dias, Horas, Peso }].</summary>
    public string Etapas { get; set; } = "";
}

/// <summary>Visão salva (filtros) de uma pessoa.</summary>
public class PlVisao : Entity
{
    public string Dono { get; set; } = "";
    public string Nome { get; set; } = "";
    public string Filtros { get; set; } = "";
}

/// <summary>Alerta que a pessoa marcou como visto (não aparece de novo enquanto a situação for a mesma).</summary>
public class PlAlertaLido : Entity
{
    public string Usuario { get; set; } = "";
    public string Chave { get; set; } = "";
}

public static class PlVocab
{
    public const string PAndamento = "andamento", PConcluido = "concluido", PCancelado = "cancelado", PPausado = "pausado",
        PMateriais = "materiais", PTerceiros = "terceiros", PAprovacao = "aprovacao", PNaoIniciado = "nao_iniciado", PPlanejamento = "planejamento";

    public static readonly Item[] StatusProjeto =
    {
        new(PNaoIniciado, "Não iniciado", "st-neutral", "help", "#8793a6"),
        new(PPlanejamento, "Em planejamento", "st-info", "list", "#6366f1"),
        new(PAprovacao, "Aguardando aprovação", "st-warn", "clock", "#b7791f"),
        new(PAndamento, "Em andamento", "st-info", "play", "#0369a1"),
        new(PMateriais, "Aguardando materiais", "st-warn", "layers", "#d9822b"),
        new(PTerceiros, "Aguardando terceiros", "st-warn", "users", "#c26a14"),
        new(PPausado, "Pausado", "st-neutral", "pause", "#6b7a90"),
        new(PConcluido, "Concluído", "st-pos", "check", "#1f9254"),
        new(PCancelado, "Cancelado", "st-neutral", "x", "#9aa4b4"),
    };
    /// <summary>Encerrado: não conta mais como ativo, atrasado ou pendente.</summary>
    public static bool Encerrado(string status) => status is PConcluido or PCancelado;
    /// <summary>Parado por espera: tratado à parte nos indicadores (não é "em andamento").</summary>
    public static bool EmEspera(string status) => status is PAprovacao or PMateriais or PTerceiros or PPausado;

    public const string AAfazer = "afazer", AAndamento = "andamento", ABloqueada = "bloqueada", AConcluida = "concluida", ACancelada = "cancelada";
    public static readonly Item[] StatusAtividade =
    {
        new(AAfazer, "A fazer", "st-neutral", "list", "#8793a6"),
        new(AAndamento, "Em andamento", "st-info", "play", "#0369a1"),
        new(ABloqueada, "Bloqueada", "st-neg", "ban", "#c0392b"),
        new(AConcluida, "Concluída", "st-pos", "check", "#1f9254"),
        new(ACancelada, "Cancelada", "st-neutral", "x", "#9aa4b4"),
    };
    public static bool AtivEncerrada(string status) => status is AConcluida or ACancelada;

    public static readonly Item[] Prioridades =
    {
        new("baixa", "Baixa", "st-neutral", "down", "#64748b"),
        new("media", "Média", "st-info", "info", "#0369a1"),
        new("alta", "Alta", "st-warn", "up", "#d9822b"),
        new("critica", "Crítica", "st-neg", "alert", "#c0392b"),
    };
    public static int PesoPrioridade(string p) => p switch { "critica" => 4, "alta" => 3, "media" => 2, _ => 1 };

    public static readonly (string Key, string Label)[] Tipos =
    {
        ("preventiva", "Preventiva"), ("corretiva", "Corretiva"), ("preditiva", "Preditiva"), ("melhoria", "Melhoria"),
        ("retrofit", "Retrofit"), ("parada", "Parada programada"), ("outro", "Outro"),
    };
    public static string TipoLabel(string k) => Tipos.FirstOrDefault(t => t.Key == k).Label ?? k;

    public static readonly Item[] StatusMaterial =
    {
        new("nao_solicitado", "Não solicitado", "st-neg", "alert", "#c0392b"),
        new("solicitado", "Solicitado", "st-warn", "clock", "#d9822b"),
        new("comprado", "Comprado", "st-info", "dollar", "#0369a1"),
        new("disponivel", "Disponível", "st-pos", "check", "#1f9254"),
        new("recebido", "Recebido", "st-pos", "check", "#14703f"),
    };
    public static bool MaterialOk(string status) => status is "disponivel" or "recebido";

    public static readonly (string Key, string Label)[] TiposPendencia = { ("material", "Material"), ("aprovacao", "Aprovação"), ("terceiros", "Terceiros"), ("outro", "Outro") };
    public static readonly Item[] Impactos =
    {
        new("baixo", "Baixo", "st-neutral", "down", "#64748b"), new("medio", "Médio", "st-info", "info", "#0369a1"),
        new("alto", "Alto", "st-warn", "up", "#d9822b"), new("critico", "Crítico", "st-neg", "alert", "#c0392b"),
    };
    public static readonly Item[] SituacoesPendencia =
    {
        new("aberta", "Aberta", "st-neg", "alert", "#c0392b"), new("tratamento", "Em tratamento", "st-warn", "clock", "#d9822b"),
        new("resolvida", "Resolvida", "st-pos", "check", "#1f9254"),
    };

    public static Item Get(Item[] set, string? key) => Vocab.Get(set, key);
}
