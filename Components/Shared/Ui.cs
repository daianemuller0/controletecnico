using ControleTecnico.Logic;
using ControleTecnico.Models;

namespace ControleTecnico.Components.Shared;

public static class Ui
{
    /// <summary>Lista guardada como texto, um item por linha (equipamentos).</summary>
    public static List<string> Linhas(string? s) => (s ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    public static IEnumerable<string> ProjetosUsados(Snapshot s) =>
        s.Viagens.Values.Select(v => v.Projeto).Concat(s.Atendimentos.Select(a => a.Projeto)).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase);
    public static IEnumerable<string> EquipamentosUsados(Snapshot s) =>
        s.Viagens.Values.SelectMany(v => Linhas(v.Equipamentos)).Concat(s.Atendimentos.SelectMany(a => Linhas(a.Equipamentos))).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x);
    /// <summary>Formulários de viagem e compromisso: o número do projeto é obrigatório.</summary>
    public static void ExigirProjeto(string? projeto)
    {
        if (string.IsNullOrWhiteSpace(projeto)) throw new ValidacaoException("Informe o número do projeto: é por ele que a equipe controla as viagens.");
        if (projeto.Trim().Length > 40) throw new ValidacaoException("Número do projeto com até 40 caracteres.");
    }
    public static string JuntarLinhas(IEnumerable<string> l) => string.Join("\n", l.Select(x => x.Trim()).Where(x => x != "").Distinct(StringComparer.OrdinalIgnoreCase));

    /// <summary>Filtro por texto digitado: sem acento/caixa, basta conter.</summary>
    public static bool Contem(string? valor, string? filtro) => string.IsNullOrWhiteSpace(filtro) || DocEngine.Norm(valor ?? "").Contains(DocEngine.Norm(filtro));

    public static Item Op(string key) => Vocab.Get(Vocab.StatusOp, key);
    public static Item TipoEv(string key) => Vocab.Get(Vocab.TiposEvento, key);
    public static Item StViagem(string key) => Vocab.Get(Vocab.StatusViagem, key);
    public static Item StDoc(string key) => Vocab.Get(Vocab.StatusDoc, key);

    public static readonly string[] OrdemOp =
        { Vocab.OpAtendimento, Vocab.OpViagem, Vocab.OpDisponivel, Vocab.OpFerias, Vocab.OpIndisponivel, Vocab.OpSemProg };

    public static int PesoOp(string s) => Array.IndexOf(OrdemOp, s) is var i and >= 0 ? i : 99;

    /// <summary>Texto "dd/MM HH:mm" no fuso do local; acrescenta o offset quando difere do padrão do sistema.</summary>
    public static string Hora(DateTime utc, string? fusoLocal, Snapshot s, bool ano = false) =>
        Tempo.Hora(utc, string.IsNullOrEmpty(fusoLocal) ? s.FusoPadrao : fusoLocal, s.FusoPadrao, ano);

    public static string Periodo(DateTime ini, DateTime fim, string? fusoLocal, Snapshot s)
    {
        var f = string.IsNullOrEmpty(fusoLocal) ? s.FusoPadrao : fusoLocal;
        var a = Tempo.ParaLocal(ini, f); var b = Tempo.ParaLocal(fim, f);
        var sufixo = Tempo.Fuso(f).GetUtcOffset(ini) != Tempo.Fuso(s.FusoPadrao).GetUtcOffset(ini) ? " " + Tempo.Offset(f, ini) : "";
        return a.Date == b.Date
            ? $"{a:dd/MM HH:mm} – {b:HH:mm}{sufixo}"
            : $"{a:dd/MM HH:mm} → {b:dd/MM HH:mm}{sufixo}";
    }

    /// <summary>Valor para &lt;input type="datetime-local"&gt; no fuso informado.</summary>
    public static string ParaInput(DateTime? utc, string? fuso) =>
        utc is null ? "" : Tempo.ParaLocal(utc.Value, fuso).ToString("yyyy-MM-ddTHH:mm");

    public static DateTime? DoInput(string? v, string? fuso) =>
        DateTime.TryParse(v, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d)
            ? Tempo.ParaUtc(d, fuso) : null;

    public static string DataInput(DateTime? d) => d is null ? "" : d.Value.ToString("yyyy-MM-dd");
    public static DateTime? DoDataInput(string? v) =>
        DateTime.TryParse(v, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d)
            ? DateTime.SpecifyKind(d.Date, DateTimeKind.Utc) : null;

    public static List<string> Erros(Exception ex) => ex switch
    {
        ValidacaoException v => v.Erros,
        UnauthorizedAccessException u => new List<string> { u.Message },
        _ => new List<string> { "Não foi possível concluir a operação: " + ex.Message },
    };

    public static string[] Ids(string? csv) => Snapshot.Ids(csv);

    public static string Corte(string? s, int n) => string.IsNullOrEmpty(s) || s.Length <= n ? s ?? "" : s[..(n - 1)] + "…";
}
