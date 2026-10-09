using System.Text.Json;

namespace ControleTecnico.Logic;

/// <summary>Etapas do modo automático da apresentação e a configuração de quais aparecem (guia Administração → Apresentação).</summary>
public static class AutoEtapas
{
    public sealed record Etapa(string Chave, string Titulo, string Curto, string Cor, string Icone, string Sub);

    public static readonly Etapa[] Todas =
    {
        new("mapa", "Onde está cada técnico", "Mapa", "#004785", "map", "A equipe aparece no mapa, um local por vez"),
        new("disponivel", "Disponíveis", "Disponíveis", "#1f9254", "check", "Sem compromisso neste momento (inclui quem não tem programação)"),
        new("atendimento", "Em atendimento", "Atendimento", "#0369a1", "customers", "Na planta do cliente"),
        new("viagem", "Em viagem", "Viagem", "#d9822b", "plane", "Em deslocamento"),
        new("ferias", "Em férias", "Férias", "#7b4fc0", "sun", "Férias vigentes"),
        new("viagens", "Viagens previstas", "Previstas", "#004785", "route", "Viagens ainda não iniciadas"),
        new("conflitos", "Conflitos de agenda", "Conflitos", "#c0392b", "alert", "Sobreposição ou deslocamento a verificar"),
        new("docs", "Documentos a vencer / vencidos", "Docs", "#b7791f", "clock", "Técnicos com documento próximo do vencimento ou vencido"),
        new("pend", "Pendências documentais", "Pendências", "#c0392b", "shield", "Escalados sem todos os requisitos para o atendimento programado"),
        new("gerencial", "Indicadores gerenciais", "Indicadores", "#0f766e", "bar-chart", "Visão resumida da operação"),
        new("agenda", "Agenda", "Agenda", "#2a7f62", "calendar", "Compromissos de toda a equipe"),
    };

    public static Etapa De(string chave) => Todas.First(e => e.Chave == chave);

    /// <summary>Gráficos do painel gerencial (chave, rótulo).</summary>
    public static readonly (string Chave, string Rotulo)[] Graficos =
    {
        ("cartoes", "Cartões de destaque (ocupação, dias de campo, documentos)"), ("situacao", "Situação da equipe (rosca)"),
        ("semanas", "Ocupação das próximas 8 semanas"), ("meses", "Viagens e dias de campo por mês"),
        ("clientes", "Clientes com mais dias de atendimento"), ("docs", "Saúde documental"), ("transporte", "Meio de transporte"),
    };

    public static readonly (string Chave, string Rotulo)[] VistasAgenda = { ("30", "Próximos 30 dias"), ("mes", "Visão do mês") };

    public sealed class Cfg
    {
        /// <summary>Etapas que aparecem, NA ORDEM em que aparecem.</summary>
        public List<string> Etapas { get; set; } = Todas.Select(e => e.Chave).ToList();
        public List<string> Graficos { get; set; } = AutoEtapas.Graficos.Select(g => g.Chave).ToList();
        public List<string> Vistas { get; set; } = VistasAgenda.Select(v => v.Chave).ToList();
        /// <summary>lenta | normal | rapida</summary>
        public string Velocidade { get; set; } = "normal";
        public bool Repetir { get; set; } = true;

        public Cfg Normalizar()
        {
            Etapas = (Etapas ?? new()).Where(c => Todas.Any(e => e.Chave == c)).Distinct().ToList();
            Graficos = (Graficos ?? new()).Where(c => AutoEtapas.Graficos.Any(g => g.Chave == c)).Distinct().ToList();
            Vistas = (Vistas ?? new()).Where(c => VistasAgenda.Any(v => v.Chave == c)).Distinct().ToList();
            if (Velocidade is not ("lenta" or "normal" or "rapida")) Velocidade = "normal";
            return this;
        }
    }

    private static readonly JsonSerializerOptions Js = new() { PropertyNameCaseInsensitive = true };
    public static Cfg Ler(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new Cfg();
        try { return (JsonSerializer.Deserialize<Cfg>(json, Js) ?? new Cfg()).Normalizar(); } catch { return new Cfg(); }
    }
    public static string Gravar(Cfg c) => JsonSerializer.Serialize(c.Normalizar());
}
