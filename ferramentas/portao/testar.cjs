'use strict';
// Testes de regressão: roda a solução inteira (inclui o teste que confere o registro de regressões) e grava o
// resultado amarrado à impressão digital do código. O hook de fim de tarefa usa esse registro.
// Uso: node ferramentas/portao/testar.cjs

const { spawnSync } = require('node:child_process');
const c = require('./comum.cjs');
const e = require('./estado.cjs');

function main() {
  const impressaoAntes = e.impressao();
  console.log('portão: rodando todos os testes (dotnet test -c Release)...');
  const r = spawnSync('dotnet', ['test', 'Seceletesc.slnx', '-c', 'Release'], { cwd: c.RAIZ, encoding: 'utf8', maxBuffer: 256 * 1024 * 1024, shell: process.platform === 'win32' });
  const saida = (r.stdout || '') + (r.stderr || '');
  const resumos = saida.split('\n').filter((l) => /(Aprovado!|Passed!|Com falha!|Failed!)/.test(l)).map((l) => l.trim());
  const sucesso = r.status === 0;
  const impressaoDepois = e.impressao();
  const dados = {
    sucesso,
    impressao: impressaoAntes === impressaoDepois ? impressaoAntes : null, // código mudou durante a execução: não vale
    em: new Date().toISOString(),
    resumos,
  };
  e.gravarUltimoTeste(dados);
  c.registrar({ acao: 'testar', sucesso, resumos });

  // Registro também na memória do Ruflo (complementa o log local; o Git continua sendo a fonte).
  c.ruflo(['memory', 'store', '-k', 'execucao-' + dados.em.replace(/[:.]/g, '-'), '--value', c.seguro((sucesso ? 'sucesso' : 'falha') + ': ' + resumos.join(' / '), 400), '-n', 'execucoes', '--provenance', 'tool_result'], 60000);

  for (const l of resumos) console.log(l);
  if (!sucesso) {
    console.error(saida.split('\n').filter((l) => /\[FAIL\]|Com falha |Failed /.test(l)).slice(0, 30).join('\n'));
  }
  console.log(sucesso ? 'portão: testes passaram; registrado.' : 'portão: TESTES FALHARAM; registrado.');
  return sucesso ? 0 : 1;
}

process.exitCode = main();
