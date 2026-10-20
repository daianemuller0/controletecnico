using ControleTecnico.Data;
using ControleTecnico.Models;

namespace ControleTecnico.Logic;

/// <summary>
/// Base em pasta de rede (ex.: acesso pela VPN): confere a pasta compartilhada em SEGUNDO PLANO e atualiza a cópia local.
/// As telas nunca esperam a rede para ler — só as gravações vão direto ao servidor.
/// </summary>
public sealed class SincronizacaoService : BackgroundService
{
    private readonly Db _db; private readonly ParquetStore _store; private readonly IConfiguration _cfg; private readonly ILogger<SincronizacaoService> _log;
    public SincronizacaoService(Db db, ParquetStore store, IConfiguration cfg, ILogger<SincronizacaoService> log) { _db = db; _store = store; _cfg = cfg; _log = log; }

    /// <summary>Hora (UTC) da última conferência completa com a rede e se ela funcionou.</summary>
    public static DateTime? UltimaConferencia { get; private set; }
    public static string? UltimoErro { get; private set; }
    /// <summary>Avisa as telas que chegaram dados novos de outra máquina.</summary>
    public static event Action? DadosNovos;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_store.Remoto || !Repo<Tecnico>.SincronizarEmSegundoPlano) return;
        var intervalo = TimeSpan.FromSeconds(Math.Max(3, _cfg.GetValue("Data:SincronizarSegundos", 15)));
        while (!ct.IsCancellationRequested)
        {
            var mudou = 0; var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                // várias tabelas ao mesmo tempo: pela VPN o tempo é quase todo espera da rede
                await Parallel.ForEachAsync(_db.Repos, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, (r, _) =>
                {
                    try { if (r.ConferirRede()) Interlocked.Increment(ref mudou); }
                    catch (Exception ex) { UltimoErro = $"{r.Entidade}: {ex.Message}"; }
                    return ValueTask.CompletedTask;
                });
                UltimaConferencia = DateTime.UtcNow;
                if (mudou > 0) { _log.LogInformation("Sincronização: {N} tabela(s) atualizada(s) da pasta compartilhada em {Ms} ms.", mudou, sw.ElapsedMilliseconds); DadosNovos?.Invoke(); }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { UltimoErro = ex.Message; _log.LogWarning("Sincronização adiada: {Msg}", ex.Message); }
            try { await Task.Delay(intervalo, ct); } catch (OperationCanceledException) { return; }
        }
    }
}
