using System.Net;
using System.Net.Mail;
using System.Text;
using ControleTecnico.Models;

namespace ControleTecnico.Logic;

/// <summary>
/// Relatório do serviço: quando o técnico termina a visita ao cliente ele escolhe como terminou
/// (finalizado · com oportunidade de venda · com ocorrência), anexa o relatório de serviços e o formulário
/// POWRA STAR, e o sistema monta o e-mail padrão da equipe — destinatários, assunto e texto — que abre no
/// Outlook com a assinatura de quem envia.
/// </summary>
public static class RelatorioServico
{
    public const string Finalizado = "finalizado", Oportunidade = "oportunidade", Ocorrencia = "ocorrencia";

    public sealed record Tipo(string Key, string Rotulo, string Dica, string Icone, string Cor, string Sufixo, string TituloTexto, string PerguntaTexto);

    public static readonly Tipo[] Tipos =
    {
        new(Finalizado, "Finalizado", "O serviço foi concluído, sem nada a acrescentar.", "check", "#1f9254", "", "", ""),
        new(Oportunidade, "Finalizado com oportunidade de venda", "Concluído, e o cliente precisa de peças, serviços ou equipamentos.", "dollar", "#0369a1",
            "- Oportunidade de Venda", "Oportunidade de negócios:", "Descreva a oportunidade de venda"),
        new(Ocorrencia, "Finalizado com ocorrência", "Concluído, mas aconteceu algum problema que a equipe precisa saber.", "alert", "#c26a14",
            "- Ocorrência", "Ocorrência:", "Descreva a ocorrência"),
    };

    public static Tipo? DeKey(string? k) => Tipos.FirstOrDefault(t => t.Key == k);
    public static bool PedeTexto(string tipo) => tipo is Oportunidade or Ocorrencia;

    // destinatários: configuráveis em Administração → Configurações (valores padrão da equipe)
    public const string CfgPara = "relatorio_para", CfgParaVenda = "relatorio_para_venda";
    public const string ParaPadrao = "Demis.Buffulin@chartindustries.com; Rayssa.Costa@chartindustries.com; anajulia.zago@chartindustries.com";
    public const string ParaVendaPadrao = "adriana.slomp@chartindustries.com";

    public sealed class Rascunho
    {
        public string Tipo { get; set; } = Finalizado;
        public string Para { get; set; } = "";
        public string Assunto { get; set; } = "";
        /// <summary>Oportunidade ou ocorrência digitada pelo técnico.</summary>
        public string Texto { get; set; } = "";
    }

    /// <summary>Lista de e-mails separada por ";" — sem repetir (ignora maiúsculas) e sem espaços sobrando.</summary>
    public static string Juntar(params string?[] listas) =>
        string.Join("; ", listas.SelectMany(l => (l ?? "").Split(new[] { ';', ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase));

    /// <summary>E-mails inválidos da lista (para avisar antes de abrir o Outlook).</summary>
    public static List<string> Invalidos(string lista) =>
        Juntar(lista).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(e => !MailAddress.TryCreate(e, out var m) || m.Address != e || !e.Contains('.', StringComparison.Ordinal)).ToList();

    public static string Assunto(string? projeto, string tipo)
    {
        var p = string.IsNullOrWhiteSpace(projeto) ? "SEM PROJETO" : projeto.Trim();
        var suf = DeKey(tipo)?.Sufixo ?? "";
        return $"Ordem de serviço-{p}" + suf;
    }

    public static Rascunho Montar(Viagem v, string tipo, string texto, string para, string paraVenda) => new()
    {
        Tipo = tipo,
        Para = tipo == Finalizado ? Juntar(para) : Juntar(para, paraVenda),
        Assunto = Assunto(v.Projeto, tipo),
        Texto = texto ?? "",
    };

    private static string H(string t) => WebUtility.HtmlEncode(t ?? "").Replace("\r", "").Replace("\n", "<br>");

    /// <summary>Corpo em HTML. A assinatura não entra aqui: vem do Outlook de quem envia.</summary>
    public static string Html(Rascunho r)
    {
        var t = DeKey(r.Tipo);
        var sb = new StringBuilder("<div style=\"font-family:Calibri,Arial,sans-serif;font-size:11pt;color:#000\">");
        sb.Append("<p>Olá,</p>");
        sb.Append("<p>Segue anexo o relatório de serviços em anexo.<br>Anexo formulário do powra star.</p>");
        if (t is not null && PedeTexto(t.Key) && !string.IsNullOrWhiteSpace(r.Texto))
            sb.Append($"<p><b>{H(t.TituloTexto)}</b><br>{H(r.Texto.Trim())}</p>");
        sb.Append("<p>Atenciosamente,</p></div>");
        return sb.ToString();
    }

    public static string TextoSimples(Rascunho r)
    {
        var t = DeKey(r.Tipo);
        var sb = new StringBuilder();
        sb.AppendLine("Olá,").AppendLine().AppendLine("Segue anexo o relatório de serviços em anexo.").AppendLine("Anexo formulário do powra star.");
        if (t is not null && PedeTexto(t.Key) && !string.IsNullOrWhiteSpace(r.Texto)) sb.AppendLine().AppendLine(t.TituloTexto).AppendLine(r.Texto.Trim());
        sb.AppendLine().AppendLine("Atenciosamente,");
        return sb.ToString();
    }

    /// <summary>O que impede de enviar (lista vazia = pode enviar).</summary>
    public static List<string> Pendencias(Rascunho r, bool temRelatorio, bool temPowra)
    {
        var l = new List<string>();
        if (DeKey(r.Tipo) is null) l.Add("Escolha como terminou o serviço.");
        if (!temRelatorio) l.Add("Anexe o relatório de serviços.");
        if (!temPowra) l.Add("Anexe o formulário POWRA STAR.");
        if (PedeTexto(r.Tipo) && (r.Texto ?? "").Trim().Length < 5) l.Add(r.Tipo == Oportunidade ? "Descreva a oportunidade de venda." : "Descreva a ocorrência.");
        if (string.IsNullOrWhiteSpace(r.Para)) l.Add("Informe ao menos um destinatário.");
        else if (Invalidos(r.Para) is { Count: > 0 } inv) l.Add("E-mail inválido: " + string.Join(", ", inv));
        if (string.IsNullOrWhiteSpace(r.Assunto)) l.Add("Informe o assunto.");
        return l;
    }
}
