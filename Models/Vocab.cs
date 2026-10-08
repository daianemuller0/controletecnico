namespace ControleTecnico.Models;

public static class Roles
{
    public const string Admin = "admin", Gestao = "gestao", Controladoria = "controladoria", Tecnico = "tecnico", Consulta = "consulta";
    public static readonly (string Role, string Label)[] All =
    {
        (Admin, "Administrador"), (Gestao, "Gestão"), (Controladoria, "Controladoria"),
        (Tecnico, "Técnico"), (Consulta, "Consulta"),
    };
    public static string Label(string r) => All.FirstOrDefault(x => x.Role == r).Label ?? r;
    public static string Normalize(string? r)
    {
        var p = (r ?? "").Trim().ToLowerInvariant();
        return All.Any(x => x.Role == p) ? p : Consulta;
    }
}

/// <summary>Um item de vocabulário: valor gravado, rótulo, cor semântica e ícone.
/// A cor nunca é o único sinal: sempre acompanha rótulo e ícone.</summary>
public sealed record Item(string Key, string Label, string Css, string Icon, string Color);

public static class Vocab
{
    // ---- status da viagem ----
    public const string ViagemRascunho = "rascunho", ViagemPlanejada = "planejada", ViagemConfirmada = "confirmada",
        ViagemAndamento = "andamento", ViagemConcluida = "concluida", ViagemCancelada = "cancelada";

    public static readonly Item[] StatusViagem =
    {
        new(ViagemRascunho, "Rascunho", "st-neutral", "edit", "#6b7a90"),
        new(ViagemPlanejada, "Planejada", "st-info", "calendar", "#0369a1"),
        new(ViagemConfirmada, "Confirmada", "st-pos", "check", "#1f9254"),
        new(ViagemAndamento, "Em andamento", "st-warn", "play", "#b7791f"),
        new(ViagemConcluida, "Concluída", "st-neutral", "flag", "#3b4a61"),
        new(ViagemCancelada, "Cancelada", "st-neg", "x", "#c0392b"),
    };

    // ---- status operacional do técnico ----
    public const string OpDisponivel = "disponivel", OpAtendimento = "atendimento", OpViagem = "viagem",
        OpFerias = "ferias", OpIndisponivel = "indisponivel", OpSemProg = "semprog";

    public static readonly Item[] StatusOp =
    {
        new(OpDisponivel, "Disponível", "st-pos", "check", "#1f9254"),
        new(OpAtendimento, "Em atendimento", "st-info", "customers", "#0369a1"),
        new(OpViagem, "Em viagem", "st-warn", "plane", "#d9822b"),
        new(OpFerias, "Em férias", "st-vac", "sun", "#7b4fc0"),
        new(OpIndisponivel, "Indisponível", "st-neg", "ban", "#c0392b"),
        new(OpSemProg, "Sem programação", "st-neutral", "help", "#8793a6"),
    };

    // ---- tipos de evento da agenda ----
    public const string EvAtendimento = "atendimento", EvIda = "ida", EvRetorno = "retorno", EvEntre = "entre_plantas",
        EvFerias = "ferias", EvFolga = "folga", EvAfastamento = "afastamento", EvBloqueio = "bloqueio";

    public static readonly Item[] TiposEvento =
    {
        new(EvAtendimento, "Atendimento em planta", "st-info", "customers", "#0369a1"),
        new(EvIda, "Viagem de ida", "st-warn", "plane", "#d9822b"),
        new(EvRetorno, "Viagem de retorno", "st-warn", "plane", "#c26a14"),
        new(EvEntre, "Deslocamento entre plantas", "st-warn", "car", "#b7791f"),
        new(EvFerias, "Férias", "st-vac", "sun", "#7b4fc0"),
        new(EvFolga, "Folga", "st-neutral", "coffee", "#5a8f8f"),
        new(EvAfastamento, "Afastamento", "st-neg", "ban", "#c0392b"),
        new(EvBloqueio, "Bloqueio / indisponibilidade", "st-neutral", "lock", "#6b7a90"),
    };

    // ---- indisponibilidades ----
    public const string IndFerias = "ferias", IndFolga = "folga", IndAfastamento = "afastamento", IndBloqueio = "bloqueio";
    public static readonly (string Key, string Label)[] TiposInd =
    {
        (IndFerias, "Férias"), (IndFolga, "Folga"), (IndAfastamento, "Afastamento"), (IndBloqueio, "Bloqueio / outra"),
    };

    // ---- trechos ----
    public const string TrechoIda = "ida", TrechoRetorno = "retorno", TrechoEntre = "entre_plantas";
    public static readonly (string Key, string Label)[] TiposTrecho =
    {
        (TrechoIda, "Ida"), (TrechoEntre, "Entre plantas / deslocamento"), (TrechoRetorno, "Retorno"),
    };
    public const string ModalCarro = "carro", ModalAviao = "aviao", ModalOutro = "outro";
    public static readonly (string Key, string Label)[] Modais =
    {
        (ModalCarro, "Carro"), (ModalAviao, "Avião"), (ModalOutro, "Outro (a pé, ônibus, trem…)"),
    };

    // ---- documentos ----
    public const string DocValido = "valido", DocProximo = "proximo", DocVencido = "vencido", DocSemVenc = "semvenc", DocAusente = "ausente";
    public static readonly Item[] StatusDoc =
    {
        new(DocValido, "Válido", "st-pos", "check", "#1f9254"),
        new(DocProximo, "Próximo do vencimento", "st-warn", "clock", "#b7791f"),
        new(DocVencido, "Vencido", "st-neg", "alert", "#c0392b"),
        new(DocSemVenc, "Sem vencimento", "st-info", "infinity", "#0369a1"),
        new(DocAusente, "Ausente", "st-neg", "x", "#c0392b"),
    };

    public static readonly string[] CategoriasDoc =
        { "Documento pessoal", "Treinamento", "Certificação", "Norma / comprovação", "Exame / saúde", "Outro" };

    public static readonly string[] Paises =
        { "Brasil", "Argentina", "Chile", "Colômbia", "México", "Peru", "Uruguai", "Paraguai", "Estados Unidos", "Canadá",
          "Portugal", "Espanha", "Alemanha", "Reino Unido", "França", "Itália", "Índia", "China", "Outro" };

    public static readonly string[] Fusos =
        { "America/Sao_Paulo", "America/Manaus", "America/Fortaleza", "America/Cuiaba", "America/Rio_Branco", "America/Argentina/Buenos_Aires",
          "America/Santiago", "America/Bogota", "America/Mexico_City", "America/Lima", "America/Montevideo", "America/New_York", "America/Chicago",
          "America/Los_Angeles", "Europe/Lisbon", "Europe/Madrid", "Europe/Berlin", "Europe/London", "Asia/Kolkata", "Asia/Shanghai", "UTC" };

    public static Item Get(Item[] set, string? key) =>
        set.FirstOrDefault(i => i.Key == key) ?? new Item(key ?? "", key ?? "—", "st-neutral", "info", "#6b7a90");
}
