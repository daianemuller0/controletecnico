namespace ControleTecnico.Logic;

/// <summary>Bloqueio simples contra tentativa em massa: 5 falhas seguidas por usuário+IP
/// travam o login por 5 minutos.</summary>
public sealed class LoginGuard
{
    private readonly Dictionary<string, (int falhas, DateTime ate)> _est = new();
    private readonly object _l = new();
    private static string K(string u, string ip) => u + "|" + ip;

    public bool Bloqueado(string usuario, string ip)
    {
        lock (_l) return _est.TryGetValue(K(usuario, ip), out var e) && e.ate > DateTime.UtcNow;
    }
    public void Falha(string usuario, string ip)
    {
        lock (_l)
        {
            _est.TryGetValue(K(usuario, ip), out var e);
            var n = e.ate > DateTime.UtcNow ? e.falhas : e.falhas + 1;
            _est[K(usuario, ip)] = n >= 5 ? (0, DateTime.UtcNow.AddMinutes(5)) : (n, DateTime.MinValue);
        }
    }
    public void Sucesso(string usuario, string ip) { lock (_l) _est.Remove(K(usuario, ip)); }
}
