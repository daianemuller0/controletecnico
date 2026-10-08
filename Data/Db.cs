using ControleTecnico.Models;

namespace ControleTecnico.Data;

/// <summary>Todos os repositórios sobre o mesmo ParquetStore: a fonte única
/// dos registros que alimentam mapa, agenda e viagens.</summary>
public sealed class Db
{
    public ParquetStore Store { get; }
    public Repo<Tecnico> Tecnicos { get; }
    public Repo<Especialidade> Especialidades { get; }
    public Repo<Servico> Servicos { get; }
    public Repo<Cliente> Clientes { get; }
    public Repo<Planta> Plantas { get; }
    public Repo<Viagem> Viagens { get; }
    public Repo<Trecho> Trechos { get; }
    public Repo<Atendimento> Atendimentos { get; }
    public Repo<Indisponibilidade> Indisponibilidades { get; }
    public Repo<ConfirmacaoLocal> Confirmacoes { get; }
    public Repo<Documento> Documentos { get; }
    public Repo<Anexo> Anexos { get; }
    public Repo<Requisito> Requisitos { get; }
    public Repo<ConfigItem> Config { get; }
    public Repo<Auditoria> Auditorias { get; }
    public Repo<Usuario> Usuarios { get; }

    public Db(ParquetStore store)
    {
        Store = store;
        Tecnicos = new(store, "tecnicos");
        Especialidades = new(store, "especialidades");
        Servicos = new(store, "servicos");
        Clientes = new(store, "clientes");
        Plantas = new(store, "plantas");
        Viagens = new(store, "viagens");
        Trechos = new(store, "trechos");
        Atendimentos = new(store, "atendimentos");
        Indisponibilidades = new(store, "indisponibilidades");
        Confirmacoes = new(store, "confirmacoes_local");
        Documentos = new(store, "documentos");
        Anexos = new(store, "anexos");
        Requisitos = new(store, "requisitos");
        Config = new(store, "config");
        Auditorias = new(store, "auditoria");
        Usuarios = new(store, "usuarios");
    }

    // ---- configuração (chave/valor) ----
    public string Cfg(string chave, string padrao = "") => Config.Obter(chave)?.Valor is { Length: > 0 } v ? v : padrao;
    public void SetCfg(string chave, string valor, string? por = null) =>
        Config.Salvar(new ConfigItem { Id = chave, Valor = valor }, por);

    public const string CfgFusoPadrao = "fuso_padrao";
    public const string CfgAlertas = "alertas_dias";
    public const string CfgDemo = "ambiente_demo";
    public const string CfgConfirmacaoHoras = "confirmacao_validade_horas";

    public string FusoPadrao => Cfg(CfgFusoPadrao, "America/Sao_Paulo");
    public int[] DiasAlerta
    {
        get
        {
            var l = Cfg(CfgAlertas, "30,60,90").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => int.TryParse(x, out var n) ? n : 0).Where(n => n > 0).Distinct().OrderBy(n => n).ToArray();
            return l.Length == 0 ? new[] { 30, 60, 90 } : l;
        }
    }
    public int ConfirmacaoValidadeHoras => int.TryParse(Cfg(CfgConfirmacaoHoras, "24"), out var h) && h > 0 ? h : 24;
    public bool Demo => Cfg(CfgDemo) == "1";
}
