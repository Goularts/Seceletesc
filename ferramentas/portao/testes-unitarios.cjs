'use strict';
// Testes automáticos das funções do portão que não dependem do Ruflo nem de rede (rodam no CI).
// Uso: node ferramentas/portao/testes-unitarios.cjs

const assert = require('node:assert/strict');
const c = require('./comum.cjs');
const estado = require('./estado.cjs');
const impacto = require('./impacto.cjs');
const contexto = require('./contexto.cjs');

const casos = [];
const caso = (nome, fn) => casos.push({ nome, fn });

// Revisão da F4.5, S1: nada que o CMD ou o bash interpretem chega à linha de comando.
caso('seguro() remove metacaracteres de shell e expansões do CMD', () => {
  for (const ataque of ['%X%', '%CMDCMDLINE:~-1%&calc&', '"&calc"', 'a^b', '!VAR!', 'x\r\ny', '`id`', '$(id)', 'a|b', 'a>b', 'a<b', "a'b", 'a;b']) {
    const r = c.seguro(ataque);
    assert.ok(!/[%"&^!`$|<>';\r\n]/.test(r), `sobrou metacaractere em "${ataque}" -> "${r}"`);
  }
  assert.equal(c.seguro('e-mail institucional: atendimento'), 'e-mail institucional: atendimento');
});

// Revisão da F4.5, S2: consulta ao Ruflo sem dados pessoais nem tokens; log só com hash.
caso('semDadosPessoais() tira CPF, e-mail, telefone e tokens', () => {
  const r = c.semDadosPessoais('cpf 529.982.247-25 e-mail joao@x.com fone 48 99999-0000 token abcdefghijklmnopqrstuvwxyz0123456789');
  assert.ok(!/529|joao@|99999|abcdefghijklmnop/.test(r), r);
});

caso('resumoDeTexto() não guarda o texto', () => {
  const r = c.resumoDeTexto('meu CPF é 529.982.247-25');
  assert.deepEqual(Object.keys(r).sort(), ['hash', 'tamanho']);
  assert.ok(!JSON.stringify(r).includes('529'));
});

caso('fim de tarefa: bloqueia sem testes sobre o código atual, libera depois, não entra em laço', () => {
  assert.equal(estado.decidir({ mudancas: ['M src/a.cs'], impressaoAtual: 'b', ultimo: { sucesso: true, impressao: 'a' }, jaBloqueou: false }).bloquear, true);
  assert.equal(estado.decidir({ mudancas: ['M src/a.cs'], impressaoAtual: 'a', ultimo: { sucesso: true, impressao: 'a' }, jaBloqueou: false }).bloquear, false);
  assert.equal(estado.decidir({ mudancas: ['M src/a.cs'], impressaoAtual: 'a', ultimo: { sucesso: false, impressao: 'a' }, jaBloqueou: false }).bloquear, true);
  assert.equal(estado.decidir({ mudancas: [], impressaoAtual: 'a', ultimo: null, jaBloqueou: false }).bloquear, false);
  assert.equal(estado.decidir({ mudancas: ['M src/a.cs'], impressaoAtual: 'b', ultimo: null, jaBloqueou: true }).bloquear, false);
});

caso('impacto do validador traz regras, regressões e revisores do Git', () => {
  const r = impacto.analisar('src/Seceletesc.Dominio/Envio/ValidadorEnvio.cs');
  assert.ok(r.regras.includes('INV-6'));
  assert.ok(r.regressoes.some((g) => g.id === 'REG-001'));
  assert.ok(r.revisores.includes('guardiao-regras'));
});

caso('busca por termos no Git encontra a regressão do e-mail', () => {
  const r = contexto.buscarNoGit('comparação de e-mail da resposta automática conferido por trecho', 10);
  assert.ok(r.some((i) => i.id === 'REG-001'), JSON.stringify(r.map((i) => i.id)));
});

caso('mapa de impacto e registro de regressões são legíveis e coerentes', () => {
  const areas = c.mapaImpacto().areas;
  assert.ok(areas.length > 0);
  const ids = c.regressoes().map((r) => r.id);
  assert.equal(new Set(ids).size, ids.length);
  for (const r of c.regressoes()) assert.match(r.teste, /^[A-Za-z0-9_]+$/, r.id);
});

let falhas = 0;
for (const { nome, fn } of casos) {
  try {
    fn();
    console.log('OK    ' + nome);
  } catch (e) {
    falhas++;
    console.log('FALHA ' + nome + '\n      ' + e.message);
  }
}
console.log(`\n${casos.length - falhas} de ${casos.length} testes do portão OK.`);
process.exitCode = falhas === 0 ? 0 : 1;
