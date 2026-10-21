namespace ControleTecnico.Logic;

/// <summary>Mostra cada etapa da inicialização (no terminal e na tela de carregamento do navegador)
/// e avisa quando está esperando a pasta de rede (VPN lenta ou fora).</summary>
public static class Inicio
{
    private static readonly System.Diagnostics.Stopwatch Relogio = System.Diagnostics.Stopwatch.StartNew();
    private static string _etapa = "";
    private static long _inicioEtapa;
    private static Timer? _vigia;

    /// <summary>Estado para a tela de carregamento: etapa atual, porcentagem (0–100), detalhe e falha.</summary>
    public static string Texto { get; private set; } = "Abrindo o Controle Técnico …";
    public static int Pct { get; private set; }
    public static string Detalhe { get; private set; } = "";
    public static string? Erro { get; private set; }
    public static bool Concluido { get; private set; }

    public static void Etapa(string texto, int? pct = null)
    {
        _etapa = texto; _inicioEtapa = Relogio.ElapsedMilliseconds;
        Texto = texto; Detalhe = ""; Erro = null; if (pct is { } p) Pct = Math.Clamp(p, 0, 100);
        Console.WriteLine($"[controletecnico] {Relogio.Elapsed.TotalSeconds,5:0.0}s  {texto}");
        _vigia ??= new Timer(_ =>
        {
            var parado = (Relogio.ElapsedMilliseconds - _inicioEtapa) / 1000;
            if (_etapa != "" && parado >= 10)
            {
                Console.WriteLine($"[controletecnico]        ainda em: {_etapa} ({parado} s) — aguardando a pasta de rede; confira se a VPN está conectada.");
                if (Detalhe == "" || Detalhe.StartsWith("Aguardando")) Detalhe = $"Aguardando a pasta de rede há {parado} s — confira se a VPN está conectada.";
            }
        }, null, 10000, 10000);
    }

    /// <summary>Avanço dentro da etapa (ex.: tabelas carregadas, MB copiados).</summary>
    public static void Progresso(int pct, string detalhe = "") { Pct = Math.Clamp(pct, 0, 100); Detalhe = detalhe; }

    /// <summary>Falha que impede abrir (ex.: sem acesso à base): a tela de carregamento mostra e oferece tentar de novo.</summary>
    public static void Falha(string mensagem) { Erro = mensagem; Console.Error.WriteLine("[controletecnico] ERRO: " + mensagem); }

    public static void Pronto(string url)
    {
        _etapa = ""; _vigia?.Dispose(); _vigia = null; Pct = 100; Texto = "Pronto"; Detalhe = ""; Erro = null; Concluido = true;
        Console.WriteLine($"[controletecnico] {Relogio.Elapsed.TotalSeconds,5:0.0}s  PRONTO — abra {url} no navegador.");
    }
}
