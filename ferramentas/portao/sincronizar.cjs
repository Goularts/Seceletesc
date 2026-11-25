'use strict';
// Copia decisões, invariantes e regressões (Git) para a memória do Ruflo, para busca semântica.
// Só refaz quando os documentos mudam (hash). Uso: node ferramentas/portao/sincronizar.cjs [--forcar]

const fs = require('node:fs');
const path = require('node:path');
const c = require('./comum.cjs');

const MARCA = path.join(c.DADOS, 'sincronizado.json');

function main() {
  const forcar = process.argv.includes('--forcar');
  const hash = c.hashDocs();
  if (!forcar && fs.existsSync(MARCA) && JSON.parse(fs.readFileSync(MARCA, 'utf8')).hash === hash) {
    console.log('portão: memória do Ruflo já está em dia com o Git.');
    return 0;
  }

  const grupos = {
    decisoes: c.decisoes().map((d) => ({ key: d.id, value: d.texto })),
    invariantes: c.invariantes().map((d) => ({ key: d.id, value: d.texto })),
    regressoes: c.regressoes().map((r) => ({
      key: r.id,
      value: `${r.componente}: ${r.problema}. Correção: ${r.correcao}. Teste: ${r.teste}. Origem: ${r.origem}`,
    })),
  };

  fs.mkdirSync(c.DADOS, { recursive: true });
  const resumo = {};
  for (const [ns, entradas] of Object.entries(grupos)) {
    // Recria o namespace: o Git é a fonte, nada antigo sobra.
    c.ruflo(['memory', 'purge', '-n', ns, '--force'], 60000);
    const arquivo = path.join(c.DADOS, `import-${ns}.json`);
    fs.writeFileSync(arquivo, JSON.stringify({
      schema: 'ruflo-memory-export/v1.1',
      count: entradas.length,
      entries: entradas.map((e) => ({ key: e.key, namespace: ns, value: e.value, tags: ['git', ns] })),
    }));
    const r = c.ruflo(['memory', 'import', '-i', arquivo, '-n', ns], 120000);
    if (!r.ok) {
      console.error(`portão: falha ao importar ${ns} no Ruflo:\n${r.saida.slice(-500)}`);
      return 1;
    }
    resumo[ns] = entradas.length;
  }

  fs.writeFileSync(MARCA, JSON.stringify({ hash, em: new Date().toISOString(), resumo }));
  c.registrar({ acao: 'sincronizar', resumo });
  console.log('portão: memória do Ruflo sincronizada com o Git: ' + JSON.stringify(resumo));
  return 0;
}

process.exitCode = main();
