using System.Globalization;
using System.Text;
using ControleTecnico.Models;

namespace ControleTecnico.Logic;

public sealed record Pendencia(Requisito Requisito, string Tipo, string Motivo, bool Bloqueia, Documento? Documento);

public sealed record Aptidao(Tecnico Tecnico, bool Apto, bool Bloqueado, List<Pendencia> Pendencias)
{
    public bool ComAlertas => Pendencias.Any(p => !p.Bloqueia);
}

/// <summary>Validade de documentos e verificação de requisitos por serviço/planta.
/// A validade é do registro do técnico (ou da regra configurada) — nunca da norma.</summary>
public static class DocEngine
{
    public static string Norm(string? s)
    {
        var t = (s ?? "").Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(t.Length);
        foreach (var c in t)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    public static DateTime Hoje(string fusoPadrao, DateTime? agora = null) =>
        DateTime.SpecifyKind(Tempo.ParaLocal(agora ?? DateTime.UtcNow, fusoPadrao).Date, DateTimeKind.Utc);

    public static int? DiasParaVencer(Documento d, DateTime hoje) =>
        d.SemVencimento || d.Vencimento is null ? null : (int)(d.Vencimento.Value.Date - hoje.Date).TotalDays;

    public static string Status(Documento d, DateTime hoje, int[] alertas)
    {
        var dias = DiasParaVencer(d, hoje);
        if (dias is null) return Vocab.DocSemVenc;
        if (dias < 0) return Vocab.DocVencido;
        return dias <= (alertas.Length == 0 ? 90 : alertas.Max()) ? Vocab.DocProximo : Vocab.DocValido;
    }

    /// <summary>Menor limite de alerta que já foi atingido (30, 60 ou 90), ou null.</summary>
    public static int? NivelAlerta(Documento d, DateTime hoje, int[] alertas)
    {
        var dias = DiasParaVencer(d, hoje);
        if (dias is null or < 0) return null;
        foreach (var a in alertas.OrderBy(x => x)) if (dias <= a) return a;
        return null;
    }

    public static bool Atende(Requisito r, Documento d) =>
        (Norm(r.Nome).Length > 0 && Norm(r.Nome) == Norm(d.Nome)) ||
        (Norm(r.Norma).Length > 0 && Norm(r.Norma) == Norm(d.Norma));

    public static List<Requisito> RequisitosDe(Snapshot s, IEnumerable<string?> servicoIds, IEnumerable<string?> plantaIds)
    {
        var sv = servicoIds.Where(x => !string.IsNullOrEmpty(x)).ToHashSet();
        var pl = plantaIds.Where(x => !string.IsNullOrEmpty(x)).ToHashSet();
        return s.Requisitos.Where(r => (r.Escopo == "servico" && sv.Contains(r.EscopoId)) || (r.Escopo == "planta" && pl.Contains(r.EscopoId)))
            .GroupBy(r => r.Id).Select(g => g.First()).ToList();
    }

    /// <summary>Verifica se o técnico atende aos requisitos durante TODO o período
    /// [ini, fim]: ausência, vencido hoje, vence antes do início ou durante o atendimento.</summary>
    public static Aptidao Avaliar(Snapshot s, Tecnico t, IEnumerable<Requisito> reqs, DateTime iniUtc, DateTime fimUtc,
        DateTime hoje, string? fusoLocal = null)
    {
        var fuso = fusoLocal ?? s.FusoPadrao;
        var iniDia = Tempo.ParaLocal(iniUtc, fuso).Date;
        var fimDia = Tempo.ParaLocal(fimUtc, fuso).Date;
        var docs = s.Documentos.Where(d => d.TecnicoId == t.Id && d.Atual).ToList();
        var pend = new List<Pendencia>();

        foreach (var r in reqs)
        {
            var candidatos = docs.Where(d => Atende(r, d)).ToList();
            if (candidatos.Count == 0)
            {
                pend.Add(new Pendencia(r, "ausente", $"{r.Nome}: não há registro cadastrado para o técnico.", r.Bloqueia, null));
                continue;
            }
            // o melhor registro: sem vencimento ou o de vencimento mais distante
            var d = candidatos.OrderByDescending(x => x.SemVencimento || x.Vencimento is null ? DateTime.MaxValue : x.Vencimento.Value).First();
            if (d.SemVencimento || d.Vencimento is null) continue;
            var venc = d.Vencimento.Value.Date;
            if (venc < hoje.Date)
                pend.Add(new Pendencia(r, "vencido", $"{r.Nome}: vencido em {venc:dd/MM/yyyy}.", r.Bloqueia, d));
            else if (venc < iniDia)
                pend.Add(new Pendencia(r, "vence_antes", $"{r.Nome}: vence em {venc:dd/MM/yyyy}, antes do início do atendimento ({iniDia:dd/MM/yyyy}).", r.Bloqueia, d));
            else if (venc < fimDia)
                pend.Add(new Pendencia(r, "vence_durante", $"{r.Nome}: vence em {venc:dd/MM/yyyy}, durante o atendimento (término previsto em {fimDia:dd/MM/yyyy}).", r.Bloqueia, d));
        }
        var bloqueado = pend.Any(p => p.Bloqueia);
        return new Aptidao(t, pend.Count == 0, bloqueado, pend);
    }

    // ---------------------------------------------------------- vencimentos
    public sealed record Linha(Documento Doc, Tecnico? Tecnico, string Status, int? Dias, int? Alerta);

    public static List<Linha> Vencimentos(Snapshot s, DateTime hoje, bool soAtuais = true) =>
        s.Documentos.Where(d => !soAtuais || d.Atual)
            .Select(d => new Linha(d, s.Tecnico(d.TecnicoId), Status(d, hoje, s.DiasAlerta), DiasParaVencer(d, hoje), NivelAlerta(d, hoje, s.DiasAlerta)))
            .ToList();
}
