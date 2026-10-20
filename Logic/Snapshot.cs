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
    /// <summary>Local da empresa usado quando o técnico está "na Howden" (Administração → Configurações).</summary>
    public string HowdenNome { get; init; } = "Howden South America";
    public string HowdenEndereco { get; init; } = "";
    public double? HowdenLat { get; init; }
    public double? HowdenLon { get; init; }

    /// <summary>Precisão da posição da Howden (endereco | manual | link = exata).</summary>
    public string HowdenNivel { get; init; } = "";

    /// <summary>Nível de precisão da posição de uma planta: manual | endereco | cep | cidade | estado | pais | "" (sem posição).</summary>
    public static string NivelPlanta(Planta? p) =>
        p is null || !p.TemCoord ? "" : p.GeoStatus == "manual" ? "manual" : string.IsNullOrEmpty(p.GeoNivel) ? "cidade" : p.GeoNivel;   // sem nível registrado: localizada antes da conferência de precisão -> tratada como aproximada
    /// <summary>Posição que pode ir para o mapa: exata ou, no máximo, aproximada pela cidade. Estado/país nunca (ficaria "no meio do nada").</summary>
    public static bool Aproveitavel(string nivel) => Geocoder.Precisao(nivel) >= Geocoder.Precisao("cidade");
    public static bool Exata(string nivel) => Geocoder.Precisao(nivel) >= Geocoder.Precisao("endereco");

    public static Snapshot Carregar(Db db)
    {
        var ci = System.Globalization.CultureInfo.InvariantCulture; var nf = System.Globalization.NumberStyles.Float;
        var plantas = db.Plantas.Todos().ToDictionary(x => x.Id);
        // posição vaga (só estado/país) não vale como localização: some do mapa até ser corrigida
        foreach (var p in plantas.Values) if (p.TemCoord && !Aproveitavel(NivelPlanta(p))) { p.Lat = null; p.Lon = null; }
        var tecnicos = db.Tecnicos.Todos().ToDictionary(x => x.Id);
        foreach (var t in tecnicos.Values)
        {
            var vaga = t.CasaLat is not null && !string.IsNullOrEmpty(t.CasaGeoNivel) && !Aproveitavel(t.CasaGeoNivel);
            if (vaga)
            {
                if (t.OrigemLat == t.CasaLat && t.OrigemLon == t.CasaLon) { t.OrigemLat = null; t.OrigemLon = null; }   // copiada da casa vaga
                t.CasaLat = null; t.CasaLon = null;
            }
        }
        var trechos = db.Trechos.Todos();
        foreach (var t in trechos)
        {
            // a planta manda: usa a posição atual (corrigida) da planta, nunca a copiada no trecho
            if (!string.IsNullOrEmpty(t.DestPlantaId) && plantas.TryGetValue(t.DestPlantaId, out var pd)) { t.DestLat = pd.Lat; t.DestLon = pd.Lon; }
            if (!string.IsNullOrEmpty(t.OrigemPlantaId) && plantas.TryGetValue(t.OrigemPlantaId, out var po)) { t.OrigemLat = po.Lat; t.OrigemLon = po.Lon; }
        }
        var hNivel = db.Cfg("howden_nivel");
        var hExata = Exata(hNivel);
        var hNome = db.Cfg("howden_nome", "Howden South America");
        double? hLat = hExata && double.TryParse(db.Cfg("howden_lat"), nf, ci, out var hl) ? hl : null;
        double? hLon = hExata && double.TryParse(db.Cfg("howden_lon"), nf, ci, out var ho) ? ho : null;
        var confs = db.Confirmacoes.Todos();
        foreach (var c in confs)
        {
            if (c.PlantaId == ConfirmacaoLocal.Howden || (string.IsNullOrEmpty(c.PlantaId) && c.Lat is null && DocEngine.Norm(c.Texto) == DocEngine.Norm(hNome)))
            { c.Lat = hLat; c.Lon = hLon; }                                                         // "na Howden": sempre a posição exata cadastrada da empresa
            else if (c.PlantaId == ConfirmacaoLocal.Casa)
            { var tc = tecnicos.GetValueOrDefault(c.TecnicoId); c.Lat = tc?.CasaLat; c.Lon = tc?.CasaLon; }
            else if (!string.IsNullOrEmpty(c.PlantaId) && plantas.TryGetValue(c.PlantaId, out var pc)) { c.Lat = pc.Lat; c.Lon = pc.Lon; }
        }
        return new Snapshot
        {
            Tecnicos = tecnicos, Viagens = db.Viagens.Todos().ToDictionary(x => x.Id), Trechos = trechos,
            Atendimentos = db.Atendimentos.Todos(), Indisp = db.Indisponibilidades.Todos(), Plantas = plantas,
            Clientes = db.Clientes.Todos().ToDictionary(x => x.Id), Servicos = db.Servicos.Todos().ToDictionary(x => x.Id),
            Especialidades = db.Especialidades.Todos().ToDictionary(x => x.Id), Confirmacoes = confs,
            Documentos = db.Documentos.Todos(), Requisitos = db.Requisitos.Todos(),
            FusoPadrao = db.FusoPadrao, DiasAlerta = db.DiasAlerta, ConfirmacaoHoras = db.ConfirmacaoValidadeHoras,
            HowdenNome = hNome, HowdenEndereco = db.Cfg("howden_endereco"), HowdenNivel = hNivel,
            // a Howden só vai ao mapa com posição EXATA (endereço encontrado ou marcada à mão)
            HowdenLat = hLat, HowdenLon = hLon,
        };
    }

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
