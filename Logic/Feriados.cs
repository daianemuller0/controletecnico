namespace ControleTecnico.Logic;

/// <summary>
/// Feriados para a Agenda: nacionais do Brasil calculados para qualquer ano (inclusive os móveis, pela Páscoa)
/// e os cadastrados pela equipe (estaduais, municipais, da empresa), guardados na configuração.
/// Cadastrado = linhas "aaaa-mm-dd|Nome" (só naquele dia) ou "mm-dd|Nome" (todo ano).
/// </summary>
public static class Feriados
{
    public const string CfgChave = "feriados";

    public sealed record Feriado(DateTime Dia, string Nome, bool Nacional, bool Anual, string Chave);

    /// <summary>Domingo de Páscoa (algoritmo de Meeus/Jones/Butcher).</summary>
    public static DateTime Pascoa(int ano)
    {
        int a = ano % 19, b = ano / 100, c = ano % 100, d = b / 4, e = b % 4, f = (b + 8) / 25, g = (b - f + 1) / 3;
        int h = (19 * a + b - d - g + 15) % 30, i = c / 4, k = c % 4, l = (32 + 2 * e + 2 * i - h - k) % 7, m = (a + 11 * h + 22 * l) / 451;
        int mes = (h + l - 7 * m + 114) / 31, dia = (h + l - 7 * m + 114) % 31 + 1;
        return new DateTime(ano, mes, dia);
    }

    public static IEnumerable<(DateTime Dia, string Nome)> Nacionais(int ano)
    {
        var p = Pascoa(ano);
        yield return (new DateTime(ano, 1, 1), "Confraternização Universal");
        yield return (p.AddDays(-48), "Carnaval (ponto facultativo)");
        yield return (p.AddDays(-47), "Carnaval (ponto facultativo)");
        yield return (p.AddDays(-2), "Sexta-feira Santa");
        yield return (new DateTime(ano, 4, 21), "Tiradentes");
        yield return (new DateTime(ano, 5, 1), "Dia do Trabalho");
        yield return (p.AddDays(60), "Corpus Christi (ponto facultativo)");
        yield return (new DateTime(ano, 9, 7), "Independência do Brasil");
        yield return (new DateTime(ano, 10, 12), "Nossa Senhora Aparecida");
        yield return (new DateTime(ano, 11, 2), "Finados");
        yield return (new DateTime(ano, 11, 15), "Proclamação da República");
        if (ano >= 2024) yield return (new DateTime(ano, 11, 20), "Dia Nacional de Zumbi e da Consciência Negra");
        yield return (new DateTime(ano, 12, 25), "Natal");
    }

    public static List<(string Chave, string Nome)> LerCadastrados(string? cfg) =>
        (cfg ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Split('|', 2)).Where(p => p.Length == 2 && p[1].Trim() != "" && Valida(p[0])).Select(p => (p[0], p[1].Trim())).ToList();

    public static string Gravar(IEnumerable<(string Chave, string Nome)> itens) => string.Join("\n", itens.Select(i => $"{i.Chave}|{i.Nome}"));

    private static bool Valida(string chave) =>
        DateTime.TryParseExact(chave, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out _) ||
        DateTime.TryParseExact("2000-" + chave, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out _);

    /// <summary>Todos os feriados do ano (nacionais + cadastrados), em ordem de data.</summary>
    public static List<Feriado> DoAno(string? cfg, int ano)
    {
        var res = Nacionais(ano).Select(n => new Feriado(n.Dia, n.Nome, true, true, "")).ToList();
        foreach (var (chave, nome) in LerCadastrados(cfg))
        {
            if (chave.Length == 5)
            {
                if (DateTime.TryParseExact($"{ano}-{chave}", "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var d)) res.Add(new Feriado(d, nome, false, true, chave));
            }
            else if (DateTime.TryParseExact(chave, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var d) && d.Year == ano) res.Add(new Feriado(d, nome, false, false, chave));
        }
        return res.OrderBy(f => f.Dia).ThenBy(f => f.Nacional ? 0 : 1).ToList();
    }

    /// <summary>Dia → nome(s) do feriado, para pintar o calendário no período.</summary>
    public static Dictionary<DateTime, string> DoPeriodo(string? cfg, DateTime de, DateTime ate)
    {
        var res = new Dictionary<DateTime, string>();
        for (var ano = de.Year; ano <= ate.Year; ano++)
            foreach (var f in DoAno(cfg, ano).Where(f => f.Dia >= de.Date && f.Dia <= ate.Date))
                res[f.Dia] = res.TryGetValue(f.Dia, out var n) ? $"{n} · {f.Nome}" : f.Nome;
        return res;
    }
}
