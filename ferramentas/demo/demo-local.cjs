'use strict';
// Demonstração local de ponta a ponta, SEM serviços externos: nenhum número real, nada enviado à Meta.
// Sobe o PostgreSQL local (Docker), aplica as migrações, cria uma empresa de demonstração, sobe a API e simula o
// WhatsApp mandando um webhook assinado. Mostra o registro mascarado, a supervisão e a API de supervisão.
// Uso (CMD ou PowerShell): node ferramentas\demo\demo-local.cjs
// Tudo o que é gerado (chaves, relatório) fica em .local\demo\ (fora do Git). A chave de API do usuário de
// demonstração nunca é exibida.

process.noDeprecation = true;
const { spawn, spawnSync } = require('node:child_process');
const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');

const RAIZ = path.resolve(__dirname, '..', '..');
const PASTA = path.join(RAIZ, '.local', 'demo', new Date().toISOString().replace(/[:.]/g, '-'));
const SENHA = process.env.SECELETESC_DB_SENHA_LOCAL || 'somente-dev-local'; // padrão do docker-compose (só local)
const PORTA_DB = process.env.SECELETESC_DB_PORTA || '55432';
const URL = 'http://127.0.0.1:5288';
const SEGREDO_APP = crypto.randomBytes(24).toString('hex'); // gerado a cada demo; simula o App Secret da Meta
const WIN = process.platform === 'win32';

const conexao = (usuario) => `Host=127.0.0.1;Port=${PORTA_DB};Database=seceletesc;Username=${usuario};Password=${SENHA}`;
const relatorio = [];
function passo(titulo, detalhe) {
  const linha = `- ${titulo}${detalhe ? ': ' + detalhe : ''}`;
  relatorio.push(linha);
  console.log(linha);
}

// Sem shell (revisão da F4.5, S4): dotnet e docker são executáveis; argumentos com espaço chegam inteiros.
function rodar(cmd, args, env = {}) {
  const r = spawnSync(cmd, args, { cwd: RAIZ, encoding: 'utf8', env: { ...process.env, ...env }, shell: false });
  if (r.status !== 0) throw new Error(`${cmd} ${args.join(' ')} falhou:\n${(r.stdout || '') + (r.stderr || '')}`);
  return r.stdout || '';
}

const cli = (...args) => rodar('dotnet', ['run', '--no-build', '--project', 'src/Seceletesc.Api', '--', ...args], { SECELETESC_CONEXAO_DONO: conexao('seceletesc_dono') });

async function http(metodo, rota, { chave, corpo, cabecalhos } = {}) {
  const r = await fetch(URL + rota, {
    method: metodo,
    headers: { 'Content-Type': 'application/json', ...(chave ? { Authorization: 'Bearer ' + chave } : {}), ...(cabecalhos || {}) },
    body: corpo === undefined ? undefined : typeof corpo === 'string' ? corpo : JSON.stringify(corpo),
  });
  const texto = await r.text();
  let json = null;
  try { json = JSON.parse(texto); } catch { /* não é JSON */ }
  return { status: r.status, json, texto };
}

const esperar = (ms) => new Promise((ok) => setTimeout(ok, ms));

function webhookTexto(numero, waId, wamid, texto) {
  return JSON.stringify({
    object: 'whatsapp_business_account',
    entry: [{ id: 'waba-demo', changes: [{ field: 'messages', value: {
      messaging_product: 'whatsapp', metadata: { phone_number_id: numero },
      messages: [{ from: waId, id: wamid, timestamp: String(Math.floor(Date.now() / 1000)), type: 'text', text: { body: texto } }],
    } }] }],
  });
}

const assinar = (corpo) => 'sha256=' + crypto.createHmac('sha256', SEGREDO_APP).update(Buffer.from(corpo, 'utf8')).digest('hex');

async function main() {
  fs.mkdirSync(PASTA, { recursive: true });
  console.log('Demonstração local do Seceletesc (sem serviços externos)\n');

  rodar('docker', ['compose', 'up', '-d', '--wait']);
  passo('PostgreSQL local no ar', `127.0.0.1:${PORTA_DB} (Docker)`);

  rodar('dotnet', ['build', 'src/Seceletesc.Api', '-v', 'q']);
  cli('migrar');
  passo('Migrações aplicadas', '0001 a 0008');

  const tenant = cli('criar-tenant', '--nome', 'Empresa de demonstração').trim().split('\n').pop().trim();
  const saidaAdmin = cli('criar-supervisor', '--tenant', tenant, '--nome', 'Administrador da demo', '--papel', 'Administrador');
  const chave = /chave:\s+(\S+)/.exec(saidaAdmin)[1];
  const numero = 'demo-' + crypto.randomBytes(4).toString('hex');
  const waba = 'demo-' + crypto.randomBytes(4).toString('hex');
  // Operador da plataforma (decisão 34): conecta a conta simulada, liga o número e autoriza a operação. O envio do
  // número fica desligado (nenhuma credencial é registrada na demonstração).
  cli('conectar-conta', '--tenant', tenant, '--waba', waba, '--operador', 'Operador da demo');
  cli('registrar-numero', '--tenant', tenant, '--phone-number-id', numero, '--waba', waba, '--operador', 'Operador da demo');
  cli('autorizar-operacao', '--tenant', tenant, '--phone-number-id', numero, '--operador', 'Operador da demo', '--motivo', 'demonstração local');
  cli('gerar-chaves', '--pasta', PASTA);
  passo('Empresa de demonstração criada', `tenant ${tenant}, conta simulada ${waba}, número simulado ${numero} (operação autorizada, envio desligado), administrador (chave não exibida)`);

  const api = spawn('dotnet', ['run', '--no-build', '--project', 'src/Seceletesc.Api', '--urls', URL], {
    cwd: RAIZ, shell: false, stdio: 'ignore',
    env: {
      ...process.env,
      ASPNETCORE_ENVIRONMENT: 'Development',
      ConnectionStrings__Supervisao: conexao('supervisao_local'),
      ConnectionStrings__App: conexao('app_local'),
      ConnectionStrings__Worker: conexao('worker_local'),
      WhatsApp__AppSecret: SEGREDO_APP,
      WhatsApp__TokenVerificacao: 'demo-token-verificacao',
      WhatsApp__ProcessamentoHabilitado: 'true',
      // O envio fica desligado mesmo que o administrador tenha configurado credenciais reais em user-secrets.
      WhatsApp__EnvioHabilitado: 'false',
      WhatsApp__ChaveCredenciais: '',
      DadosProtegidos__ChavePublicaPem: fs.readFileSync(path.join(PASTA, 'dados-protegidos-publica.pem'), 'utf8'),
    },
  });

  try {
    for (let i = 0; i < 60; i++) {
      try { if ((await http('GET', '/saude')).status === 200) break; } catch { /* subindo */ }
      await esperar(1000);
    }
    passo('API no ar', URL + ' (envio ao WhatsApp DESLIGADO)');

    const openapi = await http('GET', '/openapi/v1.json');
    passo('Documento OpenAPI', `${openapi.status} em /openapi/v1.json, ${Object.keys(openapi.json?.paths || {}).length} rotas descritas`);

    const politica = await http('PUT', '/supervisao/politica', { chave, corpo: { versaoEsperada: 0, conteudo: {
      assuntosAutonomos: ['horario'], textoAvisoTransferencia: 'Vou encaminhar sua mensagem para nossa equipe.',
    } } });
    passo('Política publicada pelo administrador', `HTTP ${politica.status}`);

    const sugestao = await http('POST', '/supervisao/conhecimento', { chave, corpo: { assunto: 'horario', conteudo: 'Atendemos de segunda a sexta, das 8h às 18h.' } });
    const aprovar = await http('POST', `/supervisao/conhecimento/${sugestao.json.id}/aprovar`, { chave, corpo: {
      versaoProposta: 1, permissoes: { classificacoes: ['Publico'], escopoTipo: 'Empresa' } } });
    passo('Conhecimento sugerido e aprovado por humano', `sugestão ${sugestao.status}, aprovação ${aprovar.status} (versão ${aprovar.json?.versao})`);

    const corpo = webhookTexto(numero, '5500000000001', 'wamid.demo.' + crypto.randomBytes(4).toString('hex'), 'Olá, meu CPF é 529.982.247-25. Qual o horário de vocês?');
    const semAssinatura = await http('POST', '/whatsapp/webhook', { corpo });
    passo('Webhook sem assinatura', `HTTP ${semAssinatura.status} (recusado, nada gravado)`);
    const assinado = await http('POST', '/whatsapp/webhook', { corpo, cabecalhos: { 'X-Hub-Signature-256': assinar(corpo) } });
    const reenvio = await http('POST', '/whatsapp/webhook', { corpo, cabecalhos: { 'X-Hub-Signature-256': assinar(corpo) } });
    passo('Mensagem simulada do cliente (webhook assinado)', `HTTP ${assinado.status}; reenvio do mesmo evento HTTP ${reenvio.status} (sem duplicar)`);

    let conversas = [];
    for (let i = 0; i < 20; i++) {
      conversas = (await http('GET', '/supervisao/conversas', { chave })).json || [];
      if (conversas.some((c) => c.estado !== 'Automatico')) break;
      await esperar(500);
    }
    const conversa = conversas[0];
    passo('Trabalhador da IA tratou a mensagem', `estado da conversa: ${conversa?.estado} (motor de IA ainda não escolhido: vai para supervisão)`);

    const detalhe = (await http('GET', `/supervisao/conversas/${conversa.id}`, { chave })).json;
    const entrada = detalhe.mensagens.find((m) => m.direcao === 'entrada');
    passo('Mensagem guardada com o CPF mascarado', `"${entrada.texto}"`);
    passo('Decisões registradas', detalhe.decisoes.map((d) => `${d.tipo}=${d.resultado}${d.motivo ? ' (' + d.motivo + ')' : ''}`).join('; '));

    const assumir = await http('POST', `/supervisao/conversas/${conversa.id}/assumir`, { chave });
    const responder = await http('POST', `/supervisao/conversas/${conversa.id}/responder`, { chave, corpo: { texto: 'Olá! Atendemos de segunda a sexta, das 8h às 18h.' } });
    const envios = (await http('GET', `/supervisao/conversas/${conversa.id}`, { chave })).json.envios;
    passo('Humano assumiu e respondeu pela API', `assumir ${assumir.status}, responder ${responder.status}; fila de envio: ${envios.map((e) => e.origem + '=' + e.status).join(', ')} (nada sai: envio desligado)`);

    const verificacao = await http('GET', '/whatsapp/webhook?hub.mode=subscribe&hub.verify_token=demo-token-verificacao&hub.challenge=4242');
    passo('Verificação do webhook (como a Meta faria)', `HTTP ${verificacao.status}, desafio devolvido: ${verificacao.texto}`);
  } finally {
    if (WIN) spawnSync('taskkill', ['/pid', String(api.pid), '/T', '/F'], { stdio: 'ignore' });
    else api.kill('SIGTERM');
  }

  const arquivo = path.join(PASTA, 'relatorio.md');
  fs.writeFileSync(arquivo, '# Demonstração local\n\n' + new Date().toISOString() + '\n\n' + relatorio.join('\n') + '\n');
  console.log(`\nDemonstração concluída. Relatório: ${arquivo}`);
}

main().catch((e) => { console.error('\nA demonstração falhou: ' + e.message); process.exitCode = 1; });
