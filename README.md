# Controle Técnico

Sistema web de controle de **viagens, agenda, disponibilidade e documentação dos técnicos**, construído com a
**mesma stack, arquitetura e identidade visual do projeto Previsão** (`daianemuller0/previsao`): C# / .NET 8 / Blazor Server,
persistência em **Parquet consolidado por DuckDB** (sem servidor de banco), ClosedXML para Excel, login por cookie com perfis e o
mesmo design system CSS (`wwwroot/app.css` herdado + `wwwroot/tecnico.css`).

## Como executar

```bash
dotnet run                  # requer .NET 8 SDK; abre em http://localhost:5090
dotnet test Tests           # 22 testes de regras de negócio
```

Na primeira execução com a base vazia, `Seed:Demo=true` (padrão do `appsettings.json`) carrega **dados fictícios** e exibe a faixa
**AMBIENTE DE DEMONSTRAÇÃO**. Usuários de demonstração (senha `demo2026`): `admin`, `gestao`, `controladoria`, `carlos` (técnico), `consulta`.

Para uso real: `Seed__Demo=false` e defina `Seed__AdminSenha` (ou uma senha aleatória é gerada e impressa no console).
Em *Administração → Dados* é possível apagar os dados fictícios.

Dados e anexos ficam em `Data:Folder` (padrão `./data`; pode ser caminho de rede/volume persistente):
`<entidade>/*.parquet` e `arquivos/` (anexos com nome opaco, acessados só por `/arquivos/{id}` autenticado).

## Telas

| Rota | Tela |
|---|---|
| `/` | **Visão operacional**: mapa (Leaflet), indicadores clicáveis, filtros, data/hora de previsão, painel do técnico, modo apresentação |
| `/agenda` | **Agenda**: linha do tempo por técnico, mês, semana, dia; criar/editar; arrastar e soltar com validação; conflitos; períodos livres |
| `/clientes` | **Clientes e plantas**: cadastros, localização no mapa (corrigível), importação `.xlsx/.csv` em 4 passos |
| `/viagens`, `/viagens/{id}` | **Controladoria e viagens**: lista, editor com trechos (carro/avião), atendimentos, tempos, verificação, anexos |
| `/tecnicos`, `/tecnicos/{id}` | **Técnicos**: equipe, vencimentos, requisitos e aptidão, ficha com 8 seções (docs/treinamentos com anexos e renovação) |
| `/admin` | Usuários/perfis, parâmetros, integrações, histórico de alterações, dados de demonstração |

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
| Camada do mapa | `Mapa__TileUrl`, `Mapa__Atribuicao` | Usa o servidor público do OpenStreetMap (adequado só a baixo volume; em produção use provedor com chave ou instância própria) |

## Limitações e pendências reais

* **Geocodificação e rotas não foram exercitadas contra os serviços reais** (o ambiente de desenvolvimento não tinha acesso externo): o código está implementado, mas a integração fica desativada até a configuração; o estado "pendente" foi testado.
* Os tiles do mapa vêm da internet; sem acesso o mapa aparece sem fundo, mas marcadores, rotas e listas funcionam.
* Posição durante a viagem **não** é rastreada (por requisito); não há GPS.
* Arrastar e soltar altera a **data** (dias); mudar o horário ou o técnico é feito no formulário/viagem.
* Reagendar um bloco de viagem permite mover só o bloco ou a viagem inteira (padrão: inteira, para manter a viagem coerente).
* O cálculo de viabilidade de deslocamento entre compromissos sem trecho **sinaliza "verificar"** (com distância em linha reta como referência); não calcula tempo de percurso sem integração configurada.
* Persistência em Parquet é pensada para equipe pequena/média e uma instância do servidor (cache em memória por processo). Não há controle de edição concorrente entre instâncias.
* Notificações por e-mail de vencimento não foram implementadas (os alertas aparecem na ficha, na visão operacional e em *Técnicos → Vencimentos*, com exportação CSV).
* Sem renderização automática de testes de interface em CI; os testes automatizados cobrem regras de negócio e persistência (22 testes).
