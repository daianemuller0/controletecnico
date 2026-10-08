using ControleTecnico.Data;
using ControleTecnico.Models;

namespace ControleTecnico.Logic;

public sealed partial class Servicos
{
    // ---------------------------------------------------------------- técnicos
    public Tecnico SalvarTecnico(Ator ator, Tecnico t)
    {
        ator.Exigir(Perm.EditarTecnicos);
        var erros = new List<string>();
        if (string.IsNullOrWhiteSpace(t.Nome)) erros.Add("Informe o nome do técnico.");
        if (string.IsNullOrWhiteSpace(t.Cidade)) erros.Add("Informe a cidade onde o técnico mora.");
        if (!Tempo.FusoValido(t.FusoHorario)) erros.Add("Fuso horário inválido.");
        if (!TimeSpan.TryParse(t.JornadaInicio, out var ji) || !TimeSpan.TryParse(t.JornadaFim, out var jf) || jf <= ji)
            erros.Add("Jornada inválida: o fim deve ser posterior ao início (formato HH:mm).");
        if (Snapshot.Ids(t.DiasUteis).Length == 0) erros.Add("Selecione ao menos um dia útil da jornada.");
        if (!string.IsNullOrWhiteSpace(t.Matricula) &&
            Db.Tecnicos.Onde(x => x.Id != t.Id && DocEngine.Norm(x.Matricula) == DocEngine.Norm(t.Matricula)).Count > 0)
            erros.Add("Já existe um técnico com este identificador interno.");
        if ((t.OrigemLat.HasValue ^ t.OrigemLon.HasValue) || !(t.OrigemLat is null || Geo.CoordValida(t.OrigemLat, t.OrigemLon)))
            erros.Add("Coordenadas da origem habitual inválidas.");
        if (erros.Count > 0) throw new ValidacaoException(erros);
        var novo = string.IsNullOrEmpty(t.Id);
        if (string.IsNullOrWhiteSpace(t.OrigemHabitual)) t.OrigemHabitual = $"{t.Cidade}{(string.IsNullOrWhiteSpace(t.Estado) ? "" : "/" + t.Estado)}";
        Db.Tecnicos.Salvar(t, ator.Login);
        Auditar(ator, novo ? "tecnico.criar" : "tecnico.alterar", "tecnico", t.Id, $"Técnico {t.Nome} {(novo ? "cadastrado" : "atualizado")}");
        return t;
    }

    public Especialidade SalvarEspecialidade(Ator ator, Especialidade e)
    {
        ator.Exigir(Perm.EditarTecnicos);
        if (string.IsNullOrWhiteSpace(e.Nome)) throw new ValidacaoException("Informe o nome da especialidade.");
        if (Db.Especialidades.Onde(x => x.Id != e.Id && DocEngine.Norm(x.Nome) == DocEngine.Norm(e.Nome)).Count > 0)
            throw new ValidacaoException("Essa especialidade já existe.");
        Db.Especialidades.Salvar(e, ator.Login);
        Auditar(ator, "especialidade.salvar", "especialidade", e.Id, e.Nome);
        return e;
    }

    public Servico SalvarServico(Ator ator, Servico s)
    {
        ator.Exigir(Perm.EditarTecnicos);
        if (string.IsNullOrWhiteSpace(s.Nome)) throw new ValidacaoException("Informe o nome do serviço.");
        if (Db.Servicos.Onde(x => x.Id != s.Id && DocEngine.Norm(x.Nome) == DocEngine.Norm(s.Nome)).Count > 0)
            throw new ValidacaoException("Esse serviço já existe.");
        Db.Servicos.Salvar(s, ator.Login);
        Auditar(ator, "servico.salvar", "servico", s.Id, s.Nome);
        return s;
    }

    public async Task<Anexo> DefinirFotoAsync(Ator ator, string tecnicoId, string nome, Stream conteudo)
    {
        ator.Exigir(Perm.EditarTecnicos);
        var t = Db.Tecnicos.Obter(tecnicoId) ?? throw new ValidacaoException("Técnico não encontrado.");
        var ext = Path.GetExtension(nome).ToLowerInvariant();
        if (ext is not (".jpg" or ".jpeg" or ".png")) throw new ValidacaoException("A foto deve ser JPG ou PNG.");
        var anexo = await Arquivos.SalvarAsync(ator, "tecnico", tecnicoId, nome, conteudo);
        if (!string.IsNullOrEmpty(t.FotoAnexoId) && Db.Anexos.Obter(t.FotoAnexoId) is { } velho) Arquivos.Remover(velho);
        t.FotoAnexoId = anexo.Id;
        Db.Tecnicos.Salvar(t, ator.Login);
        Auditar(ator, "tecnico.foto", "tecnico", tecnicoId, "Foto atualizada");
        return anexo;
    }

    // ---------------------------------------------------------------- clientes
    public Cliente SalvarCliente(Ator ator, Cliente c)
    {
        ator.Exigir(Perm.EditarClientes);
        if (string.IsNullOrWhiteSpace(c.Nome)) throw new ValidacaoException("Informe o nome do cliente.");
        if (Db.Clientes.Onde(x => x.Id != c.Id && DocEngine.Norm(x.Nome) == DocEngine.Norm(c.Nome)).Count > 0)
            throw new ValidacaoException("Já existe um cliente com este nome.");
        if (!string.IsNullOrWhiteSpace(c.Email) && !c.Email.Contains('@')) throw new ValidacaoException("E-mail do cliente inválido.");
        var novo = string.IsNullOrEmpty(c.Id);
        Db.Clientes.Salvar(c, ator.Login);
        Auditar(ator, novo ? "cliente.criar" : "cliente.alterar", "cliente", c.Id, c.Nome);
        return c;
    }

    public static string MontarEndereco(Planta p)
    {
        var rua = string.Join(", ", new[] { p.Rua, p.Numero }.Where(x => !string.IsNullOrWhiteSpace(x)));
        var partes = new[] { rua, p.Complemento, p.Cep, p.Cidade, p.Estado, p.Pais }.Where(x => !string.IsNullOrWhiteSpace(x));
        return string.Join(", ", partes);
    }

    private static string ChaveEndereco(Planta p) => DocEngine.Norm($"{p.Rua}|{p.Numero}|{p.Cep}|{p.Cidade}|{p.Estado}|{p.Pais}|{p.EnderecoCompleto}");

    public void ValidarPlanta(Planta p)
    {
        var erros = new List<string>();
        if (string.IsNullOrWhiteSpace(p.Nome)) erros.Add("Informe o nome da planta/unidade.");
        if (!Db.Clientes.Existe(p.ClienteId)) erros.Add("Selecione o cliente.");
        if (string.IsNullOrWhiteSpace(p.Cidade) && string.IsNullOrWhiteSpace(p.EnderecoCompleto)) erros.Add("Informe a cidade ou o endereço completo.");
        if (string.IsNullOrWhiteSpace(p.Pais)) erros.Add("Informe o país.");
        if (!Tempo.FusoValido(p.FusoHorario)) erros.Add("Fuso horário inválido.");
        if ((p.Lat.HasValue ^ p.Lon.HasValue) || !(p.Lat is null || Geo.CoordValida(p.Lat, p.Lon))) erros.Add("Latitude/longitude inválidas (lat −90 a 90; lon −180 a 180).");
        if (Db.Plantas.Onde(x => x.Id != p.Id && x.ClienteId == p.ClienteId && DocEngine.Norm(x.Nome) == DocEngine.Norm(p.Nome)).Count > 0)
            erros.Add("Este cliente já possui uma planta com este nome.");
        if (erros.Count > 0) throw new ValidacaoException(erros);
    }

    /// <summary>Grava a planta. As coordenadas são consultadas UMA vez e guardadas: só há nova
    /// consulta se o endereço mudou (e não foi posicionada à mão) ou se o usuário pedir.</summary>
    public async Task<Planta> SalvarPlantaAsync(Ator ator, Planta p, Geocoder? geo, bool forcarGeocodificar = false)
    {
        ator.Exigir(Perm.EditarClientes);
        ValidarPlanta(p);
        var antes = Db.Plantas.Obter(p.Id);
        if (string.IsNullOrWhiteSpace(p.EnderecoCompleto)) p.EnderecoCompleto = MontarEndereco(p);
        var enderecoMudou = antes is null || ChaveEndereco(antes) != ChaveEndereco(p);
        var coordMudouNaMao = antes is not null && (antes.Lat != p.Lat || antes.Lon != p.Lon) && p.TemCoord;

        if (coordMudouNaMao || (antes is null && p.TemCoord && p.GeoStatus != "ok"))
        {
            p.GeoStatus = "manual"; p.GeoFonte = "Posição informada manualmente"; p.GeoEm = DateTime.UtcNow;
        }
        else if (p.GeoStatus != "manual" && (enderecoMudou || forcarGeocodificar || !p.TemCoord))
        {
            if (enderecoMudou || forcarGeocodificar) { p.Lat = null; p.Lon = null; p.GeoStatus = "pendente"; p.GeoFonte = ""; }
            if (geo is { Configurado: true } && (enderecoMudou || forcarGeocodificar))
            {
                var r = await geo.BuscarAsync(p.EnderecoCompleto, p.Pais);
                if (r.Ok) { p.Lat = r.Lat; p.Lon = r.Lon; p.GeoStatus = "ok"; p.GeoFonte = r.Fonte; p.GeoEm = DateTime.UtcNow; }
                else { p.GeoStatus = "pendente"; p.GeoFonte = r.Mensagem; }
            }
        }
        if (!p.TemCoord && p.GeoStatus != "manual") p.GeoStatus = "pendente";
        var novo = string.IsNullOrEmpty(p.Id);
        Db.Plantas.Salvar(p, ator.Login);
        Auditar(ator, novo ? "planta.criar" : "planta.alterar", "planta", p.Id, $"{Db.Clientes.Obter(p.ClienteId)?.Nome} · {p.Nome} ({p.GeoStatus})");
        return p;
    }

    /// <summary>Define a posição corrigida à mão (clique/arraste no mapa).</summary>
    public Planta CorrigirPosicao(Ator ator, string plantaId, double lat, double lon)
    {
        ator.Exigir(Perm.EditarClientes);
        if (!Geo.CoordValida(lat, lon)) throw new ValidacaoException("Coordenadas inválidas.");
        var p = Db.Plantas.Obter(plantaId) ?? throw new ValidacaoException("Planta não encontrada.");
        p.Lat = lat; p.Lon = lon; p.GeoStatus = "manual"; p.GeoFonte = "Posição corrigida manualmente no mapa"; p.GeoEm = DateTime.UtcNow;
        Db.Plantas.Salvar(p, ator.Login);
        Auditar(ator, "planta.posicao", "planta", p.Id, $"{p.Nome}: posição corrigida para {lat:0.00000}, {lon:0.00000}");
        return p;
    }

    /// <summary>Geocodifica as plantas pendentes (1 por segundo, política do serviço). Devolve (ok, falhas).</summary>
    public async Task<(int ok, int falhas)> LocalizarPendentesAsync(Ator ator, Geocoder geo, IProgress<string>? progresso = null, CancellationToken ct = default)
    {
        ator.Exigir(Perm.EditarClientes);
        if (!geo.Configurado) throw new ValidacaoException(geo.Pendencia);
        int ok = 0, falhas = 0;
        foreach (var p in Db.Plantas.Onde(x => x.Ativo && !x.TemCoord && x.GeoStatus != "manual"))
        {
            ct.ThrowIfCancellationRequested();
            progresso?.Report($"Localizando {p.Nome}…");
            var r = await geo.BuscarAsync(string.IsNullOrWhiteSpace(p.EnderecoCompleto) ? MontarEndereco(p) : p.EnderecoCompleto, p.Pais, ct);
            if (r.Ok) { p.Lat = r.Lat; p.Lon = r.Lon; p.GeoStatus = "ok"; p.GeoFonte = r.Fonte; p.GeoEm = DateTime.UtcNow; ok++; }
            else { p.GeoStatus = "pendente"; p.GeoFonte = r.Mensagem; falhas++; }
            Db.Plantas.Salvar(p, ator.Login);
        }
        Auditar(ator, "planta.geocodificar", "planta", "", $"Localização em lote: {ok} localizada(s), {falhas} pendente(s)");
        return (ok, falhas);
    }

    // -------------------------------------------------------------- requisitos
    public Requisito SalvarRequisito(Ator ator, Requisito r)
    {
        ator.Exigir(Perm.EditarRequisitos);
        var erros = new List<string>();
        if (string.IsNullOrWhiteSpace(r.Nome)) erros.Add("Informe o documento/treinamento exigido.");
        if (r.Escopo == "servico" ? !Db.Servicos.Existe(r.EscopoId) : !Db.Plantas.Existe(r.EscopoId)) erros.Add("Selecione o serviço ou a planta.");
        if (Db.Requisitos.Onde(x => x.Id != r.Id && x.Escopo == r.Escopo && x.EscopoId == r.EscopoId && DocEngine.Norm(x.Nome) == DocEngine.Norm(r.Nome)).Count > 0)
            erros.Add("Este requisito já está configurado para o serviço/planta.");
        if (erros.Count > 0) throw new ValidacaoException(erros);
        var antes = Db.Requisitos.Obter(r.Id);
        Db.Requisitos.Salvar(r, ator.Login);
        Auditar(ator, antes is null ? "requisito.criar" : "requisito.alterar", "requisito", r.Id,
            $"{r.Nome} ({(r.Escopo == "servico" ? "serviço " + Db.Servicos.Obter(r.EscopoId)?.Nome : "planta " + Db.Plantas.Obter(r.EscopoId)?.Nome)}) — {(r.Bloqueia ? "bloqueia a confirmação" : "apenas alerta")}");
        return r;
    }

    public void ExcluirRequisito(Ator ator, string id)
    {
        ator.Exigir(Perm.EditarRequisitos);
        var r = Db.Requisitos.Obter(id) ?? throw new ValidacaoException("Requisito não encontrado.");
        Db.Requisitos.Apagar(id);
        Auditar(ator, "requisito.excluir", "requisito", id, $"Requisito removido: {r.Nome}");
    }

    // --------------------------------------------------------------- documentos
    private static void ValidarDoc(Documento d)
    {
        var erros = new List<string>();
        if (string.IsNullOrWhiteSpace(d.Nome)) erros.Add("Informe o nome do documento, treinamento ou certificação.");
        if (string.IsNullOrWhiteSpace(d.Categoria)) erros.Add("Informe a categoria.");
        if (!d.SemVencimento && d.Vencimento is null) erros.Add("Informe a data de vencimento ou marque \"sem vencimento\".");
        if (d.SemVencimento) d.Vencimento = null;
        if (d.Emissao.HasValue && d.Vencimento.HasValue && d.Vencimento < d.Emissao) erros.Add("O vencimento não pode ser anterior à emissão.");
        if (erros.Count > 0) throw new ValidacaoException(erros);
        if (d.Emissao.HasValue) d.Emissao = Tempo.SoData(d.Emissao.Value);
        if (d.Vencimento.HasValue) d.Vencimento = Tempo.SoData(d.Vencimento.Value);
    }

    public Documento SalvarDocumento(Ator ator, Documento d)
    {
        ator.Exigir(Perm.EditarDocs, d.TecnicoId);
        if (!Db.Tecnicos.Existe(d.TecnicoId)) throw new ValidacaoException("Técnico não encontrado.");
        ValidarDoc(d);
        var novo = string.IsNullOrEmpty(d.Id);
        if (novo)
        {
            d.Id = Repo<Documento>.NovoId(); d.RaizId = d.Id; d.Versao = 1; d.Atual = true; d.CadastradoPor = ator.Nome;
        }
        else
        {
            var ant = Db.Documentos.Obter(d.Id) ?? throw new ValidacaoException("Documento não encontrado.");
            if (!ant.Atual) throw new ValidacaoException("Versões anteriores são histórico e não podem ser editadas.");
            if (ant.TecnicoId != d.TecnicoId) throw new ValidacaoException("Não é possível mudar o técnico do documento.");
            d.RaizId = ant.RaizId; d.Versao = ant.Versao; d.Atual = true; d.CadastradoPor = ant.CadastradoPor;
        }
        Db.Documentos.Salvar(d, ator.Login);
        Auditar(ator, novo ? "documento.criar" : "documento.alterar", "documento", d.Id,
            $"{d.Nome} de {Db.Tecnicos.Obter(d.TecnicoId)?.Nome}{(d.Vencimento.HasValue ? ", vence em " + Tempo.Dia(d.Vencimento) : ", sem vencimento")}");
        return d;
    }

    /// <summary>Renova: cria uma nova versão e mantém a anterior no histórico, com seus anexos.</summary>
    public Documento Renovar(Ator ator, string documentoAtualId, Documento novaVersao)
    {
        var atual = Db.Documentos.Obter(documentoAtualId) ?? throw new ValidacaoException("Documento não encontrado.");
        ator.Exigir(Perm.EditarDocs, atual.TecnicoId);
        if (!atual.Atual) throw new ValidacaoException("Só a versão atual pode ser renovada.");
        novaVersao.TecnicoId = atual.TecnicoId;
        ValidarDoc(novaVersao);
        var maxVersao = Db.Documentos.Onde(x => x.RaizId == atual.RaizId).Max(x => x.Versao);
        novaVersao.Id = Repo<Documento>.NovoId();
        novaVersao.RaizId = atual.RaizId; novaVersao.Versao = maxVersao + 1; novaVersao.Atual = true; novaVersao.CadastradoPor = ator.Nome;
        atual.Atual = false;
        Db.Documentos.Salvar(novaVersao, ator.Login);
        Db.Documentos.Salvar(atual, ator.Login);
        Auditar(ator, "documento.renovar", "documento", novaVersao.Id,
            $"{novaVersao.Nome} de {Db.Tecnicos.Obter(novaVersao.TecnicoId)?.Nome} renovado (v{atual.Versao} → v{novaVersao.Versao}), novo vencimento {Tempo.Dia(novaVersao.Vencimento)}");
        return novaVersao;
    }

    public void ExcluirDocumento(Ator ator, string id)
    {
        var d = Db.Documentos.Obter(id) ?? throw new ValidacaoException("Documento não encontrado.");
        ator.Exigir(Perm.EditarDocs, d.TecnicoId);
        if (!ator.Pode(Perm.Administrar) && !ator.Pode(Perm.EditarRequisitos))
            throw new UnauthorizedAccessException("Somente Gestão ou Administrador podem excluir documentos.");
        var versoes = Db.Documentos.Onde(x => x.RaizId == d.RaizId);
        if (versoes.Count > 1 && d.Atual) throw new ValidacaoException("Este documento tem histórico de renovações; ele não pode ser excluído para preservar os certificados anteriores.");
        foreach (var a in Db.Anexos.Onde(a => a.DonoTipo == "documento" && a.DonoId == id)) Arquivos.Remover(a);
        Db.Documentos.Apagar(id);
        Auditar(ator, "documento.excluir", "documento", id, $"{d.Nome} de {Db.Tecnicos.Obter(d.TecnicoId)?.Nome} excluído");
    }

    public async Task<Anexo> AnexarAsync(Ator ator, string donoTipo, string donoId, string nome, Stream conteudo)
    {
        switch (donoTipo)
        {
            case "documento":
                var d = Db.Documentos.Obter(donoId) ?? throw new ValidacaoException("Documento não encontrado.");
                ator.Exigir(Perm.EditarDocs, d.TecnicoId);
                if (!d.Atual) throw new ValidacaoException("Versões anteriores são histórico e não aceitam novos anexos.");
                break;
            case "viagem":
                ator.Exigir(Perm.EditarViagens);
                if (!Db.Viagens.Existe(donoId)) throw new ValidacaoException("Viagem não encontrada.");
                break;
            default: throw new ValidacaoException("Destino de anexo inválido.");
        }
        var a = await Arquivos.SalvarAsync(ator, donoTipo, donoId, nome, conteudo);
        Auditar(ator, "anexo.adicionar", donoTipo, donoId, $"Anexo \"{a.NomeOriginal}\" adicionado");
        return a;
    }

    public void RemoverAnexo(Ator ator, string anexoId)
    {
        var a = Db.Anexos.Obter(anexoId) ?? throw new ValidacaoException("Anexo não encontrado.");
        if (a.DonoTipo == "documento")
        {
            var d = Db.Documentos.Obter(a.DonoId);
            ator.Exigir(Perm.EditarDocs, d?.TecnicoId);
            if (d is not null && !d.Atual) throw new ValidacaoException("Anexos de versões anteriores são histórico e não podem ser removidos.");
        }
        else if (a.DonoTipo == "viagem") ator.Exigir(Perm.EditarViagens);
        else throw new UnauthorizedAccessException();
        Arquivos.Remover(a);
        Auditar(ator, "anexo.remover", a.DonoTipo, a.DonoId, $"Anexo \"{a.NomeOriginal}\" removido");
    }

    /// <summary>Quem pode ver/baixar o conteúdo deste anexo (aplicado no endpoint de download).</summary>
    public bool PodeAcessar(Ator ator, Anexo a)
    {
        switch (a.DonoTipo)
        {
            case "documento":
                var d = Db.Documentos.Obter(a.DonoId);
                return d is not null && ator.Pode(Perm.VerDocsConteudo, d.TecnicoId);
            case "viagem":
                if (ator.Pode(Perm.VerAnexosViagem)) return true;
                var v = Db.Viagens.Obter(a.DonoId);
                return v is not null && ator.Papel == Roles.Tecnico && Snapshot.Ids(v.TecnicoIds).Contains(ator.TecnicoId);
            case "tecnico":
                return ator.VeTecnico(a.DonoId);
            default: return false;
        }
    }

    // ---------------------------------------------------------- usuários e config
    public Usuario SalvarUsuario(Ator ator, Usuario u, string? novaSenha)
    {
        ator.Exigir(Perm.Administrar);
        u.Id = (u.Id ?? "").Trim().ToLowerInvariant();
        var erros = new List<string>();
        if (string.IsNullOrWhiteSpace(u.Id) || u.Id.Any(c => !(char.IsLetterOrDigit(c) || c is '.' or '_' or '-'))) erros.Add("Login inválido (use letras, números, ponto, hífen ou sublinhado).");
        if (string.IsNullOrWhiteSpace(u.Nome)) erros.Add("Informe o nome.");
        u.Papel = Roles.Normalize(u.Papel);
        if (u.Papel == Roles.Tecnico && !Db.Tecnicos.Existe(u.TecnicoId)) erros.Add("O perfil Técnico precisa estar ligado a uma ficha de técnico.");
        var existente = Db.Usuarios.Obter(u.Id);
        if (existente is null && string.IsNullOrEmpty(novaSenha)) erros.Add("Defina uma senha para o novo usuário.");
        if (!string.IsNullOrEmpty(novaSenha) && novaSenha.Length < 8) erros.Add("A senha deve ter ao menos 8 caracteres.");
        if (existente is { Papel: Roles.Admin } && (u.Papel != Roles.Admin || !u.Ativo) &&
            Db.Usuarios.Onde(x => x.Papel == Roles.Admin && x.Ativo && x.Id != u.Id).Count == 0)
            erros.Add("Deve existir ao menos um administrador ativo.");
        if (erros.Count > 0) throw new ValidacaoException(erros);
        if (existente is not null) { u.Hash = existente.Hash; u.Salt = existente.Salt; }
        if (!string.IsNullOrEmpty(novaSenha)) (u.Hash, u.Salt) = HashSenha(novaSenha);
        Db.Usuarios.Salvar(u, ator.Login);
        Auditar(ator, existente is null ? "usuario.criar" : "usuario.alterar", "usuario", u.Id, $"{u.Id} ({Roles.Label(u.Papel)})");
        return u;
    }

    public void SalvarConfig(Ator ator, string alertas, string fusoPadrao, int confirmacaoHoras)
    {
        ator.Exigir(Perm.Administrar);
        var dias = alertas.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => int.TryParse(x, out var n) ? n : -1).ToList();
        if (dias.Count == 0 || dias.Any(n => n is <= 0 or > 1000)) throw new ValidacaoException("Alertas: informe dias separados por vírgula (ex.: 30, 60, 90).");
        if (!Tempo.FusoValido(fusoPadrao)) throw new ValidacaoException("Fuso horário padrão inválido.");
        if (confirmacaoHoras is < 1 or > 720) throw new ValidacaoException("Validade da confirmação de local: de 1 a 720 horas.");
        Db.SetCfg(Db.CfgAlertas, string.Join(",", dias.Distinct().OrderBy(x => x)), ator.Login);
        Db.SetCfg(Db.CfgFusoPadrao, fusoPadrao, ator.Login);
        Db.SetCfg(Db.CfgConfirmacaoHoras, confirmacaoHoras.ToString(), ator.Login);
        Auditar(ator, "config.alterar", "config", "", $"Alertas {string.Join("/", dias)} dias; fuso {fusoPadrao}; confirmação de local {confirmacaoHoras} h");
    }
}

public sealed partial class Servicos
{
    public void AlterarSenha(Ator ator, string atual, string nova)
    {
        var u = Db.Usuarios.Obter(ator.Login) ?? throw new ValidacaoException("Usuário não encontrado.");
        if (!ConferirSenha(u, atual)) throw new ValidacaoException("A senha atual está incorreta.");
        if (nova.Length < 8) throw new ValidacaoException("A nova senha deve ter ao menos 8 caracteres.");
        (u.Hash, u.Salt) = HashSenha(nova);
        Db.Usuarios.Salvar(u, ator.Login);
        Auditar(ator, "usuario.senha", "usuario", u.Id, "Senha alterada pelo próprio usuário");
    }

    /// <summary>Recarrega os dados fictícios (apaga os operacionais e semeia de novo).</summary>
    public void RecarregarDemo(Ator ator)
    {
        ator.Exigir(Perm.Administrar);
        Seed.LimparTudo(Db, Arquivos);
        Seed.Demo(Db, Arquivos);
        Auditar(ator, "demo.recarregar", "sistema", "", "Dados de demonstração recarregados");
    }

    /// <summary>Remove todos os dados operacionais e a marca de demonstração (preparo para uso real).</summary>
    public void LimparDadosOperacionais(Ator ator)
    {
        ator.Exigir(Perm.Administrar);
        Seed.LimparTudo(Db, Arquivos);
        Db.SetCfg(Db.CfgDemo, "0", ator.Login);
        Auditar(ator, "dados.limpar", "sistema", "", "Todos os dados operacionais foram removidos");
    }
}
