using System.Text;
using ControleTecnico.Logic;
using ControleTecnico.Models;

namespace ControleTecnico.Data;

/// <summary>Dados FICTÍCIOS e coerentes para demonstração: todas as datas são relativas
/// a hoje, de modo que o mapa e a agenda sempre mostram técnicos disponíveis, em
/// viagem, em atendimento e em férias. Só roda com a base vazia.</summary>
public static class Seed
{
    public static bool BaseVazia(Db db) => db.Usuarios.Contar() == 0 && db.Tecnicos.Contar() == 0;

    public static void Usuarios(Db db, string senha, bool demo)
    {
        void U(string login, string nome, string papel, string tec = "") =>
            db.Usuarios.Salvar(Make(login, nome, papel, senha, tec), "seed");
        U("admin", "Administrador", Roles.Admin);
        if (!demo) return;
        U("gestao", "Marina Gestora", Roles.Gestao);
        U("controladoria", "Paulo Controladoria", Roles.Controladoria);
        U("consulta", "Visitante (consulta)", Roles.Consulta);
        U("carlos", "Carlos Mendes", Roles.Tecnico, "tec_carlos");
    }

    private static Usuario Make(string login, string nome, string papel, string senha, string tec)
    {
        var (h, s) = Servicos.HashSenha(senha);
        return new Usuario { Id = login, Nome = nome, Papel = papel, Hash = h, Salt = s, TecnicoId = tec, Ativo = true };
    }

    public static void Demo(Db db, Armazenamento arq)
    {
        var fuso = db.FusoPadrao;
        var hoje = Tempo.ParaLocal(DateTime.UtcNow, fuso).Date;
        DateTime H(int dias, double hora, string? tz = null) => Tempo.ParaUtc(hoje.AddDays(dias).AddHours(hora), tz ?? fuso);
        DateTime Dia(int dias) => DateTime.SpecifyKind(hoje.AddDays(dias), DateTimeKind.Utc);
        var agora = DateTime.UtcNow;

        db.SetCfg(Db.CfgDemo, "1", "seed");

        // ---- especialidades e serviços ----
        var espVib = new Especialidade { Id = "esp_vib", Nome = "Vibração e balanceamento" };
        var espInst = new Especialidade { Id = "esp_inst", Nome = "Instrumentação e controle" };
        var espMec = new Especialidade { Id = "esp_mec", Nome = "Mecânica de rotativos" };
        var espEle = new Especialidade { Id = "esp_ele", Nome = "Elétrica industrial" };
        var espCom = new Especialidade { Id = "esp_com", Nome = "Comissionamento" };
        var espLub = new Especialidade { Id = "esp_lub", Nome = "Lubrificação" };
        db.Especialidades.SalvarVarios(new[] { espVib, espInst, espMec, espEle, espCom, espLub }, "seed");

        var sPrev = new Servico { Id = "srv_prev", Nome = "Manutenção preventiva", Descricao = "Inspeção e manutenção programada de rotativos" };
        var sCom = new Servico { Id = "srv_com", Nome = "Comissionamento", Descricao = "Comissionamento de equipamentos novos" };
        var sBal = new Servico { Id = "srv_bal", Nome = "Balanceamento em campo", Descricao = "Balanceamento dinâmico no local" };
        var sPart = new Servico { Id = "srv_part", Nome = "Partida assistida", Descricao = "Acompanhamento de partida (start-up)" };
        var sRet = new Servico { Id = "srv_ret", Nome = "Retrofit de controle", Descricao = "Atualização do sistema de controle" };
        db.Servicos.SalvarVarios(new[] { sPrev, sCom, sBal, sPart, sRet }, "seed");

        // ---- clientes e plantas (ilustrativos) ----
        var cMin = new Cliente { Id = "cli_min", Nome = "Mineração Boa Vista S.A.", RazaoSocial = "Mineração Boa Vista S.A.", Contato = "Helena Prado", Telefone = "+55 31 5555-0101", Email = "contato@boavista.example", Obs = "Cliente fictício" };
        var cPet = new Cliente { Id = "cli_pet", Nome = "Petroquímica Sul", Contato = "Ricardo Faria", Telefone = "+55 71 5555-0102", Email = "contato@petrosul.example" };
        var cSid = new Cliente { Id = "cli_sid", Nome = "Siderúrgica Aço Norte", Contato = "Daniela Rocha", Email = "contato@aconorte.example" };
        var cAnd = new Cliente { Id = "cli_and", Nome = "Andes Copper", RazaoSocial = "Andes Copper SpA", Contato = "Matías Rojas", Telefone = "+56 2 5555 0103", Email = "contacto@andescopper.example" };
        var cCim = new Cliente { Id = "cli_cim", Nome = "Cimentos Pioneiros", Contato = "Sérgio Lopes", Email = "contato@pioneiros.example" };
        db.Clientes.SalvarVarios(new[] { cMin, cPet, cSid, cAnd, cCim }, "seed");

        Planta P(string id, string cli, string nome, string pais, string uf, string cidade, string rua, string num, string cep, double? lat, double? lon, string tz, string acesso = "")
        {
            var p = new Planta
            {
                Id = id, ClienteId = cli, Nome = nome, Pais = pais, Estado = uf, Cidade = cidade, Rua = rua, Numero = num, Cep = cep,
                FusoHorario = tz, Lat = lat, Lon = lon, GeoStatus = lat.HasValue ? "ok" : "pendente",
                GeoFonte = lat.HasValue ? "Dados de demonstração (posição aproximada)" : "", GeoEm = lat.HasValue ? agora : null,
                ContatoLocal = "Portaria", Acesso = acesso, Obs = "Endereço fictício",
            };
            p.EnderecoCompleto = Servicos.MontarEndereco(p);
            return p;
        }
        var pIta = P("pl_ita", "cli_min", "Mina Itabira", "Brasil", "MG", "Itabira", "Rodovia MG-129", "km 12", "35900-000", -19.6190, -43.2269, "America/Sao_Paulo", "Integração presencial às 07:30");
        var pCon = P("pl_con", "cli_min", "Usina Congonhas", "Brasil", "MG", "Congonhas", "Av. Industrial", "800", "36415-000", -20.4997, -43.8578, "America/Sao_Paulo");
        var pTub = P("pl_tub", "cli_min", "Porto Tubarão", "Brasil", "ES", "Vitória", "Av. Portuária", "1", "29090-000", -20.2860, -40.2400, "America/Sao_Paulo");
        var pTri = P("pl_tri", "cli_pet", "Planta Triunfo", "Brasil", "RS", "Triunfo", "Rua do Polo", "300", "95840-000", -29.9370, -51.3780, "America/Sao_Paulo");
        var pCam = P("pl_cam", "cli_pet", "Planta Camaçari", "Brasil", "BA", "Camaçari", "Via do Polo", "55", "42800-000", -12.6996, -38.3263, "America/Bahia");
        var pFor = P("pl_for", "cli_sid", "Planta Fortaleza", "Brasil", "CE", "Fortaleza", "Av. Industrial", "2000", "60000-000", -3.7319, -38.5267, "America/Fortaleza");
        var pPec = P("pl_pec", "cli_sid", "Planta Pecém", "Brasil", "CE", "São Gonçalo do Amarante", "Complexo Industrial do Pecém", "s/n", "62674-000", -3.5440, -38.8070, "America/Fortaleza");
        var pNov = P("pl_nov", "cli_sid", "Planta Nova (a localizar)", "Brasil", "CE", "Caucaia", "Estrada sem nome", "s/n", "", null, null, "America/Fortaleza");
        var pCal = P("pl_cal", "cli_and", "Planta Calama", "Chile", "Antofagasta", "Calama", "Ruta 21", "km 8", "1390000", -22.4667, -68.9333, "America/Santiago", "Curso de seguridad obrigatório");
        var pRan = P("pl_ran", "cli_and", "Planta Rancagua", "Chile", "O'Higgins", "Rancagua", "Camino a la Mina", "100", "2820000", -34.1708, -70.7444, "America/Santiago");
        var pSor = P("pl_sor", "cli_cim", "Fábrica Sorocaba", "Brasil", "SP", "Sorocaba", "Rod. Raposo Tavares", "km 100", "18000-000", -23.5015, -47.4526, "America/Sao_Paulo");
        var pCui = P("pl_cui", "cli_cim", "Fábrica Cuiabá", "Brasil", "MT", "Cuiabá", "Av. do CPA", "400", "78000-000", -15.6014, -56.0979, "America/Cuiaba");
        db.Plantas.SalvarVarios(new[] { pIta, pCon, pTub, pTri, pCam, pFor, pPec, pNov, pCal, pRan, pSor, pCui }, "seed");

        // ---- técnicos ----
        Tecnico T(string id, string nome, string mat, string cidade, string uf, double lat, double lon, string esp, string srv, string tel, string tz = "America/Sao_Paulo") => new()
        {
            Id = id, Nome = nome, Matricula = mat, Cidade = cidade, Estado = uf, Pais = "Brasil", OrigemHabitual = $"{cidade}/{uf}",
            OrigemLat = lat, OrigemLon = lon, EspecialidadeIds = esp, ServicoIds = srv, Telefone = tel, FusoHorario = tz,
            Email = nome.Split(' ')[0].ToLowerInvariant() + "@tecnicos.example", Obs = "Técnico fictício de demonstração",
        };
        var tCarlos = T("tec_carlos", "Carlos Mendes", "T-001", "São Paulo", "SP", -23.5505, -46.6333, "esp_vib,esp_mec", "srv_prev,srv_bal,srv_part", "+55 11 95555-0001");
        var tFer = T("tec_fer", "Fernanda Lima", "T-002", "Campinas", "SP", -22.9099, -47.0626, "esp_inst,esp_com", "srv_com,srv_part,srv_ret", "+55 19 95555-0002");
        var tRafa = T("tec_rafa", "Rafael Souza", "T-003", "Belo Horizonte", "MG", -19.9167, -43.9345, "esp_mec,esp_lub", "srv_prev,srv_bal", "+55 31 95555-0003");
        var tJu = T("tec_ju", "Juliana Costa", "T-004", "Curitiba", "PR", -25.4284, -49.2733, "esp_ele,esp_inst", "srv_prev,srv_ret", "+55 41 95555-0004");
        var tMar = T("tec_marcos", "Marcos Pereira", "T-005", "Porto Alegre", "RS", -30.0346, -51.2177, "esp_vib,esp_mec", "srv_prev,srv_bal", "+55 51 95555-0005");
        var tPat = T("tec_pat", "Patrícia Alves", "T-006", "Recife", "PE", -8.0476, -34.8770, "esp_com,esp_ele", "srv_com,srv_part", "+55 81 95555-0006");
        var tThi = T("tec_thi", "Thiago Ribeiro", "T-007", "Rio de Janeiro", "RJ", -22.9068, -43.1729, "esp_mec,esp_com", "srv_prev,srv_part", "+55 21 95555-0007");
        var tLu = T("tec_lu", "Luciana Prado", "T-008", "Salvador", "BA", -12.9777, -38.5016, "esp_inst,esp_ele", "srv_com,srv_prev", "+55 71 95555-0008", "America/Bahia");
        var tAnd = T("tec_and", "André Martins", "T-009", "Curitiba", "PR", -25.4284, -49.2733, "esp_lub,esp_mec", "srv_prev", "+55 41 95555-0009");
        var tBru = T("tec_bru", "Bruno Teixeira", "T-010", "Sorocaba", "SP", -23.5015, -47.4526, "esp_vib", "srv_bal", "+55 15 95555-0010");
        tFer.JornadaInicio = "07:30"; tFer.JornadaFim = "16:30";
        db.Tecnicos.SalvarVarios(new[] { tCarlos, tFer, tRafa, tJu, tMar, tPat, tThi, tLu, tAnd, tBru }, "seed");

        // ---- requisitos ----
        Requisito R(string esc, string escId, string nome, string cat, string norma, bool bloq, string obs = "") =>
            new() { Escopo = esc, EscopoId = escId, Nome = nome, Categoria = cat, Norma = norma, Bloqueia = bloq, Obs = obs };
        db.Requisitos.SalvarVarios(new[]
        {
            R("servico", "srv_prev", "NR-10 Segurança em eletricidade", "Treinamento", "NR-10", true),
            R("servico", "srv_prev", "NR-35 Trabalho em altura", "Treinamento", "NR-35", true),
            R("servico", "srv_part", "NR-10 Segurança em eletricidade", "Treinamento", "NR-10", true),
            R("servico", "srv_part", "NR-12 Máquinas e equipamentos", "Treinamento", "NR-12", true),
            R("servico", "srv_part", "Certificação do fabricante", "Certificação", "", false, "Recomendada; não impede a confirmação"),
            R("servico", "srv_com", "NR-10 Segurança em eletricidade", "Treinamento", "NR-10", true),
            R("planta", "pl_ita", "Integração de segurança — Mineração Boa Vista", "Treinamento", "", true),
            R("planta", "pl_ita", "ASO — Atestado de saúde ocupacional", "Exame / saúde", "", true),
            R("planta", "pl_cal", "Curso de seguridad Sernageomin", "Treinamento", "", true),
            R("planta", "pl_cal", "Passaporte válido", "Documento pessoal", "", true),
            R("planta", "pl_cam", "NR-33 Espaço confinado", "Treinamento", "NR-33", true),
            R("planta", "pl_pec", "NR-33 Espaço confinado", "Treinamento", "NR-33", true),
            R("planta", "pl_pec", "ASO — Atestado de saúde ocupacional", "Exame / saúde", "", false),
        }, "seed");

        // ---- documentos ----
        var docsComArquivo = new List<Documento>();
        Documento D(string id, string tec, string nome, string cat, string norma, string emissor, int emissaoDias, int? vencDias, string versao = "")
        {
            var d = new Documento
            {
                Id = id, TecnicoId = tec, Nome = nome, Categoria = cat, Norma = norma, NormaVersao = versao, Emissor = emissor,
                Emissao = Dia(emissaoDias), Vencimento = vencDias.HasValue ? Dia(vencDias.Value) : null, SemVencimento = !vencDias.HasValue,
                CadastradoPor = "Marina Gestora", RaizId = id, Versao = 1, Atual = true,
            };
            return d;
        }
        const string NR10 = "NR-10 Segurança em eletricidade", NR35 = "NR-35 Trabalho em altura", NR12 = "NR-12 Máquinas e equipamentos", NR33 = "NR-33 Espaço confinado";
        const string ASO = "ASO — Atestado de saúde ocupacional", INTEG = "Integração de segurança — Mineração Boa Vista";
        var docs = new List<Documento>
        {
            D("d_c1", "tec_carlos", NR10, "Treinamento", "NR-10", "Instituto Técnico Demo", -60, 300),
            D("d_c3", "tec_carlos", ASO, "Exame / saúde", "", "Clínica Demo", -245, 120),
            D("d_c4", "tec_carlos", INTEG, "Treinamento", "", "Mineração Boa Vista", -280, 80),
            D("d_c5", "tec_carlos", "Certificação de analista de vibração Cat. II", "Certificação", "ISO 18436-2", "Instituto Demo de Vibração", -400, 900, "2014"),
            D("d_f1", "tec_fer", NR10, "Treinamento", "NR-10", "Instituto Técnico Demo", -140, 220),
            D("d_f2", "tec_fer", NR35, "Treinamento", "NR-35", "Instituto Técnico Demo", -30, 400),
            D("d_f3", "tec_fer", "Passaporte válido", "Documento pessoal", "", "Polícia Federal (demo)", -900, 1200),
            D("d_f4", "tec_fer", "Curso de seguridad Sernageomin", "Treinamento", "", "Sernageomin (demo)", -270, 95),
            D("d_f5", "tec_fer", ASO, "Exame / saúde", "", "Clínica Demo", -300, 60),
            D("d_f6", "tec_fer", NR12, "Treinamento", "NR-12", "Instituto Técnico Demo", -100, 500),
            D("d_r1", "tec_rafa", NR10, "Treinamento", "NR-10", "Instituto Técnico Demo", -200, 150),
            D("d_r2", "tec_rafa", NR35, "Treinamento", "NR-35", "Instituto Técnico Demo", -320, 45),
            D("d_r3", "tec_rafa", ASO, "Exame / saúde", "", "Clínica Demo", -160, 200),
            D("d_r4", "tec_rafa", INTEG, "Treinamento", "", "Mineração Boa Vista", -100, 250),
            D("d_j1", "tec_ju", NR10, "Treinamento", "NR-10", "Instituto Técnico Demo", -380, -15),
            D("d_j2", "tec_ju", NR35, "Treinamento", "NR-35", "Instituto Técnico Demo", -265, 100),
            D("d_m1", "tec_marcos", NR10, "Treinamento", "NR-10", "Instituto Técnico Demo", -185, 180),
            D("d_m2", "tec_marcos", NR35, "Treinamento", "NR-35", "Instituto Técnico Demo", -340, 16),
            D("d_m3", "tec_marcos", ASO, "Exame / saúde", "", "Clínica Demo", -100, 150),
            D("d_p1", "tec_pat", NR10, "Treinamento", "NR-10", "Instituto Técnico Demo", -65, 300),
            D("d_p2", "tec_pat", NR33, "Treinamento", "NR-33", "Instituto Técnico Demo", -215, 150),
            D("d_p3", "tec_pat", NR35, "Treinamento", "NR-35", "Instituto Técnico Demo", -165, 200),
            D("d_p4", "tec_pat", ASO, "Exame / saúde", "", "Clínica Demo", -275, 90),
            D("d_t1", "tec_thi", NR10, "Treinamento", "NR-10", "Instituto Técnico Demo", -265, 100),
            D("d_t2", "tec_thi", NR35, "Treinamento", "NR-35", "Instituto Técnico Demo", -15, 350),
            D("d_t3", "tec_thi", NR12, "Treinamento", "NR-12", "Instituto Técnico Demo", 0, 500),
            D("d_t4", "tec_thi", ASO, "Exame / saúde", "", "Clínica Demo", -335, 30),
            D("d_t5", "tec_thi", NR33, "Treinamento", "NR-33", "Instituto Técnico Demo", -60, 300),
            D("d_l1", "tec_lu", NR10, "Treinamento", "NR-10", "Instituto Técnico Demo", -115, 250),
            D("d_l2", "tec_lu", NR33, "Treinamento", "NR-33", "Instituto Técnico Demo", -305, 60),
            D("d_l3", "tec_lu", NR35, "Treinamento", "NR-35", "Instituto Técnico Demo", -50, 315),
            D("d_a1", "tec_and", NR10, "Treinamento", "NR-10", "Instituto Técnico Demo", -90, 275),
            D("d_a2", "tec_and", NR35, "Treinamento", "NR-35", "Instituto Técnico Demo", -400, -35),
            D("d_b1", "tec_bru", NR10, "Treinamento", "NR-10", "Instituto Técnico Demo", -30, 335),
            D("d_b2", "tec_bru", "Habilitação para condução de veículos", "Documento pessoal", "", "DETRAN (demo)", -1500, null),
        };
        // NR-35 do Carlos: versão antiga (vencida) preservada no histórico + versão atual
        docs.Add(new Documento
        {
            Id = "d_c2_v1", TecnicoId = "tec_carlos", Nome = NR35, Categoria = "Treinamento", Norma = "NR-35", Emissor = "Instituto Técnico Demo",
            Emissao = Dia(-760), Vencimento = Dia(-395), CadastradoPor = "Marina Gestora", RaizId = "d_c2", Versao = 1, Atual = false,
        });
        docs.Add(new Documento
        {
            Id = "d_c2", TecnicoId = "tec_carlos", Nome = NR35, Categoria = "Treinamento", Norma = "NR-35", Emissor = "Instituto Técnico Demo",
            Emissao = Dia(-395), Vencimento = Dia(335), CadastradoPor = "Marina Gestora", RaizId = "d_c2", Versao = 2, Atual = true,
            Obs = "Renovação da versão anterior",
        });
        db.Documentos.SalvarVarios(docs, "seed");
        foreach (var d in docs.Where(d => d.Id is "d_c1" or "d_c2" or "d_c2_v1" or "d_c5" or "d_f1" or "d_f4" or "d_r2" or "d_j1" or "d_m2" or "d_l2"))
            AnexoPdf(db, arq, "documento", d.Id, $"{d.Nome.Split(' ')[0]}_{d.Id}.pdf", $"{d.Nome} — {db.Tecnicos.Obter(d.TecnicoId)?.Nome}", "DOCUMENTO FICTICIO DE DEMONSTRACAO");

        // ---- viagens ----
        Trecho Tr(string id, string viagem, int ordem, string tipo, string modal, string origem, double? olat, double? olon, string destino, double? dlat, double? dlon,
            DateTime partida, DateTime chegada, string tecs = "", string destPlanta = "", string fusoDest = "", int? dur = null, string fonte = "informada") => new()
        {
            Id = id, ViagemId = viagem, Ordem = ordem, Tipo = tipo, Modal = modal, Origem = origem, OrigemLat = olat, OrigemLon = olon,
            Destino = destino, DestLat = dlat, DestLon = dlon, PartidaPrev = partida, ChegadaPrev = chegada, TecnicoIds = tecs,
            DestPlantaId = destPlanta, FusoDestino = fusoDest, DuracaoMin = dur ?? (int)(chegada - partida).TotalMinutes, DuracaoFonte = fonte,
        };
        Atendimento At(string id, string viagem, string tecs, string cli, string planta, string serv, DateTime ini, DateTime fim, double? horas, string j1 = "08:00", string j2 = "17:00") => new()
        {
            Id = id, ViagemId = viagem, TecnicoIds = tecs, ClienteId = cli, PlantaId = planta, ServicoId = serv, IniPrev = ini, FimPrev = fim, HorasPrev = horas, JanelaInicio = j1, JanelaFim = j2,
        };
        Viagem V(string id, string cod, string status, string cli, string planta, string serv, string tecs, string origem, string destino, string resp = "Paulo Controladoria", string obs = "") => new()
        {
            Id = id, Codigo = cod, Status = status, ClienteId = cli, PlantaId = planta, ServicoId = serv, TecnicoIds = tecs,
            Origem = origem, Destino = destino, Responsavel = resp, Obs = obs,
        };

        var viagens = new List<Viagem>(); var trechos = new List<Trecho>(); var atends = new List<Atendimento>();
        int ano = hoje.Year;

        // V1 — Carlos e Rafael, Mina Itabira, de carro (em andamento). Trechos individuais.
        viagens.Add(V("v1", $"V-{ano}-0001", Vocab.ViagemAndamento, "cli_min", "pl_ita", "srv_prev", "tec_carlos,tec_rafa", "São Paulo/SP e Belo Horizonte/MG", "Mina Itabira", obs: "Preventiva anual do britador. Dois técnicos saem de cidades diferentes."));
        trechos.Add(Tr("v1t1", "v1", 1, Vocab.TrechoIda, Vocab.ModalCarro, "São Paulo/SP", -23.5505, -46.6333, "Mina Itabira", -19.6190, -43.2269, H(-4, 6), H(-4, 15), "tec_carlos", "pl_ita"));
        trechos.Add(Tr("v1t2", "v1", 2, Vocab.TrechoIda, Vocab.ModalCarro, "Belo Horizonte/MG", -19.9167, -43.9345, "Mina Itabira", -19.6190, -43.2269, H(-3, 5.5), H(-3, 7.75), "tec_rafa", "pl_ita"));
        trechos.Add(Tr("v1t3", "v1", 3, Vocab.TrechoRetorno, Vocab.ModalCarro, "Mina Itabira", -19.6190, -43.2269, "São Paulo/SP", -23.5505, -46.6333, H(5, 7), H(5, 16), "tec_carlos"));
        trechos.Add(Tr("v1t4", "v1", 4, Vocab.TrechoRetorno, Vocab.ModalCarro, "Mina Itabira", -19.6190, -43.2269, "Belo Horizonte/MG", -19.9167, -43.9345, H(4, 17.5), H(4, 20), "tec_rafa"));
        trechos[0].Veiculo = "Van corporativa ABC-1D23"; trechos[0].Motorista = "Carlos Mendes"; trechos[0].DistanciaKm = 570;
        trechos[1].Veiculo = "Carro alugado (demo)"; trechos[1].Motorista = "Rafael Souza"; trechos[1].DistanciaKm = 110; trechos[1].DuracaoFonte = "estimada";
        var a1 = At("v1a1", "v1", "tec_carlos,tec_rafa", "cli_min", "pl_ita", "srv_prev", H(-3, 8), H(4, 17), 64);
        a1.IniReal = H(-3, 8.25); a1.HorasReal = 40;
        atends.Add(a1);

        // V2 — Fernanda, Planta Calama (Chile), avião (confirmada). Internacional, em voo agora.
        viagens.Add(V("v2", $"V-{ano}-0002", Vocab.ViagemConfirmada, "cli_and", "pl_cal", "srv_part", "tec_fer", "Campinas/SP", "Planta Calama (Chile)", obs: "Partida assistida do compressor C-201."));
        var voo1 = Tr("v2t1", "v2", 1, Vocab.TrechoIda, Vocab.ModalAviao, "Aeroporto de Guarulhos (GRU)", -23.4356, -46.4731, "Aeroporto de Santiago (SCL)", -33.3930, -70.7858,
            agora.AddHours(-1), agora.AddHours(3), "", "", "America/Santiago", fonte: "informada");
        voo1.Companhia = "Aérea Demo"; voo1.Voo = "AD 1234"; voo1.AeroOrigem = "GRU"; voo1.AeroDestino = "SCL"; voo1.Reserva = "DEMO42"; voo1.FusoOrigem = "America/Sao_Paulo";
        var voo2 = Tr("v2t2", "v2", 2, Vocab.TrechoIda, Vocab.ModalAviao, "Aeroporto de Santiago (SCL)", -33.3930, -70.7858, "Aeroporto de Calama (CJC)", -22.4982, -68.9036,
            H(1, 8, "America/Santiago"), H(1, 10.25, "America/Santiago"), "", "", "America/Santiago");
        voo2.Companhia = "Aérea Demo"; voo2.Voo = "AD 5678"; voo2.AeroOrigem = "SCL"; voo2.AeroDestino = "CJC"; voo2.Reserva = "DEMO42"; voo2.FusoOrigem = "America/Santiago";
        var terr = Tr("v2t3", "v2", 3, Vocab.TrechoIda, Vocab.ModalCarro, "Aeroporto de Calama (CJC)", -22.4982, -68.9036, "Planta Calama", -22.4667, -68.9333,
            H(1, 10.75, "America/Santiago"), H(1, 11.5, "America/Santiago"), "", "pl_cal", "America/Santiago");
        terr.Motorista = "Motorista do cliente"; terr.DistanciaKm = 6; terr.Obs = "Traslado fornecido pelo cliente";
        var ret = Tr("v2t4", "v2", 4, Vocab.TrechoRetorno, Vocab.ModalAviao, "Aeroporto de Calama (CJC)", -22.4982, -68.9036, "Campinas/SP", -22.9099, -47.0626,
            H(6, 8, "America/Santiago"), H(6, 17, "America/Santiago"), "", "", "America/Sao_Paulo");
        ret.Companhia = "Aérea Demo"; ret.Voo = "AD 9012"; ret.AeroOrigem = "CJC"; ret.AeroDestino = "VCP"; ret.Reserva = "DEMO43"; ret.Obs = "Com conexão em SCL";
        trechos.AddRange(new[] { voo1, voo2, terr, ret });
        atends.Add(At("v2a1", "v2", "tec_fer", "cli_and", "pl_cal", "srv_part", H(1, 13, "America/Santiago"), H(5, 17, "America/Santiago"), 36, "08:00", "17:00"));
        AnexoPdf(db, arq, "viagem", "v2", "bilhete_V2.pdf", "Bilhete eletronico — Fernanda Lima — VIAGEM DE DEMONSTRACAO", "DOCUMENTO FICTICIO");

        // V3 — Patrícia, Planta Camaçari: avião + carro (em andamento)
        viagens.Add(V("v3", $"V-{ano}-0003", Vocab.ViagemAndamento, "cli_pet", "pl_cam", "srv_com", "tec_pat", "Recife/PE", "Planta Camaçari"));
        var p1 = Tr("v3t1", "v3", 1, Vocab.TrechoIda, Vocab.ModalAviao, "Recife (REC)", -8.1265, -34.9231, "Salvador (SSA)", -12.9111, -38.3311, H(-2, 7, "America/Sao_Paulo"), H(-2, 8.33));
        p1.Companhia = "Aérea Demo"; p1.Voo = "AD 3301"; p1.AeroOrigem = "REC"; p1.AeroDestino = "SSA"; p1.Reserva = "DEMO77";
        var p2 = Tr("v3t2", "v3", 2, Vocab.TrechoIda, Vocab.ModalCarro, "Salvador (SSA)", -12.9111, -38.3311, "Planta Camaçari", -12.6996, -38.3263, H(-2, 9.5), H(-2, 10.75), "", "pl_cam");
        p2.Veiculo = "Carro alugado (demo)"; p2.Motorista = "Patrícia Alves"; p2.DistanciaKm = 38; p2.DuracaoFonte = "estimada";
        var p3 = Tr("v3t3", "v3", 3, Vocab.TrechoRetorno, Vocab.ModalAviao, "Salvador (SSA)", -12.9111, -38.3311, "Recife/PE", -8.0476, -34.8770, H(3, 7.5), H(3, 9));
        p3.Companhia = "Aérea Demo"; p3.Voo = "AD 3310"; p3.AeroOrigem = "SSA"; p3.AeroDestino = "REC"; p3.Reserva = "DEMO78";
        trechos.AddRange(new[] { p1, p2, p3 });
        var a3 = At("v3a1", "v3", "tec_pat", "cli_pet", "pl_cam", "srv_com", H(-1, 8), H(2, 17), 32); a3.IniReal = H(-1, 8); a3.HorasReal = 9;
        atends.Add(a3);

        // V4 — Luciana, Camaçari, de carro: saindo agora
        viagens.Add(V("v4", $"V-{ano}-0004", Vocab.ViagemConfirmada, "cli_pet", "pl_cam", "srv_prev", "tec_lu", "Salvador/BA", "Planta Camaçari"));
        trechos.Add(Tr("v4t1", "v4", 1, Vocab.TrechoIda, Vocab.ModalCarro, "Salvador/BA", -12.9777, -38.5016, "Planta Camaçari", -12.6996, -38.3263, agora.AddMinutes(-30), agora.AddMinutes(50), "", "pl_cam", "", 80, "estimada"));
        trechos.Add(Tr("v4t2", "v4", 2, Vocab.TrechoRetorno, Vocab.ModalCarro, "Planta Camaçari", -12.6996, -38.3263, "Salvador/BA", -12.9777, -38.5016, H(3, 17.5), H(3, 18.7)));
        atends.Add(At("v4a1", "v4", "tec_lu", "cli_pet", "pl_cam", "srv_prev", H(1, 8), H(3, 17), 24));

        // V5 — Thiago, Pecém (planejada) + atendimento avulso em Sorocaba no mesmo período (CONFLITO de demonstração)
        viagens.Add(V("v5", $"V-{ano}-0005", Vocab.ViagemPlanejada, "cli_sid", "pl_pec", "srv_part", "tec_thi", "Rio de Janeiro/RJ", "Planta Pecém"));
        var t1 = Tr("v5t1", "v5", 1, Vocab.TrechoIda, Vocab.ModalAviao, "Rio de Janeiro (GIG)", -22.8089, -43.2436, "Fortaleza (FOR)", -3.7763, -38.5326, H(7, 6), H(7, 10.5));
        t1.Companhia = "Aérea Demo"; t1.Voo = "AD 2201"; t1.AeroOrigem = "GIG"; t1.AeroDestino = "FOR"; t1.Reserva = "DEMO90";
        var t2 = Tr("v5t2", "v5", 2, Vocab.TrechoIda, Vocab.ModalCarro, "Fortaleza (FOR)", -3.7763, -38.5326, "Planta Pecém", -3.5440, -38.8070, H(7, 11), H(7, 12.5), "", "pl_pec");
        t2.DistanciaKm = 60; t2.DuracaoFonte = "estimada";
        var t3 = Tr("v5t3", "v5", 3, Vocab.TrechoRetorno, Vocab.ModalAviao, "Fortaleza (FOR)", -3.7763, -38.5326, "Rio de Janeiro/RJ", -22.9068, -43.1729, H(11, 7), H(11, 11.5));
        t3.Companhia = "Aérea Demo"; t3.Voo = "AD 2210"; t3.Reserva = "DEMO91";
        trechos.AddRange(new[] { t1, t2, t3 });
        atends.Add(At("v5a1", "v5", "tec_thi", "cli_sid", "pl_pec", "srv_part", H(8, 8), H(10, 17), 24));
        atends.Add(new Atendimento
        {
            Id = "av1", TecnicoIds = "tec_thi", ClienteId = "cli_cim", PlantaId = "pl_sor", ServicoId = "srv_prev", IniPrev = H(9, 8), FimPrev = H(9, 17),
            HorasPrev = 8, Status = Vocab.ViagemPlanejada, Obs = "Visita técnica solicitada pelo cliente — conflita com a viagem a Pecém (demonstração).",
        });

        // V6 — Marcos, Pecém, RASCUNHO (NR-35 vence durante o atendimento; NR-33 ausente)
        viagens.Add(V("v6", $"V-{ano}-0006", Vocab.ViagemRascunho, "cli_sid", "pl_pec", "srv_prev", "tec_marcos", "Porto Alegre/RS", "Planta Pecém", obs: "Rascunho: depende de renovação de NR-35 e do treinamento NR-33."));
        var m1 = Tr("v6t1", "v6", 1, Vocab.TrechoIda, Vocab.ModalAviao, "Porto Alegre (POA)", -29.9944, -51.1714, "Fortaleza (FOR)", -3.7763, -38.5326, H(14, 6), H(14, 12));
        m1.Companhia = "Aérea Demo"; m1.Voo = "AD 4100"; m1.AeroOrigem = "POA"; m1.AeroDestino = "FOR";
        var m2 = Tr("v6t2", "v6", 2, Vocab.TrechoRetorno, Vocab.ModalAviao, "Fortaleza (FOR)", -3.7763, -38.5326, "Porto Alegre/RS", -30.0346, -51.2177, H(19, 7), H(19, 13));
        trechos.AddRange(new[] { m1, m2 });
        atends.Add(At("v6a1", "v6", "tec_marcos", "cli_sid", "pl_pec", "srv_prev", H(15, 8), H(18, 17), 32));

        // V7 / V8 — viagens concluídas (histórico)
        viagens.Add(V("v7", $"V-{ano}-0007", Vocab.ViagemConcluida, "cli_min", "pl_con", "srv_prev", "tec_carlos", "São Paulo/SP", "Usina Congonhas"));
        trechos.Add(Tr("v7t1", "v7", 1, Vocab.TrechoIda, Vocab.ModalCarro, "São Paulo/SP", -23.5505, -46.6333, "Usina Congonhas", -20.4997, -43.8578, H(-40, 6), H(-40, 14), "", "pl_con"));
        trechos.Add(Tr("v7t2", "v7", 2, Vocab.TrechoRetorno, Vocab.ModalCarro, "Usina Congonhas", -20.4997, -43.8578, "São Paulo/SP", -23.5505, -46.6333, H(-33, 7), H(-33, 15)));
        var a7 = At("v7a1", "v7", "tec_carlos", "cli_min", "pl_con", "srv_prev", H(-39, 8), H(-34, 17), 40); a7.IniReal = H(-39, 8.1); a7.FimReal = H(-34, 16.5); a7.HorasReal = 41.5;
        atends.Add(a7);
        viagens.Add(V("v8", $"V-{ano}-0008", Vocab.ViagemConcluida, "cli_min", "pl_tub", "srv_bal", "tec_marcos", "Porto Alegre/RS", "Porto Tubarão"));
        trechos.Add(Tr("v8t1", "v8", 1, Vocab.TrechoIda, Vocab.ModalAviao, "Porto Alegre (POA)", -29.9944, -51.1714, "Vitória (VIX)", -20.2581, -40.2864, H(-11, 6), H(-11, 9)));
        trechos.Add(Tr("v8t2", "v8", 2, Vocab.TrechoRetorno, Vocab.ModalAviao, "Vitória (VIX)", -20.2581, -40.2864, "Porto Alegre/RS", -30.0346, -51.2177, H(-5, 15), H(-5, 18)));
        var a8 = At("v8a1", "v8", "tec_marcos", "cli_min", "pl_tub", "srv_bal", H(-10, 8), H(-6, 17), 32); a8.IniReal = H(-10, 8); a8.FimReal = H(-6, 16); a8.HorasReal = 33;
        atends.Add(a8);

        db.Viagens.SalvarVarios(viagens, "seed");
        db.Trechos.SalvarVarios(trechos, "seed");
        db.Atendimentos.SalvarVarios(atends, "seed");

        // ---- indisponibilidades ----
        db.Indisponibilidades.SalvarVarios(new[]
        {
            new Indisponibilidade { Id = "i_ju", TecnicoId = "tec_ju", Tipo = Vocab.IndFerias, Ini = H(-2, 0), Fim = H(11, 0), Motivo = "Férias anuais" },
            new Indisponibilidade { Id = "i_and", TecnicoId = "tec_and", Tipo = Vocab.IndAfastamento, Ini = H(-5, 0), Fim = H(21, 0), Motivo = "Afastamento médico" },
            new Indisponibilidade { Id = "i_rafa", TecnicoId = "tec_rafa", Tipo = Vocab.IndFerias, Ini = H(30, 0), Fim = H(45, 0), Motivo = "Férias programadas" },
            new Indisponibilidade { Id = "i_fer", TecnicoId = "tec_fer", Tipo = Vocab.IndFolga, Ini = H(12, 0), Fim = H(13, 0), Motivo = "Folga (banco de horas)" },
            new Indisponibilidade { Id = "i_car", TecnicoId = "tec_carlos", Tipo = Vocab.IndBloqueio, Ini = H(20, 8), Fim = H(22, 0), Motivo = "Treinamento interno (a confirmar)", Provisoria = true },
        }, "seed");

        // ---- confirmações de localização ----
        db.Confirmacoes.SalvarVarios(new[]
        {
            new ConfirmacaoLocal { Id = "c1", TecnicoId = "tec_pat", PlantaId = "pl_cam", Texto = "Planta Camaçari — portaria", Lat = -12.6996, Lon = -38.3263, Quando = agora.AddHours(-2), Por = "Patrícia Alves", Origem = "tecnico" },
            new ConfirmacaoLocal { Id = "c2", TecnicoId = "tec_carlos", PlantaId = "pl_ita", Texto = "Mina Itabira", Lat = -19.6190, Lon = -43.2269, Quando = agora.AddHours(-30), Por = "Marina Gestora", Origem = "gestao" },
        }, "seed");

        // ---- histórico inicial ----
        db.Auditorias.Salvar(new Auditoria { Quando = agora, Usuario = "sistema", Papel = Roles.Admin, Acao = "seed.demo", Entidade = "sistema", Resumo = "Dados fictícios de demonstração carregados" });
    }

    private static void AnexoPdf(Db db, Armazenamento arq, string tipo, string dono, string nome, string titulo, string rodape)
    {
        var bytes = PdfSimples(titulo, rodape);
        using var ms = new MemoryStream(bytes);
        arq.SalvarAsync(Ator.Sistema, tipo, dono, nome, ms).GetAwaiter().GetResult();
    }

    /// <summary>PDF mínimo válido de uma página, só para exercitar anexos na demonstração.</summary>
    public static byte[] PdfSimples(string titulo, string rodape)
    {
        static string Esc(string s) => new string(s.Select(c => c < 128 ? c : '?').ToArray()).Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
        var conteudo = $"BT /F1 20 Tf 60 780 Td ({Esc(titulo)}) Tj ET\nBT /F1 12 Tf 60 740 Td ({Esc(rodape)}) Tj ET\n";
        var objs = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {conteudo.Length} >>\nstream\n{conteudo}endstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        var sb = new StringBuilder("%PDF-1.4\n");
        var offs = new List<int>();
        for (var i = 0; i < objs.Length; i++) { offs.Add(sb.Length); sb.Append($"{i + 1} 0 obj\n{objs[i]}\nendobj\n"); }
        var xref = sb.Length;
        sb.Append($"xref\n0 {objs.Length + 1}\n0000000000 65535 f \n");
        foreach (var o in offs) sb.Append($"{o:0000000000} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objs.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>Apaga TODOS os dados (inclusive anexos) — reset do ambiente de demonstração.</summary>
    public static void LimparTudo(Db db, Armazenamento arq)
    {
        arq.LimparTudo();
        db.Tecnicos.Limpar(); db.Especialidades.Limpar(); db.Servicos.Limpar(); db.Clientes.Limpar(); db.Plantas.Limpar();
        db.Viagens.Limpar(); db.Trechos.Limpar(); db.Atendimentos.Limpar(); db.Indisponibilidades.Limpar(); db.Confirmacoes.Limpar();
        db.Documentos.Limpar(); db.Requisitos.Limpar();
    }
}
