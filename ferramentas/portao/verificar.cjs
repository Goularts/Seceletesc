'use strict';
// Fim de tarefa (hook Stop do Claude Code, ou à mão): impede encerrar com mudanças em src/tests sem testes de
// regressão aprovados sobre exatamente o código atual. Uso manual: node ferramentas/portao/verificar.cjs

const c = require('./comum.cjs');
const e = require('./estado.cjs');

function main() {
  const manual = process.argv.includes('--manual');
  const entrada = manual ? {} : c.lerEntrada();
  const decisao = e.decidir({
    mudancas: e.alteracoes(),
    impressaoAtual: e.impressao(),
    ultimo: e.ultimoTeste(),
    jaBloqueou: entrada.stop_hook_active === true,
  });
  c.registrar({ acao: 'verificar', origem: manual ? 'manual' : (process.env.CLAUDE_PROJECT_DIR ? 'hook' : 'stdin-sem-claude'), bloquear: decisao.bloquear, motivo: decisao.motivo });
  if (manual) {
    console.log(decisao.bloquear ? 'portão: BLOQUEADO: ' + decisao.motivo + '. Rode: node ferramentas/portao/testar.cjs' : 'portão: liberado (' + decisao.motivo + ').');
    return decisao.bloquear ? 1 : 0;
  }
  if (decisao.bloquear) {
    process.stdout.write(JSON.stringify({
      decision: 'block',
      reason: `Portão de mudanças: ${decisao.motivo}. Rode "node ferramentas/portao/testar.cjs" e corrija o que falhar antes de concluir. Se corrigiu um bug, registre em docs/desenvolvimento/regressoes.md.`,
    }));
  }
  return 0;
}

process.exitCode = main();
