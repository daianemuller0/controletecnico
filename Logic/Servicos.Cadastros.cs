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
        if (string.IsNullOrWhiteSpace(t.OrigemHabitual) && !string.IsNullOrWhiteSpace(t.Cidade)) t.OrigemHabitual = $"{t.Cidade}{(string.IsNullOrWhiteSpace(t.Estado) ? "" : "/" + t.Estado)}";
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
        if (!ator.Pode(Perm.EditarTecnicos)) ator.Exigir(Perm.EditarViagens);      // quem planeja viagens também cadastra serviços
        if (string.IsNullOrWhiteSpace(s.Nome)) throw new ValidacaoException("Informe o nome do serviço.");
        if (Db.Servicos.Onde(x => x.Id != s.Id && DocEngine.Norm(x.Nome) == DocEngine.Norm(s.Nome)).Count > 0)
            throw new ValidacaoException("Esse serviço já existe.");
        Db.Servicos.Salvar(s, ator.Login);
        Auditar(ator, "servico.salvar", "servico", s.Id, s.Nome);
        return s;
    }

    /// <summary>Foto do usuário (topo da tela). A própria pessoa ou o administrador troca.</summary>
    public async Task<Anexo> DefinirFotoUsuarioAsync(Ator ator, string login, string nome, Stream conteudo)
    {
        if (!string.Equals(ator.Login, login, StringComparison.OrdinalIgnoreCase)) ator.Exigir(Perm.Administrar);
        var u = Db.Usuarios.Obter(login) ?? throw new ValidacaoException("Usuário não encontrado.");
        var ext = Path.GetExtension(nome).ToLowerInvariant();
        if (ext is not (".jpg" or ".jpeg" or ".png")) throw new ValidacaoException("A foto deve ser JPG ou PNG.");
        var anexo = await Arquivos.SalvarAsync(ator, "usuario", u.Id, nome, conteudo);
        if (!string.IsNullOrEmpty(u.FotoAnexoId) && Db.Anexos.Obter(u.FotoAnexoId) is { } velho) Arquivos.Remover(velho);
        u.FotoAnexoId = anexo.Id; Db.Usuarios.Salvar(u, ator.Login);
        Auditar(ator, "usuario.foto", "usuario", u.Id, "Foto atualizada");
        FotoMudou?.Invoke();
        return anexo;
    }

    public void RemoverFotoUsuario(Ator ator, string login)
    {
        if (!string.Equals(ator.Login, login, StringComparison.OrdinalIgnoreCase)) ator.Exigir(Perm.Administrar);
        var u = Db.Usuarios.Obter(login) ?? throw new ValidacaoException("Usuário não encontrado.");
        if (Db.Anexos.Obter(u.FotoAnexoId) is { } a) Arquivos.Remover(a);
        u.FotoAnexoId = ""; Db.Usuarios.Salvar(u, ator.Login); FotoMudou?.Invoke();
    }

    /// <summary>Foto a mostrar para o usuário: a dele; senão, a da ficha do técnico vinculado.</summary>
    public string FotoDoUsuario(string login)
    {
        var u = Db.Usuarios.Obter(login);
        if (u is null) return "";
        if (!string.IsNullOrEmpty(u.FotoAnexoId)) return u.FotoAnexoId;
        return string.IsNullOrEmpty(u.TecnicoId) ? "" : Db.Tecnicos.Obter(u.TecnicoId)?.FotoAnexoId ?? "";
    }
    public string FotoDoUsuarioPropria(string login) => Db.Usuarios.Obter(login)?.FotoAnexoId ?? "";
    public event Action? FotoMudou;

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

    /// <summary>Só o nome da planta é obrigatório: a cidade pode faltar no cadastro (vem de planilha) e é pedida
    /// quando a planta for selecionada para enviar um técnico.</summary>
    public void ValidarPlanta(Planta p)
    {
        var erros = new List<string>();
        if (string.IsNullOrWhiteSpace(p.Nome)) erros.Add("Informe o nome da planta/unidade.");
        if (!string.IsNullOrEmpty(p.ClienteId) && !Db.Clientes.Existe(p.ClienteId)) erros.Add("Cliente não encontrado.");
        if (!Tempo.FusoValido(p.FusoHorario)) erros.Add("Fuso horário inválido.");
        if ((p.Lat.HasValue ^ p.Lon.HasValue) || !(p.Lat is null || Geo.CoordValida(p.Lat, p.Lon))) erros.Add("Latitude/longitude inválidas (lat −90 a 90; lon −180 a 180).");
        if (Db.Plantas.Onde(x => x.Id != p.Id && x.ClienteId == p.ClienteId && DocEngine.Norm(x.Nome) == DocEngine.Norm(p.Nome)).Count > 0)
            erros.Add("Já existe uma planta com este nome" + (string.IsNullOrEmpty(p.ClienteId) ? "." : " para este cliente."));
        if (erros.Count > 0) throw new ValidacaoException(erros);
    }

    /// <summary>Aplica o resultado da busca em cascata à planta.</summary>
    public static void AplicarGeo(Planta p, GeoResultado r)
    {
        if (r.Ok)
        {
            p.Lat = r.Lat; p.Lon = r.Lon; p.GeoStatus = "ok"; p.GeoNivel = r.Nivel; p.GeoEm = DateTime.UtcNow;
            p.GeoFonte = r.Nivel == "endereco" ? r.Fonte : $"{r.Fonte} · aproximada ao nível de {r.Nivel}";
        }
        else { p.Lat = null; p.Lon = null; p.GeoStatus = "pendente"; p.GeoNivel = ""; p.GeoFonte = r.Mensagem; }
    }

    public static Task<GeoResultado> Localizar(Geocoder geo, Planta p, CancellationToken ct = default) =>
        geo.BuscarHierarquicoAsync(p.Pais, p.Estado, p.Cidade, string.Join(" ", new[] { p.Rua, p.Numero }.Where(x => !string.IsNullOrWhiteSpace(x))),
            string.IsNullOrWhiteSpace(p.Cidade) && string.IsNullOrWhiteSpace(p.Estado) ? p.EnderecoCompleto : null, ct);

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
            p.GeoStatus = "manual"; p.GeoNivel = ""; p.GeoFonte = "Posição informada manualmente"; p.GeoEm = DateTime.UtcNow;
        }
        else if (p.GeoStatus != "manual" && (enderecoMudou || forcarGeocodificar || !p.TemCoord))
        {
            if (enderecoMudou || forcarGeocodificar) { p.Lat = null; p.Lon = null; p.GeoStatus = "pendente"; p.GeoNivel = ""; p.GeoFonte = ""; }
            if (geo is { Configurado: true } && (enderecoMudou || forcarGeocodificar) && TemLocal(p))
                AplicarGeo(p, await Localizar(geo, p));
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
        p.Lat = lat; p.Lon = lon; p.GeoStatus = "manual"; p.GeoNivel = ""; p.GeoFonte = "Posição corrigida manualmente no mapa"; p.GeoEm = DateTime.UtcNow;
        Db.Plantas.Salvar(p, ator.Login);
        Auditar(ator, "planta.posicao", "planta", p.Id, $"{p.Nome}: posição corrigida para {lat:0.00000}, {lon:0.00000}");
        return p;
    }

    public static bool TemLocal(Planta p) => !string.IsNullOrWhiteSpace(p.Pais) || !string.IsNullOrWhiteSpace(p.Estado) || !string.IsNullOrWhiteSpace(p.Cidade) || !string.IsNullOrWhiteSpace(p.EnderecoCompleto);

    /// <summary>Localiza as plantas sem coordenadas, em cascata (endereço → cidade → estado → país), 1 consulta por segundo.
    /// Plantas posicionadas à mão nunca são alteradas. Devolve (localizadas, ainda pendentes).</summary>
    public async Task<(int ok, int falhas)> LocalizarPendentesAsync(Ator ator, Geocoder geo, IProgress<string>? progresso = null, CancellationToken ct = default)
    {
        ator.Exigir(Perm.EditarClientes);
        if (!geo.Configurado) throw new ValidacaoException(geo.Pendencia);
        int ok = 0, falhas = 0;
        var alvo = Db.Plantas.Onde(x => x.Ativo && !x.TemCoord && x.GeoStatus != "manual").ToList();
        var i = 0;
        foreach (var p in alvo)
        {
            ct.ThrowIfCancellationRequested();
            progresso?.Report($"Localizando {++i} de {alvo.Count}: {p.Nome}…");
            if (!TemLocal(p)) { falhas++; continue; }
            AplicarGeo(p, await Localizar(geo, p, ct));
            if (p.TemCoord) ok++; else falhas++;
            Db.Plantas.Salvar(p, ator.Login);
        }
        Auditar(ator, "planta.geocodificar", "planta", "", $"Localização em lote: {ok} localizada(s), {falhas} pendente(s)");
        return (ok, falhas);
    }

    /// <summary>A planta selecionada não tinha cidade: grava o que foi informado direto no cadastro e já localiza em cascata.</summary>
    public async Task<Planta> CompletarLocalAsync(Ator ator, string plantaId, string pais, string estado, string cidade, string endereco, Geocoder? geo)
    {
        ator.Exigir(Perm.EditarClientes);
        var p = Db.Plantas.Obter(plantaId) ?? throw new ValidacaoException("Planta não encontrada.");
        if (string.IsNullOrWhiteSpace(cidade)) throw new ValidacaoException("Informe a cidade da planta.");
        p.Cidade = cidade.Trim();
        if (!string.IsNullOrWhiteSpace(estado)) p.Estado = estado.Trim();
        if (!string.IsNullOrWhiteSpace(pais)) p.Pais = pais.Trim();
        if (!string.IsNullOrWhiteSpace(endereco)) p.Rua = endereco.Trim();
        p.EnderecoCompleto = MontarEndereco(p);
        if (p.GeoStatus != "manual") { p.Lat = null; p.Lon = null; p.GeoStatus = "pendente"; p.GeoNivel = ""; p.GeoFonte = ""; }
        if (p.GeoStatus != "manual" && geo is { Configurado: true }) AplicarGeo(p, await Localizar(geo, p));
        Db.Plantas.Salvar(p, ator.Login);
        Auditar(ator, "planta.completar", "planta", p.Id, $"{p.Nome}: cidade informada ({p.Cidade}/{p.Estado}/{p.Pais}); localização {(p.TemCoord ? "ao nível de " + (p.GeoNivel == "" ? "manual" : p.GeoNivel) : "pendente")}");
        return p;
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

    public async Task<Anexo> AnexarAsync(Ator ator, string donoTipo, string donoId, string nome, Stream conteudo, string categoria = "")
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
        var a = await Arquivos.SalvarAsync(ator, donoTipo, donoId, nome, conteudo, donoTipo == "viagem", donoTipo == "viagem" ? categoria : "");
        Auditar(ator, "anexo.adicionar", donoTipo, donoId, $"Anexo \"{a.NomeOriginal}\" adicionado" + (string.IsNullOrEmpty(a.Categoria) ? "" : $" ({a.Categoria})"));
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
            case "marca":
            case "usuario":
                return true;
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
        if (u.EquipeControle && u.Papel is Roles.Tecnico or Roles.Consulta) erros.Add("A equipe de controle precisa de um perfil que edite viagens (Controladoria, Gestão ou Administrador).");
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

    public void SalvarConfig(Ator ator, string alertas, string fusoPadrao, int confirmacaoHoras, string? contatoGeocodificacao = null)
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
        if (contatoGeocodificacao is not null)
        {
            var c = contatoGeocodificacao.Trim();
            if (c != "" && !c.Contains('@')) throw new ValidacaoException("O contato da geocodificação deve ser um e-mail.");
            Db.SetCfg("geocoding_contato", c, ator.Login);
        }
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
        if (!Db.Demo) throw new ValidacaoException("Só é possível recarregar os dados fictícios em um ambiente de demonstração. Esta base contém dados reais.");
        Seed.LimparTudo(Db, Arquivos);
        Seed.Demo(Db, Arquivos);
        Auditar(ator, "demo.recarregar", "sistema", "", "Dados de demonstração recarregados");
    }

    /// <summary>Remove todos os dados operacionais e a marca de demonstração (preparo para uso real).</summary>
    public void LimparDadosOperacionais(Ator ator)
    {
        ator.Exigir(Perm.Administrar);
        if (!Db.Demo) throw new ValidacaoException("Esta base contém dados reais e não pode ser apagada por esta tela. Use as cópias em _historico ou peça a restauração à TI.");
        Seed.LimparTudo(Db, Arquivos);
        Db.SetCfg(Db.CfgDemo, "0", ator.Login);
        Auditar(ator, "dados.limpar", "sistema", "", "Todos os dados operacionais foram removidos");
    }
}

public sealed partial class Servicos
{
    public ParquetStore.Diagnostico DiagnosticoArmazenamento(Ator ator)
    {
        ator.Exigir(Perm.Administrar);
        return Db.Store.Diagnosticar();
    }

    /// <summary>Compacta agora as entidades com mais de um arquivo (seguro entre máquinas).</summary>
    public int CompactarAgora(Ator ator)
    {
        ator.Exigir(Perm.Administrar);
        var n = 0;
        foreach (var e in Db.Store.Entities().ToList()) if (Db.Store.FileCount(e) > 1) { Db.Store.Compact(e); n++; }
        Auditar(ator, "armazenamento.compactar", "sistema", "", $"{n} entidade(s) compactada(s)");
        return n;
    }
}


public sealed partial class Servicos
{
    public static readonly string[] TiposMarca = { "login", "menu" };
    /// <summary>Avisa as telas abertas (menu lateral) que o logo ou o nome mudou.</summary>
    public event Action? MarcaMudou;

    public Anexo? LogoDe(string tipo) => Db.Cfg("marca_" + tipo) is { Length: > 0 } id ? Db.Anexos.Obter(id) : null;
    public string NomeSistema => Db.Cfg("marca_nome", "Controle Técnico");
    public string SubtituloSistema => Db.Cfg("marca_sub", "Operação de campo");

    public async Task<Anexo> DefinirLogoAsync(Ator ator, string tipo, string nome, Stream conteudo)
    {
        ator.Exigir(Perm.Administrar);
        if (!TiposMarca.Contains(tipo)) throw new ValidacaoException("Tipo de logo inválido.");
        var novo = await Arquivos.SalvarMarcaAsync(ator, tipo, nome, conteudo);
        var antigo = LogoDe(tipo);
        Db.SetCfg("marca_" + tipo, novo.Id, ator.Login);
        if (antigo is not null) Arquivos.Remover(antigo);
        Auditar(ator, "marca.logo", "marca", tipo, $"Logo ({(tipo == "login" ? "página de login" : "menu lateral")}) atualizado: {novo.NomeOriginal}");
        MarcaMudou?.Invoke();
        return novo;
    }

    public void RemoverLogo(Ator ator, string tipo)
    {
        ator.Exigir(Perm.Administrar);
        if (LogoDe(tipo) is { } a) Arquivos.Remover(a);
        Db.SetCfg("marca_" + tipo, "", ator.Login);
        Auditar(ator, "marca.logo", "marca", tipo, $"Logo ({(tipo == "login" ? "página de login" : "menu lateral")}) removido: volta ao padrão");
        MarcaMudou?.Invoke();
    }

    public void SalvarNomeSistema(Ator ator, string nome, string subtitulo)
    {
        ator.Exigir(Perm.Administrar);
        if (string.IsNullOrWhiteSpace(nome) || nome.Length > 40) throw new ValidacaoException("Informe o nome do sistema (até 40 caracteres).");
        if (subtitulo.Length > 60) throw new ValidacaoException("Subtítulo com até 60 caracteres.");
        Db.SetCfg("marca_nome", nome.Trim(), ator.Login); Db.SetCfg("marca_sub", subtitulo.Trim(), ator.Login);
        Auditar(ator, "marca.nome", "marca", "", $"Nome do sistema: {nome.Trim()}");
        MarcaMudou?.Invoke();
    }
}


public sealed partial class Servicos
{
    private static string Limpo(string? t) => string.Join(" ", (t ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Valor digitado à mão numa caixa: devolve o item existente de mesmo nome ou cria um novo. Nunca duplica.</summary>
    public Servico CriarServicoRapido(Ator ator, string nome)
    {
        nome = Limpo(nome); if (nome.Length < 2 || nome.Length > 80) throw new ValidacaoException("Informe o nome do serviço (2 a 80 caracteres).");
        return Db.Servicos.Onde(x => DocEngine.Norm(x.Nome) == DocEngine.Norm(nome)).FirstOrDefault() ?? SalvarServico(ator, new Servico { Nome = nome });
    }

    public Cliente CriarClienteRapido(Ator ator, string nome)
    {
        nome = Limpo(nome); if (nome.Length < 2 || nome.Length > 120) throw new ValidacaoException("Informe o nome do cliente (2 a 120 caracteres).");
        return Db.Clientes.Onde(x => DocEngine.Norm(x.Nome) == DocEngine.Norm(nome)).FirstOrDefault() ?? SalvarCliente(ator, new Cliente { Nome = nome });
    }

    /// <summary>Planta digitada à mão: cria só com o nome; a cidade (e o resto) é pedida em seguida e gravada no cadastro.</summary>
    public Planta CriarPlantaRapida(Ator ator, string nome)
    {
        ator.Exigir(Perm.EditarClientes);
        nome = Limpo(nome); if (nome.Length < 2 || nome.Length > 120) throw new ValidacaoException("Informe o nome da planta (2 a 120 caracteres).");
        var ex = Db.Plantas.Onde(x => DocEngine.Norm(x.Nome) == DocEngine.Norm(nome) && x.ClienteId == "").FirstOrDefault();
        if (ex is not null) return ex;
        var p = new Planta { Nome = nome, Pais = "", GeoStatus = "pendente" };
        ValidarPlanta(p); Db.Plantas.Salvar(p, ator.Login);
        Auditar(ator, "planta.criar", "planta", p.Id, $"{p.Nome} (criada ao digitar numa caixa de seleção)");
        return p;
    }
}


public sealed partial class Servicos
{
    public AutoEtapas.Cfg ApresentacaoAtual() => AutoEtapas.Ler(Db.Cfg("apresentacao"));

    public void SalvarApresentacao(Ator ator, AutoEtapas.Cfg cfg)
    {
        if (!ator.Pode(Perm.Administrar)) ator.Exigir(Perm.EditarTecnicos);
        cfg.Normalizar();
        if (cfg.Etapas.Count == 0) throw new ValidacaoException("Selecione ao menos uma etapa da apresentação.");
        if (cfg.Etapas.Contains("gerencial") && cfg.Graficos.Count == 0) throw new ValidacaoException("Selecione ao menos um gráfico para a etapa de indicadores gerenciais.");
        if (cfg.Etapas.Contains("agenda") && cfg.Vistas.Count == 0) throw new ValidacaoException("Selecione ao menos uma visualização para a etapa da agenda.");
        Db.SetCfg("apresentacao", AutoEtapas.Gravar(cfg), ator.Login);
        Auditar(ator, "config.apresentacao", "config", "", $"Apresentação automática: {string.Join(" › ", cfg.Etapas)}");
    }
}

public sealed partial class Servicos
{
    // ------------------------------------------------ em casa / na Howden
    /// <summary>Marca onde o técnico fica quando não está em viagem: em casa (endereço do cadastro) ou na Howden.</summary>
    public Tecnico DefinirLocalBase(Ator ator, string tecnicoId, string localBase)
    {
        ator.Exigir(Perm.ConfirmarLocal, tecnicoId);
        if (localBase is not (Vocab.BaseCasa or Vocab.BaseHowden)) throw new ValidacaoException("Local inválido.");
        var t = Db.Tecnicos.Obter(tecnicoId) ?? throw new ValidacaoException("Técnico não encontrado.");
        if (t.LocalBase == localBase) return t;
        t.LocalBase = localBase; t.LocalBaseDesde = DateTime.UtcNow; t.LocalBasePor = ator.Login;
        Db.Tecnicos.Salvar(t, ator.Login);
        Auditar(ator, "tecnico.local", "tecnico", t.Id, $"{t.Nome}: {(localBase == Vocab.BaseHowden ? "na " + Db.Cfg("howden_nome", "Howden South America") : "em casa")}");
        return t;
    }

    /// <summary>Localiza a casa do técnico em cascata (endereço → cidade → estado → país) e grava as coordenadas.</summary>
    public async Task<GeoResultado> LocalizarCasaAsync(Ator ator, string tecnicoId, Geocoder geo, bool limparSeFalhar = false)
    {
        ator.Exigir(Perm.EditarTecnicos);
        var t = Db.Tecnicos.Obter(tecnicoId) ?? throw new ValidacaoException("Técnico não encontrado.");
        if (string.IsNullOrWhiteSpace(t.Cidade) && string.IsNullOrWhiteSpace(t.Estado) && string.IsNullOrWhiteSpace(t.Pais))
            return new GeoResultado(false, null, null, "", "Informe ao menos a cidade onde o técnico mora.");
        var r = await geo.BuscarHierarquicoAsync(t.Pais, t.Estado, t.Cidade, t.Endereco);
        if (r.Ok)
        {
            t.CasaLat = r.Lat; t.CasaLon = r.Lon; t.CasaGeoNivel = r.Nivel;
            if (t.OrigemLat is null) { t.OrigemLat = r.Lat; t.OrigemLon = r.Lon; }
            Db.Tecnicos.Salvar(t, ator.Login);
        }
        else if (limparSeFalhar && t.CasaLat is not null) { t.CasaLat = null; t.CasaLon = null; t.CasaGeoNivel = ""; Db.Tecnicos.Salvar(t, ator.Login); }
        return r;
    }

    /// <summary>Posição da casa marcada à mão no mapa (quando o endereço não é encontrado com precisão).</summary>
    public Tecnico DefinirCasaManual(Ator ator, string tecnicoId, double lat, double lon)
    {
        ator.Exigir(Perm.EditarTecnicos);
        if (!Geo.CoordValida(lat, lon)) throw new ValidacaoException("Coordenadas inválidas.");
        var t = Db.Tecnicos.Obter(tecnicoId) ?? throw new ValidacaoException("Técnico não encontrado.");
        t.CasaLat = lat; t.CasaLon = lon; t.CasaGeoNivel = "manual";
        Db.Tecnicos.Salvar(t, ator.Login);
        Auditar(ator, "tecnico.casa", "tecnico", t.Id, $"{t.Nome}: casa posicionada no mapa ({lat:0.#####}, {lon:0.#####})");
        return t;
    }

    public (string nome, string endereco, double? lat, double? lon) Howden()
    {
        var s = Foto(); return (s.HowdenNome, s.HowdenEndereco, s.HowdenLat, s.HowdenLon);
    }

    /// <summary>Endereço da Howden South America (onde ficam os técnicos marcados "na Howden"). Geocodifica se não vierem coordenadas.</summary>
    public async Task<GeoResultado?> SalvarHowdenAsync(Ator ator, string nome, string endereco, string cidade, string estado, string pais, double? lat, double? lon, Geocoder? geo)
    {
        ator.Exigir(Perm.Administrar);
        nome = (nome ?? "").Trim(); endereco = (endereco ?? "").Trim();
        if (nome.Length is < 2 or > 80) throw new ValidacaoException("Informe o nome do local (até 80 caracteres).");
        if ((lat.HasValue ^ lon.HasValue) || !(lat is null || Geo.CoordValida(lat, lon))) throw new ValidacaoException("Coordenadas inválidas.");
        GeoResultado? r = null;
        if (lat is null && geo is { Configurado: true } && (endereco != "" || cidade != "" || estado != "" || pais != ""))
        {
            r = await geo.BuscarHierarquicoAsync(pais, estado, cidade, endereco);
            if (r.Ok) { lat = r.Lat; lon = r.Lon; }
        }
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        var texto = string.Join(", ", new[] { endereco, cidade, estado, pais }.Where(x => !string.IsNullOrWhiteSpace(x)));
        Db.SetCfg("howden_nome", nome, ator.Login); Db.SetCfg("howden_endereco", texto, ator.Login);
        Db.SetCfg("howden_end_rua", endereco, ator.Login); Db.SetCfg("howden_end_cidade", cidade ?? "", ator.Login);
        Db.SetCfg("howden_end_estado", estado ?? "", ator.Login); Db.SetCfg("howden_end_pais", pais ?? "", ator.Login);
        Db.SetCfg("howden_lat", lat?.ToString(ci) ?? "", ator.Login); Db.SetCfg("howden_lon", lon?.ToString(ci) ?? "", ator.Login);
        Auditar(ator, "config.howden", "config", "", $"{nome}: {texto}" + (lat is null ? " (sem coordenadas)" : $" ({lat:0.####}, {lon:0.####})"));
        return r;
    }
}
