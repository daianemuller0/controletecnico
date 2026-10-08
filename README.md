# Controle Técnico

Sistema web de controle de **viagens, agenda, disponibilidade e documentação dos técnicos**, construído com a
**mesma stack, arquitetura e identidade visual do projeto Previsão** (`daianemuller0/previsao`): C# / .NET 8 / Blazor Server,
persistência em **Parquet consolidado por DuckDB** (sem servidor de banco), ClosedXML para Excel, login por cookie com perfis e o
mesmo design system CSS (`wwwroot/app.css` herdado + `wwwroot/tecnico.css`).

## Base de dados compartilhada

**Toda a base (registros e anexos) fica em `\\BZVCPFIL003\proj_ramires$\DB\tec`** (`Data:Folder` no `appsettings.json`).

```
\\BZVCPFIL003\proj_ramires$\DB\tec
├── <entidade>/*.parquet     tecnicos, viagens, trechos, documentos, usuarios, auditoria… (um arquivo por gravação)
├── arquivos/yyyyMM/*.bin    anexos (nome opaco; só o servidor entrega, com checagem de perfil)
├── _historico/              arquivos retirados por compactação/limpeza/remoção (recuperáveis por 30 dias)
└── _locks/                  trava da compactação entre máquinas
```

Como foi pensado para pasta de rede com várias pessoas usando ao mesmo tempo:

* **Sem arquivo de banco compartilhado**: cada gravação cria um Parquet novo e imutável (copiado como `.tmp` e renomeado → ninguém lê arquivo pela metade; com retentativa para quedas breves de rede).
* **Leitura por espelho local** (`%LOCALAPPDATA%\ControleTecnico\espelho`): só os arquivos novos são copiados; as consultas rodam sobre a cópia local.
* **Várias instâncias/usuários**: o cache em memória confere a "assinatura" da pasta a cada poucos segundos (`Data:AtualizarCacheSegundos`) e recarrega quando outra máquina gravou. Códigos de viagem são numerados sobre a base já atualizada.
* **Compactação automática** (a cada 6 h, acima de 40 arquivos por entidade) com trava na pasta; os arquivos antigos vão para `_historico`.
* **Falha segura**: se a pasta estiver inacessível, **o programa não inicia** (não grava em outro lugar). Em Linux/macOS, caminho UNC não é suportado — monte o compartilhamento e use o ponto de montagem.
* As chaves dos cookies de login ficam **na máquina** (`%LOCALAPPDATA%\ControleTecnico\chaves`), nunca na pasta compartilhada.
* A conta que executa o programa precisa de **leitura e escrita** na pasta. Em *Administração → Armazenamento* há o teste real de leitura/escrita, latência e contagem de arquivos.
* Base real (`Seed:Demo=false`, padrão): as telas de "apagar/recarregar dados fictícios" ficam bloqueadas.

## Recuperar o acesso do administrador

```bash
dotnet run -- --redefinir-admin --senha=NovaSenha123   # sem --senha, gera uma aleatória e imprime
```

Recria/reativa o usuário `admin` com a senha informada (mín. 8 caracteres), **sem apagar dados**, e registra a operação no histórico. Só funciona em quem tem acesso à pasta da base.

## Como executar

```bash
dotnet run                  # Windows com acesso ao compartilhamento; abre em http://localhost:5090
dotnet test Tests           # 39 testes (regras de negócio, persistência e base compartilhada)
```

Primeiro acesso numa base vazia: usuário `admin` com a senha de `Seed__AdminSenha` (ou uma senha aleatória impressa no console).

**Demonstração / desenvolvimento** (nunca na pasta de rede): 
`Data__Folder=./data_demo Seed__Demo=true dotnet run` → carrega dados fictícios, exibe a faixa
**AMBIENTE DE DEMONSTRAÇÃO**; usuários `admin`, `gestao`, `controladoria`, `carlos` (técnico), `consulta`, senha `demo2026`.

## Telas

| Rota | Tela |
|---|---|
| `/` | **Visão operacional**: mapa (Leaflet), indicadores clicáveis, filtros, data/hora de previsão, painel do técnico, modo apresentação |
| `/agenda` | **Agenda**: linha do tempo por técnico, mês, semana, dia; criar/editar; arrastar e soltar com validação; conflitos; períodos livres |
| `/clientes` | **Clientes e plantas**: cadastros, localização no mapa (corrigível), importação `.xlsx/.csv` em 4 passos |
| `/viagens`, `/viagens/{id}` | **Controladoria e viagens**: lista, editor com trechos (carro/avião), atendimentos, tempos, verificação, anexos |
| `/tecnicos`, `/tecnicos/{id}` | **Técnicos**: equipe, importação em massa só de nomes (.xlsx/.csv), vencimentos, requisitos e aptidão, ficha com 8 seções (docs/treinamentos com anexos e renovação) |
| `/admin` | Usuários/perfis, parâmetros, integrações, histórico de alterações, dados de demonstração |

## Plantas: planilha e localização em cascata

* **Planilha de plantas (5 colunas):** `Planta` (nome que será selecionado ao enviar o técnico) · `Country` · `City` · `State` · `Address 1`. Cliente é opcional. Só o nome da planta é obrigatório. Sem títulos reconhecíveis, vale a ordem das colunas.
* **Localização em cascata** (Nominatim, consultas estruturadas): tenta **endereço** (rua + cidade + estado + país); se não achar, para na **cidade**; se não achar a cidade, no **estado**; se não achar o estado, no **país**. O nível alcançado fica gravado (`GeoNivel`) e aparece como *Aproximada: cidade/estado/país*. Níveis sem dado são pulados; se o serviço de mapas estiver fora do ar, não conclui "não existe".
* **Planta sem cidade:** pode ser importada; ao selecionar a planta numa viagem ou compromisso, o sistema **pede a cidade** (e estado/país/endereço) e já grava no cadastro, localizando em seguida. Viagem com planta sem cidade não pode ser confirmada.
* O e-mail de contato da geocodificação pode ser definido em *Administração → Configurações e integrações*.

## Decisões de modelagem

* **Fonte única**: a agenda não duplica lançamentos. Os blocos são calculados de *trechos*, *atendimentos* e *indisponibilidades*
  (`Logic/AgendaEngine.cs`); editar/cancelar a viagem reflete na hora no mapa, na agenda e na ficha. Viagem cancelada não bloqueia nada;
  rascunho aparece como **provisório** e não bloqueia.
* **Quatro conceitos separados**: status da viagem · status operacional do técnico · disponibilidade (jornada − compromissos) · aptidão documental.
* **Localização**: *prevista* (derivada da agenda) × *confirmada* (informada, com quem/quando; só vale enquanto recente e posterior ao último
  compromisso) × *sem localização definida*. Em viagem mostra origem→destino e período, **sem inventar posição no trajeto**.
  "Sem programação" ≠ "disponível": só há "disponível" quando a agenda do técnico é mantida (há compromissos ±90 dias).
* **Datas**: tudo em UTC; entrada/saída no fuso do local (trecho: origem/destino; atendimento: planta). Offsets são exibidos quando diferem do fuso padrão.
* **Documentos**: validade é do registro do técnico, nunca da norma. Renovar cria nova versão e preserva as anteriores e seus anexos.
  Requisitos por serviço/planta com efeito *bloqueia* ou *só alerta*; verificação cobre ausente, vencido, vence antes ou **durante** o atendimento.
* **Permissões no servidor** (`Logic/Ator.cs`, aplicadas em `Logic/Servicos*.cs` e no endpoint de arquivos): Administrador, Gestão, Controladoria, Técnico (só o próprio), Consulta.
  Toda alteração relevante grava histórico (quem, quando, o quê).

## Configuração externa (nada de segredos no código)

| Integração | Chave (`appsettings` ou variável de ambiente) | Sem configuração |
|---|---|---|
| Geocodificação (Nominatim/OSM ou instância própria) | `Geocoding__Contato` (e-mail exigido pela política de uso), `Geocoding__BaseUrl` | Plantas ficam **"localização pendente"**; posicionamento manual no mapa funciona |
| Estimativa de rota de carro (OSRM) | `Routing__BaseUrl` | Distância/duração informadas manualmente (marcadas como *informada*) |
| Camada do mapa | `Mapa__TileUrl`, `Mapa__Atribuicao` (o servidor público do OSM exige o cabeçalho Referer e bloqueia uso intenso; se aparecer "Access blocked", use outro provedor/instância própria) | Usa o servidor público do OpenStreetMap (adequado só a baixo volume; em produção use provedor com chave ou instância própria) |

## Limitações e pendências reais

* **Geocodificação e rotas não foram exercitadas contra os serviços reais** (o ambiente de desenvolvimento não tinha acesso externo): o código está implementado, mas a integração fica desativada até a configuração; o estado "pendente" foi testado.
* Os tiles do mapa vêm da internet; sem acesso o mapa aparece sem fundo, mas marcadores, rotas e listas funcionam.
* Posição durante a viagem **não** é rastreada (por requisito); não há GPS.
* Arrastar e soltar altera a **data** (dias); mudar o horário ou o técnico é feito no formulário/viagem.
* Reagendar um bloco de viagem permite mover só o bloco ou a viagem inteira (padrão: inteira, para manter a viagem coerente).
* O cálculo de viabilidade de deslocamento entre compromissos sem trecho **sinaliza "verificar"** (com distância em linha reta como referência); não calcula tempo de percurso sem integração configurada.
* Gravações simultâneas de máquinas diferentes no *mesmo registro* resolvem por "a última vence" (pelo relógio das máquinas); não há bloqueio otimista. Mantenha os relógios sincronizados.
* Notificações por e-mail de vencimento não foram implementadas (os alertas aparecem na ficha, na visão operacional e em *Técnicos → Vencimentos*, com exportação CSV).
* Sem renderização automática de testes de interface em CI; os testes automatizados cobrem regras de negócio e persistência (39 testes).
