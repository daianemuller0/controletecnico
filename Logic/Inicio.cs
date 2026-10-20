namespace ControleTecnico.Logic;

/// <summary>Mostra no terminal cada etapa da inicialização e avisa quando está esperando a pasta de rede (VPN lenta ou fora).</summary>
public static class Inicio
{
    private static readonly System.Diagnostics.Stopwatch Relogio = System.Diagnostics.Stopwatch.StartNew();
    private static string _etapa = "";
    private static long _inicioEtapa;
    private static Timer? _vigia;

    public static void Etapa(string texto)
    {
        _etapa = texto; _inicioEtapa = Relogio.ElapsedMilliseconds;
        Console.WriteLine($"[controletecnico] {Relogio.Elapsed.TotalSeconds,5:0.0}s  {texto}");
        _vigia ??= new Timer(_ =>
        {
            var parado = (Relogio.ElapsedMilliseconds - _inicioEtapa) / 1000;
            if (_etapa != "" && parado >= 10)
                Console.WriteLine($"[controletecnico]        ainda em: {_etapa} ({parado} s) — aguardando a pasta de rede; confira se a VPN está conectada.");
        }, null, 10000, 10000);
    }

    public static void Pronto(string url)
    {
        _etapa = ""; _vigia?.Dispose(); _vigia = null;
        Console.WriteLine($"[controletecnico] {Relogio.Elapsed.TotalSeconds,5:0.0}s  PRONTO — abra {url} no navegador.");
    }
}
