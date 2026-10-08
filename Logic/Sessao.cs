using Microsoft.AspNetCore.Components.Authorization;

namespace ControleTecnico.Logic;

/// <summary>Estado da sessão do circuito Blazor: quem está logado e o modo apresentação.
/// (Scoped: um por aba do navegador.)</summary>
public sealed class Sessao
{
    private readonly AuthenticationStateProvider _auth;
    private Ator? _ator;
    private bool _apresentacao;

    public Sessao(AuthenticationStateProvider auth) => _auth = auth;

    public async Task<Ator> AtorAsync()
    {
        if (_ator is not null) return _ator;
        var st = await _auth.GetAuthenticationStateAsync();
        return _ator = Ator.De(st.User);
    }

    /// <summary>Modo apresentação: amplia mapa/indicadores/agenda e oculta dados sensíveis
    /// (contatos, códigos de reserva e conteúdo de documentos).</summary>
    public bool Apresentacao
    {
        get => _apresentacao;
        set { if (_apresentacao != value) { _apresentacao = value; Mudou?.Invoke(); } }
    }

    public event Action? Mudou;

    /// <summary>Feedback pós-ação (toast): "salvo", erros de permissão etc.</summary>
    public event Action<string, bool>? Aviso;
    public void Avisar(string texto, bool erro = false) => Aviso?.Invoke(texto, erro);
}
