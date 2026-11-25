'use strict';
// Prova de funcionamento do portão de mudanças e da participação do Ruflo. Cada verificação tem critério objetivo;
// o script termina com código 1 se alguma falhar. Uso: node ferramentas/portao/prova.cjs [--exigir-hooks]
//   --exigir-hooks: exige que o log tenha disparos reais dos hooks pelo Claude Code (não só execuções manuais).

const fs = require('node:fs');
const path = require('node:path');
const c = require('./comum.cjs');
const contexto = require('./contexto.cjs');
const impacto = require('./impacto.cjs');
const estado = require('./estado.cjs');

const resultados = [];
function verificar(nome, ok, detalhe) {
  resultados.push({ nome, ok: !!ok, detalhe });
  console.log(`${ok ? 'OK   ' : 'FALHA'} ${nome}${detalhe ? ' -> ' + detalhe : ''}`);
}

// 1. Ruflo instalado e na versão fixada.
const versao = c.ruflo(['--version'], 60000);
verificar('Ruflo responde na versão fixada', versao.ok && versao.saida.includes('3.80.6'), versao.saida.trim().split('\n').pop());

// 2. Memória persistente sincronizada com o Git.
const { spawnSync } = require('node:child_process');
const s = spawnSync(process.execPath, [path.join(__dirname, 'sincronizar.cjs'), '--forcar'], { cwd: c.RAIZ, encoding: 'utf8' });
const esperado = { decisoes: c.decisoes().length, invariantes: c.invariantes().length, regressoes: c.regressoes().length };
verificar('Sincronização Git -> Ruflo (contagens iguais ao Git)', s.status === 0 && s.stdout.includes(JSON.stringify(esperado)), JSON.stringify(esperado));

const peek = c.ruflo(['memory', 'peek'], 60000);
const linhas = /memory\.db\s*\|\s*yes\s*\|\s*(\d+)/.exec(peek.saida);
verificar('Memória do Ruflo persiste em .swarm/memory.db (fora do Git)', linhas && Number(linhas[1]) >= esperado.decisoes + esperado.invariantes + esperado.regressoes, linhas ? linhas[1] + ' registros' : 'sem leitura');

// 3. Busca semântica do Ruflo encontra uma regressão conhecida.
// Limitação conhecida: os vetores locais do Ruflo são simples; paráfrases distantes podem não aparecer (ver portao.md).
// Por isso a consulta do portão soma a busca do Ruflo com a busca por termos no Git.
const busca = c.buscar('e-mail conferido por substring na fonte', 5) || [];
verificar('Ruflo encontra a regressão REG-001 por descrição (top 5)', busca.some((r) => r.key === 'REG-001'), busca.map((r) => r.key).join(', '));
const porChave = c.ruflo(['memory', 'retrieve', '-k', 'REG-001', '-n', 'regressoes'], 60000);
verificar('Ruflo devolve a regressão REG-001 pela chave', porChave.ok && porChave.saida.includes('Email_que_nao_consta_na_fonte_citada_e_bloqueado'));

// 4. Consulta automática antes da tarefa (o que o hook UserPromptSubmit injeta).
const cons = contexto.consultar('Vou mudar a comparação de e-mail institucional na resposta automática do validador');
verificar('Consulta antes da tarefa usa o Ruflo e o Git', cons.ruflo && cons.lista.length > 0, cons.lista.map((a) => `${a.id}(${a.fontes.join('+')})`).join(', '));

// 5. Impacto antes de editar (o que o hook PreToolUse injeta).
const imp = impacto.analisar('src/Seceletesc.Dominio/Envio/ValidadorEnvio.cs');
verificar('Impacto de ValidadorEnvio.cs: regras, regressões e revisores',
  imp && imp.regras.includes('INV-6') && imp.regressoes.some((r) => r.id === 'REG-001') && imp.revisores.includes('guardiao-regras'),
  imp ? `regras ${imp.regras.length}, regressões ${imp.regressoes.map((r) => r.id).join(' ')}, revisores ${imp.revisores.join(' ')}` : 'sem área');

// 6. Decisão do fim de tarefa (o que o hook Stop aplica).
const bloqueia = estado.decidir({ mudancas: ['M src/x.cs'], impressaoAtual: 'b', ultimo: { sucesso: true, impressao: 'a' }, jaBloqueou: false });
const libera = estado.decidir({ mudancas: ['M src/x.cs'], impressaoAtual: 'a', ultimo: { sucesso: true, impressao: 'a' }, jaBloqueou: false });
const falhou = estado.decidir({ mudancas: ['M src/x.cs'], impressaoAtual: 'a', ultimo: { sucesso: false, impressao: 'a' }, jaBloqueou: false });
const semLaco = estado.decidir({ mudancas: ['M src/x.cs'], impressaoAtual: 'b', ultimo: null, jaBloqueou: true });
verificar('Fim de tarefa bloqueia sem testes sobre o código atual e libera depois', bloqueia.bloquear && !libera.bloquear && falhou.bloquear && !semLaco.bloquear);

// 7. Hooks registrados no Claude Code apontando para os scripts.
const config = JSON.parse(c.ler(path.join(c.RAIZ, '.claude', 'settings.json')));
const hooks = config.hooks || {};
const comando = (ev) => JSON.stringify(hooks[ev] || []);
verificar('Hooks do Claude Code configurados (SessionStart, UserPromptSubmit, PreToolUse, Stop)',
  comando('SessionStart').includes('sincronizar.cjs') && comando('UserPromptSubmit').includes('contexto.cjs')
  && comando('PreToolUse').includes('impacto.cjs') && comando('Stop').includes('verificar.cjs'));

// 8. Registro de regressões protegido no CI.
const teste = c.ler(path.join(c.RAIZ, 'tests', 'Seceletesc.Arquitetura.Testes', 'CoberturaRegrasTestes.cs'));
verificar('CI confere que toda regressão registrada tem teste ativo', teste.includes('Toda_regressao_registrada_tem_teste_ativo'));

// 9. Disparos reais dos hooks (evidência de uso, não só configuração).
let disparos = { contexto: 0, impacto: 0, verificar: 0 };
if (fs.existsSync(c.LOG)) {
  for (const l of c.ler(c.LOG).split('\n').filter(Boolean)) {
    try { const j = JSON.parse(l); if (j.origem === 'hook' && j.acao in disparos) disparos[j.acao]++; /* só disparos com CLAUDE_PROJECT_DIR (pelo Claude Code) */ } catch { /* linha corrompida */ }
  }
}
const temDisparos = disparos.contexto > 0 && disparos.impacto > 0 && disparos.verificar > 0;
const exigir = process.argv.includes('--exigir-hooks');
verificar(`Disparos reais dos hooks pelo Claude Code${exigir ? '' : ' (informativo)'}`, temDisparos || !exigir, JSON.stringify(disparos));

const falhas = resultados.filter((r) => !r.ok).length;
console.log(`\n${resultados.length - falhas} de ${resultados.length} verificações OK.`);
c.registrar({ acao: 'prova', ok: falhas === 0, verificacoes: resultados.length, falhas, disparos });
process.exitCode = falhas === 0 ? 0 : 1;
