using ControleTecnico.Data;
using ControleTecnico.Models;

namespace ControleTecnico.Logic;

public sealed record PlantaItem(string Id, string ClienteId, string Nome, string Cliente, string Cidade, string Estado, string Pais, string Rua, bool Ativa, string Geo, string Chave, string NomeNorm);

/// <summary>Índice em memória para achar uma planta entre milhares: normaliza uma vez (sem acento/caixa) e filtra por TODAS as palavras
/// digitadas, em qualquer campo (nome, cidade, estado, país, endereço, cliente). Reconstruído só quando o cadastro muda.</summary>
public sealed class BuscaPlantas
{
    private readonly Db _db; private readonly object _l = new();
    private long _versao = -1; private long _versaoCli = -1; private List<PlantaItem> _itens = new();
    public BuscaPlantas(Db db) => _db = db;

    public IReadOnlyList<PlantaItem> Itens() { Garantir(); return _itens; }

    private void Garantir()
    {
        lock (_l)
        {
            _db.Plantas.Sincronizar(); _db.Clientes.Sincronizar();      // confere a pasta compartilhada (alterações de outras máquinas)
            if (_versao == _db.Plantas.Versao && _versaoCli == _db.Clientes.Versao) return;
            var plantas = _db.Plantas.Todos(); var clientes = _db.Clientes.Todos();
            var cli = clientes.ToDictionary(c => c.Id, c => c.Nome);
            _itens = plantas.Select(p =>
            {
                var cliente = cli.GetValueOrDefault(p.ClienteId, "");
                var geo = !p.TemCoord ? "pendente" : p.GeoStatus == "manual" ? "manual" : string.IsNullOrEmpty(p.GeoNivel) || p.GeoNivel == "endereco" ? "ok" : "aprox";
                var chave = DocEngine.Norm($"{p.Nome} {cliente} {p.Cidade} {p.Estado} {p.Pais} {p.Rua} {p.Cep}");
                return new PlantaItem(p.Id, p.ClienteId, p.Nome, cliente, p.Cidade, p.Estado, p.Pais, p.Rua, p.Ativo, geo, chave, DocEngine.Norm(p.Nome));
            }).OrderBy(i => i.Nome, StringComparer.OrdinalIgnoreCase).ToList();
            _versao = _db.Plantas.Versao; _versaoCli = _db.Clientes.Versao;
        }
    }

    /// <summary>Todas as palavras precisam aparecer; resultados ordenados por relevância (nome começa com > nome contém > outros campos).</summary>
    public IEnumerable<PlantaItem> Buscar(string? texto, bool soAtivas = true, string? incluirId = null)
    {
        Garantir();
        var palavras = DocEngine.Norm(texto).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        IEnumerable<PlantaItem> q = _itens.Where(i => (!soAtivas || i.Ativa || i.Id == incluirId));
        if (palavras.Length == 0) return q;
        var frase = string.Join(' ', palavras);
        return q.Where(i => palavras.All(w => i.Chave.Contains(w)))
                .OrderBy(i => i.NomeNorm.StartsWith(frase) ? 0 : i.NomeNorm.Contains(frase) ? 1 : palavras.All(w => i.NomeNorm.Contains(w)) ? 2 : 3)
                .ThenBy(i => i.Nome, StringComparer.OrdinalIgnoreCase);
    }

    public PlantaItem? Obter(string? id) => string.IsNullOrEmpty(id) ? null : Itens().FirstOrDefault(i => i.Id == id);
}
