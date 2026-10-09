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

    // ---- modo automático (atravessa páginas: mapa → agenda → mapa …) ----
    public bool AutoAtivo { get; private set; }
    public bool AutoPausa { get; set; }
    public string AutoVel { get; set; } = "normal";
    public int AutoEtapa { get; set; }
    /// <summary>Qual página conduz o ciclo agora: "mapa" (Visão operacional) ou "agenda".</summary>
    public string AutoFase { get; set; } = "mapa";
    private bool _autoApresAntes;

    public void IniciarAuto() { if (AutoAtivo) return; _autoApresAntes = Apresentacao; AutoAtivo = true; AutoPausa = false; AutoFase = "mapa"; AutoEtapa = 0; Apresentacao = true; Mudou?.Invoke(); }
    public void PararAuto() { if (!AutoAtivo) return; AutoAtivo = false; if (!_autoApresAntes) Apresentacao = false; Mudou?.Invoke(); }
    public void NotificarAuto() => Mudou?.Invoke();

    /// <summary>Espera respeitando pausa e velocidade (a velocidade "normal" é lenta de propósito, para acompanhar). Cancela se o modo for encerrado.</summary>
    public async Task AutoEspera(int ms, CancellationToken ct)
    {
        var rest = ms * (AutoVel switch { "lenta" => 2.8, "rapida" => 1.0, _ => 1.8 });
        while (rest > 0)
        {
            await Task.Delay(100, ct);
            if (!AutoAtivo) throw new OperationCanceledException();
            if (!AutoPausa) rest -= 100;
        }
    }

    /// <summary>Feedback pós-ação (toast): "salvo", erros de permissão etc.</summary>
    public event Action<string, bool>? Aviso;
    public void Avisar(string texto, bool erro = false) => Aviso?.Invoke(texto, erro);
}
