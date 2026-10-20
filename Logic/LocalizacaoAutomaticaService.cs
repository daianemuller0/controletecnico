using ControleTecnico.Data;

namespace ControleTecnico.Logic;

/// <summary>
/// Localiza no mapa, sozinho e em segundo plano, as plantas que ninguém localizou (importadas, recém-cadastradas, ou não achadas
/// por uma versão antiga da busca) — antes só eram localizadas com o botão "Localizar pendentes" ou abrindo a planta.
/// Vai primeiro nas usadas em viagens e compromissos. Com vários computadores abertos na mesma base, só um faz o trabalho
/// (reserva gravada na base); quem clica em Parar é respeitado por algumas horas, e falha do serviço de mapas espera 1 hora.
/// </summary>
public sealed class LocalizacaoAutomaticaService : BackgroundService
{
    private readonly GeocodificacaoJob _job; private readonly Geocoder _geo; private readonly Db _db; private readonly IConfiguration _cfg; private readonly ILogger<LocalizacaoAutomaticaService> _log;
    private static readonly string Maquina = Environment.MachineName;
    public LocalizacaoAutomaticaService(GeocodificacaoJob job, Geocoder geo, Db db, IConfiguration cfg, ILogger<LocalizacaoAutomaticaService> log)
    { _job = job; _geo = geo; _db = db; _cfg = cfg; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _job.AutomaticoLigado = _cfg.GetValue("Geocoding:Automatico", true) && _geo.Configurado;
        if (!_job.AutomaticoLigado) return;
        var intervalo = TimeSpan.FromMinutes(Math.Max(1, _cfg.GetValue("Geocoding:AutomaticoMinutos", 10)));
        try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, _cfg.GetValue("Geocoding:AutomaticoAtrasoSegundos", 120))), ct); }   // não pesa na abertura
        catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            try { Rodada(DateTime.UtcNow); }
            catch (Exception ex) { _log.LogWarning("Localização automática adiada: {Msg}", ex.Message); }
            try { await Task.Delay(intervalo, ct); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Uma verificação: inicia a rodada se houver o que localizar e ninguém mais estiver fazendo. Devolve se iniciou.</summary>
    public bool Rodada(DateTime agora)
    {
        if (_job.Rodando) { if (_job.Automatico) Reservar(agora); return false; }
        if (_job.ParadoPeloUsuarioEm is { } pu && agora - pu < TimeSpan.FromHours(6)) return false;
        if (_job.Automatico && _job.Erro is not null && _job.Fim is { } f && agora - f < TimeSpan.FromHours(1)) return false;
        if (_job.FaltamAutomatico() == 0) return false;
        if (!Reservar(agora)) return false;
        var iniciou = _job.IniciarAutomatico();
        if (iniciou) _log.LogInformation("Localização automática: {N} planta(s) para localizar no mapa.", _job.Total);
        return iniciou;
    }

    /// <summary>Reserva (ou renova) a vez deste computador por 20 minutos. Outro computador com reserva válida = este não faz.</summary>
    private bool Reservar(DateTime agora)
    {
        var atual = _db.Cfg("geo_auto_reserva").Split('|');
        if (atual.Length == 2 && atual[0] != Maquina && DateTime.TryParse(atual[1], null, System.Globalization.DateTimeStyles.RoundtripKind, out var ate) && ate > agora) return false;
        _db.SetCfg("geo_auto_reserva", $"{Maquina}|{agora.AddMinutes(20):o}", "sistema");
        return true;
    }
}
