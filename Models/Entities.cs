using ControleTecnico.Data;

namespace ControleTecnico.Models;

// Todas as datas/horas de instante são guardadas em UTC. Datas "de calendário"
// (emissão e vencimento de documento) são guardadas como meia-noite UTC do dia.

public class Tecnico : Entity
{
    public string Nome { get; set; } = "";
    public string Matricula { get; set; } = "";
    public string Telefone { get; set; } = "";
    public string Email { get; set; } = "";
    public string Cidade { get; set; } = "";
    public string Estado { get; set; } = "";
    public string Pais { get; set; } = "Brasil";
    public string OrigemHabitual { get; set; } = "";
    public double? OrigemLat { get; set; }
    public double? OrigemLon { get; set; }
    public string FusoHorario { get; set; } = "America/Sao_Paulo";
    public string EspecialidadeIds { get; set; } = "";   // csv
    public string ServicoIds { get; set; } = "";         // csv
    public string JornadaInicio { get; set; } = "08:00";
    public string JornadaFim { get; set; } = "17:00";
    public string DiasUteis { get; set; } = "1,2,3,4,5"; // DayOfWeek, 0 = domingo
    public bool Ativo { get; set; } = true;
    public string Obs { get; set; } = "";
    public string FotoAnexoId { get; set; } = "";
    public string UsuarioLogin { get; set; } = "";
}

public class Especialidade : Entity { public string Nome { get; set; } = ""; }

public class Servico : Entity
{
    public string Nome { get; set; } = "";
    public string Descricao { get; set; } = "";
    public bool Ativo { get; set; } = true;
}

public class Cliente : Entity
{
    public string Nome { get; set; } = "";
    public string RazaoSocial { get; set; } = "";
    public string Documento { get; set; } = "";
    public string Contato { get; set; } = "";
    public string Telefone { get; set; } = "";
    public string Email { get; set; } = "";
    public string Obs { get; set; } = "";
    public bool Ativo { get; set; } = true;
}

public class Planta : Entity
{
    public string ClienteId { get; set; } = "";
    public string Nome { get; set; } = "";
    public string Pais { get; set; } = "Brasil";
    public string Estado { get; set; } = "";
    public string Cidade { get; set; } = "";
    public string Cep { get; set; } = "";
    public string Rua { get; set; } = "";
    public string Numero { get; set; } = "";
    public string Complemento { get; set; } = "";
    public string EnderecoCompleto { get; set; } = "";
    public string FusoHorario { get; set; } = "America/Sao_Paulo";
    public double? Lat { get; set; }
    public double? Lon { get; set; }
    /// <summary>ok (geocodificada) | manual (corrigida pelo usuário) | pendente.</summary>
    public string GeoStatus { get; set; } = "pendente";
    public string GeoFonte { get; set; } = "";
    /// <summary>Até onde a localização em cascata chegou: endereco | cidade | estado | pais (vazio = manual/pendente).</summary>
    public string GeoNivel { get; set; } = "";
    public DateTime? GeoEm { get; set; }
    public string ContatoLocal { get; set; } = "";
    public string Acesso { get; set; } = "";
    public string Obs { get; set; } = "";
    public bool Ativo { get; set; } = true;
    public bool TemCoord => Lat.HasValue && Lon.HasValue;
}

public class Viagem : Entity
{
    public string Codigo { get; set; } = "";
    public string ClienteId { get; set; } = "";
    public string PlantaId { get; set; } = "";       // destino principal
    public string ServicoId { get; set; } = "";
    public string Status { get; set; } = Vocab.ViagemRascunho;
    public string TecnicoIds { get; set; } = "";     // participantes (csv)
    public string Origem { get; set; } = "";
    public string Destino { get; set; } = "";
    public string Responsavel { get; set; } = "";
    public string JanelaInicio { get; set; } = "08:00";
    public string JanelaFim { get; set; } = "17:00";
    public double? HorasPrevistas { get; set; }
    public double? HorasRealizadas { get; set; }
    public string Obs { get; set; } = "";
}

public class Trecho : Entity
{
    public string ViagemId { get; set; } = "";
    public int Ordem { get; set; }
    public string Tipo { get; set; } = Vocab.TrechoIda;
    public string Modal { get; set; } = Vocab.ModalCarro;
    public string TecnicoIds { get; set; } = "";     // vazio = todos da viagem
    public string Origem { get; set; } = "";
    public double? OrigemLat { get; set; }
    public double? OrigemLon { get; set; }
    public string Destino { get; set; } = "";
    public double? DestLat { get; set; }
    public double? DestLon { get; set; }
    public string DestPlantaId { get; set; } = "";
    public string OrigemPlantaId { get; set; } = "";
    public DateTime? PartidaPrev { get; set; }
    public DateTime? ChegadaPrev { get; set; }
    public DateTime? PartidaReal { get; set; }
    public DateTime? ChegadaReal { get; set; }
    public string FusoOrigem { get; set; } = "";
    public string FusoDestino { get; set; } = "";
    public int? DuracaoMin { get; set; }
    /// <summary>informada | estimada | horarios (derivada de saída e chegada).</summary>
    public string DuracaoFonte { get; set; } = "";
    public double? DistanciaKm { get; set; }
    public string Veiculo { get; set; } = "";
    public string Motorista { get; set; } = "";
    public string Companhia { get; set; } = "";
    public string Voo { get; set; } = "";
    public string AeroOrigem { get; set; } = "";
    public string AeroDestino { get; set; } = "";
    public string Reserva { get; set; } = "";        // restrito
    public string Obs { get; set; } = "";
}

public class Atendimento : Entity
{
    public string ViagemId { get; set; } = "";       // vazio = atendimento avulso
    public string TecnicoIds { get; set; } = "";
    public string ClienteId { get; set; } = "";
    public string PlantaId { get; set; } = "";
    public string ServicoId { get; set; } = "";
    public DateTime IniPrev { get; set; }
    public DateTime FimPrev { get; set; }
    public DateTime? IniReal { get; set; }
    public DateTime? FimReal { get; set; }
    public double? HorasPrev { get; set; }
    public double? HorasReal { get; set; }
    public string JanelaInicio { get; set; } = "";
    public string JanelaFim { get; set; } = "";
    /// <summary>Só vale para atendimento avulso; vinculado herda o status da viagem.</summary>
    public string Status { get; set; } = Vocab.ViagemPlanejada;
    public string Obs { get; set; } = "";
}

public class Indisponibilidade : Entity
{
    public string TecnicoId { get; set; } = "";
    public string Tipo { get; set; } = Vocab.IndFerias;
    public DateTime Ini { get; set; }
    public DateTime Fim { get; set; }
    public string Motivo { get; set; } = "";
    public bool Provisoria { get; set; }
}

public class ConfirmacaoLocal : Entity
{
    public string TecnicoId { get; set; } = "";
    public string PlantaId { get; set; } = "";
    public string Texto { get; set; } = "";
    public double? Lat { get; set; }
    public double? Lon { get; set; }
    public DateTime Quando { get; set; }
    public string Por { get; set; } = "";
    public string Origem { get; set; } = "gestao";   // tecnico | gestao
    public string Obs { get; set; } = "";
}

public class Documento : Entity
{
    public string TecnicoId { get; set; } = "";
    public string Nome { get; set; } = "";
    public string Categoria { get; set; } = "Documento";
    public string Norma { get; set; } = "";
    public string NormaVersao { get; set; } = "";
    public string Emissor { get; set; } = "";
    public DateTime? Emissao { get; set; }
    public DateTime? Vencimento { get; set; }
    public bool SemVencimento { get; set; }
    public string Obs { get; set; } = "";
    public string CadastradoPor { get; set; } = "";
    /// <summary>Todas as versões (renovações) de um mesmo documento partilham a raiz.</summary>
    public string RaizId { get; set; } = "";
    public int Versao { get; set; } = 1;
    public bool Atual { get; set; } = true;
}

public class Anexo : Entity
{
    /// <summary>documento | viagem | tecnico (foto).</summary>
    public string DonoTipo { get; set; } = "";
    public string DonoId { get; set; } = "";
    public string NomeOriginal { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long Tamanho { get; set; }
    public string Chave { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string EnviadoPor { get; set; } = "";
}

public class Requisito : Entity
{
    /// <summary>servico | planta.</summary>
    public string Escopo { get; set; } = "servico";
    public string EscopoId { get; set; } = "";
    public string Nome { get; set; } = "";
    public string Categoria { get; set; } = "";
    public string Norma { get; set; } = "";
    public bool Bloqueia { get; set; } = true;
    public string Obs { get; set; } = "";
}

public class ConfigItem : Entity { public string Valor { get; set; } = ""; }

public class Auditoria : Entity
{
    public DateTime Quando { get; set; }
    public string Usuario { get; set; } = "";
    public string Papel { get; set; } = "";
    public string Acao { get; set; } = "";
    public string Entidade { get; set; } = "";
    public string EntidadeId { get; set; } = "";
    public string Resumo { get; set; } = "";
}

public class Usuario : Entity
{
    // Id = login
    public string Nome { get; set; } = "";
    public string Papel { get; set; } = Roles.Consulta;
    public string Hash { get; set; } = "";
    public string Salt { get; set; } = "";
    public string TecnicoId { get; set; } = "";
    public bool Ativo { get; set; } = true;
}
