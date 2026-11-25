# Plano técnico consolidado

**Status:** v2, proposta para o Victor (2026-11-24). Nenhum código foi escrito ainda. A v1 foi revisada pelos agentes `arquiteto` e `seguranca` (coordenação via Ruflo); os achados estão incorporados.
**Base:** `docs/business-rules/` (A e B), `docs/desenvolvimento/qualidade.md` (C).

Princípio central: **o modelo de IA sugere, o backend decide.** Toda garantia sobre o comportamento do chatbot vem de código determinístico e de restrições do banco, testadas e auditadas. Nenhuma depende do prompt.

---

## 1. Visão geral

```
WhatsApp Cloud API
      │  webhook (assinatura sobre corpo bruto, wamid único)
      ▼
┌────────────── Backend Seceletesc (.NET 10) ──────────────────┐
│ Entrada ──► Conversas (máquina de estados, versão do estado) │
│                │ estado = Automatico?                        │
│                ▼                                             │
│           Montador de contexto ── filtra: este cliente,      │
│                │                  aprovado, vigente,         │
│                │                  classificação permitida,   │
│                │                  minimização de dados       │
│                ▼                                             │
│           Motor de IA (projeto isolado) ── sem banco,        │
│                │  rascunho em schema estrito                 │
│                ▼                                             │
│           Validador (determinístico)                         │
│             ├─ reprovado → motivo registrado → supervisão    │
│             └─ aprovado → decisão + outbox (mesma transação, │
│                           FOR UPDATE na conversa)            │
│                ▼                                             │
│           Worker do outbox ── trava de novo, revalida tudo,  │
│                │              lease, sem retry ambíguo       │
│                ▼                                             │
│           Gateway ── única credencial de envio               │
│                                                              │
│ Supervisão (API) ── assumir, responder, orientar (→ REV),    │
│                     liberar; tudo sai pelo outbox/gateway    │
│ Conhecimento ── itens, versões imutáveis, ciclo de vida      │
│ Política por empresa ── versões imutáveis                    │
│ Histórico ── eventos append-only                             │
└──────────────────────────┬───────────────────────────────────┘
                           ▼
              PostgreSQL (RLS + papéis separados)
```

## 2. Tecnologias

| Item | Escolha | Motivo |
|---|---|---|
| Runtime | .NET 10 (LTS), ASP.NET Core | LTS até 2028; SDK já instalado |
| Banco | PostgreSQL 17+ | decidido; RLS |
| Acesso a dados | EF Core + Npgsql | filtros globais por tenant como segunda camada |
| Testes | xUnit, Testcontainers | PostgreSQL real nos testes de isolamento e concorrência |
| Testes de arquitetura | NetArchTest (ou equivalente) | fronteiras entre projetos |
| Dev local | Docker Compose | Docker já instalado |
| CI | GitHub Actions | repositório no GitHub |
| Logs | `Microsoft.Extensions.Logging` com redação | sem conteúdo de mensagem nem telefone. OpenTelemetry fica para depois |

## 3. Estrutura da solução

Arquitetura em camadas, com o motor de IA isolado:

```
src/
  Seceletesc.Dominio/          estados, validador, política, conhecimento, contratos (sem dependências)
  Seceletesc.Aplicacao/        casos de uso
  Seceletesc.Infraestrutura/   EF Core, outbox, worker, gateway WhatsApp
  Seceletesc.MotorIA/          adaptador do provedor; depende só dos contratos do Dominio
  Seceletesc.Api/              webhook, API de supervisão, API de conhecimento/política
tests/
  Seceletesc.Dominio.Testes/       unitários + regras CTX/ADV sem I/O
  Seceletesc.Integracao.Testes/    PostgreSQL real: RLS, pool, append-only, concorrência, outbox
  Seceletesc.Adversarial.Testes/   pipeline completo com motor de IA simulado e malicioso
  Seceletesc.Arquitetura.Testes/   MotorIA não referencia Infraestrutura; só o gateway tem credencial de envio; pipeline não chama aprovação de conhecimento
```

## 4. Como cada garantia é imposta

### 4.1 Máquina de estados

| Estado | Quem fala com o cliente |
|---|---|
| `Automatico` | IA, via validador |
| `TransferenciaPendente` | só o aviso de transferência (caminho próprio, 4.3) |
| `Humano` | só o humano. Inclui "aguardando supervisão" após bloqueio |

- `Automatico → TransferenciaPendente → Humano` (bloqueio, impasse ou assunto que exige humano).
- `qualquer → Humano` (humano assume).
- `Humano → Automatico` só por liberação explícita na API de supervisão.
- Toda transição é feita com `SELECT ... FOR UPDATE` na conversa, incrementa a **versão do estado** e grava um evento.
- **Retomada:** por padrão, a IA só considera mensagens recebidas **após** a liberação. Configurável por política (lacuna funcional registrada).

### 4.2 Montador de contexto (CTX-007, invariantes 10, 11, 20)

O modelo só vê o que pode aparecer numa resposta automática. Assim, o validador não precisa "pegar" vazamento de conteúdo que nunca foi entregue ao modelo.

- Histórico: só **desta** conversa.
- Conhecimento: só itens **aprovados**, **vigentes**, com escopo desta empresa/cliente/proposta/atendimento **e** classificação permitida para resposta automática pela política vigente. INT, NEG, EXC e RES ficam fora por padrão.
- **Dados protegidos** (F1): na entrada, um detector identifica dados sensíveis (CPF, CNPJ, RG, cartão com verificação Luhn, e-mail, CEP/endereço; lista configurável). O valor original é gravado **criptografado** (AES-GCM, chave fora do banco: user-secrets em dev, cofre de chaves em produção) e o texto que segue no pipeline e vai ao modelo contém só um marcador (`[DADO PROTEGIDO:CPF]`). O papel `app` não tem a chave de leitura; só a API de supervisão, para usuário com permissão. Limite: o que o detector não reconhecer segue como texto. O conjunto de testes de detecção cresce com casos reais.
- Telefone e identificadores do cliente não vão ao modelo (minimização).
- Campos do WhatsApp fora do texto (`profile.name`, legendas, mensagens citadas) não vão ao modelo, ou vão marcados como não confiáveis.
- O montador registra no histórico quais itens (id + versão) entregou ao modelo.

### 4.3 Validador de envio (invariantes 6 a 9, 11 a 13)

Rascunho do modelo em **schema estrito** com limite de tamanho: `texto`, `fontes citadas (id + versão)`, `assunto declarado`. Inválido = bloqueio.

O texto é normalizado (Unicode NFKC) antes das regras. Para na primeira falha:

1. Estado = `Automatico` e versão igual à lida no início.
2. Assunto declarado ∈ assuntos autônomos da política vigente **e** coincide com o assunto das fontes citadas.
3. Ao menos uma fonte citada. Toda fonte citada foi entregue pelo montador **nesta** rodada, está aprovada e vigente (consulta direta ao banco).
4. Todo valor crítico no texto (preço, percentual, data, horário) aparece numa fonte citada. Termos de compromisso sem número ("aprovado", "garantido", "confirmado", "amanhã"; lista mantida na política) também exigem fonte que os contenha. Na dúvida, bloqueia.
5. Padrões proibidos: autodeclaração como humano, menu numerado, elementos interativos, **qualquer URL** fora da allowlist da política.
6. Nenhum número de telefone diferente do cliente atual. O isolamento de dados de outros clientes é estrutural (4.2), não detectado por lista de nomes.

Reprovado → nada é enviado; o motivo é registrado; a conversa vai para `TransferenciaPendente`; **nunca há nova geração**.

**Aviso de transferência:** texto fixo da política, não gerado. Caminho próprio no validador: exige estado `TransferenciaPendente` com a versão esperada e passa pelo outbox como qualquer mensagem. Se o humano já assumiu, não sai.

**Esclarecimento:** desligado por padrão (`permite_esclarecimento = false` em todo assunto). Bloqueio vai sempre para supervisão. Como gerar um esclarecimento, quando permitido, continua como lacuna funcional.

Limite assumido: pertinência fina e tom são julgamento do modelo. Esse risco é coberto pelos testes de Avaliação e pela exigência de que tudo que for factual esteja numa fonte.

### 4.4 Outbox, worker e gateway (invariantes 5 e 6, CTX-010/011)

- **Restrição no banco:** toda mensagem automática no outbox referencia (FK obrigatória) uma decisão `aprovada` com a mesma versão do estado, verificada por trigger. Mensagens humanas referenciam a ação humana registrada. Código que pular o validador não consegue gravar.
- **Worker**, para cada mensagem, numa transação:
  - `FOR UPDATE` na conversa, a mesma trava da tomada de controle humana;
  - revalida estado e versão, status e validade das fontes citadas, janela de 24h e prazo de validade (TTL) da mensagem;
  - qualquer falha cancela a mensagem e registra o motivo; mensagem automática vencida nunca é enviada.
- **Sem envio duplicado:** a mensagem passa a `enviando` com lease antes da chamada HTTP. Resultado ambíguo (timeout) vira `incerto` e **não** é repetido automaticamente; a supervisão é avisada. Preferimos o risco de não enviar ao de enviar duas vezes.
- **Gateway:** único componente com a credencial de envio (teste de arquitetura). Rejeita mensagem interativa, template com botões e texto livre fora da janela de 24h.

### 4.5 Isolamento entre empresas e clientes (invariante 10)

**Opções avaliadas:**

| Opção | Prós | Contras |
|---|---|---|
| Esquema compartilhado + `tenant_id` + **RLS** (escolhida) | uma migração; o banco recusa consulta cruzada | exige disciplina de papéis e `SET LOCAL` |
| Schema por tenant | separação física | migrações N vezes; pool e roteamento complexos |
| Banco por tenant | isolamento máximo | custo e operação altos para uma empresa |

**Como o RLS é aplicado:**

- Três papéis no banco: `migrador` (dono das tabelas), `app` e `worker`. Nenhum dos dois últimos é dono das tabelas. `FORCE ROW LEVEL SECURITY` em todas as tabelas de negócio.
- Tenant definido com `SET LOCAL app.tenant_id` **dentro de cada transação**, para não vazar pela conexão reaproveitada do pool. Política com `current_setting('app.tenant_id')` **sem** `missing_ok`: sem tenant definido, a consulta falha em vez de devolver tudo.
- Resolução do tenant pelo `phone_number_id` do webhook: função `SECURITY DEFINER` mínima que devolve só o id. É o único caminho antes do RLS.
- Worker: lê a fila por função mínima que devolve `(id, tenant_id)` e processa cada mensagem numa transação com `SET LOCAL` do tenant dela.
- Filtro global do EF Core como segunda camada.
- **Isolamento por cliente** não é feito pelo RLS. Ele é imposto pela aplicação (montador de contexto e consultas por `cliente_id`) e coberto por testes de integração dedicados.
- Cliente identificado pelo telefone (`wa_id`) dentro do tenant. Número compartilhado ou reciclado fica registrado como lacuna funcional.

### 4.6 Conhecimento e política (invariantes 16 a 21)

- `item_conhecimento` + `versao_conhecimento` imutável. Classificação, escopo, validade, permissões e status são colunas independentes.
- O pipeline só cria sugestões REV. Aprovar, editar, rejeitar, restringir e revogar existem só na API de supervisão, com usuário humano autenticado.
- Orientação interna vira automaticamente uma sugestão REV, com origem na conversa.
- **Política por empresa**, com versões imutáveis. Cada versão contém:
  - assuntos autônomos e assuntos que exigem humano;
  - classificações permitidas em resposta automática;
  - `permite_esclarecimento` por assunto;
  - texto do aviso de transferência;
  - termos de compromisso;
  - allowlist de URLs;
  - minimização de dados.
- Antes do painel (F6), a política é editada pela API de supervisão, e cada versão é auditada.

### 4.7 Histórico (invariante 15)

- Eventos append-only. `app` e `worker` não têm `UPDATE`, `DELETE` nem `TRUNCATE`, e um trigger bloqueia as três operações.
- Cada decisão registra a versão da política, os itens entregues ao modelo e as fontes citadas.
- Histórico e base de conhecimento ficam em tabelas separadas.

### 4.8 WhatsApp (F4)

- Assinatura `X-Hub-Signature-256` calculada sobre o **corpo bruto**, com comparação em tempo constante; limite de tamanho do corpo.
- `wamid` com `UNIQUE` persistente, o que dá idempotência contra reenvio.
- Eventos de status separados das mensagens; ordenação por timestamp; processamento serializado por conversa.
- Mensagens que não são texto (mídia, áudio, reação, edição): por padrão vão para supervisão, e a IA trata só texto. Lacuna funcional registrada.
- **Sem templates pela API** (decisão do Victor). Fora da janela de 24h, o gateway bloqueia, a conversa recebe a marcação "retomada humana" e aparece no painel com status e resumo interno.
- **Resposta humana pelo celular** exige que o número opere em modo de coexistência (WhatsApp Business App + Cloud API). As mensagens enviadas pelo app chegam como eventos de eco no webhook: cada eco é registrado no histórico e coloca a conversa em `Humano` imediatamente. **Verificar na F4** a disponibilidade da coexistência para o número e se o eco cobre todos os casos; sem isso, a resposta humana teria de passar pela API.

### 4.9 API de supervisão

- O tenant da requisição vem do usuário autenticado e é aplicado via `SET LOCAL`. Toda operação (assumir, responder, orientar, liberar, aprovar) confere que a conversa ou o item pertence a esse tenant.
- Testes de acesso direto por id (IDOR) entre empresas.

### 4.10 Segurança do repositório e dados

- **Repositório público:**
  - nenhum segredo no Git;
  - user-secrets em dev e variáveis de ambiente em produção;
  - gitleaks no CI.
- **Configurações do GitHub** (precisam do Victor ou do `gh`):
  - secret scanning e push protection, gratuitos em repositório público;
  - proteção da `master` exigindo **só o CI verde**, sem revisão humana.
- **CI:**
  - `permissions: read` por padrão;
  - actions fixadas por SHA;
  - gatilho `pull_request` (nunca `pull_request_target`);
  - sem segredos em execuções de fork.
- **Logs e dados:**
  - logs sem conteúdo de mensagem; telefone só em hash;
  - `EnableSensitiveDataLogging` desligado, com teste que garante isso;
  - redação de dados em exceções;
  - dados de teste só sintéticos; nada real na memória do Ruflo.

## 5. Fases

Cada fase termina com CI verde, os testes de regra da fase cobertos e uma entrada no QA do piloto.

| Fase | Entrega | Depende do Victor |
|---|---|---|
| **F0 Fundação** | solução; CI endurecido; Docker Compose; papéis do banco; RLS com `SET LOCAL` e testes de pool; histórico append-only; checagem automática de cobertura das regras; gitleaks | ativar secret scanning e proteção da branch (ou autorizar instalar o `gh`) |
| **F1 Núcleo** | modelo de conhecimento e política versionada; dados protegidos (detecção + criptografia + marcador); montador de contexto com minimização; máquina de estados; validador; outbox, worker e restrições; gateway simulado; adversariais ADV-01 a ADV-23 com motor de IA simulado | — |
| **F2 Supervisão (API)** | assumir, responder, orientar (→ REV), liberar, editar política; checagem de tenant e testes de IDOR | autenticação e usuários (em dev, chave de API) |
| **F3 Conhecimento (API)** | ciclo de vida completo | papéis de aprovação (em dev, papel único) |
| **F4 WhatsApp real** | webhook, envio, janela de 24h + marcação de retomada, coexistência e ecos do app | app Meta + número de teste (**infra externa**) |
| **F5 Motor de IA** | adaptador do provedor, resumo interno da conversa, avaliação | escolha do provedor |
| **F6 Painel** | interface de supervisão, conhecimento e política | tecnologia do front, usuários |
| **F7 Piloto** | primeira empresa | assuntos autônomos, textos fixos, templates |

F0 a F3 não dependem de decisão pendente nem de infraestrutura externa. Todas as garantias do chatbot ficam prontas e testadas antes de existir IA ou WhatsApp de verdade.

## 6. Riscos

| Risco | Mitigação |
|---|---|
| Detector de valores e compromissos deixa passar um formato | na dúvida bloqueia; o conjunto adversarial cresce a cada caso real |
| Bloqueio em excesso deixa o atendimento pouco útil | métricas de bloqueio por motivo; humanos ajustam a política |
| Corrida entre humano assumindo e envio em andamento | trava única (FOR UPDATE) na decisão, no worker e na tomada de controle; janela residual = só a chamada HTTP |
| Timeout ambíguo no envio | `incerto` sem repetição; supervisão avisada |
| Mensagem parada no outbox enquanto a fonte é revogada | worker revalida fontes, com TTL |
| Repositório público | gitleaks, push protection, nenhum dado real |

## 7. Pendências que afetam o plano

Ver `docs/project-context.md`. Afetam diretamente:

- F1: categorias de dados protegidos (padrão técnico definido; o Victor pode ampliar).
- F4: coexistência do número (app + API).
- F5: provedor de IA (desbloqueada: dados delimitados).
- F2, F3 e F6: autenticação, usuários e papéis.
- F7: assuntos autônomos e textos fixos.
- Lacunas com padrão conservador já definido no plano (o Victor pode mudar):
  - retomada não responde mensagens anteriores à liberação;
  - mensagens que não são texto vão para supervisão;
  - esclarecimento desligado;
  - número compartilhado ou reciclado.
- Hospedagem de produção (infraestrutura externa).
