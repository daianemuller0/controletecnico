using System.Collections.Concurrent;

namespace ControleTecnico.Logic;

/// <summary>Arquivos gerados na hora (ex.: e-mail .eml da OS) entregues por um link de uso único, só para quem gerou, por 10 minutos.</summary>
public sealed class DownloadsTemporarios
{
    private sealed record Item(byte[] Dados, string Nome, string Tipo, string Login, DateTime Expira);
    private readonly ConcurrentDictionary<string, Item> _itens = new();

    public string Guardar(byte[] dados, string nome, string tipo, string login)
    {
        foreach (var k in _itens.Where(x => x.Value.Expira < DateTime.UtcNow).Select(x => x.Key).ToList()) _itens.TryRemove(k, out _);
        var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        _itens[token] = new Item(dados, nome, tipo, login, DateTime.UtcNow.AddMinutes(10));
        return token;
    }

    public (byte[] dados, string nome, string tipo)? Retirar(string token, string login)
    {
        if (!_itens.TryGetValue(token, out var i) || i.Expira < DateTime.UtcNow || !string.Equals(i.Login, login, StringComparison.OrdinalIgnoreCase)) return null;
        _itens.TryRemove(token, out _);
        return (i.Dados, i.Nome, i.Tipo);
    }
}
