using System.Diagnostics;

namespace ControleTecnico.Logic;

/// <summary>
/// Programa publicado na pasta da rede (\\BZVCPFIL003\proj_ramires$\Controletec): quem abre o ControleTecnico.exe de lá recebe
/// uma CÓPIA desta versão no próprio computador (%LOCALAPPDATA%\ControleTecnico\versoes, só na primeira vez de cada versão)
/// e é a cópia que roda. Abre rápido mesmo pela VPN e a pasta da rede fica livre para publicar versões novas.
/// </summary>
public static class Lancador
{
    /// <summary>Versão gravada pela publicação (versao.txt). Vazio no "dotnet run".</summary>
    public static string Versao { get; } = LerVersao(AppContext.BaseDirectory);
    /// <summary>Pasta da rede de onde esta cópia veio (para avisar quando sair versão nova).</summary>
    public static string? Origem { get; } = Environment.GetEnvironmentVariable("CONTROLETECNICO_ORIGEM");
    /// <summary>Versão nova publicada na rede enquanto o programa está aberto.</summary>
    public static string? VersaoNova { get; private set; }
    public static event Action? VersaoNovaPublicada;
    private static Timer? _vigia;

    private static string LerVersao(string pasta)
    {
        try { var f = Path.Combine(pasta, "versao.txt"); return File.Exists(f) ? File.ReadAllText(f).Trim() : ""; } catch { return ""; }
    }

    /// <summary>Publicado (wwwroot ao lado do .exe): a pasta do programa é a raiz do conteúdo, de onde quer que tenha sido aberto.</summary>
    public static string? RaizConteudo() => Directory.Exists(Path.Combine(AppContext.BaseDirectory, "wwwroot")) ? AppContext.BaseDirectory : null;

    public static bool EhPastaDeRede(string pasta)
    {
        if (Environment.GetEnvironmentVariable("CONTROLETECNICO_FORCAR_COPIA") == "1") return true;
        if (!OperatingSystem.IsWindows()) return false;
        if (pasta.StartsWith(@"\\")) return true;
        try { return new DriveInfo(Path.GetPathRoot(pasta)!).DriveType == DriveType.Network; } catch { return false; }
    }

    /// <summary>Aberto da pasta da rede: copia esta versão para o computador (se ainda não tiver) e abre a cópia local.
    /// Devolve true quando a cópia local assumiu (este processo só encerra).</summary>
    public static bool AbrirCopiaLocal(string[] args)
    {
        var origem = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (Versao == "" || Origem is not null || args.Contains("--na-rede") || !EhPastaDeRede(origem)) return false;
        try
        {
            var raiz = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ControleTecnico", "versoes");
            var cfg = Path.Combine(origem, "appsettings.json");
            // a mesma versão com o appsettings.json alterado na rede vira outra cópia
            var id = string.Concat(Versao.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' ? c : '_')) + (File.Exists(cfg) ? "-" + File.GetLastWriteTimeUtc(cfg).Ticks.ToString("x") : "");
            var destino = Path.Combine(raiz, id);
            if (!File.Exists(Path.Combine(destino, ".completa")))
            {
                Console.WriteLine($"[controletecnico] Copiando a versão {Versao} para este computador (só na primeira vez de cada versão) …");
                var sw = Stopwatch.StartNew();
                var tmp = destino + ".tmp-" + Guid.NewGuid().ToString("N")[..6];
                Copiar(origem, tmp);
                File.WriteAllText(Path.Combine(tmp, ".completa"), DateTime.Now.ToString("o"));
                try { if (Directory.Exists(destino)) Directory.Delete(destino, true); Directory.Move(tmp, destino); }
                catch { if (!File.Exists(Path.Combine(destino, ".completa"))) throw; try { Directory.Delete(tmp, true); } catch { } }   // outra janela copiou junto
                Console.WriteLine($"[controletecnico] Cópia pronta em {sw.Elapsed.TotalSeconds:0} s.");
            }
            LimparAntigas(raiz, destino);
            var exe = Path.Combine(destino, Path.GetFileName(Environment.ProcessPath ?? "ControleTecnico.exe"));
            var psi = new ProcessStartInfo(exe) { WorkingDirectory = destino, UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.Environment["CONTROLETECNICO_ORIGEM"] = origem;
            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[controletecnico] Não foi possível copiar para este computador ({ex.Message}); abrindo direto da rede.");
            return false;
        }
    }

    private static void Copiar(string de, string para)
    {
        Directory.CreateDirectory(para);
        foreach (var f in Directory.GetFiles(de)) File.Copy(f, Path.Combine(para, Path.GetFileName(f)), true);
        foreach (var d in Directory.GetDirectories(de)) Copiar(d, Path.Combine(para, Path.GetFileName(d)));
    }

    /// <summary>Guarda só a versão atual e a anterior (as que estiverem abertas não são apagadas).</summary>
    private static void LimparAntigas(string raiz, string atual)
    {
        try
        {
            var antigas = new DirectoryInfo(raiz).GetDirectories().Where(d => !string.Equals(d.FullName, atual, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(d => d.LastWriteTimeUtc).Skip(1);
            foreach (var d in antigas) try { d.Delete(true); } catch { }
        }
        catch { }
    }

    /// <summary>Segundo clique no atalho com o programa já aberto: só abre o navegador.</summary>
    public static bool JaAberto(string url)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            return http.GetAsync(url.TrimEnd('/') + "/saude").GetAwaiter().GetResult().IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public static void AbrirNavegador(string url)
    {
        if (!OperatingSystem.IsWindows()) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    /// <summary>Confere a cada 5 min se saiu versão nova na rede (para avisar na tela).</summary>
    public static void VigiarVersaoNova()
    {
        if (Origem is null || Versao == "") return;
        _vigia = new Timer(_ =>
        {
            var v = LerVersao(Origem);
            if (v != "" && v != Versao && v != VersaoNova) { VersaoNova = v; VersaoNovaPublicada?.Invoke(); }
        }, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5));
    }
}
