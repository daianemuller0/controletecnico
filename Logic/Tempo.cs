using System.Globalization;

namespace ControleTecnico.Logic;

/// <summary>Fusos e formatação. Tudo é gravado em UTC; aqui se converte para
/// exibir e para interpretar o que a pessoa digitou (que vale no fuso do local).</summary>
public static class Tempo
{
    public static readonly CultureInfo Pt = new("pt-BR");

    public static TimeZoneInfo Fuso(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return TimeZoneInfo.Utc;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch { return TimeZoneInfo.Utc; }
    }

    public static bool FusoValido(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        try { TimeZoneInfo.FindSystemTimeZoneById(id); return true; } catch { return false; }
    }

    public static DateTime ParaLocal(DateTime utc, string? fuso) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Fuso(fuso));

    /// <summary>Converte uma hora "de parede" digitada no fuso do local para UTC.
    /// Horas inexistentes (salto do horário de verão) avançam; ambíguas usam o offset padrão.</summary>
    public static DateTime ParaUtc(DateTime local, string? fuso)
    {
        var tz = Fuso(fuso);
        var l = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (tz.IsInvalidTime(l)) l = l.AddHours(1);
        return TimeZoneInfo.ConvertTimeToUtc(l, tz);
    }

    public static DateTime Agora => DateTime.UtcNow;

    /// <summary>Início do dia (meia-noite local) em UTC.</summary>
    public static DateTime InicioDia(DateTime utc, string? fuso) =>
        ParaUtc(ParaLocal(utc, fuso).Date, fuso);

    public static string Offset(string? fuso, DateTime utc)
    {
        var o = Fuso(fuso).GetUtcOffset(utc);
        return "UTC" + (o < TimeSpan.Zero ? "−" : "+") + o.ToString(o.Minutes == 0 ? "hh" : "hh\\:mm");
    }

    /// <summary>"12/03 14:30"; acrescenta o fuso quando difere do de referência.</summary>
    public static string Hora(DateTime utc, string? fuso, string? referencia = null, bool ano = false)
    {
        var l = ParaLocal(utc, fuso);
        var s = l.ToString(ano ? "dd/MM/yyyy HH:mm" : "dd/MM HH:mm", Pt);
        if (!string.IsNullOrEmpty(referencia) && Fuso(fuso).BaseUtcOffset != Fuso(referencia).BaseUtcOffset
            || (!string.IsNullOrEmpty(referencia) && Fuso(fuso).GetUtcOffset(utc) != Fuso(referencia).GetUtcOffset(utc)))
            s += " " + Offset(fuso, utc);
        return s;
    }

    public static string Data(DateTime? utc, string? fuso = null) =>
        utc is null ? "—" : ParaLocal(utc.Value, fuso).ToString("dd/MM/yyyy", Pt);

    /// <summary>Data de calendário (vencimento/emissão): guardada como dia UTC, sem conversão de fuso.</summary>
    public static string Dia(DateTime? d) => d is null ? "—" : d.Value.ToString("dd/MM/yyyy", Pt);

    public static DateTime SoData(DateTime d) => DateTime.SpecifyKind(d.Date, DateTimeKind.Utc);

    public static string Duracao(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = -t;
        if (t.TotalMinutes < 1) return "0 min";
        if (t.TotalHours < 1) return $"{(int)t.TotalMinutes} min";
        if (t.TotalHours < 48)
        {
            var h = (int)t.TotalHours; var m = t.Minutes;
            return m == 0 ? $"{h} h" : $"{h} h {m:00} min";
        }
        return $"{(int)t.TotalDays} d {t.Hours} h";
    }

    public static TimeSpan ParseHora(string? hhmm, TimeSpan padrao) =>
        TimeSpan.TryParse(hhmm, CultureInfo.InvariantCulture, out var t) ? t : padrao;

    public static string Iso(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("o", CultureInfo.InvariantCulture);
}
