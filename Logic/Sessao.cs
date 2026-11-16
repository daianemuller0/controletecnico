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

    /// <summary>Período e técnicos escolhidos nos indicadores gerenciais (valem enquanto a pessoa estiver no programa).</summary>
    public sealed record FiltroIndicadores(DateTime De, DateTime Ate, string Atalho, List<string> Tecnicos);
    public FiltroIndicadores? Indicadores { get; set; }

    /// <summary>Filtros da Agenda: continuam valendo ao ir para outra tela e voltar (zeram ao sair do programa).</summary>
    public FiltroAgenda? Agenda { get; set; }
    public sealed class FiltroAgenda
    {
        public string View { get; set; } = "linha";
        public DateTime Ref { get; set; }
        public int Dias { get; set; } = 14;
        public bool TecColunas { get; set; } = true;
        public List<string> TecSel { get; set; } = new();
        public List<string> TiposOcultos { get; set; } = new();
        public bool MostraProv { get; set; } = true;
        public bool MostraLivres { get; set; } = true;
        public bool SoConflito { get; set; }
    }

    /// <summary>Filtros do Planner: continuam valendo ao abrir um projeto e voltar.</summary>
    public PlFiltro PlannerFiltro { get; set; } = new();

    // ---- modo automático (atravessa páginas: mapa ⇄ agenda) ----
    public bool AutoAtivo { get; private set; }
    public bool AutoPausa { get; set; }
    public string AutoVel { get; set; } = "normal";
    /// <summary>Etapas escolhidas na configuração, em ordem.</summary>
    public List<AutoEtapas.Etapa> AutoPlano { get; private set; } = new();
    public AutoEtapas.Cfg AutoCfg { get; private set; } = new();
    /// <summary>Posição da etapa em andamento dentro de AutoPlano.</summary>
    public int AutoEtapa { get; set; }
    /// <summary>Qual página conduz o ciclo agora: "mapa" (Visão operacional) ou "agenda".</summary>
    public string AutoFase { get; set; } = "mapa";
    private bool _autoApresAntes;

    public bool IniciarAuto(AutoEtapas.Cfg cfg)
    {
        if (AutoAtivo) return true;
        AutoCfg = cfg.Normalizar(); AutoPlano = AutoCfg.Etapas.Select(AutoEtapas.De).ToList();
        if (AutoPlano.Count == 0) return false;
        _autoApresAntes = Apresentacao; AutoAtivo = true; AutoPausa = false; AutoVel = AutoCfg.Velocidade; AutoEtapa = 0;
        AutoFase = AutoPlano[0].Chave == "agenda" ? "agenda" : "mapa"; Apresentacao = true; Mudou?.Invoke(); return true;
    }
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
