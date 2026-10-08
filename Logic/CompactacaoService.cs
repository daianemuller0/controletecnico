using ControleTecnico.Data;

namespace ControleTecnico.Logic;

/// <summary>Em segundo plano, consolida as entidades que acumularam muitos arquivos pequenos (cada gravação cria um).
/// Seguro entre máquinas: trava na pasta compartilhada e os arquivos antigos vão para _historico.</summary>
public sealed class CompactacaoService : BackgroundService
{
    private readonly ParquetStore _store; private readonly IConfiguration _cfg; private readonly ILogger<CompactacaoService> _log;
    public CompactacaoService(ParquetStore store, IConfiguration cfg, ILogger<CompactacaoService> log) { _store = store; _cfg = cfg; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var limite = _cfg.GetValue("Data:CompactarAcimaDeArquivos", 40);
        var horas = Math.Max(1, _cfg.GetValue("Data:CompactarACadaHoras", 6));
        try { await Task.Delay(TimeSpan.FromSeconds(45), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            foreach (var e in _store.Entities().ToList())
            {
                try { if (_store.FileCount(e) >= limite) { _store.Compact(e, limite); _log.LogInformation("Entidade {Entidade} compactada.", e); } }
                catch (Exception ex) { _log.LogWarning("Compactação de {Entidade} adiada: {Msg}", e, ex.Message); }
            }
            try { await Task.Delay(TimeSpan.FromHours(horas), ct); } catch (OperationCanceledException) { return; }
        }
    }
}
