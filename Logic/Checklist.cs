using System.Text.Json;
using ControleTecnico.Models;

namespace ControleTecnico.Logic;

/// <summary>Lista de verificação da viagem (controle da equipe de serviços): itens, feito, quem marcou e quando.</summary>
public static class Checklist
{
    public sealed class Item
    {
        public string Texto { get; set; } = "";
        public bool Feito { get; set; }
        public string Por { get; set; } = "";
        public DateTime? Em { get; set; }
    }

    /// <summary>Itens padrão de toda viagem nova (a equipe pode acrescentar ou remover em cada viagem).</summary>
    public static readonly string[] Padrao =
    {
        "Solicitação de Serviço (PJ)", "LGPD", "Documentação", "Integração", "Política de viagem", "Adiantamento",
        "Voo", "Hotel", "Carro", "LIBERADO PARA ENTRADA NO CLIENTE", "Desenhos no TEAMS", "Ordem de Serviço",
        "Itens arquivados no PV", "Relatório de Despesas", "Relatório Assessoria", "POWRA", "Cobrança (Rayssa)",
    };

    public const string ItemOrdemServico = "Ordem de Serviço";
    public const string ItemLiberado = "LIBERADO PARA ENTRADA NO CLIENTE";

    private static readonly JsonSerializerOptions Js = new() { PropertyNameCaseInsensitive = true };

    public static List<Item> Ler(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Novo();
        try { return JsonSerializer.Deserialize<List<Item>>(json, Js) ?? Novo(); } catch { return Novo(); }
    }
    public static string Gravar(IEnumerable<Item> itens) => JsonSerializer.Serialize(itens.ToList());
    public static List<Item> Novo() => Padrao.Select(t => new Item { Texto = t }).ToList();

    public static (int feitos, int total) Progresso(Viagem v)
    {
        var l = Ler(v.Checklist); return (l.Count(i => i.Feito), l.Count);
    }
}
