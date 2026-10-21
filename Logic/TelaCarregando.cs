using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace ControleTecnico.Logic;

/// <summary>
/// Tela de carregamento no navegador (logo + barra de progresso) enquanto o programa abre: um servidor mínimo atende o endereço
/// do sistema só durante a inicialização (ou a cópia da versão nova) e sai do caminho logo antes do sistema começar a atender.
/// A página fica consultando o progresso e, quando o sistema responde, entra sozinha.
/// </summary>
public static class TelaCarregando
{
    private static WebApplication? _app;
    private static readonly object _l = new();
    private static readonly SemaphoreSlim _tentar = new(0);
    /// <summary>Última vez que a página consultou o progresso (para saber se alguém ainda está esperando).</summary>
    public static DateTime UltimaConsulta { get; private set; } = DateTime.UtcNow;

    public static bool Ativa { get { lock (_l) return _app is not null; } }

    public static void Iniciar(string url)
    {
        lock (_l)
        {
            if (_app is not null) return;
            try
            {
                var b = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
                b.WebHost.UseKestrelCore().UseUrls(url);
                var app = b.Build();
                ((IApplicationBuilder)app).Run(Atender);
                app.StartAsync().GetAwaiter().GetResult();
                _app = app; UltimaConsulta = DateTime.UtcNow;
            }
            catch (Exception ex) { Console.WriteLine("[controletecnico] Tela de carregamento indisponível: " + ex.Message); }
        }
    }

    /// <summary>Libera o endereço para o sistema (a página continua tentando e entra quando ele responder).</summary>
    public static void Parar()
    {
        WebApplication? app;
        lock (_l) { app = _app; _app = null; }
        if (app is null) return;
        try { app.StopAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult(); app.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
    }

    /// <summary>Com falha na tela: espera o clique em "Tentar de novo" ou o tempo da nova tentativa automática.
    /// Devolve false quando ninguém está mais olhando (página fechada há 1 min).</summary>
    public static bool AguardarNovaTentativa(TimeSpan espera)
    {
        var fim = DateTime.UtcNow + espera;
        while (DateTime.UtcNow < fim)
        {
            if (_tentar.Wait(500)) return true;
            if (Ativa && DateTime.UtcNow - UltimaConsulta > TimeSpan.FromMinutes(1)) return false;
        }
        return true;
    }

    private static async Task Atender(HttpContext ctx)
    {
        var p = ctx.Request.Path.Value ?? "/";
        ctx.Response.Headers.CacheControl = "no-store";
        ctx.Response.Headers["X-ControleTecnico"] = "carregando";
        if (p == "/progresso")
        {
            UltimaConsulta = DateTime.UtcNow;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            await ctx.Response.WriteAsync(JsonSerializer.Serialize(new { pronto = false, etapa = Inicio.Texto, pct = Inicio.Pct, detalhe = Inicio.Detalhe, erro = Inicio.Erro }));
        }
        else if (p == "/tentar" && HttpMethods.IsPost(ctx.Request.Method)) { _tentar.Release(); ctx.Response.StatusCode = 204; }
        else if (p == "/saude") { ctx.Response.StatusCode = 503; await ctx.Response.WriteAsync("carregando"); }
        else if (p == "/img/howden-logo.png" && Logo() is { } logo) { ctx.Response.ContentType = "image/png"; await ctx.Response.Body.WriteAsync(logo); }
        else if (p is "/favicon.svg" or "/favicon.ico" or "/vivo" or "/saindo") ctx.Response.StatusCode = 204;
        else { ctx.Response.ContentType = "text/html; charset=utf-8"; await ctx.Response.WriteAsync(Html); }
    }

    private static byte[]? _logo;
    private static byte[]? Logo()
    {
        if (_logo is not null) return _logo;
        foreach (var raiz in new[] { Lancador.RaizConteudo(), Directory.GetCurrentDirectory() })
        {
            var f = raiz is null ? null : Path.Combine(raiz, "wwwroot", "img", "howden-logo.png");
            if (f is not null && File.Exists(f)) return _logo = File.ReadAllBytes(f);
        }
        return null;
    }

    public const string Html = """
<!DOCTYPE html>
<html lang="pt-BR"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>Controle Técnico · abrindo…</title>
<style>
  :root { --azul: #063f86; --teal: #0077a0; --txt: #1f2a3c; --muted: #6a768a; }
  * { box-sizing: border-box; }
  body { margin: 0; min-height: 100vh; display: grid; place-items: center; font-family: "Segoe UI", system-ui, -apple-system, sans-serif;
         background: radial-gradient(1200px 600px at 50% -10%, #e8f2fb 0%, #f5f7fa 55%, #eef1f5 100%); color: var(--txt); }
  .cartao { width: min(460px, calc(100vw - 32px)); background: #fff; border-radius: 18px; padding: 34px 34px 28px;
            box-shadow: 0 18px 50px rgba(6, 63, 134, .12), 0 2px 6px rgba(6, 63, 134, .06); text-align: center; }
  .logo { width: 210px; max-width: 70%; height: auto; display: block; margin: 0 auto 18px; }
  h1 { font-size: 19px; margin: 0 0 4px; color: var(--azul); letter-spacing: .2px; }
  .sub { font-size: 12.5px; color: var(--muted); margin: 0 0 24px; }
  .barra { height: 10px; border-radius: 99px; background: #e6ecf3; overflow: hidden; position: relative; }
  .barra i { display: block; height: 100%; width: 3%; border-radius: 99px; background: linear-gradient(90deg, var(--azul), var(--teal));
             transition: width .45s ease; position: relative; overflow: hidden; }
  .barra i::after { content: ""; position: absolute; inset: 0; background: linear-gradient(90deg, transparent, rgba(255,255,255,.45), transparent);
                    animation: brilho 1.4s linear infinite; transform: translateX(-100%); }
  @keyframes brilho { to { transform: translateX(100%); } }
  .linha { display: flex; justify-content: space-between; gap: 12px; margin-top: 10px; font-size: 13px; text-align: left; }
  .etapa { font-weight: 600; } .pct { color: var(--azul); font-weight: 700; font-variant-numeric: tabular-nums; }
  .det { font-size: 12px; color: var(--muted); margin-top: 6px; min-height: 16px; text-align: left; }
  .erro { display: none; margin-top: 18px; text-align: left; background: #fff4f2; border: 1px solid #f3c2b8; color: #8a2b16; border-radius: 10px; padding: 12px 14px; font-size: 13px; }
  .erro button { margin-top: 10px; background: var(--azul); color: #fff; border: 0; border-radius: 8px; padding: 8px 14px; font-weight: 600; cursor: pointer; }
  .falhou .barra i { background: #d9573a; } .falhou .barra i::after { display: none; }
  .rodape { margin-top: 22px; font-size: 11px; color: #9aa4b4; }
</style></head>
<body><div class="cartao" id="c">
  <img class="logo" src="/img/howden-logo.png" alt="Howden" onerror="this.style.display='none'">
  <h1>Controle Técnico</h1>
  <p class="sub">Viagens, agenda e disponibilidade dos técnicos</p>
  <div class="barra"><i id="b"></i></div>
  <div class="linha"><span class="etapa" id="e">Abrindo…</span><span class="pct" id="p">0%</span></div>
  <div class="det" id="d"></div>
  <div class="erro" id="x"><div id="xm"></div><button id="xt" type="button">Tentar de novo</button></div>
  <div class="rodape">Esta página entra no sistema sozinha quando ele estiver pronto.</div>
</div>
<script>
(function () {
  var destino = (location.pathname === "/carregando" ? "/" : location.pathname) + location.search;
  var falhas = 0, ultimo = 0;
  function el(id) { return document.getElementById(id); }
  el("xt").onclick = function () { fetch("/tentar", { method: "POST" }); el("xm").textContent = "Tentando de novo…"; };
  function mostrar(j) {
    var pct = Math.max(ultimo, j.pct || 0); if (j.etapa && j.etapa.indexOf("Copiando") === 0) pct = j.pct || 0; ultimo = pct;
    el("b").style.width = Math.max(3, pct) + "%"; el("p").textContent = pct + "%";
    el("e").textContent = j.etapa || "Abrindo…"; el("d").textContent = j.detalhe || "";
    var c = el("c");
    if (j.erro) { c.classList.add("falhou"); el("x").style.display = "block"; el("xm").textContent = j.erro; }
    else { c.classList.remove("falhou"); el("x").style.display = "none"; }
  }
  function tick() {
    fetch("/progresso", { cache: "no-store" }).then(function (r) { return r.json(); }).then(function (j) {
      falhas = 0;
      if (j.pronto) { el("b").style.width = "100%"; el("p").textContent = "100%"; el("e").textContent = "Pronto"; setTimeout(function () { location.replace(destino); }, 250); return; }
      mostrar(j); setTimeout(tick, 400);
    }).catch(function () {
      falhas++;   // troca de servidor (tela de carregamento → sistema) leva 1–3 s
      if (falhas > 120) { mostrar({ etapa: "Sem resposta", pct: ultimo, erro: "O Controle Técnico não respondeu. Feche esta aba e abra de novo pelo atalho." }); el("xt").style.display = "none"; }
      setTimeout(tick, 500);
    });
  }
  tick();
})();
</script></body></html>
""";
}
