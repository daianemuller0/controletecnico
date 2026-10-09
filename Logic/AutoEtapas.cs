namespace ControleTecnico.Logic;

/// <summary>Etapas do modo automático da apresentação (a última, "agenda", roda na página Agenda).</summary>
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
        new("agenda", "Agenda", "Agenda", "#2a7f62", "calendar", "Compromissos de toda a equipe"),
    };
    public const int IndiceAgenda = 9;
}
