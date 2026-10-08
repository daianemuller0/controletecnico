using Microsoft.Extensions.Configuration;
using ControleTecnico.Data;
using ControleTecnico.Logic;
using ControleTecnico.Models;
using Xunit;

namespace ControleTecnico.Tests;

/// <summary>Cada teste usa uma pasta de dados própria (Parquet de verdade, em disco).</summary>
public sealed class Amb : IDisposable
{
    public string Dir = Path.Combine(Path.GetTempPath(), "ct_test_" + Guid.NewGuid().ToString("N"));
    public Db Db; public Armazenamento Arq; public Servicos Svc;
    public Ator Admin = new() { Login = "admin", Nome = "Admin", Papel = Roles.Admin };
    public Ator Gestao = new() { Login = "g", Nome = "Gestor", Papel = Roles.Gestao };
    public Ator Contr = new() { Login = "c", Nome = "Controladoria", Papel = Roles.Controladoria };
    public Ator Consulta = new() { Login = "q", Nome = "Consulta", Papel = Roles.Consulta };
    public Amb(bool demo = false)
    {
        Db = new Db(new ParquetStore(Dir)); Arq = new Armazenamento(Db); Svc = new Servicos(Db, Arq);
        if (demo) Seed.Demo(Db, Arq);
    }
    public void Dispose() { try { Directory.Delete(Dir, true); } catch { } }
    public static DateTime Fut(int dias, double h = 8) => DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(dias).AddHours(h), DateTimeKind.Utc);

    public Tecnico NovoTec(string nome = "Teste Um")
    {
        var t = Svc.SalvarTecnico(Admin, new Tecnico { Nome = nome, Cidade = "Campinas", Estado = "SP" });
        return t;
    }
    public Planta NovaPlanta(string nome = "Planta A", double lat = -23, double lon = -47)
    {
        var c = Svc.SalvarCliente(Admin, new Cliente { Nome = "Cli " + Guid.NewGuid().ToString("N")[..4] });
        return Svc.SalvarPlantaAsync(Admin, new Planta { ClienteId = c.Id, Nome = nome, Cidade = "X", Lat = lat, Lon = lon, GeoStatus = "manual" }, null).GetAwaiter().GetResult();
    }
}

public class AgendaTests
{
    [Fact]
    public void Viagem_confirmada_com_conflito_e_bloqueada_e_cancelamento_libera()
    {
        using var a = new Amb(); var t = a.NovoTec(); var p = a.NovaPlanta();
        var v1 = new Viagem { PlantaId = p.Id, ClienteId = p.ClienteId, TecnicoIds = t.Id, Status = Vocab.ViagemConfirmada };
        var at1 = new Atendimento { PlantaId = p.Id, IniPrev = Amb.Fut(3), FimPrev = Amb.Fut(5, 17), TecnicoIds = t.Id };
        var salva = a.Svc.SalvarViagem(a.Contr, v1, new(), new() { at1 });
        // segundo atendimento sobreposto, avulso e planejado => conflito de erro
        var prev = a.Svc.PreviaAtendimento(new Atendimento { PlantaId = p.Id, TecnicoIds = t.Id, IniPrev = Amb.Fut(4), FimPrev = Amb.Fut(4, 17), Status = Vocab.ViagemPlanejada });
        Assert.Contains(prev.conflitos, c => c.Erro && c.Tipo == "sobreposicao");
        // tentar confirmar viagem em conflito falha
        var v2 = new Viagem { PlantaId = p.Id, ClienteId = p.ClienteId, TecnicoIds = t.Id, Status = Vocab.ViagemConfirmada };
        var ex = Assert.Throws<ValidacaoException>(() => a.Svc.SalvarViagem(a.Contr, v2, new(), new() { new Atendimento { PlantaId = p.Id, IniPrev = Amb.Fut(4), FimPrev = Amb.Fut(4, 17), TecnicoIds = t.Id } }));
        Assert.Contains("confirmar", ex.Message);
        // cancelar a primeira libera a disponibilidade
        a.Svc.AlterarStatusViagem(a.Contr, salva.Id, Vocab.ViagemCancelada, "teste");
        var snap = a.Svc.Foto(); var ev = AgendaEngine.Eventos(snap);
        Assert.DoesNotContain(ev, e => e.ViagemId == salva.Id);
        var dias = AgendaEngine.Dias(snap, ev, t, Tempo.ParaLocal(Amb.Fut(3), snap.FusoPadrao).Date, Tempo.ParaLocal(Amb.Fut(3), snap.FusoPadrao).Date);
        Assert.NotEqual("ocupado", dias[0].Situacao);
    }

    [Fact]
    public void Rascunho_e_provisorio_e_nao_bloqueia()
    {
        using var a = new Amb(); var t = a.NovoTec(); var p = a.NovaPlanta();
        a.Svc.SalvarViagem(a.Contr, new Viagem { PlantaId = p.Id, ClienteId = p.ClienteId, TecnicoIds = t.Id, Status = Vocab.ViagemRascunho }, new(),
            new() { new Atendimento { PlantaId = p.Id, IniPrev = Amb.Fut(2), FimPrev = Amb.Fut(2, 17), TecnicoIds = t.Id } });
        var snap = a.Svc.Foto(); var ev = AgendaEngine.Eventos(snap);
        Assert.All(ev, e => Assert.True(e.Provisorio));
        var est = AgendaEngine.EstadoEm(snap, ev, snap.Tecnicos[t.Id], Amb.Fut(2, 10), DateTime.UtcNow);
        Assert.NotEqual(Vocab.OpAtendimento, est.Status);
        Assert.NotNull(est.Provisorio);
    }

    [Fact]
    public void Tecnico_sem_agenda_e_sem_programacao_nao_disponivel()
    {
        using var a = new Amb(); var t = a.NovoTec();
        var snap = a.Svc.Foto(); var ev = AgendaEngine.Eventos(snap);
        var est = AgendaEngine.EstadoEm(snap, ev, snap.Tecnicos[t.Id], DateTime.UtcNow, DateTime.UtcNow);
        Assert.Equal(Vocab.OpSemProg, est.Status);
        Assert.Equal("nenhum", est.LocalFonte);
    }

    [Fact]
    public void Ferias_geram_status_e_conflito_com_atendimento()
    {
        using var a = new Amb(); var t = a.NovoTec(); var p = a.NovaPlanta();
        a.Svc.SalvarIndisponibilidade(a.Gestao, new Indisponibilidade { TecnicoId = t.Id, Tipo = Vocab.IndFerias, Ini = Amb.Fut(-1, 0), Fim = Amb.Fut(10, 0) });
        var snap = a.Svc.Foto(); var ev = AgendaEngine.Eventos(snap);
        Assert.Equal(Vocab.OpFerias, AgendaEngine.EstadoEm(snap, ev, snap.Tecnicos[t.Id], DateTime.UtcNow, DateTime.UtcNow).Status);
        var prev = a.Svc.PreviaAtendimento(new Atendimento { PlantaId = p.Id, TecnicoIds = t.Id, IniPrev = Amb.Fut(2), FimPrev = Amb.Fut(3), Status = Vocab.ViagemConfirmada });
        Assert.Contains(prev.conflitos, c => c.Erro);
    }

    [Fact]
    public void Troca_de_localidade_sem_trecho_sinaliza_verificacao()
    {
        using var a = new Amb(); var t = a.NovoTec(); var p1 = a.NovaPlanta("A", -23, -47); var p2 = a.NovaPlanta("B", -3, -38);
        a.Svc.SalvarAtendimentoAvulso(a.Gestao, new Atendimento { PlantaId = p1.Id, TecnicoIds = t.Id, IniPrev = Amb.Fut(2), FimPrev = Amb.Fut(2, 12), Status = Vocab.ViagemPlanejada });
        var prev = a.Svc.PreviaAtendimento(new Atendimento { PlantaId = p2.Id, TecnicoIds = t.Id, IniPrev = Amb.Fut(2, 14), FimPrev = Amb.Fut(2, 18), Status = Vocab.ViagemPlanejada });
        var c = Assert.Single(prev.conflitos);
        Assert.Equal("deslocamento", c.Tipo); Assert.Contains("verifique a viabilidade", c.Motivo);
    }

    [Fact]
    public void Viagem_internacional_preserva_instante_e_mostra_fuso()
    {
        using var a = new Amb(); var t = a.NovoTec();
        // 08:00 em Lisboa != 08:00 em São Paulo (offsets diferentes)
        var sp = Tempo.ParaUtc(new DateTime(2026, 10, 20, 8, 0, 0), "America/Sao_Paulo");
        var scl = Tempo.ParaUtc(new DateTime(2026, 10, 20, 8, 0, 0), "Europe/Lisbon");
        Assert.NotEqual(sp, scl);
        Assert.Contains("UTC", Tempo.Hora(scl, "Europe/Lisbon", "America/Sao_Paulo"));
        Assert.Equal(new DateTime(2026, 10, 20, 8, 0, 0), Tempo.ParaLocal(scl, "Europe/Lisbon"));
    }

    [Fact]
    public void Reagendar_valida_antes_de_salvar()
    {
        using var a = new Amb(); var t = a.NovoTec(); var p = a.NovaPlanta();
        var at = a.Svc.SalvarAtendimentoAvulso(a.Gestao, new Atendimento { PlantaId = p.Id, TecnicoIds = t.Id, IniPrev = Amb.Fut(2), FimPrev = Amb.Fut(2, 17), Status = Vocab.ViagemPlanejada });
        a.Svc.SalvarIndisponibilidade(a.Gestao, new Indisponibilidade { TecnicoId = t.Id, Tipo = Vocab.IndFolga, Ini = Amb.Fut(5, 0), Fim = Amb.Fut(6, 0) });
        var sim = a.Svc.SimularReagendar(a.Gestao, "atend", at.Id, TimeSpan.FromDays(3), false);
        Assert.True(sim.Bloqueado);
        Assert.Throws<ValidacaoException>(() => a.Svc.Reagendar(a.Gestao, "atend", at.Id, TimeSpan.FromDays(3), false, true));
        Assert.Equal(Amb.Fut(2), a.Db.Atendimentos.Obter(at.Id)!.IniPrev);   // nada foi salvo
        a.Svc.Reagendar(a.Gestao, "atend", at.Id, TimeSpan.FromDays(1), false, false);
        Assert.Equal(Amb.Fut(3), a.Db.Atendimentos.Obter(at.Id)!.IniPrev);
    }
}

public class DocumentoTests
{
    [Fact]
    public void Status_valido_proximo_vencido_semvenc()
    {
        var hoje = new DateTime(2026, 10, 8, 0, 0, 0, DateTimeKind.Utc); var al = new[] { 30, 60, 90 };
        Documento D(int? dias) => new() { Vencimento = dias is null ? null : hoje.AddDays(dias.Value), SemVencimento = dias is null };
        Assert.Equal(Vocab.DocValido, DocEngine.Status(D(200), hoje, al));
        Assert.Equal(Vocab.DocProximo, DocEngine.Status(D(45), hoje, al));
        Assert.Equal(Vocab.DocProximo, DocEngine.Status(D(0), hoje, al));
        Assert.Equal(Vocab.DocVencido, DocEngine.Status(D(-1), hoje, al));
        Assert.Equal(Vocab.DocSemVenc, DocEngine.Status(D(null), hoje, al));
        Assert.Equal(30, DocEngine.NivelAlerta(D(10), hoje, al)); Assert.Equal(60, DocEngine.NivelAlerta(D(45), hoje, al)); Assert.Null(DocEngine.NivelAlerta(D(200), hoje, al));
    }

    [Fact]
    public void Certificado_que_vence_durante_o_atendimento_bloqueia_confirmacao()
    {
        using var a = new Amb(); var t = a.NovoTec(); var p = a.NovaPlanta();
        var s = a.Svc.SalvarServico(a.Gestao, new Servico { Nome = "Prev" });
        a.Svc.SalvarRequisito(a.Gestao, new Requisito { Escopo = "servico", EscopoId = s.Id, Nome = "NR-10", Norma = "NR-10", Bloqueia = true });
        a.Svc.SalvarDocumento(a.Gestao, new Documento { TecnicoId = t.Id, Nome = "NR-10", Categoria = "Treinamento", Norma = "NR-10", Vencimento = Amb.Fut(4, 0) });
        Viagem V(string st) => new() { PlantaId = p.Id, ClienteId = p.ClienteId, ServicoId = s.Id, TecnicoIds = t.Id, Status = st };
        Atendimento At() => new() { PlantaId = p.Id, IniPrev = Amb.Fut(3), FimPrev = Amb.Fut(7, 17), TecnicoIds = t.Id };
        var ex = Assert.Throws<ValidacaoException>(() => a.Svc.SalvarViagem(a.Contr, V(Vocab.ViagemConfirmada), new(), new() { At() }));
        Assert.Contains("durante o atendimento", ex.Message);
        // como rascunho/planejada salva, mostrando a pendência
        var res = a.Svc.Validar(V(Vocab.ViagemPlanejada), new(), new() { At() });
        Assert.Contains(res.Aptidoes.SelectMany(x => x.Pendencias), pd => pd.Tipo == "vence_durante" && pd.Bloqueia);
        // renovar mantém histórico e libera a confirmação
        var atual = a.Db.Documentos.Onde(d => d.TecnicoId == t.Id).Single();
        var nova = a.Svc.Renovar(a.Gestao, atual.Id, new Documento { Nome = "NR-10", Categoria = "Treinamento", Norma = "NR-10", Vencimento = Amb.Fut(400, 0) });
        Assert.Equal(2, nova.Versao);
        Assert.Equal(2, a.Db.Documentos.Onde(d => d.RaizId == atual.RaizId).Count);
        Assert.False(a.Db.Documentos.Obter(atual.Id)!.Atual);
        a.Svc.SalvarViagem(a.Contr, V(Vocab.ViagemConfirmada), new(), new() { At() });
    }

    [Fact]
    public void Documento_vencido_e_ausente_e_requisito_apenas_alerta_nao_bloqueia()
    {
        using var a = new Amb(); var t = a.NovoTec(); var p = a.NovaPlanta();
        var s = a.Svc.SalvarServico(a.Gestao, new Servico { Nome = "Prev" });
        a.Svc.SalvarRequisito(a.Gestao, new Requisito { Escopo = "planta", EscopoId = p.Id, Nome = "ASO", Bloqueia = false });
        a.Svc.SalvarRequisito(a.Gestao, new Requisito { Escopo = "servico", EscopoId = s.Id, Nome = "NR-35", Bloqueia = true });
        a.Svc.SalvarDocumento(a.Gestao, new Documento { TecnicoId = t.Id, Nome = "NR-35", Categoria = "Treinamento", Vencimento = Amb.Fut(-3, 0) });
        var snap = a.Svc.Foto();
        var reqs = DocEngine.RequisitosDe(snap, new[] { s.Id }, new[] { p.Id });
        var ap = DocEngine.Avaliar(snap, snap.Tecnicos[t.Id], reqs, Amb.Fut(2), Amb.Fut(3), DocEngine.Hoje(snap.FusoPadrao));
        Assert.Contains(ap.Pendencias, x => x.Tipo == "vencido" && x.Bloqueia);
        Assert.Contains(ap.Pendencias, x => x.Tipo == "ausente" && !x.Bloqueia);
        Assert.True(ap.Bloqueado);
    }

    [Fact]
    public void Validacoes_de_anexo_formato_tamanho_e_conteudo()
    {
        Assert.Throws<ValidacaoException>(() => Armazenamento.Validar("x.exe", 10, new byte[] { 1, 2, 3, 4 }));
        Assert.Throws<ValidacaoException>(() => Armazenamento.Validar("x.pdf", 20L * 1024 * 1024, "%PDF"u8));
        Assert.Throws<ValidacaoException>(() => Armazenamento.Validar("x.pdf", 10, "MZ.."u8));   // extensão mente
        Armazenamento.Validar("x.pdf", 10, "%PDF-1"u8);
        Armazenamento.Validar("x.png", 10, new byte[] { 0x89, 0x50, 0x4E, 0x47 });
    }

    [Fact]
    public async Task Anexo_persiste_e_acesso_respeita_perfil()
    {
        using var a = new Amb(); var t = a.NovoTec();
        var d = a.Svc.SalvarDocumento(a.Gestao, new Documento { TecnicoId = t.Id, Nome = "ASO", Categoria = "Exame / saúde", SemVencimento = true });
        using var ms = new MemoryStream(Seed.PdfSimples("x", "y"));
        var an = await a.Svc.AnexarAsync(a.Gestao, "documento", d.Id, "aso.pdf", ms);
        Assert.True(a.Svc.PodeAcessar(a.Gestao, an)); Assert.True(a.Svc.PodeAcessar(a.Admin, an));
        Assert.False(a.Svc.PodeAcessar(a.Contr, an)); Assert.False(a.Svc.PodeAcessar(a.Consulta, an));
        Assert.False(a.Svc.PodeAcessar(new Ator { Papel = Roles.Tecnico, TecnicoId = "outro" }, an));
        Assert.True(a.Svc.PodeAcessar(new Ator { Papel = Roles.Tecnico, TecnicoId = t.Id }, an));
        using var s = a.Arq.Abrir(an)!; Assert.True(s.Length > 100);
        // "reabrir a aplicação": novo Db sobre a mesma pasta
        var db2 = new Db(new ParquetStore(a.Dir));
        Assert.Single(db2.Anexos.Todos()); Assert.Equal("ASO", db2.Documentos.Obter(d.Id)!.Nome);
    }
}

public class PermissaoTests
{
    [Fact]
    public void Papeis_sao_aplicados_no_servidor()
    {
        using var a = new Amb(); var t = a.NovoTec(); var p = a.NovaPlanta();
        Assert.Throws<UnauthorizedAccessException>(() => a.Svc.SalvarCliente(a.Consulta, new Cliente { Nome = "X" }));
        Assert.Throws<UnauthorizedAccessException>(() => a.Svc.SalvarTecnico(a.Contr, new Tecnico { Nome = "Y", Cidade = "Z" }));
        Assert.Throws<UnauthorizedAccessException>(() => a.Svc.SalvarViagem(a.Consulta, new Viagem(), new(), new()));
        Assert.Throws<UnauthorizedAccessException>(() => a.Svc.SalvarDocumento(a.Contr, new Documento { TecnicoId = t.Id, Nome = "n", SemVencimento = true }));
        Assert.Throws<UnauthorizedAccessException>(() => a.Svc.SalvarRequisito(a.Contr, new Requisito()));
        Assert.Throws<UnauthorizedAccessException>(() => a.Svc.SalvarConfig(a.Gestao, "30", "UTC", 24));
        var tecnico = new Ator { Login = "t", Papel = Roles.Tecnico, TecnicoId = t.Id };
        Assert.Throws<UnauthorizedAccessException>(() => a.Svc.SalvarViagem(tecnico, new Viagem(), new(), new()));
        a.Svc.SalvarDocumento(tecnico, new Documento { TecnicoId = t.Id, Nome = "Meu doc", Categoria = "Outro", SemVencimento = true });   // o próprio: permitido
        var outro = a.NovoTec("Outro");
        Assert.Throws<UnauthorizedAccessException>(() => a.Svc.SalvarDocumento(tecnico, new Documento { TecnicoId = outro.Id, Nome = "x", SemVencimento = true }));
        Assert.False(tecnico.VeTecnico(outro.Id)); Assert.False(a.Consulta.Pode(Perm.VerReservas)); Assert.True(a.Contr.Pode(Perm.VerReservas));
    }

    [Fact]
    public void Auditoria_registra_quem_e_quando()
    {
        using var a = new Amb(); var t = a.NovoTec(); var p = a.NovaPlanta();
        var v = a.Svc.SalvarViagem(a.Contr, new Viagem { PlantaId = p.Id, ClienteId = p.ClienteId, TecnicoIds = t.Id, Status = Vocab.ViagemRascunho }, new(), new());
        a.Svc.SalvarIndisponibilidade(a.Gestao, new Indisponibilidade { TecnicoId = t.Id, Tipo = Vocab.IndFolga, Ini = Amb.Fut(1, 0), Fim = Amb.Fut(2, 0) });
        a.Svc.SalvarDocumento(a.Gestao, new Documento { TecnicoId = t.Id, Nome = "d", Categoria = "Outro", SemVencimento = true });
        var log = a.Db.Auditorias.Todos();
        Assert.Contains(log, x => x.Acao == "viagem.criar" && x.Usuario == "c");
        Assert.Contains(log, x => x.Acao == "indisponibilidade.criar" && x.Usuario == "g");
        Assert.Contains(log, x => x.Acao == "documento.criar");
        Assert.All(log, x => Assert.NotEqual(default, x.Quando));
    }
}

public class ImportacaoTests
{
    private static ArquivoLido Csv(string texto) => Importador.Ler("t.csv", new MemoryStream(System.Text.Encoding.UTF8.GetBytes(texto)));

    [Fact]
    public void Importacao_invalida_explica_cada_linha_rejeitada()
    {
        using var a = new Amb(); var imp = new Importador(a.Db, a.Svc);
        var arq = Csv("Cliente;Planta;Rua;Numero;Cidade;Estado;Pais;Latitude;Longitude\n" +
                      "Alfa;P1;Rua A;10;Campinas;SP;Brasil;;\n" +
                      ";P2;Rua B;11;Campinas;SP;Brasil;;\n" +           // sem cliente
                      "Alfa;P3;Rua C;12;;SP;Brasil;;\n" +               // sem cidade
                      "Beta;P4;Rua D;13;Lima;;Peru;abc;-77\n" +          // coordenada inválida
                      "Beta;P5;Rua E;1;Lima;;Peru;-12;-77\n" +
                      "Alfa;P1;Rua A;10;Campinas;SP;Brasil;;\n");        // duplicada no arquivo
        var map = Importador.SugerirMapeamento(arq.Cabecalhos);
        var linhas = imp.Analisar(arq, map);
        Assert.Equal(6, linhas.Count);
        Assert.Equal(new[] { false, false, false, true, false, true }, linhas.Select(l => l.Rejeitada).ToArray());   // coordenada inválida e duplicada
        Assert.Contains(linhas[2].Avisos, w => w.Contains("Sem cidade"));                                          // cidade ausente NÃO rejeita
        Assert.Contains(linhas[5].Erros, e => e.Contains("Duplicada"));
        var res = imp.Aplicar(a.Admin, linhas);
        Assert.Equal(4, res.Importadas); Assert.Equal(2, res.Rejeitadas.Count); Assert.Equal(2, res.ClientesCriados);
        Assert.All(res.Rejeitadas, r => Assert.False(string.IsNullOrWhiteSpace(r.motivo)));
        var p5 = a.Db.Plantas.Onde(p => p.Nome == "P5").Single(); Assert.True(p5.TemCoord);
        var p1 = a.Db.Plantas.Onde(p => p.Nome == "P1").Single();
        Assert.False(p1.TemCoord); Assert.Equal("pendente", p1.GeoStatus);       // sem coordenadas inventadas
        Assert.Contains("Brasil", p1.EnderecoCompleto);
    }

    [Fact]
    public void Existente_mostra_diferencas_e_so_atualiza_quando_escolhido()
    {
        using var a = new Amb(); var imp = new Importador(a.Db, a.Svc);
        imp.Aplicar(a.Admin, imp.Analisar(Csv("Cliente;Planta;Cidade;Pais\nAlfa;P1;Campinas;Brasil\n"), Importador.SugerirMapeamento(new() { "Cliente", "Planta", "Cidade", "Pais" })));
        var arq = Csv("Cliente;Planta;Cidade;Pais\nAlfa;P1;Sorocaba;Brasil\n");
        var linhas = imp.Analisar(arq, Importador.SugerirMapeamento(arq.Cabecalhos));
        Assert.True(linhas[0].Existente); Assert.Contains(linhas[0].Diferencas, d => d.Contains("Campinas") && d.Contains("Sorocaba"));
        linhas[0].Acao = AcaoImport.Ignorar;
        var r1 = imp.Aplicar(a.Admin, linhas); Assert.Equal(1, r1.Ignoradas);
        Assert.Equal("Campinas", a.Db.Plantas.Todos().Single().Cidade);
        linhas[0].Acao = AcaoImport.Atualizar; var r2 = imp.Aplicar(a.Admin, linhas); Assert.Equal(1, r2.Atualizadas);
        Assert.Equal("Sorocaba", a.Db.Plantas.Todos().Single().Cidade);
        Assert.Single(a.Db.Plantas.Todos());
    }

    [Fact]
    public void Planilha_modelo_xlsx_e_lida_de_volta_e_endereco_internacional_aceito()
    {
        using var ms = new MemoryStream(Importador.Modelo());
        var arq = Importador.Ler("modelo.xlsx", ms);
        Assert.True(arq.Linhas.Count >= 2);
        var map = Importador.SugerirMapeamento(arq.Cabecalhos);
        Assert.True(map.ContainsKey("planta") && map.ContainsKey("pais") && map.ContainsKey("cidade") && map.ContainsKey("estado") && map.ContainsKey("rua"));
        Assert.Equal(new[] { "Planta", "Country", "City", "State", "Address 1" }, arq.Cabecalhos.ToArray());
        using var a = new Amb(); var imp = new Importador(a.Db, a.Svc);
        var l = imp.Analisar(arq, map);
        var mex = l.Single(x => x.V["pais"] == "México"); Assert.Equal("Monterrey", mex.V["cidade"]); Assert.False(mex.Rejeitada); Assert.Equal("America/Mexico_City", mex.V["fuso"]);
    }

    [Fact]
    public void Arquivo_invalido_ou_vazio_e_recusado_com_mensagem()
    {
        Assert.Throws<ValidacaoException>(() => Importador.Ler("x.xlsx", new MemoryStream(new byte[] { 1, 2, 3 })));
        Assert.Throws<ValidacaoException>(() => Importador.Ler("x.csv", new MemoryStream()));
        Assert.Throws<ValidacaoException>(() => Importador.Ler("x.docx", new MemoryStream(new byte[] { 1 })));
        Assert.Throws<ValidacaoException>(() => Csv("Cliente;Planta\n"));
    }

    [Fact]
    public async Task Endereco_sem_coordenadas_salva_como_localizacao_pendente()
    {
        using var a = new Amb(); var c = a.Svc.SalvarCliente(a.Admin, new Cliente { Nome = "C" });
        var p = await a.Svc.SalvarPlantaAsync(a.Admin, new Planta { ClienteId = c.Id, Nome = "P", Cidade = "Lugar Nenhum", Pais = "Brasil" }, null);
        Assert.Equal("pendente", p.GeoStatus); Assert.Null(p.Lat);
        var p2 = a.Svc.CorrigirPosicao(a.Admin, p.Id, -10.5, -40.5);
        Assert.Equal("manual", p2.GeoStatus); Assert.Equal(-10.5, a.Db.Plantas.Obter(p.Id)!.Lat);
        Assert.Throws<ValidacaoException>(() => a.Svc.CorrigirPosicao(a.Admin, p.Id, 95, 0));
    }
}

public class DemoTests
{
    [Fact]
    public void Demo_cobre_todos_os_estados_e_cenarios()
    {
        using var a = new Amb(true);
        var snap = a.Svc.Foto(); var ev = AgendaEngine.Eventos(snap); var agora = DateTime.UtcNow;
        var estados = snap.Tecnicos.Values.Select(t => AgendaEngine.EstadoEm(snap, ev, t, agora, agora).Status).ToHashSet();
        Assert.Contains(Vocab.OpDisponivel, estados); Assert.Contains(Vocab.OpAtendimento, estados); Assert.Contains(Vocab.OpViagem, estados);
        Assert.Contains(Vocab.OpFerias, estados); Assert.Contains(Vocab.OpSemProg, estados); Assert.Contains(Vocab.OpIndisponivel, estados);
        Assert.Contains(snap.Trechos, t => t.Modal == Vocab.ModalAviao); Assert.Contains(snap.Trechos, t => t.Modal == Vocab.ModalCarro);
        Assert.True(snap.Plantas.Values.GroupBy(p => p.ClienteId).Any(g => g.Count() > 1));
        var hoje = DocEngine.Hoje(snap.FusoPadrao);
        var st = DocEngine.Vencimentos(snap, hoje).Select(l => l.Status).ToHashSet();
        Assert.Contains(Vocab.DocValido, st); Assert.Contains(Vocab.DocProximo, st); Assert.Contains(Vocab.DocVencido, st);
        Assert.True(a.Db.Demo);
        Assert.Contains(AgendaEngine.Conflitos(snap, ev), c => c.Erro);                                   // conflito da demo
        Assert.Equal(2, a.Db.Documentos.Onde(d => d.RaizId == "d_c2").Count);                            // histórico de renovação
    }

    [Fact]
    public void Reabrir_a_aplicacao_encontra_dados_e_anexos()
    {
        using var a = new Amb(true);
        var db2 = new Db(new ParquetStore(a.Dir));
        Assert.Equal(a.Db.Tecnicos.Contar(), db2.Tecnicos.Contar()); Assert.Equal(a.Db.Anexos.Contar(), db2.Anexos.Contar());
        Assert.True(db2.Anexos.Contar() > 0);
        var arq2 = new Armazenamento(db2);
        Assert.All(db2.Anexos.Todos(), an => { using var s = arq2.Abrir(an); Assert.NotNull(s); });
    }

    [Fact]
    public void Visao_apta_e_livre_separa_agenda_de_documentos()
    {
        using var a = new Amb(true);
        var snap = a.Svc.Foto(); var ev = AgendaEngine.Eventos(snap);
        var pec = snap.Plantas["pl_pec"]; var serv = snap.Servicos["srv_prev"];
        var reqs = DocEngine.RequisitosDe(snap, new[] { serv.Id }, new[] { pec.Id });
        var marcos = snap.Tecnicos["tec_marcos"]; var hoje = DocEngine.Hoje(snap.FusoPadrao);
        var ap = DocEngine.Avaliar(snap, marcos, reqs, Amb.Fut(15), Amb.Fut(18, 17), hoje);
        Assert.False(ap.Apto);                                              // NR-33 ausente + NR-35 vence durante
        var dias = AgendaEngine.Dias(snap, ev, marcos, Tempo.ParaLocal(Amb.Fut(15), snap.FusoPadrao).Date, Tempo.ParaLocal(Amb.Fut(15), snap.FusoPadrao).Date);
        Assert.NotEqual("indisponivel", dias[0].Situacao);                  // agenda livre/provisória, mas sem aptidão documental
    }
}

public class BaseCompartilhadaTests
{
    private static string Nova() => Path.Combine(Path.GetTempPath(), "ct_share_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Duas_instancias_na_mesma_pasta_enxergam_as_gravacoes_uma_da_outra()
    {
        var share = Nova(); Repo<Tecnico>.SegundosEntreChecagens = 0;
        try
        {
            // dois "computadores": mesma pasta compartilhada, espelhos locais distintos
            var dbA = new Db(new ParquetStore(share, Nova(), forcarEspelho: true));
            var dbB = new Db(new ParquetStore(share, Nova(), forcarEspelho: true));
            Assert.Empty(dbB.Tecnicos.Todos());
            dbA.Tecnicos.Salvar(new Tecnico { Id = "t1", Nome = "Ana", Cidade = "X" });
            Assert.Equal("Ana", dbB.Tecnicos.Obter("t1")!.Nome);           // B vê o que A gravou
            var t = dbB.Tecnicos.Obter("t1")!; t.Nome = "Ana Maria"; dbB.Tecnicos.Salvar(t);
            Assert.Equal("Ana Maria", dbA.Tecnicos.Obter("t1")!.Nome);     // e vice-versa
            dbA.Tecnicos.Apagar("t1");
            Assert.Null(dbB.Tecnicos.Obter("t1"));
            // reabrir em uma terceira máquina
            Assert.Empty(new Db(new ParquetStore(share, Nova(), true)).Tecnicos.Todos());
        }
        finally { Repo<Tecnico>.SegundosEntreChecagens = 3; try { Directory.Delete(share, true); } catch { } }
    }

    [Fact]
    public void Compactacao_preserva_dados_move_antigos_para_historico_e_nao_deixa_tmp()
    {
        var share = Nova(); Repo<Tecnico>.SegundosEntreChecagens = 0;
        try
        {
            var store = new ParquetStore(share, Nova(), true); var db = new Db(store);
            for (var i = 0; i < 6; i++) db.Tecnicos.Salvar(new Tecnico { Id = "t" + (i % 3), Nome = "v" + i, Cidade = "X" });
            db.Tecnicos.Apagar("t2");
            Assert.True(store.FileCount("tecnicos") >= 7);
            store.Compact("tecnicos");
            Assert.Equal(1, store.FileCount("tecnicos"));
            Assert.Equal(2, db.Tecnicos.Todos().Count);
            Assert.Equal("v3", db.Tecnicos.Obter("t0")!.Nome);
            Assert.True(Directory.GetFiles(Path.Combine(share, "_historico", "tecnicos"), "*.parquet", SearchOption.AllDirectories).Length >= 7);
            Assert.Empty(Directory.GetFiles(Path.Combine(share, "tecnicos"), "*.tmp"));
            Assert.Empty(Directory.GetFiles(Path.Combine(share, "_locks")));        // trava liberada
            var outra = new Db(new ParquetStore(share, Nova(), true));
            Assert.Equal(2, outra.Tecnicos.Todos().Count);
        }
        finally { Repo<Tecnico>.SegundosEntreChecagens = 3; try { Directory.Delete(share, true); } catch { } }
    }

    [Fact]
    public void Trava_de_compactacao_impede_duas_maquinas_ao_mesmo_tempo()
    {
        var share = Nova();
        try
        {
            var store = new ParquetStore(share); var db = new Db(store);
            db.Tecnicos.Salvar(new Tecnico { Id = "a", Nome = "a", Cidade = "X" }); db.Tecnicos.Salvar(new Tecnico { Id = "b", Nome = "b", Cidade = "X" });
            Directory.CreateDirectory(Path.Combine(share, "_locks"));
            using var fs = new FileStream(Path.Combine(share, "_locks", "compactar_tecnicos.lock"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            Assert.Equal(2, store.Compact("tecnicos"));                                // outra máquina compactando: nada acontece
            Assert.Equal(2, store.FileCount("tecnicos"));
        }
        finally { try { Directory.Delete(share, true); } catch { } }
    }

    [Fact]
    public void Diagnostico_testa_leitura_e_escrita_e_pasta_inacessivel_falha_com_mensagem()
    {
        var share = Nova();
        try
        {
            var d = new ParquetStore(share).Diagnosticar(); Assert.True(d.Ok && d.Escrita && d.Leitura);
            Assert.Empty(Directory.GetFiles(share, "_sonda_*"));
            // caminho impossível (arquivo no lugar da pasta) -> erro claro, sem criar outro lugar
            var arq = Path.Combine(share, "arquivo.txt"); File.WriteAllText(arq, "x");
            var ex = Assert.Throws<InvalidOperationException>(() => new ParquetStore(Path.Combine(arq, "sub")));
            Assert.Contains("Não foi possível acessar a pasta de dados", ex.Message);
        }
        finally { try { Directory.Delete(share, true); } catch { } }
    }

    [Fact]
    public async Task Anexos_ficam_na_pasta_compartilhada_e_remocao_vai_para_historico()
    {
        var share = Nova();
        try
        {
            var db = new Db(new ParquetStore(share, Nova(), true)); var arq = new Armazenamento(db);
            var svc = new Servicos(db, arq); var adm = new Ator { Login = "a", Nome = "A", Papel = Roles.Admin };
            var t = svc.SalvarTecnico(adm, new Tecnico { Nome = "T", Cidade = "X" });
            var d = svc.SalvarDocumento(adm, new Documento { TecnicoId = t.Id, Nome = "Doc", SemVencimento = true });
            var an = await svc.AnexarAsync(adm, "documento", d.Id, "a.pdf", new MemoryStream(Seed.PdfSimples("t", "r")));
            Assert.True(File.Exists(Path.Combine(share, "arquivos", an.Chave.Replace('/', Path.DirectorySeparatorChar))));
            Assert.Empty(Directory.GetFiles(Path.Combine(share, "arquivos"), "*.tmp", SearchOption.AllDirectories));
            svc.RemoverAnexo(adm, an.Id);
            Assert.False(File.Exists(Path.Combine(share, "arquivos", an.Chave.Replace('/', Path.DirectorySeparatorChar))));
            Assert.Single(Directory.GetFiles(Path.Combine(share, "_historico", "arquivos"), "*.bin", SearchOption.AllDirectories));
        }
        finally { try { Directory.Delete(share, true); } catch { } }
    }

    [Fact]
    public void Base_real_nao_pode_ser_apagada_pela_tela()
    {
        var share = Nova();
        try
        {
            var db = new Db(new ParquetStore(share)); var svc = new Servicos(db, new Armazenamento(db)); var adm = new Ator { Papel = Roles.Admin };
            Assert.False(db.Demo);
            Assert.Throws<ValidacaoException>(() => svc.LimparDadosOperacionais(adm));
            Assert.Throws<ValidacaoException>(() => svc.RecarregarDemo(adm));
        }
        finally { try { Directory.Delete(share, true); } catch { } }
    }

    [Fact]
    public void Codigos_de_viagem_nao_repetem_entre_maquinas()
    {
        var share = Nova(); Repo<Tecnico>.SegundosEntreChecagens = 0;
        try
        {
            var adm = new Ator { Login = "a", Nome = "A", Papel = Roles.Admin };
            var dbA = new Db(new ParquetStore(share, Nova(), true)); var dbB = new Db(new ParquetStore(share, Nova(), true));
            var sa = new Servicos(dbA, new Armazenamento(dbA)); var sb = new Servicos(dbB, new Armazenamento(dbB));
            var t = sa.SalvarTecnico(adm, new Tecnico { Nome = "T", Cidade = "X" });
            var v1 = sa.SalvarViagem(adm, new Viagem { TecnicoIds = t.Id, PlantaId = "", Status = Vocab.ViagemRascunho }, new(), new());
            var v2 = sb.SalvarViagem(adm, new Viagem { TecnicoIds = t.Id, PlantaId = "", Status = Vocab.ViagemRascunho }, new(), new());
            Assert.NotEqual(v1.Codigo, v2.Codigo);
        }
        finally { Repo<Tecnico>.SegundosEntreChecagens = 3; try { Directory.Delete(share, true); } catch { } }
    }
}

public class ImportacaoTecnicosTests
{
    private static ArquivoLido Csv(string t) => Importador.Ler("t.csv", new MemoryStream(System.Text.Encoding.UTF8.GetBytes(t)));

    [Fact]
    public void Importa_somente_nomes_valida_e_nao_duplica()
    {
        using var a = new Amb(); var imp = new ImportadorTecnicos(a.Db, a.Svc);
        var arq = Csv("Nome\nAna  Souza\nBruno Lima\n \nana souza\nCaio Reis\n");
        var map = ImportadorTecnicos.SugerirMapeamento(arq.Cabecalhos);
        var l = imp.Analisar(arq, map);
        Assert.Equal(4, l.Count);                                           // linha em branco é descartada na leitura
        Assert.Contains(l[2].Erros, e => e.Contains("repetido"));          // "ana souza" repete "Ana Souza"
        var r = imp.Aplicar(a.Admin, l);
        Assert.Equal(3, r.Importadas); Assert.Single(r.Rejeitadas);
        var ana = a.Db.Tecnicos.Onde(t => t.Nome == "Ana Souza").Single();
        Assert.Equal("", ana.Cidade); Assert.True(ana.Ativo); Assert.Equal("08:00", ana.JornadaInicio); Assert.Equal("1,2,3,4,5", ana.DiasUteis);
        Assert.Contains(a.Db.Auditorias.Todos(), x => x.Acao == "importacao.tecnicos");
        // reimportar: todos já cadastrados -> ignorados, nada duplica
        var l2 = imp.Analisar(arq, map);
        Assert.All(l2.Where(x => !x.Rejeitada), x => { Assert.True(x.Existente); Assert.Equal(AcaoImport.Ignorar, x.Acao); });
        var r2 = imp.Aplicar(a.Admin, l2); Assert.Equal(0, r2.Importadas); Assert.Equal(3, a.Db.Tecnicos.Contar());
        // homônimo só se pedido explicitamente
        l2[0].Acao = AcaoImport.Criar; Assert.Equal(1, imp.Aplicar(a.Admin, l2).Importadas); Assert.Equal(4, a.Db.Tecnicos.Contar());
    }

    [Fact]
    public void Tecnico_so_com_nome_pode_ser_completado_na_ficha_sem_cidade()
    {
        using var a = new Amb(); var imp = new ImportadorTecnicos(a.Db, a.Svc);
        var arq = Csv("Nome\nAna\n"); imp.Aplicar(a.Admin, imp.Analisar(arq, ImportadorTecnicos.SugerirMapeamento(arq.Cabecalhos)));
        var t = a.Db.Tecnicos.Todos().Single(); t.EspecialidadeIds = "x"; a.Svc.SalvarTecnico(a.Gestao, t);   // salvar sem cidade é permitido
        t.Cidade = "Campinas"; t.Estado = "SP"; a.Svc.SalvarTecnico(a.Gestao, t);
        Assert.Equal("Campinas/SP", a.Db.Tecnicos.Obter(t.Id)!.OrigemHabitual);
    }

    [Fact]
    public void Modelo_sem_titulo_reconhecivel_e_permissao()
    {
        var arq = Importador.Ler("m.xlsx", new MemoryStream(ImportadorTecnicos.Modelo()));
        Assert.Equal(new[] { "Nome" }, arq.Cabecalhos.ToArray());
        var map = ImportadorTecnicos.SugerirMapeamento(arq.Cabecalhos);
        using var a = new Amb(); var imp = new ImportadorTecnicos(a.Db, a.Svc);
        Assert.Equal(2, imp.Analisar(arq, map).Count);
        Assert.Throws<UnauthorizedAccessException>(() => imp.Aplicar(a.Contr, imp.Analisar(arq, map)));
        Assert.Equal(0, ImportadorTecnicos.SugerirMapeamento(new() { "Funcionário" })["nome"]);   // primeira coluna
    }
}

public class PlantasPlanilhaSimplesTests
{
    private static ArquivoLido Csv(string t) => Importador.Ler("t.csv", new MemoryStream(System.Text.Encoding.UTF8.GetBytes(t)));

    [Fact]
    public void Planilha_de_5_colunas_sem_cliente_e_sem_cidade_e_aceita()
    {
        using var a = new Amb(); var imp = new Importador(a.Db, a.Svc);
        var arq = Csv("Planta;Country;City;State;Address 1\nPlanta A;Brasil;Campinas;SP;Av. X, 10\nPlanta B;Chile;;Antofagasta;\nPlanta C;Peru;;;\n");
        var map = Importador.SugerirMapeamento(arq.Cabecalhos);
        var l = imp.Analisar(arq, map);
        Assert.All(l, x => Assert.False(x.Rejeitada));
        Assert.Contains(l[1].Avisos, w => w.Contains("Sem cidade")); Assert.Contains(l[2].Avisos, w => w.Contains("Sem cidade"));
        var r = imp.Aplicar(a.Admin, l);
        Assert.Equal(3, r.Importadas); Assert.Equal(0, r.ClientesCriados);
        var pa = a.Db.Plantas.Onde(p => p.Nome == "Planta A").Single();
        Assert.Equal("", pa.ClienteId); Assert.Equal("Av. X, 10", pa.Rua); Assert.Equal("Campinas", pa.Cidade); Assert.Equal("SP", pa.Estado); Assert.Equal("pendente", pa.GeoStatus);
        Assert.Equal("Planta A", a.Svc.Foto().NomePlanta(pa.Id));        // sem cliente: o nome é o que se seleciona
        // reimportar: reconhecida pelo nome, idêntica
        var l2 = imp.Analisar(arq, map); Assert.All(l2, x => { Assert.True(x.Existente); Assert.Equal(AcaoImport.Ignorar, x.Acao); });
    }

    [Fact]
    public void Sem_titulos_reconheciveis_vale_a_ordem_das_colunas()
    {
        var map = Importador.SugerirMapeamento(new() { "Col A", "Col B", "Col C", "Col D", "Col E" });
        Assert.Equal(0, map["planta"]); Assert.Equal(1, map["pais"]); Assert.Equal(2, map["cidade"]); Assert.Equal(3, map["estado"]); Assert.Equal(4, map["rua"]);
    }

    [Fact]
    public void Planta_sem_cidade_marca_a_viagem_e_completar_grava_no_cadastro()
    {
        using var a = new Amb(); var t = a.NovoTec();
        var p = a.Db.Plantas.Salvar(new Planta { Nome = "Planta Sem Cidade", Pais = "Brasil" });
        var v = new Viagem { PlantaId = p.Id, TecnicoIds = t.Id, Status = Vocab.ViagemPlanejada };
        var res = a.Svc.Validar(v, new(), new());
        Assert.Contains(p.Id, res.PlantasSemCidade); Assert.Contains(res.Erros, e => e.Contains("não tem cidade"));
        Assert.Throws<ValidacaoException>(() => a.Svc.CompletarLocalAsync(a.Admin, p.Id, "Brasil", "SP", "  ", "", null).GetAwaiter().GetResult());
        var ok = a.Svc.CompletarLocalAsync(a.Admin, p.Id, "Brasil", "SP", "Campinas", "Rua Y, 5", null).GetAwaiter().GetResult();
        Assert.Equal("Campinas", a.Db.Plantas.Obter(p.Id)!.Cidade); Assert.Equal("SP", ok.Estado); Assert.Contains("Campinas", ok.EnderecoCompleto);
        Assert.Empty(a.Svc.Validar(v, new(), new()).PlantasSemCidade);
        Assert.Throws<UnauthorizedAccessException>(() => a.Svc.CompletarLocalAsync(a.Consulta, p.Id, "", "", "X", "", null).GetAwaiter().GetResult());
    }
}

public class GeocodificacaoCascataTests
{
    private sealed class Fake : HttpMessageHandler
    {
        public List<string> Chamadas = new(); public Func<Dictionary<string, string>, bool> Acha = _ => false;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var q = System.Web.HttpUtility.ParseQueryString(req.RequestUri!.Query).AllKeys.Where(k => k is not null).ToDictionary(k => k!, k => System.Web.HttpUtility.ParseQueryString(req.RequestUri!.Query)[k]!);
            var nivel = q.ContainsKey("street") ? "endereco" : q.ContainsKey("city") ? "cidade" : q.ContainsKey("state") ? "estado" : "pais";
            Chamadas.Add(nivel);
            var corpo = Acha(q) ? "[{\"lat\":\"-23.5\",\"lon\":\"-47.4\",\"display_name\":\"Local " + nivel + "\"}]" : "[]";
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(corpo) });
        }
    }
    private sealed class Fab : IHttpClientFactory { public HttpMessageHandler H = null!; public HttpClient CreateClient(string name) => new(H); }
    private static (Geocoder g, Fake f) Novo(Func<Dictionary<string, string>, bool> acha, string contato = "ti@x.com")
    {
        var f = new Fake { Acha = acha };
        var cfg = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Geocoding:Contato"] = contato, ["Geocoding:IntervaloMs"] = "0" }).Build();
        return (new Geocoder(new Fab { H = f }, cfg), f);
    }

    [Fact]
    public async Task Acha_o_endereco_para_no_endereco()
    {
        var (g, f) = Novo(_ => true);
        var r = await g.BuscarHierarquicoAsync("Brasil", "SP", "Campinas", "Av. X, 10");
        Assert.True(r.Ok); Assert.Equal("endereco", r.Nivel); Assert.Equal(new[] { "endereco" }, f.Chamadas);
    }

    [Fact]
    public async Task Sem_endereco_para_na_cidade_sem_cidade_para_no_estado_sem_estado_para_no_pais()
    {
        var (g, f) = Novo(q => !q.ContainsKey("street"));
        var r = await g.BuscarHierarquicoAsync("Brasil", "SP", "Campinas", "Rua inexistente");
        Assert.Equal("cidade", r.Nivel); Assert.Equal(new[] { "endereco", "cidade" }, f.Chamadas); Assert.Contains("não encontrado: endereco", r.Mensagem);

        (g, f) = Novo(q => !q.ContainsKey("street") && !q.ContainsKey("city"));
        r = await g.BuscarHierarquicoAsync("Brasil", "SP", "Cidadeinexistente", "Rua");
        Assert.Equal("estado", r.Nivel); Assert.Equal(new[] { "endereco", "cidade", "estado" }, f.Chamadas);

        (g, f) = Novo(q => q.Keys.All(k => k is "country" or "format" or "limit" or "accept-language" or "email"));
        r = await g.BuscarHierarquicoAsync("Brasil", "Estadoinexistente", "Cidade", "Rua");
        Assert.Equal("pais", r.Nivel); Assert.Equal(new[] { "endereco", "cidade", "estado", "pais" }, f.Chamadas);

        (g, f) = Novo(_ => false);
        r = await g.BuscarHierarquicoAsync("Xyz", "", "", "");
        Assert.False(r.Ok); Assert.Equal(new[] { "pais" }, f.Chamadas);
    }

    [Fact]
    public async Task Niveis_sem_dado_sao_pulados_e_sem_configuracao_nao_simula()
    {
        var (g, f) = Novo(_ => true);
        var r = await g.BuscarHierarquicoAsync("Chile", "Antofagasta", "", "Rua sem cidade");   // sem cidade: nem tenta endereço
        Assert.Equal("estado", r.Nivel); Assert.Equal(new[] { "estado" }, f.Chamadas);
        var (g2, f2) = Novo(_ => true, contato: "");
        var r2 = await g2.BuscarHierarquicoAsync("Brasil", "SP", "Campinas", "");
        Assert.False(r2.Ok); Assert.Empty(f2.Chamadas); Assert.Contains("não configurada", r2.Mensagem);
    }

    [Fact]
    public async Task Planta_e_localizada_em_cascata_e_marca_o_nivel()
    {
        using var a = new Amb(); var (g, _) = Novo(q => !q.ContainsKey("street"));
        var p = await a.Svc.SalvarPlantaAsync(a.Admin, new Planta { Nome = "P", Pais = "Brasil", Estado = "SP", Cidade = "Campinas", Rua = "Rua que não existe" }, g);
        Assert.True(p.TemCoord); Assert.Equal("ok", p.GeoStatus); Assert.Equal("cidade", p.GeoNivel); Assert.Contains("aproximada ao nível de cidade", p.GeoFonte);
        a.Svc.CorrigirPosicao(a.Admin, p.Id, -10, -40);
        Assert.Equal("manual", a.Db.Plantas.Obter(p.Id)!.GeoStatus); Assert.Equal("", a.Db.Plantas.Obter(p.Id)!.GeoNivel);
    }
}
