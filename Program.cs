using Microsoft.AspNetCore.DataProtection;
using System.Security.Claims;
using System.Text;
using ControleTecnico;
using ControleTecnico.Components;
using ControleTecnico.Data;
using ControleTecnico.Logic;
using ControleTecnico.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

// publicado (sem janela): o que antes ia para a janela preta fica em %LOCALAPPDATA%\ControleTecnico\logs
Lancador.RegistrarEmArquivo();
// publicado: a pasta do programa é a raiz do conteúdo (wwwroot, appsettings.json), de onde quer que tenha sido aberto
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = Lancador.RaizConteudo() });
var urlApp = builder.Configuration["Kestrel:Endpoints:Http:Url"] ?? "http://localhost:5090";
if (OperatingSystem.IsWindows()) try { Console.Title = "Controle Técnico" + (Lancador.Versao == "" ? "" : " · " + Lancador.Versao); } catch { }
if (Lancador.Versao != "")
{
    switch (Lancador.JaAberto(urlApp))
    {
        // segundo clique no atalho com o programa já aberto (ou abrindo) neste computador: só abre o navegador
        case Lancador.Situacao.MesmaVersao or Lancador.Situacao.Carregando:
            Console.WriteLine("[controletecnico] O Controle Técnico já está aberto neste computador: abrindo o navegador.");
            Lancador.AbrirNavegador(urlApp); return 0;
        // versão antiga ainda aberta (ex.: reabriu logo depois de publicar): fecha a antiga e abre esta
        case Lancador.Situacao.OutraVersao:
            Console.WriteLine("[controletecnico] Fechando a versão anterior que estava aberta …");
            if (!Lancador.EncerrarAberto(urlApp)) { Lancador.AbrirNavegador(urlApp); return 0; }
            break;
    }
}
// aberto da pasta da rede: roda uma cópia local desta versão (rápido pela VPN e libera a rede para publicar)
if (Lancador.AbrirCopiaLocal(args, urlApp)) return 0;
// tela de carregamento no navegador (logo + barra de progresso) enquanto o sistema abre
TelaCarregando.Iniciar(urlApp);
if (Lancador.Versao != "" && !Lancador.NavegadorAberto) Lancador.AbrirNavegador(urlApp);
Inicio.Etapa("Preparando …", 3);

builder.Services.AddRazorComponents().AddInteractiveServerComponents(o =>
{
    o.DisconnectedCircuitRetentionPeriod = TimeSpan.FromMinutes(5);
});
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath = "/login";
        o.AccessDeniedPath = "/";
        o.ExpireTimeSpan = TimeSpan.FromHours(12);
        o.SlidingExpiration = true;
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        o.Cookie.Name = "controletecnico.auth";
    });
builder.Services.AddAuthorization();

// Dados: DuckDB sobre Parquet (mesmo padrão do Previsão). Anexos em disco ao lado.
// A base inteira vive na pasta de rede compartilhada. Leitura por espelho local; gravação atômica na rede.
var pastaDados = builder.Configuration["Data:Folder"] ?? @"\\BZVCPFIL003\proj_ramires$\DB\tec";
ParquetStore store;
for (var tentativa = 1; ; tentativa++)
{
    try
    {
        Inicio.Etapa("Conectando à base de dados …", 8); Inicio.Progresso(8, pastaDados);
        store = new ParquetStore(pastaDados, builder.Configuration["Data:EspelhoLocal"], builder.Configuration.GetValue("Data:UsarEspelho", false));
        Inicio.Etapa("Testando leitura e gravação na pasta …", 15);
        var diag = store.Diagnosticar(contarArquivos: false);       // na inicialização só o teste de leitura/escrita (listar tudo pela VPN é lento)
        if (!diag.Ok) throw new InvalidOperationException($"A pasta de dados '{diag.Pasta}' está acessível, mas sem leitura e escrita: {diag.Mensagem}");
        Console.WriteLine($"[controletecnico] Base de dados: {diag.Pasta} (rede: {(diag.Rede ? "sim" : "não")}; espelho local: {diag.Espelho ?? "não usado"}; {diag.LatenciaMs} ms)");
        break;
    }
    catch (Exception ex)
    {
        // publicado (sem janela): mostra na tela de carregamento e tenta de novo (ex.: VPN ainda conectando)
        if (Lancador.Versao != "" && TelaCarregando.Ativa && tentativa < 60)
        {
            Console.Error.WriteLine("[controletecnico] Sem acesso à base: " + ex.Message);
            Inicio.Falha($"Sem acesso à pasta da base de dados ({pastaDados}). Confira se a VPN está conectada — o sistema tenta de novo a cada 20 s.");
            if (TelaCarregando.AguardarNovaTentativa(TimeSpan.FromSeconds(20))) continue;
        }
        Console.Error.WriteLine("[controletecnico] ERRO: " + ex.Message);
        Console.Error.WriteLine("O programa não inicia sem acesso à base compartilhada, para nunca gravar dados num lugar diferente do combinado. " +
                                "Para desenvolvimento use a variável Data__Folder apontando para uma pasta local.");
        TelaCarregando.Parar();
        return 1;
    }
}
// Recuperação de acesso: dotnet run -- --redefinir-admin [--senha=NovaSenha]
// Só quem tem acesso à pasta da base (e à máquina) consegue executar; não apaga nenhum dado.
if (args.Contains("--redefinir-admin"))
{
    var dbR = new Db(store);
    var senhaR = args.FirstOrDefault(a => a.StartsWith("--senha="))?["--senha=".Length..] ?? builder.Configuration["Seed:AdminSenha"];
    if (string.IsNullOrEmpty(senhaR))
        senhaR = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12)).Replace('/', 'x').Replace('+', 'y');
    if (senhaR.Length < 8) { Console.Error.WriteLine("A senha deve ter ao menos 8 caracteres."); return 1; }
    var uAdm = dbR.Usuarios.Obter("admin") ?? new Usuario { Id = "admin", Nome = "Administrador" };
    uAdm.Papel = Roles.Admin; uAdm.Ativo = true; (uAdm.Hash, uAdm.Salt) = Servicos.HashSenha(senhaR);
    dbR.Usuarios.Salvar(uAdm, "recuperacao");
    dbR.Auditorias.Salvar(new Auditoria { Quando = DateTime.UtcNow, Usuario = $"{Environment.UserName}@{Environment.MachineName}", Papel = Roles.Admin,
        Acao = "usuario.recuperar", Entidade = "usuario", EntidadeId = "admin", Resumo = "Senha do administrador redefinida por linha de comando" });
    Console.WriteLine($"[controletecnico] Usuário 'admin' redefinido e ativo. Senha: {senhaR}");
    Console.WriteLine("Entre e troque a senha pelo cadeado no topo da tela.");
    return 0;
}
Repo<Tecnico>.SegundosEntreChecagens = builder.Configuration.GetValue("Data:AtualizarCacheSegundos", 3);
// pasta de rede (VPN): telas leem a cópia local; a rede é conferida em segundo plano
Repo<Tecnico>.SincronizarEmSegundoPlano = store.Remoto && builder.Configuration.GetValue("Data:SincronizarEmSegundoPlano", true);
builder.Services.AddHostedService<SincronizacaoService>();
builder.Services.AddSingleton(store);
builder.Services.AddHostedService<CompactacaoService>();
// chaves dos cookies ficam NO COMPUTADOR (nunca na pasta compartilhada, onde ficariam legíveis para outros)
builder.Services.AddDataProtection().SetApplicationName("ControleTecnico")
    .PersistKeysToFileSystem(new DirectoryInfo(builder.Configuration["Data:ChavesLocal"] ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ControleTecnico", "chaves")));
builder.Services.AddSingleton<Db>();
builder.Services.AddSingleton<Armazenamento>();
builder.Services.AddSingleton<Servicos>();
builder.Services.AddSingleton<DownloadsTemporarios>();
builder.Services.AddSingleton<BuscaPlantas>();
builder.Services.AddSingleton<Importador>();
builder.Services.AddSingleton<ImportadorTecnicos>();
// rede corporativa: usa o proxy do Windows com o login do usuário (sem isso o proxy recusa e o mapa "não acha" nenhum endereço)
builder.Services.AddHttpClient("geo", c => c.Timeout = TimeSpan.FromSeconds(15))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { DefaultProxyCredentials = System.Net.CredentialCache.DefaultCredentials });
builder.Services.AddSingleton<Geocoder>();
builder.Services.AddSingleton<Rota>();
builder.Services.AddSingleton<GeocodificacaoJob>();
builder.Services.AddHostedService<LocalizacaoAutomaticaService>();
builder.Services.AddScoped<Microsoft.AspNetCore.Components.Server.Circuits.CircuitHandler, PresencaCircuitos>();
builder.Services.AddHostedService<EncerrarAoFecharService>();
builder.Services.AddSingleton<LoginGuard>();
builder.Services.AddScoped<Sessao>();

var app = builder.Build();

// ---- semente: só com a base vazia ----
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<Db>();
    var arq = scope.ServiceProvider.GetRequiredService<Armazenamento>();
    Inicio.Etapa("Conferindo usuários e cadastros …", 25);
    if (Seed.BaseVazia(db))
    {
        var demo = app.Configuration.GetValue("Seed:Demo", false);
        var senha = app.Configuration["Seed:SenhaPadrao"];
        if (demo && string.IsNullOrEmpty(senha)) senha = "demo2026";
        if (string.IsNullOrEmpty(senha))
        {
            senha = app.Configuration["Seed:AdminSenha"];
            if (string.IsNullOrEmpty(senha))
            {
                senha = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12)).Replace('/', 'x').Replace('+', 'y');
                Console.WriteLine($"[controletecnico] Usuário inicial: admin · senha gerada: {senha} (troque após o primeiro acesso)");
            }
        }
        Seed.Usuarios(db, senha, demo);
        if (demo) Seed.Demo(db, arq);
    }
}

// carrega todas as tabelas em memória já na inicialização (da cópia local, em paralelo): a primeira tela abre sem esperar
{
    var dbW = app.Services.GetRequiredService<Db>();
    Inicio.Etapa(store.Remoto ? "Carregando os dados …" : "Carregando os dados …", 30);
    if (store.Remoto) Inicio.Progresso(30, "Da cópia local; na primeira vez neste computador baixa tudo da rede (pode levar alguns minutos pela VPN).");
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var repos = dbW.Repos; var prontas = 0;
    Parallel.ForEach(repos, new ParallelOptions { MaxDegreeOfParallelism = 6 }, r =>
    {
        try { r.Aquecer(); } catch { }
        var n = Interlocked.Increment(ref prontas);
        Inicio.Progresso(30 + 60 * n / repos.Count, $"{n} de {repos.Count} tabelas carregadas");
    });
    Console.WriteLine($"[controletecnico] Dados carregados em {sw.ElapsedMilliseconds} ms{(store.Remoto ? " (cópia local; a rede é conferida em segundo plano)" : "")}.");
}

app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    // o servidor de mapas (OSM) exige o Referer da origem; "same-origin" fazia o pedido ser bloqueado (403)
    ctx.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    ctx.Response.Headers["X-Frame-Options"] = "SAMEORIGIN";
    await next();
});

if (!app.Environment.IsDevelopment()) app.UseExceptionHandler("/Error", createScopeForErrors: true);
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

// ---- login / logout ----
app.MapPost("/auth/login", async (HttpContext http, Db db, Servicos svc, LoginGuard guard) =>
{
    var form = await http.Request.ReadFormAsync();
    var login = form["usuario"].ToString().Trim().ToLowerInvariant();
    if (login.Contains('@')) login = login.Split('@')[0];
    var senha = form["senha"].ToString();
    var ip = http.Connection.RemoteIpAddress?.ToString() ?? "";
    if (guard.Bloqueado(login, ip)) return Results.Redirect("/login?error=2");
    var u = db.Usuarios.Obter(login);
    if (u is null || !u.Ativo || !Servicos.ConferirSenha(u, senha))
    {
        guard.Falha(login, ip);
        svc.Auditar(Ator.Sistema, "login.falha", "usuario", login, $"Tentativa de login recusada ({login})");
        return Results.Redirect("/login?error=1");
    }
    guard.Sucesso(login, ip);
    var claims = new List<Claim>
    {
        new(ClaimTypes.NameIdentifier, u.Id), new(ClaimTypes.Name, u.Nome), new(ClaimTypes.Role, Roles.Normalize(u.Papel)),
        new("tecnico", u.TecnicoId ?? ""),
    };
    await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
        new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)));
    svc.Auditar(new Ator { Login = u.Id, Nome = u.Nome, Papel = u.Papel }, "login.ok", "usuario", u.Id, "Login realizado");
    return Results.Redirect("/");
}).DisableAntiforgery();

app.MapPost("/auth/logout", async (HttpContext http) =>
{
    await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/login");
}).DisableAntiforgery();

// logos da identidade visual: públicos (a tela de login precisa deles antes da autenticação)
app.MapGet("/marca/{tipo}", (string tipo, HttpContext http, Servicos svc, Armazenamento arq) =>
{
    if (!Servicos.TiposMarca.Contains(tipo)) return Results.NotFound();
    var a = svc.LogoDe(tipo);
    var s = a is null ? null : arq.Abrir(a);
    if (a is null || s is null) return Results.NotFound();
    http.Response.Headers["Cache-Control"] = "public, max-age=3600";
    http.Response.Headers["ETag"] = $"\"{a.Id}\"";
    // SVG servido como imagem isolada: sem script, sem recursos externos, mesmo se aberto direto no navegador
    http.Response.Headers["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'; sandbox";
    return Results.Stream(s, a.ContentType);
}).AllowAnonymous();

app.MapGet("/saude", (HttpContext http) => { http.Response.Headers["X-ControleTecnico-Versao"] = Lancador.Versao; return Results.Ok("ok"); }).AllowAnonymous();
// tela de carregamento: quando o sistema responde aqui, a página entra sozinha
app.MapGet("/progresso", (HttpContext http) => { http.Response.Headers.CacheControl = "no-store"; return Results.Json(new { pronto = true }); }).AllowAnonymous();
// presença do navegador: o programa publicado fecha sozinho quando o sistema é fechado no navegador
app.MapPost("/vivo", () => { Presenca.Vivo(); return Results.NoContent(); }).AllowAnonymous();
app.MapPost("/saindo", () => { Presenca.Saindo(); return Results.NoContent(); }).AllowAnonymous();
// versão nova aberta enquanto a antiga ainda rodava: a nova pede para a antiga fechar (só deste computador; o cabeçalho barra páginas de outros sites)
app.MapPost("/encerrar", (HttpContext http, IHostApplicationLifetime vida) =>
{
    if (!System.Net.IPAddress.IsLoopback(http.Connection.RemoteIpAddress ?? System.Net.IPAddress.None) || http.Request.Headers["X-ControleTecnico"] != "encerrar") return Results.StatusCode(403);
    Console.WriteLine("[controletecnico] Encerrando: uma versão nova foi aberta neste computador.");
    _ = Task.Run(async () => { await Task.Delay(300); vida.StopApplication(); });
    return Results.NoContent();
}).AllowAnonymous();

// ---- anexos: sempre mediados pelo servidor, com a regra de acesso ----
app.MapGet("/download/{token}", (string token, HttpContext http, DownloadsTemporarios dl) =>
{
    var ator = Ator.De(http.User);
    if (dl.Retirar(token, ator.Login) is not { } f) return Results.NotFound("Link expirado: gere o arquivo de novo.");
    var ascii = new string(f.nome.Replace("–", "-").Normalize(System.Text.NormalizationForm.FormD).Where(c => c < 128 && c != '"' && !char.IsControl(c)).ToArray());
    http.Response.Headers["Content-Disposition"] = $"attachment; filename=\"{ascii}\"; filename*=UTF-8''{Uri.EscapeDataString(f.nome)}";
    http.Response.Headers["Cache-Control"] = "private, no-store";
    return Results.Bytes(f.dados, f.tipo);
}).RequireAuthorization();

app.MapGet("/arquivos/{id}", (string id, bool? baixar, HttpContext http, Db db, Servicos svc, Armazenamento arq) =>
{
    var ator = Ator.De(http.User);
    var a = db.Anexos.Obter(id);
    if (a is null) return Results.NotFound();
    if (!svc.PodeAcessar(ator, a)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    var s = arq.Abrir(a);
    if (s is null) return Results.NotFound();
    if (a.DonoTipo != "tecnico")
        svc.Auditar(ator, baixar == true ? "anexo.baixar" : "anexo.visualizar", a.DonoTipo, a.DonoId, $"\"{a.NomeOriginal}\" acessado");
    var nome = a.NomeOriginal.Replace("\"", "");
    http.Response.Headers["Content-Disposition"] = $"{(baixar == true ? "attachment" : "inline")}; filename*=UTF-8''{Uri.EscapeDataString(nome)}";
    http.Response.Headers["Cache-Control"] = "private, no-store";
    return Results.Stream(s, a.ContentType);
}).RequireAuthorization();

app.MapGet("/modelo/plantas.xlsx", () => Results.File(Importador.Modelo(),
    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "modelo_clientes_plantas.xlsx")).RequireAuthorization();

app.MapGet("/modelo/tecnicos.xlsx", () => Results.File(ImportadorTecnicos.Modelo(),
    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "modelo_tecnicos.xlsx")).RequireAuthorization();

app.MapPost("/export/erros-importacao", async (HttpContext http, IServiceProvider sp) =>
{
    var form = await http.Request.ReadFormAsync();
    var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(form["csv"].ToString())).ToArray();
    return Results.File(bytes, "text/csv; charset=utf-8", "erros_importacao.csv");
}).RequireAuthorization().DisableAntiforgery();

app.MapGet("/export/vencimentos.csv", (HttpContext http, Db db) =>
{
    var ator = Ator.De(http.User);
    if (!ator.Pode(Perm.VerDocsStatus)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    var snap = Snapshot.Carregar(db);
    var hoje = DocEngine.Hoje(snap.FusoPadrao);
    static string Q(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
    var sb = new StringBuilder("Técnico;Documento;Categoria;Norma;Versão;Emissão;Vencimento;Dias;Situação\n");
    foreach (var l in DocEngine.Vencimentos(snap, hoje).Where(l => l.Tecnico is not null && ator.VeTecnico(l.Tecnico.Id))
                 .OrderBy(l => l.Dias ?? int.MaxValue))
        sb.AppendLine(string.Join(';', Q(l.Tecnico!.Nome), Q(l.Doc.Nome), Q(l.Doc.Categoria), Q(l.Doc.Norma), l.Doc.Versao,
            Tempo.Dia(l.Doc.Emissao), Tempo.Dia(l.Doc.Vencimento), l.Dias?.ToString() ?? "", Vocab.Get(Vocab.StatusDoc, l.Status).Label));
    return Results.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray(), "text/csv; charset=utf-8", "vencimentos.csv");
}).RequireAuthorization();

app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
// revisão única da precisão das posições já gravadas (em segundo plano, não atrasa a abertura)
_ = Task.Run(() =>
{
    // busca de endereços melhorou: a localização automática tenta de novo as pendentes/aproximadas (uma vez por versão)
    try { var db = app.Services.GetRequiredService<Db>(); if (db.Cfg("geo_algoritmo") != Geocoder.Versao.ToString()) { db.SetCfg("geo_algoritmo_em", DateTime.UtcNow.ToString("o"), "sistema"); db.SetCfg("geo_algoritmo", Geocoder.Versao.ToString(), "sistema"); } } catch { }
});
_ = Task.Run(() => { try { var r = app.Services.GetRequiredService<Servicos>().RevisarPosicoesVagas(); if (r.plantas + r.casas > 0) Console.WriteLine($"[controletecnico]        mapa: {r.plantas} planta(s) e {r.casas} casa(s) com posição vaga (estado/país) voltaram a pendente."); } catch { } });
Inicio.Etapa("Iniciando o servidor web …", 95);
app.Lifetime.ApplicationStarted.Register(() =>
{
    Inicio.Pronto(urlApp);
    // a página de carregamento já está aberta (publicado) e entra sozinha; no "dotnet run" só mostra o endereço
    if (!Lancador.NavegadorAberto && app.Configuration.GetValue("AbrirNavegador", false)) Lancador.AbrirNavegador(urlApp);
    Lancador.VigiarVersaoNova();
});
TelaCarregando.Parar();          // libera o endereço: a partir daqui quem responde é o sistema
app.Run();
return 0;

public partial class Program { }
