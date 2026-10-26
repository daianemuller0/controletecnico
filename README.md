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

* **Planilha de plantas (somente 5 colunas):** `Account` (nome da planta, o que se seleciona ao enviar o técnico) · `Country` · `City` · `State` · `Address1`. Nada além disso. Só o nome é obrigatório. Sem títulos reconhecíveis, vale a ordem das colunas.
* **Volume:** a importação aceita até **20.000 linhas / 25 MB** (testado com 7.000: leitura e validação < 1 s, gravação ≈ 12 s num único arquivo Parquet). A prévia e a lista de plantas são paginadas (100 por página).
* **Localização em segundo plano:** o botão "Localizar pendentes" inicia um serviço no servidor (não depende da página aberta), com barra de progresso, estimativa e botão Parar. Grava em lotes de 50, reaproveita consultas repetidas (plantas da mesma cidade/estado/país) e interrompe sozinho se o serviço de mapas começar a recusar. **Atenção:** o servidor público do Nominatim limita a 1 consulta/s e não admite uso intensivo; para milhares de plantas use uma instância própria ou um provedor contratado (`Geocoding__BaseUrl`).
* **Localização em cascata** (Nominatim, consultas estruturadas): tenta **endereço** (rua + cidade + estado + país); se não achar, para na **cidade**; se não achar a cidade, no **estado**; se não achar o estado, no **país**. O nível alcançado fica gravado (`GeoNivel`) e aparece como *Aproximada: cidade/estado/país*. Níveis sem dado são pulados; se o serviço de mapas estiver fora do ar, não conclui "não existe".
* **Planta sem cidade:** pode ser importada; ao selecionar a planta numa viagem ou compromisso, o sistema **pede a cidade** (e estado/país/endereço) e já grava no cadastro, localizando em seguida. Viagem com planta sem cidade não pode ser confirmada.
* O e-mail de contato da geocodificação pode ser definido em *Administração → Configurações e integrações*.

## Buscar entre milhares de plantas

* **Seletor de planta com busca** (em viagem, agenda, filtros e requisitos): digite nome, cidade, estado, país, cliente ou endereço, em qualquer ordem e sem acento; mostra as 40 melhores correspondências com destaque, cidade/UF·país, situação no mapa, navegação por setas/Enter. Testado com 6.000 plantas (resposta em milissegundos; o índice só é refeito quando o cadastro muda).
* **Lista de plantas** (Clientes e plantas): busca grande (atalho `/`), chips com contagem (Todas, Localizadas, Aproximadas, Sem localização, Sem cidade), filtros por cliente/país/estado, ordenação, 50/100/200 por página e abertura da planta com um clique.
* **Identidade visual** (Administração → Identidade visual): logo da página de login e do menu lateral (PNG, JPG, SVG ou WebP até 3 MB; SVG com script é recusado), nome e subtítulo do sistema. Vale na hora para todos; o logo anterior vai para `_historico`; "Remover" volta ao padrão.

## Nova viagem (tela enxuta)

Planta + serviço + técnicos e **três blocos de diárias**: *deslocamento de ida*, *serviço* e *deslocamento de volta* (início/fim de cada um; a tela conta as diárias de cada etapa e o total). Ao escolher a planta, o **mapa mostra onde o técnico vai estar** (e o deslocamento desde a origem do técnico, quando cadastrada). Por baixo, a viagem continua sendo gravada como trechos de ida/retorno e atendimento, então agenda, mapa e conflitos funcionam igual. Viagens antigas com itinerário complexo (vários trechos, trechos por técnico, entre plantas) abrem automaticamente no **modo avançado** (`/viagens/{id}/avancado`), sem perder dados.

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

## Caixas com opção manual

Nenhuma caixa de lista prende o usuário ao que já existe:

- **Fuso, categoria de documento, norma, país, estado e cidade** (formulários e filtros): campo com sugestões que aceita qualquer texto digitado. Nos filtros, o texto casa por "contém", sem acento e sem diferenciar maiúsculas.
- **Serviço e cliente** (viagem, agenda, planta, requisitos): seletor com busca. Se o texto não existe, aparece **“Criar serviço/cliente «texto»”** e o item é cadastrado na hora (sem duplicar nomes iguais; exige a permissão de edição correspondente).
- **Planta**: ao digitar um nome que não existe, **“Usar «texto» como nova planta”** cria a planta e o sistema pede a cidade em seguida, gravando no cadastro.
- **Técnico e especialidade** (filtros): seletor com busca.
- Continuam como lista fechada apenas valores com regra no sistema: status, tipo de evento, meio de transporte, perfil de acesso, ordenação, itens por página e opções da importação.
- Categorias digitadas à mão aparecem na seção **Documentos** do técnico (só as três categorias de treinamento ficam em Treinamentos).

## Indicadores gerenciais (Visão operacional)

Na parte de baixo da Visão operacional há um painel visual para a diretoria, calculado só a partir da agenda e dos cadastros (respeita a visibilidade de cada perfil e funciona no modo apresentação):

- Cartões: ocupação hoje, ocupação média dos próximos 30 dias, dias de campo previstos e % de documentos em dia.
- Situação da equipe agora (rosca), ocupação semanal das próximas 8 semanas (atendimento, deslocamento e rascunho), viagens e dias de campo por mês (6 meses), clientes com mais dias de atendimento (90 dias), saúde documental e meio de transporte.
- A ocupação considera dias úteis (seg–sex); dias de campo = técnico-dias com atendimento ou deslocamento planejado/confirmado. Rascunhos aparecem hachurados e não entram nos cartões.

## Modo automático (apresentação)

Botão **Modo automático** no topo da Visão operacional: abre o mapa em tela cheia e conduz a apresentação sozinho, em ciclo:

0. Velocidade padrão **Normal** é propositalmente calma (≈1,8× o tempo base); há também **Lenta** (≈2,8×) e **Rápida**.
1. O mapa monta a equipe **um local por vez** (com a lista de quem aparece), depois mostra, em sequência: **Disponíveis, Em atendimento, Em viagem, Em férias, Viagens previstas, Conflitos, Docs a vencer/vencidos e Pendências documentais** — cada etapa com o total grande, a lista de técnicos (ou viagens) e o filtro correspondente no mapa.
1b. Depois dos indicadores o ciclo vai para a **Agenda** (próximos 30 dias e visão do mês, com rolagem lenta e constante pela equipe; a etapa dura o que a rolagem precisar) e volta ao mapa, recomeçando com dados atualizados.
2. Controles no canto: **Pausar/Continuar**, velocidade **Lenta / Normal / Rápida** e **Parar** (ou tecla **Esc**). Ao parar, volta ao estado anterior da tela.
3. A cada ciclo os dados são recarregados; o modo ativa o modo apresentação (sem dados sensíveis).

Técnico **sem programação** agora conta como **Disponível** (a ficha avisa que não há programação cadastrada perto da data).

### Configurando a apresentação

**Administração → Apresentação** (Admin e Gestão): marque quais etapas aparecem e reordene com as setas (mapa, cada indicador, **Indicadores gerenciais**, Agenda), escolha quais gráficos entram na tela de indicadores gerenciais, quais visualizações da agenda (30 dias / mês), a velocidade inicial e se o ciclo repete. A configuração vale para todos e fica na base compartilhada.

## Alterações de outubro/2026 (PDF de ajustes)

- **Excluir viagem** (Controladoria e viagens): ícone de lixeira em cada linha; pergunta *"Você tem certeza que quer excluir essa viagem?"* e, no **Sim**, remove a viagem com trechos, atendimentos e anexos (qualquer status). Fica registrado no histórico.
- **Agenda**: saíram as visões **Dia** e **Semana**. A linha do tempo tem período livre **De / Até** (até 3 anos por tela, com rolagem horizontal) e atalhos 14 dias, 30 dias, 3 meses, 6 meses e 1 ano. No Mês, clicar no dia abre a linha do tempo a partir dele.
- **Número do projeto e equipamentos**: obrigatórios na nova viagem (simples e avançada) e no novo compromisso da agenda; equipamentos aceitam 1 ou mais (Enter ou "Adicionar", com sugestões dos já usados). O projeto aparece **sempre**: primeira coluna da lista de viagens, título da viagem, todos os blocos da agenda/mapa ("Proj. 1234 · …"), painel de viagens previstas, apresentação e histórico. A busca da lista procura por projeto e equipamento.
- **Em casa / na Howden**: quem não está em viagem nem em atendimento aparece **em casa** (endereço residencial da ficha, localizado em cascata endereço → cidade → estado → país) ou, se marcado no painel do técnico, **na Howden South America**. Os dois contam como **disponíveis**. O endereço da empresa é configurado em **Administração → Configurações → Endereço da empresa** (pode informar latitude/longitude ou deixar localizar pelo endereço).

## Identidade visual

Aba **Identidade visual** dentro de **Administração** (só para o administrador): troca o **logo da página de entrada** (tela de login) e o **logo do sistema** (barra lateral), além do nome/subtítulo usados quando não há logo. PNG, JPG, SVG ou WebP até 3 MB; vale para todos na hora e "Remover" volta ao padrão.

## Controle das viagens pela equipe de serviços (e-mail de outubro/2026)

- **Lista de verificação em cada viagem** (coluna da direita da viagem): os 17 itens padrão (Solicitação de Serviço (PJ), LGPD, Documentação, Integração, Política de viagem, Adiantamento, Voo, Hotel, Carro, LIBERADO PARA ENTRADA NO CLIENTE, Desenhos no TEAMS, Ordem de Serviço, Itens arquivados no PV, Relatório de Despesas, Relatório Assessoria, POWRA, Cobrança (Rayssa)). Clique no item para marcar como pronto (fica registrado quem e quando); "Adicionar um item" e × para remover. Anel de progresso, mosaico e selo ("Liberado para entrada no cliente", "Tudo pronto!"); a lista de viagens mostra a barra 12/17.
- **Responsável da viagem e "Minhas viagens"**: em Administração → Usuários marque **Equipe de controle de viagens** nas duas usuárias (perfil Controladoria). Cada viagem tem a **Responsável** (trocável a qualquer momento, com histórico) e a Controladoria abre na aba **Minhas viagens**.
- **Documentos da viagem**: botões **Voo/passagem, Hotel, Carro, Outros** (PDF, JPG, PNG ou e-mail salvo .msg/.eml).
- **E-mail da OS** (botão no topo da viagem): para os e-mails dos técnicos do cadastro; assunto *Projeto – VISITA TÉCNICA – Unidade*; saudação com bom dia/boa tarde/boa noite pelo horário; ORDEM DE SERVIÇO – VISITA TÉCNICA – Unidade; endereço do cliente; "A viagem será em dd/mm com retorno previsto para dd/mm" (pelas datas da viagem); Hotel/Carro/Voo, Contato, Escopo do Serviço e Informações adicionais editáveis (ficam guardados na viagem); anexa os documentos escolhidos.
  - **Abrir no Outlook**: cria o e-mail no Outlook do computador onde o sistema está rodando, **com a assinatura padrão** de quem envia (Windows + Outlook instalado).
  - **Baixar e-mail (.eml)**: alternativa; o Outlook abre como e-mail novo com os anexos (a assinatura pode precisar ser inserida em Inserir → Assinatura).
  - Opcionalmente marca "Ordem de Serviço" na lista de verificação; o envio fica no histórico.

## Histórico da viagem

Cada viagem tem a aba **Histórico da viagem** (ao lado de "Viagem"): a equipe escreve tudo o que aconteceu — ocorrência, cliente, técnico, logística (voo/hotel/carro), financeiro ou anotação — com a data/hora em que aconteceu. Fica registrado quem escreveu; só a autora (ou o administrador) edita ou exclui. Na mesma linha do tempo aparecem, se quiser, as alterações automáticas (status, responsável, lista de verificação, e-mail da OS, documentos). Há busca e filtro por tipo. Técnicos veem o histórico das próprias viagens, sem editar.

### Viagem nova já completa

Ao criar uma viagem, a tela já traz a **lista de verificação**, os **documentos da viagem**, a aba **Histórico da viagem** e o botão **E-mail da OS**. O que for marcado, anexado ou escrito antes de salvar é gravado junto com a viagem ao clicar em Salvar; o botão do e-mail salva a viagem e abre o e-mail em seguida.

## Foto do usuário e localização da casa / Howden

- **Foto do usuário**: clique no avatar do topo (ou no cadeado) → **Meu perfil**: enviar/trocar/remover a foto (JPG/PNG) e alterar a senha. O administrador também troca a foto em Administração → Usuários. Sem foto própria, usuário ligado a um técnico usa a foto da ficha.
- **Casa do técnico**: o endereço pode ser digitado inteiro num campo só ("Rua X, 120 - Centro, Sorocaba - SP, 18010-000"): o sistema separa CEP, cidade e UF, tenta rua → texto livre → CEP → cidade → estado → país e só cai no país se nada mais existir. Se ainda assim não ficar exato, **Ajustar no mapa** na ficha: clique no mapa para marcar a casa (nível "manual").
- **Howden South America**: em Administração → Configurações → Endereço da empresa, informe o endereço (ou **Ajustar a posição no mapa**) e salve. Sem coordenadas, quem está "na Howden" não aparece no mapa — a Visão operacional avisa com o link para configurar.

## Desempenho pela VPN

- **Leitura local**: com a base na pasta de rede, as telas leem a **cópia local** (`%LOCALAPPDATA%\ControleTecnico\espelho`) e a memória; a pasta compartilhada é conferida **em segundo plano** a cada 15 s (`Data:SincronizarSegundos`). Gravações continuam indo direto ao servidor. Alterações de outras pessoas aparecem em até ~15 s.
- **Inicialização**: carrega todas as tabelas da cópia local em paralelo; só a primeira vez num computador baixa tudo (cópias em paralelo).
- **Anexos, fotos e logos**: cada arquivo é baixado uma vez e guardado em `%LOCALAPPDATA%\ControleTecnico\arquivos_cache`.
- **Menos arquivos**: a compactação roda a cada hora e consolida tabelas com 20+ arquivos.
- **Translados no meio da viagem** (opcional): na viagem, o botão **Adicionar translado** cria "volta para casa" e "retorno à planta" (pode adicionar quantos precisar e trocar o sentido ou remover cada um). O serviço é **dividido sozinho** nessa pausa (da saída para casa até a chegada de volta à planta), então não há conflito de agenda; as horas previstas/realizadas ficam na primeira parte. Ao reabrir a viagem, o serviço aparece inteiro (início da primeira parte ao fim da última) e os translados continuam no formulário simples. Translados em branco são ignorados.
- **Cliente (contratante) e cliente final**: na viagem há o campo **Cliente (contratante)** — opcional, para quando quem comprou é outro cliente (ex.: um contratista) — e o **Cliente final (planta)**, que é a planta onde o técnico vai (o antigo campo "Planta"; o cliente final é o dono dessa planta). Em Controladoria e viagens a coluna "Cliente final / destino" mostra "via contratante" quando houver, e o filtro e a busca por cliente acham a viagem pelos dois.
- **Feriados na Agenda**: os dias de feriado aparecem em **rosa** (nome do feriado no dia, na linha do tempo e no mês; passe o mouse para ver). Os **nacionais** entram sozinhos, inclusive os móveis (Carnaval, Sexta-feira Santa e Corpus Christi, calculados pela Páscoa). No botão **Feriados** da Agenda dá para ver a lista do ano e **cadastrar** estaduais, municipais ou da empresa — só naquele dia ou **repetindo todo ano** — e excluir os cadastrados. É só marcação visual: não bloqueia a agenda dos técnicos.
- **Agenda — linha do tempo**: por padrão os **técnicos ficam nas colunas (eixo x)** e os **dias nas linhas (eixo y)**, com o dia e os técnicos fixos ao rolar; arrastar e soltar, períodos livres, conflitos e a linha de "agora" continuam. O botão **Técnicos nas linhas / nas colunas** troca os eixos.
- **Tipos de compromisso**: em Agenda → Novo compromisso, ao lado de **Tipo** há o botão **Novo tipo** para criar itens próprios (ex.: Treinamento, Integração, Exame médico). O tipo criado já fica selecionado, aparece também nas indisponibilidades da ficha do técnico e o compromisso aparece na agenda com esse nome (o técnico fica ocupado, como num bloqueio). Tipos criados podem ser excluídos no mesmo lugar (×), desde que nenhum compromisso use o tipo.
- **Técnicos**: a lista mostra só os **ativos** por padrão; o botão **Ver inativos (n)** troca para os inativos (clique de novo para voltar).
- **Perfil de quem viaja** (ficha do técnico → Dados gerais): *Técnico de campo* (padrão, sempre na disponibilidade), *Viaja esporadicamente*, *Engenharia*, *Terceiros* e *Fábrica*. Os quatro últimos **não aparecem como disponíveis**: só entram na Visão operacional, no mapa, nas contagens e na Agenda quando estão em **viagem ou atendimento**. O cadastro e o histórico continuam e eles podem ser escalados em qualquer viagem (aparecem no fim da lista, com a etiqueta do perfil). Em Técnicos há filtro por perfil, e em "Quem pode atender?" eles entram só marcando a opção.
- **Controladoria e viagens**: o filtro de técnicos mostra só os ativos; o botão **Somente inativos** troca para os inativos (e as viagens deles). A tela abre sempre em **Todas as viagens**: quem é da controladoria vê todas, independente de quem criou ("Minhas viagens" é só um atalho).
- **Serviços e especialidades**: na ficha do técnico (Especialidades e serviços), desmarcar tira só daquele técnico; a **lixeira** exclui do cadastro e de todos os técnicos (serviço já usado em viagem/requisito fica só desativado, para o histórico).
- **Usuários** (Administração): botões **Editar** e **Excluir** (com confirmação; não exclui a si mesmo nem o último administrador; o histórico continua). A aba "Dados" não aparece mais.
- **Publicar na rede** (para todos usarem, sem precisar do .NET instalado): `powershell -ExecutionPolicy Bypass -File .\publicar.ps1` compila um único **ControleTecnico.exe com o ícone da Howden** e publica em `\\BZVCPFIL003\proj_ramires$\Controletec` (outra pasta: `-Destino "..."`). Rode de novo depois de cada `git pull` com novidades.
  - Na pasta da rede ficam: `ControleTecnico.exe`, `appsettings.json`, `wwwroot\`, `versao.txt`, `LEIA-ME.txt` e `Criar atalho na Area de Trabalho.cmd` (cada pessoa roda uma vez para ter o atalho com o ícone).
  - Quem abre o .exe da rede recebe uma **cópia da versão no próprio computador** (`%LOCALAPPDATA%\ControleTecnico\versoes`, só na primeira vez de cada versão; guarda a atual e a anterior) e é a cópia que roda: abre rápido mesmo pela VPN e a pasta da rede fica livre para publicar com o sistema aberto nos outros computadores. Alterar o `appsettings.json` da rede também gera uma cópia nova.
  - **Sem janela preta**: publicado como aplicativo de janela (`OutputType=WinExe`), o servidor roda escondido. O navegador abre na hora numa **tela de carregamento** (logo da Howden, barra de progresso e etapa atual: cópia da versão, conexão com a base, carga das tabelas) e entra sozinho no sistema quando ele fica pronto. Sem acesso à base (VPN desconectada), a tela mostra o aviso e tenta de novo a cada 20 s (ou no botão **Tentar de novo**). O que aparecia na janela preta fica em `%LOCALAPPDATA%\ControleTecnico\logs`.
  - **Fecha sozinho**: quando o sistema é fechado no navegador (todas as abas), o servidor encerra em ~15 s; recarregar a página não fecha. Se o navegador for encerrado à força, fecha após 10 min sem nenhuma janela. Desligar: `EncerrarAoFecharNavegador=false`.
  - Clicar de novo no atalho com o programa aberto só abre o navegador; se a versão aberta for a anterior (acabou de publicar), ela é fechada e a nova abre. A versão aparece no rodapé do menu e, quando uma versão nova é publicada, aparece o aviso **"Nova versão publicada"** (feche o sistema no navegador e abra de novo pelo atalho).
  - O ícone vem de `howden.ico` (logo completo nos tamanhos grandes, emblema nos pequenos), definido em `ApplicationIcon` no `.csproj`.
  - `dotnet run` continua para desenvolvimento (recompila a cada abertura).
- Para voltar ao modo antigo: `Data:SincronizarEmSegundoPlano=false`.

## Mapa assertivo (precisão das posições)

- O serviço de mapas informa a **precisão real** do que encontrou (rua, bairro/cidade, estado, país). Um resultado mais vago do que o pedido **não vale** como endereço — antes, quando o endereço não era achado, o serviço devolvia "Brasil" e o ponto caía no centro do país (Mato Grosso).
- **Nunca vão ao mapa** posições só de estado ou país. Posição pela **cidade/CEP** aparece com o marcador **≈ (aproximada)** e aviso no balão. Exata = endereço encontrado, link do Google Maps/coordenadas ou marcada no mapa.
- **Howden South America**: só entra no mapa com posição **exata** (Administração → Configurações → Endereço da empresa → cole o link do Google Maps ou clique no mapa).
- **Endereço da casa (e das plantas)**: o campo é separado em **logradouro, número e bairro** ("Rua Paraíba, 244 Jardim Tarumã" → rua "Rua Paraíba", nº 244, bairro "Jardim Tarumã"). A busca tenta rua + número → só a rua (o número quase nunca está no mapa) → texto livre → **Photon** (segundo serviço do OpenStreetMap, que aceita abreviações; só vale se a rua, a cidade e o estado conferirem) → CEP → **bairro** → cidade. Se o Nominatim recusar ou não responder, a busca continua pelo Photon; se nenhum serviço responder, a ficha diz que é problema de acesso (internet/VPN/proxy), não do endereço. O acesso usa o proxy do Windows com o login do usuário.
- **Localização automática das plantas**: plantas importadas ou cadastradas sem localização não dependem mais do botão "Localizar pendentes" nem de abrir uma por uma. Um serviço em segundo plano (começa 2 min depois de abrir o programa e confere a cada 10 min) procura sozinho as pendentes ainda não consultadas — **primeiro as usadas em viagens e compromissos** —, 1 consulta por segundo. Quando a busca de endereços melhora (versão nova), tenta de novo as pendentes e reconfere as aproximadas uma vez; as que não forem achadas são tentadas de novo só depois de 30 dias. Com vários computadores abertos na mesma base, só um faz o trabalho (reserva gravada na base); quem clicar em **Parar** é respeitado por 6 horas; se o serviço de mapas não responder, pausa 1 hora. A gravação não desfaz edições feitas na planta durante a rodada. Na lista, "Sem localização" mostra o motivo (ainda não consultada, não encontrada, sem cidade…). Desligar: `Geocoding__Automatico=false`; intervalo: `Geocoding__AutomaticoMinutos`.
- Na ficha do técnico, **Localizar pelo endereço** usa o que está digitado (grava antes de buscar) e mostra na tela o que foi encontrado ou por que não foi; se ficar aproximado ou não achar, já abre o campo para colar o link do Google Maps.
- **Confirmar localização** tem os atalhos **Na Howden South America** e **Em casa**: a posição vem sempre do cadastro atual (endereço exato da empresa / casa da ficha) e o técnico também passa a "fica na Howden"/"fica em casa". Confirmações antigas só em texto com o nome da empresa seguem a mesma posição. Enquanto a Howden não tiver posição exata, a Visão operacional mostra um aviso acima do mapa com quantos técnicos estão fora dele, e o painel do técnico mostra o botão **Definir a posição exata da Howden**.
- **Casa do técnico**: ficha → "Definir a posição exata" (link do Google Maps, coordenadas ou clique). Posições só de estado/país são descartadas.
- **Plantas**: no cadastro, campo para colar o link do Google Maps além do mapa; busca também pelo **nome** do local quando não há endereço. Em Clientes e plantas, **Reconferir aproximadas** busca de novo o endereço exato das plantas posicionadas pela cidade (nunca piora uma posição). Filtro "Endereço exato / Aproximadas / Sem localização".
- **Revisão automática** (uma vez): plantas e casas gravadas com posição só de estado/país voltam a pendente. Confirmações e trechos de viagem usam sempre a posição atual (corrigida) da planta.
- Como pegar a posição exata no Google Maps: clique com o botão direito no ponto exato → clique nas coordenadas (copia) → cole no sistema. Ou "Compartilhar → Copiar link".
