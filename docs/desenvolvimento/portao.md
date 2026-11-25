# Portão de mudanças (Ruflo + Git + testes)

Mecanismo que roda **sozinho** em toda tarefa do Claude Code neste repositório, sem invocação manual. Decisão do Victor (2026-11-25, F4.5): o Ruflo faz parte da estratégia e precisa participar de fato do processo.

## O que acontece em cada tarefa

| Momento | Hook do Claude Code | Script | O que faz |
|---|---|---|---|
| Início da sessão | `SessionStart` | `ferramentas/portao/sincronizar.cjs` | copia decisões, invariantes e regressões do Git para a memória do Ruflo (só se os documentos mudaram) |
| Cada pedido | `UserPromptSubmit` | `ferramentas/portao/contexto.cjs` | busca no Ruflo (semântica) e no Git (termos) as decisões, regras e regressões relacionadas e as injeta no contexto do agente |
| Antes de editar um arquivo | `PreToolUse` (Edit, Write) | `ferramentas/portao/impacto.cjs` | mostra a área afetada, as regras, as regressões já corrigidas ali, os revisores obrigatórios e os testes principais (pelo `mapa-impacto.json`) |
| Fim da tarefa | `Stop` | `ferramentas/portao/verificar.cjs` | **bloqueia** o encerramento se houver mudança em `src/` ou `tests/` sem testes aprovados sobre exatamente aquele código |
| Testes de regressão | (manual ou pedido pelo bloqueio) | `ferramentas/portao/testar.cjs` | roda a solução inteira, grava o resultado amarrado à impressão digital do código e registra a execução no Ruflo |
| Bug corrigido | (manual) | `ferramentas/portao/registrar.cjs regressao ...` | acrescenta a regressão ao Git e ao Ruflo |

Revisão de código, segurança e regras: o portão indica os revisores obrigatórios por área (`seguranca`, `guardiao-regras`); a revisão é feita pelos subagentes antes do PR e registrada em `docs/desenvolvimento/revisoes/`.

## Instalação (uma vez por máquina)

```cmd
npm ci --ignore-scripts
```

Instala o Ruflo `3.80.6` no projeto (`node_modules/`, fora do Git) com todas as dependências travadas em `package-lock.json`, sem rodar scripts de instalação. O portão chama esse binário pelo Node **sem shell** e com ambiente mínimo (sem conexões, tokens nem chaves do terminal). Sem a instalação, cai para `npx ruflo@3.80.6` com o texto filtrado. Testes do portão que não dependem de rede: `node ferramentas\portao	estes-unitarios.cjs` (também no CI).

## Fontes

- **Git (fonte de verdade):** `docs/project-context.md` (decisões), `docs/business-rules/invariants.md`, `docs/desenvolvimento/regressoes.md`, `docs/desenvolvimento/mapa-impacto.json`, `docs/desenvolvimento/revisoes/`.
- **Ruflo (complemento):** memória persistente em `.swarm/memory.db` (fora do Git), namespaces `decisoes`, `invariantes`, `regressoes` (cópias do Git) e `execucoes` (resultados de testes). Usada para busca por significado.
- **Log local:** `.claude-flow/data/portao.log` (fora do Git): cada consulta (só hash e tamanho, nunca o texto), impacto, verificação e execução, com `origem` = `hook` quando disparado pelo Claude Code.

## O que o CI garante (independe de Ruflo e de ferramenta)

- Testes de regra com mínimos (`cobertura-obrigatoria.txt`), que não podem cair.
- `Toda_regressao_registrada_tem_teste_ativo`: toda linha de `regressoes.md` aponta para um teste que existe e não está ignorado.

## Prova de funcionamento

```cmd
node ferramentas\portao\prova.cjs
```

São 11 verificações com critério objetivo (sai com código 1 se alguma falhar): versão do Ruflo, sincronização com contagens iguais ao Git, persistência da memória, busca semântica e por chave, consulta antes da tarefa, impacto antes de editar, decisão do fim de tarefa, hooks configurados, verificação no CI e disparos reais dos hooks. Com `--exigir-hooks`, falha se o log não tiver disparos reais pelo Claude Code.

## Limitações conhecidas (verificadas)

- **Busca semântica do Ruflo é limitada:** os vetores são locais e simples. Paráfrases próximas funcionam (REG-001 em 1º lugar para "e-mail conferido por substring na fonte"), paráfrases distantes podem não aparecer (REG-001 ficou fora dos 5 primeiros para "conferido por trecho em vez de comparação exata"). Por isso a consulta soma o Ruflo com a busca por termos no Git, e o impacto por arquivo usa só o Git (determinístico).
- **Codex não tem hooks:** o Codex lê `AGENTS.md`, que manda rodar os scripts do portão. Para ele, a garantia automática é o CI. O mesmo vale para qualquer outra ferramenta.
- **Revisão não é verificável pelo portão:** o portão indica os revisores; o registro da revisão fica em `docs/desenvolvimento/revisoes/`.
- **Tempo:** a consulta do Ruflo leva cerca de 2 segundos por pedido; a sincronização, cerca de 12 segundos quando os documentos mudam.
- O servidor MCP e a linha de comando do Ruflo usam o mesmo arquivo de memória no Windows (`.swarm/memory.db`, verificado lendo pelo MCP uma regressão gravada pela linha de comando).
