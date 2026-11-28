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

- Histórico: só **desta** conversa, as 20 mensagens mais recentes (parâmetro técnico; revisar na F5).
- Conhecimento: só itens **aprovados**, **vigentes**, com escopo desta empresa/cliente/proposta/atendimento **e** classificação permitida para resposta automática pela política vigente. INT, NEG, EXC e RES ficam fora por padrão.
- **Dados protegidos** (F1): na entrada, um detector identifica dados sensíveis (CPF, CNPJ, RG, cartão com verificação Luhn, e-mail, CEP/endereço; lista configurável). O valor original é gravado **criptografado** (AES-GCM, chave fora do banco: user-secrets em dev, cofre de chaves em produção) e o texto que segue no pipeline e vai ao modelo contém só um marcador (`[DADO PROTEGIDO:CPF]`). O papel `app` não tem a chave de leitura; só a API de supervisão, para usuário com permissão. Limite: o que o detector não reconhecer segue como texto. O conjunto de testes de detecção cresce com casos reais.
- Telefone e identificadores do cliente não vão ao modelo (minimização).
- Campos do WhatsApp fora do texto (`profile.name`, legendas, mensagens citadas) não vão ao modelo, ou vão marcados como não confiáveis.
- O montador registra no histórico quais itens (id + versão) entregou ao modelo.

### 4.3 Validador de envio (invariantes 6 a 9, 11 a 13)

Rascunho do modelo em **schema estrito** com limite de tamanho: `texto`, `fontes citadas (id + versão)`, `assunto declarado`. Inválido = bloqueio.

O texto é normalizado antes das regras: NFKC, remoção de todo caractere de formatação, dígitos em ASCII e sem acentos. As regras param na primeira falha, e tempo esgotado numa expressão regular conta como bloqueio:

0. A resposta só contém caracteres do português: ASCII, letras do Latin-1, pontuação tipográfica e emoji. Isso fecha homóglifos de outros alfabetos, como o cirílico.

1. Estado = `Automatico` e versão igual à lida no início.
2. Assunto declarado ∈ assuntos autônomos da política vigente **e** coincide com o assunto das fontes citadas.
3. Ao menos uma fonte citada. Toda fonte citada foi entregue pelo montador **nesta** rodada, está aprovada e vigente (consulta direta ao banco).
4. Todo número do texto, comparado junto com a sua unidade ("18h" ≠ "18 vezes"; "150,00" ≠ "150.000,00"), e todo número por extenso aparecem numa fonte citada. Termos de compromisso sem número ("aprovado", "garantido", "confirmado", "amanhã"; lista mantida na política) também exigem fonte que os contenha. Na dúvida, bloqueia.
5. Padrões proibidos: autodeclaração como humano, menu numerado, elementos interativos, **qualquer URL** fora da allowlist da política.
6. Nenhum número de telefone. O modelo nunca recebe telefones, então nenhum telefone pode sair numa resposta automática. O isolamento de dados de outros clientes é estrutural (4.2), não detectado por lista de nomes.

Reprovado → nada é enviado; o motivo é registrado; a conversa vai para `TransferenciaPendente`; **nunca há nova geração**.

**Aviso de transferência:** texto fixo da política, não gerado. Caminho próprio no validador: exige estado `TransferenciaPendente` com a versão esperada e passa pelo outbox como qualquer mensagem. Se o humano já assumiu, não sai.

**Esclarecimento:** desligado por padrão (`permite_esclarecimento = false` em todo assunto). Bloqueio vai sempre para supervisão. Como gerar um esclarecimento, quando permitido, continua como lacuna funcional.

Limite assumido: pertinência fina e tom são julgamento do modelo. Esse risco é coberto pelos testes de Avaliação e pela exigência de que tudo que for factual esteja numa fonte.

### 4.4 Outbox, worker e gateway (invariantes 5 e 6, CTX-010/011)

- **Restrição no banco:** toda mensagem automática no outbox referencia uma decisão `aprovada` da mesma conversa, com a mesma versão do estado, para o mesmo texto (hash) e com tipo compatível com o estado (resposta só em Automatico; aviso só em TransferenciaPendente), verificada por trigger. O banco não prova que o validador rodou, porque ele é código da aplicação; prova que só entra na fila o que tem decisão aprovada e auditável.
- **Máquina de estados no banco:** fora da supervisão humana, a única transição permitida é Automatico → TransferenciaPendente; assumir e liberar são exclusivos do papel de supervisão. Tenant e cliente da conversa são imutáveis.
- **Supervisão acionada pelo worker:** ao cancelar uma resposta, em falha definitiva ou em envio incerto, o worker interrompe a automação (função mínima `transferir_para_supervisao`).
- **Worker**, para cada mensagem, numa transação:
  - `FOR UPDATE` na conversa, a mesma trava da tomada de controle humana;
  - revalida estado e versão, status e validade das fontes citadas, janela de 24h e prazo de validade (TTL) da mensagem;
  - qualquer falha cancela a mensagem e registra o motivo; mensagem automática vencida nunca é enviada.
- **Sem envio duplicado:** a mensagem passa a `enviando` com lease (60 s) antes da chamada HTTP, que tem limite de 30 s. Resultado ambíguo (timeout) vira `incerto` e **não** é repetido automaticamente; a supervisão é avisada. Preferimos o risco de não enviar ao de enviar duas vezes.
- **Gateway:** único componente que chama a Meta (teste de arquitetura); a credencial de cada empresa é decifrada por `RepositorioCredenciaisWhatsApp` só no momento do envio (M1). Rejeita mensagem interativa, template com botões e texto livre fora da janela de 24h.

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
- **Implementado na F4 (sem número real):**
  - `POST /whatsapp/webhook`: assinatura sobre o corpo bruto (HMAC-SHA256 com o App Secret, tempo constante), limite de 1 MB, falha fechada sem App Secret (503). `GET /whatsapp/webhook`: desafio só com o token de verificação certo e só numérico.
  - O webhook só **registra** (idempotente pelo `wamid`; ecos primeiro; evento com erro de dado é recusado sem derrubar o lote) e responde; a IA trata depois pela **fila durável** `fila_ia` (lease de 120 s, RLS por empresa, no máximo 3 tentativas, motor limitado a 60 s, mensagem com mais de 15 min na fila vai para supervisão). A decisão sai uma única vez: quem tira a mensagem da fila na transação da decisão decide. Mensagens seguidas: as anteriores são agrupadas à mais nova ainda na fila (evento `mensagem_agrupada`); fora de ordem, vão para supervisão (a confirmar). Processamento desligado: supervisão já no registro.
  - Mensagem que não é texto: registra só o tipo e vai para supervisão. Eco do app (`smb_message_echoes`): registrado mascarado e cifrado, conversa em `Humano` (função mínima `registrar_eco_humano`); não abre nem estende a janela. Status `failed` de mensagem nossa aciona a supervisão; status idempotentes.
  - Gateway real (`GatewayWhatsAppCloud`): só texto, sem prévia de link; 2xx sem id, 5xx e falha de rede = Incerta (nunca repetida); 429 = não enviada; 4xx = falha definitiva. **Desligado por padrão**: sem `WhatsApp:EnvioHabilitado` e token, nenhum trabalhador de envio é registrado e o gateway não chama a Meta. Teste de arquitetura: só o gateway usa HTTP de saída.
  - Trabalhador da IA só com `WhatsApp:ProcessamentoHabilitado`; até a F5, o motor é indisponível (tudo vai para supervisão).
- **Resposta humana pelo celular** exige que o número opere em modo de coexistência (WhatsApp Business App + Cloud API). As mensagens enviadas pelo app chegam como eventos de eco no webhook: cada eco é registrado no histórico e coloca a conversa em `Humano` imediatamente. **Verificar na F4** a disponibilidade da coexistência para o número e se o eco cobre todos os casos; sem isso, a resposta humana teria de passar pela API.

### 4.9 API de supervisão

- O tenant da requisição vem do usuário autenticado e é aplicado via `SET LOCAL`. Toda operação (assumir, responder, orientar, liberar, aprovar) confere que a conversa ou o item pertence a esse tenant.
- Testes de acesso direto por id (IDOR) entre empresas.
- **Autenticação de desenvolvimento (F2):** chave de API por usuário (256 bits aleatórios; o banco guarda só o SHA-256), criada por linha de comando com a conexão do dono. A busca pela chave é feita por uma função `SECURITY DEFINER` mínima, e a tabela de usuários não tem acesso direto. Autenticação de produção, papéis humanos e quem vê dados protegidos continuam pendentes.
- **Assumir** vale de qualquer estado. **Liberar** exige a conversa em `Humano` e a versão que o humano viu: se outro humano ou o pipeline mudou a conversa, a liberação é recusada (409). Também é recusada enquanto houver resposta humana na fila, para a IA não responder sem ela e fora de ordem.
- **Permissões da supervisão na conversa:** só `estado`, `versao_estado` e `liberada_em`. O trigger exige `liberada_em` novo na volta ao automático. A supervisão não consegue mexer na última mensagem do cliente, que define a janela de 24h.
- **Autenticação antes de ler o corpo:** um middleware confere a chave antes do endpoint; corpo limitado a 64 KB. Falhas são registradas sem a chave.
- **Resposta humana:** entra no outbox sem decisão e com autor. O trigger só aceita essa mensagem vinda do papel de supervisão, com autor ativo da mesma empresa, validade curta e a conversa em `Humano`. O worker respeita a janela de 24h e a validade (10 min); se ela não sair, a supervisão é acionada. A API recusa texto com dado protegido (ele não fica guardado em claro) e texto fora da janela (retomada pelo celular).
- **Orientação interna:** vira item REV sem classificação, com escopo neste atendimento e origem no usuário. Não muda o estado: a IA continua muda até a liberação. Texto ou assunto com dado protegido é recusado (derivado da invariante 23: a orientação pode virar fonte depois de aprovada).
- **Política:** cada publicação cria uma versão nova (imutável), exige a versão vigente que o humano viu e grava a forma canônica (campos desconhecidos são descartados; classificação só pelo nome exato). O aviso de transferência passa pelas regras de conteúdo do validador, sem fonte (nenhum prazo, valor ou compromisso), na publicação e de novo ao enfileirar. Domínio permitido precisa ser um domínio completo.
- **Requisito de implantação:** HTTPS obrigatório fora de desenvolvimento (a chave vai no cabeçalho); limite de taxa e credenciais distintas por papel ficam com a autenticação de produção.

### 4.11 Governança do conhecimento, papéis e dados proporcionais (F3)

- **Papéis:** atendente, supervisor e administrador (`governanca-conhecimento.md`, seção 3a). O papel vem do usuário autenticado, é lido a cada requisição e conferido pela API em toda operação. O banco confere de novo, a partir do usuário gravado na linha, nas operações de risco: aprovar (versão), mudar status (item), publicar a política e encerrar a pendência. Desde a migração 0005, o banco só aceita autoria do usuário comprovado na própria transação (4.12).
- **Versões:** imutáveis. Sugestão e edição geram versões propostas (sem aprovador). A aprovação gera uma versão nova com aprovador, data carimbada pelo banco e permissões explícitas (classificação, escopo, validade). Só a versão apontada como vigente, aprovada e com status Aprovado chega ao agente. Editar um item aprovado não muda a versão vigente até a nova ser aprovada.
- **Ciclo de vida:** Rev → Aprovado | Rejeitado; Aprovado → Aprovado (nova vigente) | Revogado. Rejeitado e revogado são finais. Todo item nasce Rev, inclusive pela supervisão. Aprovar, editar, rejeitar e revogar fazem `FOR UPDATE` no item e exigem a última versão vista.
- **Limites:** supervisor aprova PUB, INT e TMP; NEG, EXC, RES e CLI só administrador. NEG e EXC exigem escopo de cliente ou atendimento e validade (também no banco). Só PUB fica sem validade. Escopo de proposta fora de uso.
- **Dados protegidos:** três níveis (comum, identificação, financeiro). Resposta automática: comum só se constar na fonte citada; os demais nunca. Resposta humana e conhecimento: comum aceito; os demais recusados. Ao terminar um envio, o worker grava o histórico mascarado, cifra o valor e troca o texto da fila pela versão mascarada (uma única vez, permitido pelo trigger só nessa transição). Logs e eventos não registram valores.
- **Pendência de retomada:** só supervisor ou administrador encerra, com motivo; o banco carimba a data e exige o responsável. Enquanto ativa, o validador bloqueia afirmações de "assunto resolvido" (lista determinística de expressões).

### 4.12 Ajustes pós-F3: identidade, atribuição, visibilidade e piso da política

Direcionamento do Victor de 2026-11-25 (decisões 24 a 30).

- **Identidade comprovada no banco (invariante 26):** cada transação da supervisão apresenta ao banco a chave do usuário (`autenticar_transacao`, migração 0005), que calcula o hash, confere empresa e usuário ativo e registra a identidade da transação. As funções de autoria (`papel_supervisor`, `supervisor_ativo`) só respondem pelo usuário comprovado na própria transação, então todo trigger que confere papel (aprovar, revogar, rejeitar, publicar política, liberar, encerrar pendência, responder, atribuir) recusa autoria forjada. Toda escrita do papel da supervisão sem identidade comprovada é recusada (42501). Fecha o limite registrado na F3 ("o banco confia no id gravado pela API"). O usuário registrado nos eventos da supervisão e o autor das versões propostas também são os comprovados. Limites restantes: quem tem a chave de um usuário age como ele, e a API recebe a chave em claro a cada requisição (uma API comprometida pode reusá-la; aceito para o MVP); a chave chega ao PostgreSQL como parâmetro, então o papel de login da supervisão precisa de `log_parameter_max_length = 0` e `log_parameter_max_length_on_error = 0` (obrigatório no provisionamento). Leitura por papel (atendente, visibilidade) é imposta pela API; a RLS isola por empresa.
- **Atribuição (decisão 27):** `conversas.atribuida_a`. Fila = conversa que precisa de humano (não automática, ou com pendência de retomada) sem responsável. Atendente lista as suas e a fila, lê e atua só nas suas, pega da fila ao assumir (o banco só deixa o atendente se atribuir, e só de uma conversa sem responsável). Supervisor e administrador atribuem, reatribuem e devolvem à fila. Liberar para a IA tira a atribuição (o banco zera). O banco recusa resposta humana de atendente em conversa que não é dele.
- **Visibilidade do conhecimento (decisão 24):** filtro por papel na listagem e no detalhe, pela classificação aprovada (governanca-conhecimento.md, 3a). Atendente vê só versões aprovadas e as próprias propostas. Imposto pela API (não pelo RLS).
- **Políticas fundamentais (invariante 25):** no domínio, a lista padrão de termos de compromisso sempre vale (a política só acrescenta; grava-se só o acréscimo) e INT e RES nunca entram nas classificações de resposta automática; a publicação que pede INT ou RES é recusada. As demais regras do piso já eram estruturais no validador.
- **E-mail institucional (decisão 29):** lista `emailsInstitucionais` na política. Resposta automática: e-mail só se constar na fonte citada **e** estiver na lista. Conhecimento (sugestão, orientação, edição, aprovação): e-mail só institucional; CEP/endereço só em conhecimento que não nasceu de cliente ou atendimento; identificação e financeiro nunca.
- **Validade máxima (decisão 28):** `validadeMaximaDias` por classificação na política, aplicada na aprovação (não retroage a conhecimento já aprovado).
- **HTTPS (decisão 30):** fora do desenvolvimento, a supervisão recusa requisição sem HTTPS (403, sem redirecionar) e envia HSTS; atrás de proxy, só os proxies listados em `Seguranca:ProxiesConfiaveis` informam o esquema. Autenticação de produção (identidade federada, expiração e rotação de credenciais, limite de taxa) continua pendente e é pré-requisito para expor a API.

### 4.13 SaaS multi-tenant como Tech Provider (decisão 34)

Diagnóstico (2026-11-26): o isolamento por empresa (RLS forçado, identidade comprovada no banco), o webhook assinado e idempotente, as filas duráveis no PostgreSQL e a regra de nunca repetir envio incerto já servem ao SaaS e ficam como estão. A Meta envia os webhooks de todos os clientes para a URL única do app, e o webhook já resolve a empresa pelo `phone_number_id`. Faltava: credencial e número por empresa (eram globais, na configuração), cadastro da WABA, eventos de conta no webhook, registro de eventos recusados, rodízio entre empresas na fila de envio, Embedded Signup, papel de operador da plataforma. Sem microsserviços nem fila externa: a fila no PostgreSQL (`FOR UPDATE SKIP LOCKED`) atende o volume previsto; reavaliar com métricas do piloto. O conteúdo bruto do webhook não é guardado (pode conter dado protegido em claro): a tabela de mensagens, gravada antes do 200, é a caixa de entrada.

Fatos da Meta usados (documentação oficial, lida em 2026-11-26; reconfirmar antes da M3): Tech Provider usa só business tokens (um por cliente, obtido trocando o código do Embedded Signup em `GET /oauth/access_token`; o código vale 5 minutos); por cliente, inscrever o app na WABA (`POST /{waba_id}/subscribed_apps`) e registrar o número (`POST /{phone_number_id}/register`, com PIN); o cliente paga a Meta diretamente; webhooks de todos os clientes vão para a URL do app, com reenvio por até 7 dias e possíveis duplicatas; `account_update` com `PARTNER_ADDED` ao concluir o cadastro; limite de 10 novos clientes por 7 dias antes da verificação. Embedded Signup v2/v3 descontinuados em 15/10/2026: implementar a v4. Pontos que a documentação não deixa claros (`entry.id` do `account_update`; evento de remoção do parceiro; validade do business token) não são presumidos.

Etapas (um PR cada, testes e CI verde):

| Etapa | Entrega |
|---|---|
| **M1 Contas WhatsApp por empresa** | cadastro da WABA e dos números por empresa; credencial cifrada no banco (AES-256-GCM, chave fora do banco, ligada à empresa e à WABA), legível só pelo processo de envio por função mínima; número de origem = número em que o cliente escreveu por último; três etapas separadas por número (conta conectada com credencial, autorização operacional, envio habilitado), todas registradas no histórico com operador e motivo; envio desligado por padrão em cada número e no interruptor geral |
| **M2 Webhook e filas** | eventos de conta (`account_update`, `phone_number_quality_update`, `account_alerts`) sem conteúdo de cliente; registro de eventos recusados ou de número desconhecido só com metadados e hash; nova tentativa só quando é comprovadamente seguro (resposta da Meta de que nada foi aceito), com espera crescente e limite, depois supervisão; incerto nunca é repetido; rodízio entre empresas na fila de envio |
| **M3 Embedded Signup (backend)** | etapas separadas: conexão da WABA (troca do código, inscrição do app, registro do número), autorização operacional e ativação do envio; retomáveis e idempotentes; contra a Meta simulada; desligado sem as credenciais do app e sem login de produção (decisão 30) |
| **M4 Ciclo de vida** | credencial recusada ou conta desativada pausa o número e aciona a supervisão; suspensão e desligamento da empresa |
| **M5 Rastreabilidade** | identificador de correlação do webhook ao status; auditoria do cadastro; situação das filas por empresa |

Fora do plano: templates, interface do Embedded Signup (F6, só testável com o app real), coexistência, qualquer conexão real.

Limites conhecidos da M1 (revisão em `revisoes/2026-11-27-m1.md`):
- O papel `worker` atende todas as empresas e tem a chave das credenciais: `credencial_envio` isola as empresas para os caminhos do código, não contra um processo de envio comprometido (S8).
- O operador age com a conexão do dono; a auditoria depende de usar as funções `plataforma_*`, e o nome do operador é declarado. Na M3, papel de operador com autenticação e `execute` só nessas funções, nada nas tabelas (G3, S11).
- Nada confere ainda, na Meta, que o número e o token pertencem à WABA informada: o operador confere manualmente antes de registrar. Na M3, os números vêm de `GET /{waba_id}/phone_numbers` com o token do cliente; conferir `entry.id` no webhook depende de confirmar o significado do campo (S1).

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

## 5a. Andamento

| Fase | Situação |
|---|---|
| M1 Contas WhatsApp por empresa | implementada: conta, credencial cifrada e números por empresa; três etapas separadas e auditadas; número de saída pela última mensagem do cliente; envio desligado por padrão. Revisão em revisoes/2026-11-27-m1.md |
| F4.5 Auditoria e Ruflo | decisões da F4 implementadas (conciliação fora de ordem, atraso configurável, reações, reavaliação de conhecimento, endereço institucional); portão de mudanças com Ruflo nos hooks (docs/desenvolvimento/portao.md); demonstração local; OpenAPI; guia do administrador. Revisão em revisoes/2026-11-25-f4-5.md |
| F4 WhatsApp (preparação) | implementada sem número real: webhook assinado, fila durável da IA, eco do app, status de entrega, gateway real desligado. Revisão em revisoes/2026-11-25-f4.md. Requisitos externos em docs/setup/whatsapp-integracao.md |
| Pós-F3 | implementada: identidade comprovada no banco, atribuição de conversas, visibilidade por papel, piso da política, e-mail institucional, validade máxima, HTTPS. Revisão em revisoes/2026-11-25-pos-f3.md |
| F3 Conhecimento e papéis | implementada: papéis no backend e no banco, ciclo de vida versionado e auditado, limites comerciais, dados protegidos proporcionais, pendência de retomada. Revisão em revisoes/2026-11-25-f3.md |
| F2 Supervisão (API) | implementada: autenticação de desenvolvimento, assumir, liberar, responder, orientar, política; travas no banco para a mensagem humana. Revisão em revisoes/2026-11-25-f2.md |
| F1b Pipeline | implementada: orquestrador, worker, criptografia, travas do banco. Revisão em revisoes/2026-11-25-f1b.md |
| F1a Domínio | implementada: estados, validador, elegibilidade, rascunho estrito, dados protegidos. Revisão em revisoes/2026-11-25-f1a.md |
| F0 Fundação | implementada (PR #2): papéis sem superusuário, RLS forçado com `SET LOCAL`, histórico append-only com carimbo do banco, migrador com checksum, CI endurecido, contagem de testes por regra |

## 6. Riscos

| Risco | Mitigação |
|---|---|
| Detector de valores e compromissos deixa passar um formato | na dúvida bloqueia; o conjunto adversarial cresce a cada caso real |
| Bloqueio em excesso deixa o atendimento pouco útil | métricas de bloqueio por motivo; humanos ajustam a política |
| Corrida entre humano assumindo e envio em andamento | trava única (FOR UPDATE) na decisão, no worker e na tomada de controle; janela residual = a leitura da credencial (M1) e a chamada HTTP |
| Timeout ambíguo no envio | `incerto` sem repetição; supervisão avisada |
| Mensagem parada no outbox enquanto a fonte é revogada | worker revalida fontes, com TTL |
| Repositório público | gitleaks, push protection, nenhum dado real |
| O dono das tabelas consegue desligar triggers ou políticas (é o dono) | a aplicação nunca usa o dono; credencial do dono só no pipeline de migração; mudanças no banco só por migração revisada pelo CI |
| Qualquer papel pode definir `app.tenant_id` na própria transação | só a infraestrutura define o tenant (`ExecutorTenant`/`UnidadeDeTrabalhoTenant`); nenhum SQL vindo de entrada externa é executado; consultas sempre parametrizadas |
| O papel `app` pode descobrir qual tenant usa um `phone_number_id` testando valores | só serve a quem já recebe o webhook; aceito como risco baixo |
| A aplicação comprometida pode gravar uma decisão aprovada sem rodar o validador | decisões imutáveis e auditáveis, com hash do texto; o trigger exige estado e versão atuais; revisão de código e mutação no validador |

## 7. Pendências que afetam o plano

Ver `docs/project-context.md`. Afetam diretamente:

- F1: categorias de dados protegidos (padrão técnico definido; o Victor pode ampliar).
- F4: coexistência do número (app + API).
- F5: provedor de IA (desbloqueada: dados delimitados).
- F6: autenticação de produção e gestão de usuários pelo painel (hoje: chave de API e papéis por linha de comando).
- F7: assuntos autônomos e textos fixos.
- Lacunas com padrão conservador já definido no plano (o Victor pode mudar):
  - retomada não responde mensagens anteriores à liberação;
  - mensagens que não são texto vão para supervisão;
  - esclarecimento desligado;
  - número compartilhado ou reciclado.
- Hospedagem de produção (infraestrutura externa).
