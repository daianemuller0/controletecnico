using ControleTecnico.Data;
using ControleTecnico.Models;

namespace ControleTecnico.Logic;

/// <summary>Foto consistente dos dados para os cálculos (mapa, agenda, conflitos).
/// Carregada uma vez por atualização de tela, não a cada renderização.</summary>
public sealed class Snapshot
{
    public Dictionary<string, Tecnico> Tecnicos { get; init; } = new();
    public Dictionary<string, Viagem> Viagens { get; init; } = new();
    public List<Trecho> Trechos { get; init; } = new();
    public List<Atendimento> Atendimentos { get; init; } = new();
    public List<Indisponibilidade> Indisp { get; init; } = new();
    public Dictionary<string, Planta> Plantas { get; init; } = new();
    public Dictionary<string, Cliente> Clientes { get; init; } = new();
    public Dictionary<string, Servico> Servicos { get; init; } = new();
    public Dictionary<string, Especialidade> Especialidades { get; init; } = new();
    public List<ConfirmacaoLocal> Confirmacoes { get; init; } = new();
    public List<Documento> Documentos { get; init; } = new();
    public List<Requisito> Requisitos { get; init; } = new();
    public string FusoPadrao { get; init; } = "America/Sao_Paulo";
    public int[] DiasAlerta { get; init; } = { 30, 60, 90 };
    public int ConfirmacaoHoras { get; init; } = 24;

    public static Snapshot Carregar(Db db) => new()
    {
        Tecnicos = db.Tecnicos.Todos().ToDictionary(x => x.Id),
        Viagens = db.Viagens.Todos().ToDictionary(x => x.Id),
        Trechos = db.Trechos.Todos(),
        Atendimentos = db.Atendimentos.Todos(),
        Indisp = db.Indisponibilidades.Todos(),
        Plantas = db.Plantas.Todos().ToDictionary(x => x.Id),
        Clientes = db.Clientes.Todos().ToDictionary(x => x.Id),
        Servicos = db.Servicos.Todos().ToDictionary(x => x.Id),
        Especialidades = db.Especialidades.Todos().ToDictionary(x => x.Id),
        Confirmacoes = db.Confirmacoes.Todos(),
        Documentos = db.Documentos.Todos(),
        Requisitos = db.Requisitos.Todos(),
        FusoPadrao = db.FusoPadrao,
        DiasAlerta = db.DiasAlerta,
        ConfirmacaoHoras = db.ConfirmacaoValidadeHoras,
    };

    public static string[] Ids(string? csv) =>
        (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public Planta? Planta(string? id) => !string.IsNullOrEmpty(id) && Plantas.TryGetValue(id, out var p) ? p : null;
    public Cliente? Cliente(string? id) => !string.IsNullOrEmpty(id) && Clientes.TryGetValue(id, out var p) ? p : null;
    public Servico? Servico(string? id) => !string.IsNullOrEmpty(id) && Servicos.TryGetValue(id, out var p) ? p : null;
    public Tecnico? Tecnico(string? id) => !string.IsNullOrEmpty(id) && Tecnicos.TryGetValue(id, out var p) ? p : null;
    public Viagem? Viagem(string? id) => !string.IsNullOrEmpty(id) && Viagens.TryGetValue(id, out var p) ? p : null;

    public string NomePlanta(string? id)
    {
        var p = Planta(id);
        return p is null ? "" : (Cliente(p.ClienteId)?.Nome is { } c ? $"{c} · {p.Nome}" : p.Nome);
    }
}
