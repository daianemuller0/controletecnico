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
    /// <summary>O navegador já foi aberto para esta abertura (pela cópia da rede ou por este processo): não abre outra aba.</summary>
    public static bool NavegadorAberto { get; private set; } = Environment.GetEnvironmentVariable("CONTROLETECNICO_NAVEGADOR_ABERTO") == "1";

    /// <summary>Publicado não tem janela: o que antes aparecia na janela preta vai para %LOCALAPPDATA%\ControleTecnico\logs.</summary>
    public static void RegistrarEmArquivo()
    {
        if (Versao == "") return;
        try
        {
            var pasta = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ControleTecnico", "logs");
            Directory.CreateDirectory(pasta);
            foreach (var f in new DirectoryInfo(pasta).GetFiles("*.log").Where(f => f.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-14))) try { f.Delete(); } catch { }
            var fs = new FileStream(Path.Combine(pasta, $"controletecnico-{DateTime.Now:yyyyMMdd}.log"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            var w = TextWriter.Synchronized(new StreamWriter(fs) { AutoFlush = true });
            Console.SetOut(w); Console.SetError(w);
            Console.WriteLine($"==== {DateTime.Now:dd/MM/yyyy HH:mm:ss} · versão {Versao} · {Environment.ProcessPath} · pid {Environment.ProcessId}");
        }
        catch { }
    }

    /// <summary>Fechado = porta livre · Ocupada = outro programa (não é o Controle Técnico) está nela.</summary>
    public enum Situacao { Fechado, Carregando, MesmaVersao, OutraVersao, Ocupada }

    /// <summary>Quantas portas seguintes à configurada (5090) são tentadas quando ela está em uso.</summary>
    public const int PortasTentadas = 30;

    /// <summary>
    /// Escolhe o endereço em que o sistema vai abrir, a partir do configurado (http://localhost:5090):
    /// a porta ocupada por outro programa é pulada e usa-se a próxima livre (5091, 5092…).
    /// Publicado: se encontrar o Controle Técnico desta versão já aberto numa dessas portas, devolve esse endereço
    /// com <see cref="Situacao.MesmaVersao"/> (só abrir o navegador); versão antiga aberta é encerrada e a porta reaproveitada.
    /// No "dotnet run", um Controle Técnico já aberto (publicado ou outro teste) também é pulado.
    /// </summary>
    public static (string url, Situacao situacao) EscolherEndereco(string urlConfigurada)
    {
        var u = new Uri(urlConfigurada);
        string Url(int porta) => new UriBuilder(u) { Port = porta }.Uri.ToString().TrimEnd('/');
        var portas = new List<int>();
        // cópia local aberta pela versão da rede: usa a porta que ela já escolheu (o navegador já está nela)
        if (Environment.GetEnvironmentVariable("CONTROLETECNICO_URL") is { Length: > 0 } herdada && Uri.TryCreate(herdada, UriKind.Absolute, out var h)) portas.Add(h.Port);
        for (var p = u.Port; p < u.Port + PortasTentadas && p <= 65535; p++) if (!portas.Contains(p)) portas.Add(p);
        foreach (var porta in portas)
        {
            var url = Url(porta);
            var s = JaAberto(url);
            if (s == Situacao.Fechado && PortaLivre(porta)) return (url, Situacao.Fechado);
            if (s == Situacao.Fechado || s == Situacao.Ocupada)
            {
                Console.WriteLine($"[controletecnico] A porta {porta} está em uso por outro programa; tentando a próxima …");
                continue;
            }
            if (Versao != "")
            {
                if (s is Situacao.MesmaVersao or Situacao.Carregando) return (url, s);
                Console.WriteLine($"[controletecnico] Fechando a versão anterior que estava aberta em {url} …");
                if (EncerrarAberto(url) && PortaLivre(porta)) return (url, Situacao.Fechado);
                continue;
            }
            Console.WriteLine($"[controletecnico] Já existe um Controle Técnico aberto em {url}; este vai abrir na próxima porta livre.");
        }
        throw new InvalidOperationException($"Nenhuma porta livre entre {u.Port} e {u.Port + PortasTentadas - 1} neste computador.");
    }

    /// <summary>A porta está livre neste computador? (ninguém escutando nela, em nenhum endereço, e dá para reservá-la)</summary>
    public static bool PortaLivre(int porta)
    {
        try
        {
            if (System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == porta)) return false;
        }
        catch { }
        try
        {
            var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, porta) { ExclusiveAddressUse = OperatingSystem.IsWindows() };
            l.Start(); l.Stop();
            return true;
        }
        catch { return false; }
    }

    /// <summary>Já existe o sistema aberto neste computador? (segundo clique no atalho, ou versão antiga ainda aberta)</summary>
    public static Situacao JaAberto(string url)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var r = http.GetAsync(url.TrimEnd('/') + "/saude").GetAwaiter().GetResult();
            if (r.Headers.TryGetValues("X-ControleTecnico", out var v) && v.FirstOrDefault() == "carregando") return Situacao.Carregando;
            // sem a marca do sistema: é outro programa usando a porta
            if (!r.Headers.Contains("X-ControleTecnico-Versao") && !r.Headers.Contains("X-ControleTecnico")) return Situacao.Ocupada;
            var versao = r.Headers.TryGetValues("X-ControleTecnico-Versao", out var vv) ? vv.FirstOrDefault() ?? "" : "";
            return versao == Versao ? Situacao.MesmaVersao : Situacao.OutraVersao;
        }
        catch { return Situacao.Fechado; }
    }

    /// <summary>Pede para a versão antiga aberta encerrar e espera liberar o endereço.</summary>
    public static bool EncerrarAberto(string url)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var req = new HttpRequestMessage(HttpMethod.Post, url.TrimEnd('/') + "/encerrar");
            req.Headers.Add("X-ControleTecnico", "encerrar");
            http.Send(req);
        }
        catch { }
        for (var i = 0; i < 30; i++) { if (JaAberto(url) == Situacao.Fechado) return true; Thread.Sleep(500); }
        return false;
    }

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
    public static bool AbrirCopiaLocal(string[] args, string url)
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
                // cópia demora (1–2 min pela VPN): a tela de carregamento já abre no navegador com o progresso
                TelaCarregando.Iniciar(url); AbrirNavegador(url);
                Inicio.Etapa($"Copiando a versão {Versao} para este computador …", 0);
                var sw = Stopwatch.StartNew();
                var tmp = destino + ".tmp-" + Guid.NewGuid().ToString("N")[..6];
                var total = Math.Max(1, new DirectoryInfo(origem).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length)); long feito = 0;
                Copiar(origem, tmp, n => { feito += n; Inicio.Progresso((int)(feito * 100 / total), $"{feito / 1048576.0:0} de {total / 1048576.0:0} MB · só na primeira vez de cada versão"); });
                File.WriteAllText(Path.Combine(tmp, ".completa"), DateTime.Now.ToString("o"));
                try { if (Directory.Exists(destino)) Directory.Delete(destino, true); Directory.Move(tmp, destino); }
                catch { if (!File.Exists(Path.Combine(destino, ".completa"))) throw; try { Directory.Delete(tmp, true); } catch { } }   // outra janela copiou junto
                Console.WriteLine($"[controletecnico] Cópia pronta em {sw.Elapsed.TotalSeconds:0} s.");
            }
            LimparAntigas(raiz, destino);
            TelaCarregando.Parar();                                   // a cópia local assume o endereço e a tela de carregamento
            var exe = Path.Combine(destino, Path.GetFileName(Environment.ProcessPath ?? "ControleTecnico.exe"));
            var psi = new ProcessStartInfo(exe) { WorkingDirectory = destino, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.Environment["CONTROLETECNICO_ORIGEM"] = origem;
            psi.Environment["CONTROLETECNICO_URL"] = url;           // mesma porta em que a tela de carregamento/navegador já abriram
            if (NavegadorAberto) psi.Environment["CONTROLETECNICO_NAVEGADOR_ABERTO"] = "1";
            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[controletecnico] Não foi possível copiar para este computador ({ex.Message}); abrindo direto da rede.");
            return false;
        }
    }

    private static void Copiar(string de, string para, Action<long> avancou)
    {
        Directory.CreateDirectory(para);
        var buffer = new byte[1 << 20];
        foreach (var f in Directory.GetFiles(de))
        {
            using var src = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var dst = new FileStream(Path.Combine(para, Path.GetFileName(f)), FileMode.Create, FileAccess.Write);
            int n; while ((n = src.Read(buffer, 0, buffer.Length)) > 0) { dst.Write(buffer, 0, n); avancou(n); }
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(dst.Name, File.GetUnixFileMode(f));     // mantém o executável executável
        }
        foreach (var d in Directory.GetDirectories(de)) Copiar(d, Path.Combine(para, Path.GetFileName(d)), avancou);
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

    public static void AbrirNavegador(string url)
    {
        NavegadorAberto = true;
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
