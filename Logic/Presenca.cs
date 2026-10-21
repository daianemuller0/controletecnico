using Microsoft.AspNetCore.Components.Server.Circuits;

namespace ControleTecnico.Logic;

/// <summary>
/// Sabe se o sistema ainda está aberto em algum navegador deste computador: conexões das telas interativas
/// e sinais de vida das páginas (inclusive a de login, que não é interativa). Ao fechar a aba, a página avisa que está saindo.
/// </summary>
public static class Presenca
{
    private static int _conexoes;
    public static int Conexoes => Volatile.Read(ref _conexoes);
    /// <summary>Último sinal de página aberta (carregou, está viva, ou uma tela interativa conectou).</summary>
    public static DateTime? UltimoSinal { get; private set; }
    /// <summary>Última vez que uma página avisou que estava fechando (fechar aba/navegador, recarregar, sair).</summary>
    public static DateTime? UltimaSaida { get; private set; }

    public static void Vivo() => UltimoSinal = DateTime.UtcNow;
    public static void Saindo() => UltimaSaida = DateTime.UtcNow;
    public static void Conectou() { Interlocked.Increment(ref _conexoes); Vivo(); }
    public static void Desconectou() { if (Interlocked.Decrement(ref _conexoes) < 0) Interlocked.Exchange(ref _conexoes, 0); }

    /// <summary>Motivo para encerrar agora, ou null. Recarregar a página (sai e volta em 1–2 s) não encerra.</summary>
    public static string? MotivoParaEncerrar(DateTime agora, DateTime prontoEm)
    {
        if (Conexoes > 0) return null;
        if (UltimaSaida is { } s && (UltimoSinal is null || s >= UltimoSinal) && agora - s > TimeSpan.FromSeconds(12)) return "o navegador foi fechado";
        if (UltimoSinal is null && agora - prontoEm > TimeSpan.FromMinutes(3)) return "nenhuma janela abriu o sistema";
        // navegador fechado sem avisar (travou, foi encerrado à força): nenhuma tela conectada e nenhum sinal há 10 min
        // (uma aba aberta em segundo plano manda sinal pelo menos 1 vez por minuto)
        if (UltimoSinal is { } u && agora - u > TimeSpan.FromMinutes(10)) return "nenhuma janela do sistema aberta há 10 minutos";
        return null;
    }

    public static void Reiniciar() { _conexoes = 0; UltimoSinal = null; UltimaSaida = null; }
}

public sealed class PresencaCircuitos : CircuitHandler
{
    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken ct) { Presenca.Conectou(); return Task.CompletedTask; }
    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken ct) { Presenca.Desconectou(); return Task.CompletedTask; }
}

/// <summary>Programa publicado (sem janela): fecha o servidor quando o sistema é fechado no navegador.</summary>
public sealed class EncerrarAoFecharService : BackgroundService
{
    private readonly IHostApplicationLifetime _vida; private readonly IConfiguration _cfg; private readonly ILogger<EncerrarAoFecharService> _log;
    public EncerrarAoFecharService(IHostApplicationLifetime vida, IConfiguration cfg, ILogger<EncerrarAoFecharService> log) { _vida = vida; _cfg = cfg; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_cfg.GetValue("EncerrarAoFecharNavegador", Lancador.Versao != "")) return;
        var pronto = new TaskCompletionSource();
        using var _ = _vida.ApplicationStarted.Register(() => pronto.TrySetResult());
        try { await pronto.Task.WaitAsync(ct); } catch (OperationCanceledException) { return; }
        var prontoEm = DateTime.UtcNow;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(2), ct); } catch (OperationCanceledException) { return; }
            if (Presenca.MotivoParaEncerrar(DateTime.UtcNow, prontoEm) is { } motivo)
            {
                _log.LogInformation("Encerrando: {Motivo}.", motivo);
                Console.WriteLine($"[controletecnico] Encerrando: {motivo}.");
                _vida.StopApplication();
                return;
            }
        }
    }
}
