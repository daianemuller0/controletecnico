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

var builder = WebApplication.CreateBuilder(args);

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
try
{
    store = new ParquetStore(pastaDados, builder.Configuration["Data:EspelhoLocal"], builder.Configuration.GetValue("Data:UsarEspelho", false));
    var diag = store.Diagnosticar();
    if (!diag.Ok) throw new InvalidOperationException($"A pasta de dados '{diag.Pasta}' está acessível, mas sem leitura e escrita: {diag.Mensagem}");
    Console.WriteLine($"[controletecnico] Base de dados: {diag.Pasta} (rede: {(diag.Rede ? "sim" : "não")}; espelho local: {diag.Espelho ?? "não usado"}; {diag.LatenciaMs} ms)");
}
catch (Exception ex)
{
    Console.Error.WriteLine("[controletecnico] ERRO: " + ex.Message);
    Console.Error.WriteLine("O programa não inicia sem acesso à base compartilhada, para nunca gravar dados num lugar diferente do combinado. " +
                            "Para desenvolvimento use a variável Data__Folder apontando para uma pasta local.");
    return 1;
}
Repo<Tecnico>.SegundosEntreChecagens = builder.Configuration.GetValue("Data:AtualizarCacheSegundos", 3);
builder.Services.AddSingleton(store);
builder.Services.AddHostedService<CompactacaoService>();
// chaves dos cookies ficam NO COMPUTADOR (nunca na pasta compartilhada, onde ficariam legíveis para outros)
builder.Services.AddDataProtection().SetApplicationName("ControleTecnico")
    .PersistKeysToFileSystem(new DirectoryInfo(builder.Configuration["Data:ChavesLocal"] ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ControleTecnico", "chaves")));
builder.Services.AddSingleton<Db>();
builder.Services.AddSingleton<Armazenamento>();
builder.Services.AddSingleton<Servicos>();
builder.Services.AddSingleton<Importador>();
builder.Services.AddSingleton<ImportadorTecnicos>();
builder.Services.AddHttpClient("geo", c => c.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddSingleton<Geocoder>();
builder.Services.AddSingleton<Rota>();
builder.Services.AddSingleton<LoginGuard>();
builder.Services.AddScoped<Sessao>();

var app = builder.Build();

// ---- semente: só com a base vazia ----
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<Db>();
    var arq = scope.ServiceProvider.GetRequiredService<Armazenamento>();
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

app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["Referrer-Policy"] = "same-origin";
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

app.MapGet("/saude", () => Results.Ok("ok")).AllowAnonymous();

// ---- anexos: sempre mediados pelo servidor, com a regra de acesso ----
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
app.Run();
return 0;

public partial class Program { }
