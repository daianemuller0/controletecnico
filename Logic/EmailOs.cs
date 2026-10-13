using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using ControleTecnico.Models;

namespace ControleTecnico.Logic;

/// <summary>
/// E-mail da Ordem de Serviço da viagem: "Projeto – VISITA TÉCNICA – Unidade", saudação pelo horário,
/// endereço, datas do itinerário, Hotel/Carro/Voo, Contato, Escopo e Informações adicionais, com os
/// documentos anexados à viagem. Abre no Outlook (com a assinatura padrão de quem envia) ou gera um .eml.
/// </summary>
public static class EmailOs
{
    public sealed class Rascunho
    {
        public string Para { get; set; } = "";
        public string Cc { get; set; } = "";
        public string Assunto { get; set; } = "";
        public string Saudacao { get; set; } = "";
        public string Unidade { get; set; } = "";
        public string Endereco { get; set; } = "";
        public string Datas { get; set; } = "";
        public bool UsaHotel { get; set; } = true; public string Hotel { get; set; } = "Emitido, em anexo;";
        public bool UsaCarro { get; set; } = true; public string Carro { get; set; } = "Reservado, em anexo;";
        public bool UsaVoo { get; set; } = true; public string Voo { get; set; } = "Emitidos, em anexo;";
        public string Contato { get; set; } = "";
        public string Escopo { get; set; } = "";
        public string Info { get; set; } = "";
        public List<string> AnexoIds { get; set; } = new();
        public List<string> SemEmail { get; set; } = new();
    }

    /// <summary>"Bom dia", "boa tarde" ou "boa noite" conforme a hora local de quem envia.</summary>
    public static string Cumprimento(DateTime local) => local.Hour < 12 ? "bom dia" : local.Hour < 18 ? "boa tarde" : "boa noite";

    /// <summary>Como no exemplo da equipe: sobrenomes ("Braga e Torso").</summary>
    public static string NomesSaudacao(IEnumerable<string> nomes)
    {
        var l = nomes.Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)).Select(p => p.Length > 1 ? p[^1] : p[0]).ToList();
        return l.Count switch { 0 => "Olá", 1 => l[0], _ => string.Join(", ", l.Take(l.Count - 1)) + " e " + l[^1] };
    }

    public static string EnderecoPlanta(Planta? p)
    {
        if (p is null) return "";
        var rua = string.Join(", ", new[] { p.Rua, p.Numero, p.Complemento }.Where(x => !string.IsNullOrWhiteSpace(x)));
        if (rua == "" && !string.IsNullOrWhiteSpace(p.EnderecoCompleto)) rua = p.EnderecoCompleto;
        var partes = new[] { rua, p.Cidade, p.Estado }.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        var txt = string.Join(" – ", partes);
        if (!string.IsNullOrWhiteSpace(p.Cep)) txt += $" – CEP: {p.Cep}";
        if (!string.IsNullOrWhiteSpace(p.Pais) && !p.Pais.Equals("Brasil", StringComparison.OrdinalIgnoreCase)) txt += $" – {p.Pais}";
        return txt;
    }

    public static Rascunho Montar(Snapshot s, Viagem v, IEnumerable<Anexo> anexos, DateTime agoraLocal)
    {
        var p = s.Planta(v.PlantaId);
        var tecs = Snapshot.Ids(v.TecnicoIds).Select(s.Tecnico).Where(t => t is not null).Select(t => t!).ToList();
        var unidade = (p is null ? v.Destino : p.Nome).Trim().ToUpperInvariant();
        var ev = AgendaEngine.Eventos(s, true).Where(e => e.ViagemId == v.Id && e.Status != "cancelado").ToList();
        var fz = p?.FusoHorario ?? s.FusoPadrao;
        var datas = ev.Count == 0 ? "A viagem será em __/__ com retorno previsto para __/__."
            : $"A viagem será em {Tempo.ParaLocal(ev.Min(e => e.Ini), fz):dd/MM} com retorno previsto para {Tempo.ParaLocal(ev.Max(e => e.Fim), fz):dd/MM}.";
        var r = new Rascunho
        {
            Para = string.Join("; ", tecs.Select(t => t.Email).Where(e => !string.IsNullOrWhiteSpace(e)).Distinct(StringComparer.OrdinalIgnoreCase)),
            SemEmail = tecs.Where(t => string.IsNullOrWhiteSpace(t.Email)).Select(t => t.Nome).ToList(),
            Cc = v.OsCc,
            Assunto = $"{(string.IsNullOrWhiteSpace(v.Projeto) ? "SEM PROJETO" : v.Projeto.Trim())} – VISITA TÉCNICA – {unidade}",
            Saudacao = $"{NomesSaudacao(tecs.Select(t => t.Nome))}, {Cumprimento(agoraLocal)}!",
            Unidade = unidade, Endereco = EnderecoPlanta(p), Datas = datas,
            Contato = v.OsContato, Escopo = v.OsEscopo, Info = v.OsInfo,
            AnexoIds = anexos.Select(a => a.Id).ToList(),
        };
        if (!string.IsNullOrWhiteSpace(v.OsHotel)) { r.UsaHotel = v.OsHotel != "-"; if (r.UsaHotel) r.Hotel = v.OsHotel; }
        if (!string.IsNullOrWhiteSpace(v.OsCarro)) { r.UsaCarro = v.OsCarro != "-"; if (r.UsaCarro) r.Carro = v.OsCarro; }
        if (!string.IsNullOrWhiteSpace(v.OsVoo)) { r.UsaVoo = v.OsVoo != "-"; if (r.UsaVoo) r.Voo = v.OsVoo; }
        return r;
    }

    private static string H(string t) => WebUtility.HtmlEncode(t ?? "").Replace("\r", "").Replace("\n", "<br>");

    /// <summary>Corpo em HTML (sem assinatura: ela vem do Outlook de quem envia).</summary>
    public static string Html(Rascunho r)
    {
        const string az = "#1F4E79";
        var sb = new StringBuilder();
        sb.Append("<div style=\"font-family:Calibri,Arial,sans-serif;font-size:11pt;color:#000\">");
        sb.Append($"<p>{H(r.Saudacao)}</p>");
        sb.Append($"<p style=\"margin-bottom:0\"><b><u>ORDEM DE SERVIÇO – VISITA TÉCNICA – {H(r.Unidade)}</u></b></p>");
        if (!string.IsNullOrWhiteSpace(r.Endereco)) sb.Append($"<p style=\"margin-top:2px\"><i style=\"color:{az}\"><b>{H(r.Endereco)}</b></i></p>");
        sb.Append($"<p>{H(r.Datas)}<br>");
        if (r.UsaHotel) sb.Append($"<b>Hotel:</b> {H(r.Hotel)}<br>");
        if (r.UsaCarro) sb.Append($"<b>Carro:</b> {H(r.Carro)}<br>");
        if (r.UsaVoo) sb.Append($"<b>Voo:</b> {H(r.Voo)}");
        sb.Append("</p>");
        void Bloco(string titulo, string texto)
        {
            if (string.IsNullOrWhiteSpace(texto)) return;
            sb.Append($"<p><b style=\"color:{az}\">{titulo}</b><br>{H(texto)}</p>");
        }
        Bloco("Contato:", r.Contato); Bloco("Escopo do Serviço:", r.Escopo); Bloco("Informações adicionais:", r.Info);
        sb.Append("</div>");
        return sb.ToString();
    }

    public static string Texto(Rascunho r)
    {
        var sb = new StringBuilder();
        sb.AppendLine(r.Saudacao).AppendLine().AppendLine($"ORDEM DE SERVIÇO – VISITA TÉCNICA – {r.Unidade}");
        if (!string.IsNullOrWhiteSpace(r.Endereco)) sb.AppendLine(r.Endereco);
        sb.AppendLine().AppendLine(r.Datas);
        if (r.UsaHotel) sb.AppendLine($"Hotel: {r.Hotel}");
        if (r.UsaCarro) sb.AppendLine($"Carro: {r.Carro}");
        if (r.UsaVoo) sb.AppendLine($"Voo: {r.Voo}");
        foreach (var (t, x) in new[] { ("Contato:", r.Contato), ("Escopo do Serviço:", r.Escopo), ("Informações adicionais:", r.Info) })
            if (!string.IsNullOrWhiteSpace(x)) sb.AppendLine().AppendLine(t).AppendLine(x);
        return sb.ToString();
    }

    private static string Cab(string t) => "=?utf-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(t)) + "?=";
    private static string B64(byte[] b)
    {
        var s = Convert.ToBase64String(b); var sb = new StringBuilder();
        for (var i = 0; i < s.Length; i += 76) sb.Append(s, i, Math.Min(76, s.Length - i)).Append("\r\n");
        return sb.ToString();
    }

    /// <summary>Arquivo .eml de rascunho ("X-Unsent: 1"): o Outlook abre como e-mail novo, pronto para enviar, com os anexos.</summary>
    public static byte[] Eml(Rascunho r, IEnumerable<(string nome, string tipo, byte[] dados)> anexos)
    {
        var lim = "=_ct_" + Guid.NewGuid().ToString("N");
        var alt = "=_alt_" + Guid.NewGuid().ToString("N");
        var sb = new StringBuilder();
        sb.Append("X-Unsent: 1\r\n");
        sb.Append($"To: {r.Para.Replace(';', ',')}\r\n");
        if (!string.IsNullOrWhiteSpace(r.Cc)) sb.Append($"Cc: {r.Cc.Replace(';', ',')}\r\n");
        sb.Append($"Subject: {Cab(r.Assunto)}\r\nMIME-Version: 1.0\r\n");
        sb.Append($"Content-Type: multipart/mixed; boundary=\"{lim}\"\r\n\r\n");
        sb.Append($"--{lim}\r\nContent-Type: multipart/alternative; boundary=\"{alt}\"\r\n\r\n");
        sb.Append($"--{alt}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: base64\r\n\r\n{B64(Encoding.UTF8.GetBytes(Texto(r)))}");
        sb.Append($"--{alt}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Transfer-Encoding: base64\r\n\r\n{B64(Encoding.UTF8.GetBytes("<html><body>" + Html(r) + "</body></html>"))}");
        sb.Append($"--{alt}--\r\n");
        foreach (var (nome, tipo, dados) in anexos)
        {
            sb.Append($"--{lim}\r\nContent-Type: {tipo}; name=\"{Cab(nome)}\"\r\nContent-Transfer-Encoding: base64\r\n");
            sb.Append($"Content-Disposition: attachment; filename=\"{Cab(nome)}\"\r\n\r\n{B64(dados)}");
        }
        sb.Append($"--{lim}--\r\n");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>Nome de arquivo seguro para o anexo copiado para a pasta temporária.</summary>
    public static string NomeSeguro(string n)
    {
        foreach (var c in Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\', ':' })) n = n.Replace(c, '_');
        return string.IsNullOrWhiteSpace(n) ? "anexo" : n;
    }

    private const string Script = @"
$ErrorActionPreference = 'Stop'
$d = Get-Content -Raw -Encoding UTF8 -LiteralPath $args[0] | ConvertFrom-Json
$o = New-Object -ComObject Outlook.Application
$m = $o.CreateItem(0)
$m.Display()                      # carrega a assinatura padrão de quem envia
$sig = [string]$m.HTMLBody
$m.To = $d.para
if ($d.cc) { $m.CC = $d.cc }
$m.Subject = $d.assunto
foreach ($a in $d.anexos) { [void]$m.Attachments.Add([string]$a) }
$i = $sig.IndexOf('<body', [StringComparison]::OrdinalIgnoreCase)
if ($i -ge 0) { $j = $sig.IndexOf('>', $i); $m.HTMLBody = $sig.Insert($j + 1, $d.html) } else { $m.HTMLBody = $d.html + $sig }
";

    /// <summary>
    /// Abre o e-mail no Outlook deste computador (com a assinatura padrão), já com destinatários, assunto, corpo e anexos.
    /// Só funciona no Windows com o Outlook instalado; o sistema precisa estar rodando no computador de quem envia.
    /// </summary>
    public static async Task<(bool ok, string msg)> AbrirNoOutlookAsync(Rascunho r, IEnumerable<(string nome, byte[] dados)> anexos)
    {
        if (!OperatingSystem.IsWindows()) return (false, "O Outlook só pode ser aberto quando o sistema roda no Windows. Use \"Baixar e-mail (.eml)\".");
        var raiz = Path.Combine(Path.GetTempPath(), "ControleTecnico", "email");
        try { foreach (var d in Directory.Exists(raiz) ? Directory.GetDirectories(raiz) : Array.Empty<string>()) if (Directory.GetCreationTimeUtc(d) < DateTime.UtcNow.AddDays(-2)) Directory.Delete(d, true); } catch { }
        var pasta = Path.Combine(raiz, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(pasta);
        var caminhos = new List<string>();
        foreach (var (nome, dados) in anexos)
        {
            var c = Path.Combine(pasta, NomeSeguro(nome)); var k = 1;
            while (File.Exists(c)) c = Path.Combine(pasta, $"{Path.GetFileNameWithoutExtension(NomeSeguro(nome))} ({++k}){Path.GetExtension(nome)}");
            await File.WriteAllBytesAsync(c, dados); caminhos.Add(c);
        }
        var json = Path.Combine(pasta, "email.json");
        await File.WriteAllTextAsync(json, JsonSerializer.Serialize(new { para = r.Para, cc = r.Cc, assunto = r.Assunto, html = Html(r), anexos = caminhos }), new UTF8Encoding(true));
        var ps1 = Path.Combine(pasta, "abrir.ps1");
        await File.WriteAllTextAsync(ps1, Script, new UTF8Encoding(true));
        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true,
        };
        foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-STA", "-File", ps1, json }) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            var erro = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(60000)) { try { p.Kill(); } catch { } return (false, "O Outlook demorou a responder. Verifique se ele está aberto e tente de novo."); }
            var e = (await erro).Trim();
            return p.ExitCode == 0 ? (true, "E-mail aberto no Outlook com a sua assinatura. Confira e clique em Enviar.")
                : (false, "Não foi possível abrir o Outlook" + (e == "" ? "." : ": " + e.Split('\n')[0].Trim()) + " Use \"Baixar e-mail (.eml)\".");
        }
        catch (Exception ex) { return (false, "Não foi possível abrir o Outlook: " + ex.Message + " Use \"Baixar e-mail (.eml)\"."); }
    }
}
