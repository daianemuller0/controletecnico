using ControleTecnico.Data;
using ControleTecnico.Models;

namespace ControleTecnico.Logic;

/// <summary>Localiza no mapa, em segundo plano, as plantas ainda sem coordenadas (milhares, no ritmo do serviço de mapas).
/// Roda no servidor, independente da página: fechar o navegador não interrompe, e cada lote fica gravado — se o programa
/// reiniciar, é só iniciar de novo que continua das que faltam.</summary>
public sealed class GeocodificacaoJob
{
    private readonly Db _db; private readonly Servicos _svc; private readonly Geocoder _geo;
    private readonly object _l = new(); private CancellationTokenSource? _cts;

    public GeocodificacaoJob(Db db, Servicos svc, Geocoder geo) { _db = db; _svc = svc; _geo = geo; }

    public bool Rodando { get; private set; }
    public int Total { get; private set; }
    public int Feitas { get; private set; }
    public int Localizadas { get; private set; }
    public int Pendentes { get; private set; }
    public string Atual { get; private set; } = "";
    public string? Erro { get; private set; }
    public DateTime? Inicio { get; private set; }
    public DateTime? Fim { get; private set; }
    public event Action? Mudou;

    public TimeSpan? Restante => Rodando && Feitas > 5 && Inicio is { } i
        ? TimeSpan.FromSeconds((DateTime.UtcNow - i).TotalSeconds / Feitas * (Total - Feitas)) : null;

    public int Faltam() => _db.Plantas.Onde(p => p.Ativo && !p.TemCoord && p.GeoStatus != "manual" && Servicos.TemLocal(p)).Count;

    /// <summary>Plantas com posição só aproximada (cidade/CEP ou localizadas antes da conferência de precisão).</summary>
    public int Aproximadas() => _db.Plantas.Onde(p => p.Ativo && Aproximada(p) && Servicos.TemLocal(p)).Count;
    private static bool Aproximada(Planta p) => p.TemCoord && p.GeoStatus != "manual" && !Snapshot.Exata(Snapshot.NivelPlanta(p));

    /// <summary>Rodada atual iniciada pela localização automática (não por um clique).</summary>
    public bool Automatico { get; private set; }
    /// <summary>A localização automática está ligada nesta instalação (Geocoding:Automatico).</summary>
    public bool AutomaticoLigado { get; set; }
    /// <summary>Quando alguém clicou em Parar: a automática respeita e espera algumas horas.</summary>
    public DateTime? ParadoPeloUsuarioEm { get; private set; }

    public bool Iniciar(Ator ator, bool reconferirAproximadas = false) =>
        IniciarCom(ator, p => p.Ativo && ((!p.TemCoord && p.GeoStatus != "manual") || (reconferirAproximadas && Aproximada(p))) && Servicos.TemLocal(p), false);

    /// <summary>Rodada automática: pendentes ainda não consultadas (ou consultadas por uma versão antiga da busca, ou há mais de 30 dias)
    /// e aproximadas de versão antiga. As usadas em viagens e compromissos vão primeiro: são as que aparecem no mapa.</summary>
    public bool IniciarAutomatico()
    {
        var desde = AlgoritmoDesde(); var agora = DateTime.UtcNow;
        var sistema = new Ator { Login = "sistema", Nome = "Localização automática", Papel = Roles.Admin };
        return IniciarCom(sistema, p => ElegivelAutomatico(p, agora, desde), true);
    }

    public int FaltamAutomatico() { var desde = AlgoritmoDesde(); var agora = DateTime.UtcNow; return _db.Plantas.Onde(p => ElegivelAutomatico(p, agora, desde)).Count; }

    private DateTime? AlgoritmoDesde() => DateTime.TryParse(_db.Cfg("geo_algoritmo_em"), null, System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d : null;

    public static bool ElegivelAutomatico(Planta p, DateTime agora, DateTime? algoritmoDesde)
    {
        if (!p.Ativo || p.GeoStatus == "manual" || !Servicos.TemLocal(p)) return false;
        var t = p.GeoTentativaEm;
        var versaoAntiga = t is null || (algoritmoDesde is { } d && t < d);
        if (!p.TemCoord) return versaoAntiga || t < agora.AddDays(-30);
        return Aproximada(p) && versaoAntiga;
    }

    private bool IniciarCom(Ator ator, Func<Planta, bool> filtro, bool automatico)
    {
        ator.Exigir(Perm.EditarClientes);
        if (!_geo.Configurado) throw new ValidacaoException(_geo.Pendencia);
        lock (_l)
        {
            if (Rodando) return false;
            Rodando = true; Erro = null; Feitas = Localizadas = Pendentes = 0; Fim = null; Inicio = DateTime.UtcNow; Atual = ""; Automatico = automatico;
            if (!automatico) ParadoPeloUsuarioEm = null;
            _cts = new CancellationTokenSource();
        }
        var emUso = new HashSet<string>(_db.Viagens.Todos().Select(v => v.PlantaId)
            .Concat(_db.Atendimentos.Todos().Select(a => a.PlantaId))
            .Concat(_db.Trechos.Todos().SelectMany(t => new[] { t.OrigemPlantaId, t.DestPlantaId }))
            .Where(x => !string.IsNullOrEmpty(x)));
        var alvo = _db.Plantas.Onde(filtro)
            .OrderBy(p => emUso.Contains(p.Id) ? 0 : 1)                                   // primeiro as que aparecem no mapa (viagens, compromissos)
            .ThenBy(p => p.Pais).ThenBy(p => p.Estado).ThenBy(p => p.Cidade).ToList();   // agrupa por cidade: aproveita o cache de consultas
        Total = alvo.Count;
        var ct = _cts.Token;
        _ = Task.Run(() => Executar(ator, alvo, ct));
        Mudou?.Invoke();
        return true;
    }

    public void Parar() { lock (_l) { if (Rodando) ParadoPeloUsuarioEm = DateTime.UtcNow; _cts?.Cancel(); } }

    private async Task Executar(Ator ator, List<Planta> alvo, CancellationToken ct)
    {
        var lote = new List<Planta>(); var falhasSeguidas = 0;
        void Gravar()
        {
            if (lote.Count == 0) return;
            // a rodada é longa: grava só a posição sobre a versão ATUAL de cada planta (quem editou nesse meio-tempo não perde nada)
            var gravar = new List<Planta>();
            foreach (var p in lote)
            {
                var atual = _db.Plantas.Obter(p.Id);
                if (atual is null || atual.GeoStatus == "manual" || Servicos.ChaveEndereco(atual) != Servicos.ChaveEndereco(p)) continue;
                atual.Lat = p.Lat; atual.Lon = p.Lon; atual.GeoStatus = p.GeoStatus; atual.GeoNivel = p.GeoNivel; atual.GeoFonte = p.GeoFonte;
                atual.GeoEm = p.GeoEm; atual.GeoTentativaEm = p.GeoTentativaEm;
                gravar.Add(atual);
            }
            if (gravar.Count > 0) _db.Plantas.SalvarVarios(gravar, ator.Login);
            lote.Clear();
        }
        try
        {
            foreach (var p in alvo)
            {
                ct.ThrowIfCancellationRequested();
                Atual = p.Nome;
                var r = await Servicos.Localizar(_geo, p, ct);
                if (r.ErroServico)
                {
                    // o serviço recusou/está fora: insistir pode causar bloqueio do IP — para e avisa
                    if (++falhasSeguidas >= 3) { Erro = $"Interrompido — {r.Mensagem} As já localizadas foram gravadas" + (Automatico ? "; a localização automática tenta de novo em 1 hora." : "; tente novamente mais tarde."); break; }
                    continue;
                }
                falhasSeguidas = 0;
                if (p.TemCoord)
                {
                    // já tinha posição aproximada: só troca se a nova for MAIS precisa (nunca piora)
                    if (r.Ok && Geocoder.Precisao(r.Nivel) > Geocoder.Precisao(Snapshot.NivelPlanta(p))) Servicos.AplicarGeo(p, r);
                    p.GeoTentativaEm = DateTime.UtcNow; lote.Add(p);                         // registra a conferência: a automática não repete
                    Feitas++; if (Snapshot.Exata(Snapshot.NivelPlanta(p))) Localizadas++; else Pendentes++;
                    if (lote.Count >= 50) Gravar();
                    if (Feitas % 10 == 0) Mudou?.Invoke();
                    continue;
                }
                Servicos.AplicarGeo(p, r); lote.Add(p);
                Feitas++; if (p.TemCoord) Localizadas++; else Pendentes++;
                if (lote.Count >= 50) Gravar();
                if (Feitas % 10 == 0) Mudou?.Invoke();
            }
        }
        catch (OperationCanceledException) { Erro = "Interrompido pelo usuário. O que já foi localizado ficou gravado."; }
        catch (Exception ex) { Erro = "Falha inesperada: " + ex.Message; }
        finally
        {
            try { Gravar(); } catch (Exception ex) { Erro ??= "Falha ao gravar o último lote: " + ex.Message; }
            _svc.Auditar(ator, "planta.geocodificar", "planta", "", $"Localização em lote: {Localizadas} localizada(s), {Pendentes} pendente(s) de {Total}" + (Erro is null ? "" : $" — {Erro}"));
            lock (_l) { Rodando = false; Fim = DateTime.UtcNow; Atual = ""; }
            Mudou?.Invoke();
        }
    }
}
