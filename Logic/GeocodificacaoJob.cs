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

    public bool Iniciar(Ator ator)
    {
        ator.Exigir(Perm.EditarClientes);
        if (!_geo.Configurado) throw new ValidacaoException(_geo.Pendencia);
        lock (_l)
        {
            if (Rodando) return false;
            Rodando = true; Erro = null; Feitas = Localizadas = Pendentes = 0; Fim = null; Inicio = DateTime.UtcNow; Atual = "";
            _cts = new CancellationTokenSource();
        }
        var alvo = _db.Plantas.Onde(p => p.Ativo && !p.TemCoord && p.GeoStatus != "manual" && Servicos.TemLocal(p))
            .OrderBy(p => p.Pais).ThenBy(p => p.Estado).ThenBy(p => p.Cidade).ToList();   // agrupa por cidade: aproveita o cache de consultas
        Total = alvo.Count;
        var ct = _cts.Token;
        _ = Task.Run(() => Executar(ator, alvo, ct));
        Mudou?.Invoke();
        return true;
    }

    public void Parar() { lock (_l) _cts?.Cancel(); }

    private async Task Executar(Ator ator, List<Planta> alvo, CancellationToken ct)
    {
        var lote = new List<Planta>(); var falhasSeguidas = 0;
        void Gravar() { if (lote.Count > 0) { _db.Plantas.SalvarVarios(lote, ator.Login); lote.Clear(); } }
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
                    if (++falhasSeguidas >= 3) { Erro = $"Interrompido: o serviço de mapas não respondeu corretamente ({r.Mensagem}). As já localizadas foram gravadas; tente novamente mais tarde."; break; }
                    continue;
                }
                falhasSeguidas = 0;
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
