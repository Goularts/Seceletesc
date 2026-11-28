# Guia do administrador

O que depende de você, em ordem de necessidade. **Nada aqui é obrigatório agora:** a decisão atual é não contratar nada e demonstrar localmente (2026-11-25).

**Regra de segredos:** nunca cole tokens, senhas ou chaves no chat. Configure-os você mesmo no seu terminal com `dotnet user-secrets` (fica na sua máquina, fora do projeto e do Git) ou em variáveis de ambiente do servidor. Os comandos abaixo são para o CMD do Windows.

---

## A. O que o agente faz sozinho (sem você)

| Ação | Como verificar |
|---|---|
| Subir o banco local, aplicar migrações, criar empresa e usuários de teste | `node ferramentas\demo\demo-local.cjs` termina com "Demonstração concluída" |
| Rodar os testes de regressão | `node ferramentas\portao\testar.cjs` mostra "testes passaram" |
| Manter o portão de mudanças e a memória do Ruflo (numa máquina nova: `npm ci --ignore-scripts` uma vez) | `node ferramentas\portao\prova.cjs` mostra "11 de 11 verificações OK" |
| Documentação, testes, correções técnicas, PRs com CI verde | PR no GitHub com os 3 checks verdes |

---

## B. Ações que exigem você

### B1. Rodar a demonstração local (agora, opcional)

- **Por quê:** ver o sistema funcionando sem contratar nada.
- **Precisa ter instalado:** Docker Desktop (https://www.docker.com/products/docker-desktop/), .NET SDK 10 (https://dotnet.microsoft.com/download/dotnet/10.0) e Node.js (https://nodejs.org). Os três já estão nesta máquina.
- **Passo a passo:**
  1. Abra o Docker Desktop e espere ficar "running".
  2. No CMD, na pasta do projeto: `cd C:\dev\Seceletesc`
  3. `node ferramentas\demo\demo-local.cjs`
- **Como verificar:** a última linha é "Demonstração concluída" e aponta o relatório em `.local\demo\...\relatorio.md`.
- **Custo:** nenhum (Docker Desktop é gratuito para uso pessoal e empresas pequenas; confira a licença em https://www.docker.com/pricing/).
- **Obrigatório agora?** Não.

### B2. Consultar a API local você mesmo (opcional)

- **Por quê:** explorar as rotas com seus próprios dados de teste.
- **Passo a passo:**
  1. Crie a empresa e um administrador de teste (veja o README, seção "API de supervisão local"). A chave aparece uma única vez **no seu terminal**; guarde-a.
  2. No CMD: `set /p SECELETESC_CHAVE=Cole a chave: ` (só nessa janela; não salve em arquivo).
  3. Suba a API (README) e abra `docs\setup\api-local.http` no VS Code com a extensão REST Client, ou abra `http://127.0.0.1:5288/openapi/v1.json` num visualizador OpenAPI.
- **Como verificar:** `GET /supervisao/conversas` responde 200.
- **Custo:** nenhum. **Obrigatório agora?** Não.

### B3. Conta da Meta e WhatsApp como Tech Provider (quando decidir conectar; pode esperar)

- **Por quê:** a Seceletesc atende várias empresas (decisão 34). Ela tem **um app** na Meta; cada empresa cliente conecta a **própria** conta do WhatsApp (WABA) e os próprios números, e o sistema guarda uma credencial (business token) por cliente, cifrada.
- **Onde:** Meta Business (https://business.facebook.com) e Meta for Developers (https://developers.facebook.com/apps). Guias oficiais: https://developers.facebook.com/docs/whatsapp/solution-providers/get-started-for-tech-providers e https://developers.facebook.com/docs/whatsapp/embedded-signup/overview
- **Passo a passo (plataforma, uma vez):**
  1. No portfólio empresarial da Seceletesc (business.facebook.com), faça a **verificação da empresa**. Ela corre em paralelo e não bloqueia o desenvolvimento, mas é exigida para virar Tech Provider.
  2. Em developers.facebook.com/apps, crie um app do tipo "Business" e adicione o produto **WhatsApp**. Comece pelo **número de teste** do próprio app.
  3. Depois da verificação, peça na **App Review** o acesso avançado a `whatsapp_business_messaging` e `whatsapp_business_management` (a Meta pede vídeo do envio de mensagem e da criação de template). Sem isso, nenhum cliente pode ser cadastrado.
  4. Limite da Meta: até 10 clientes novos a cada 7 dias até concluir verificação, App Review e verificação de acesso (depois, 200). O cliente paga as mensagens diretamente à Meta (forma de pagamento no WhatsApp Manager dele).
- **Segredos da plataforma (no seu terminal, nunca no chat):** use o CMD (o PowerShell grava o histórico de comandos em arquivo). O valor é colado num prompt e não fica escrito no comando:
  ```cmd
  cd C:\dev\Seceletesc
  set /p VALOR=Cole o App Secret e tecle Enter:
  dotnet user-secrets set "WhatsApp:AppSecret" "%VALOR%" --project src\Seceletesc.Api
  set VALOR=
  ```
  Token de verificação do webhook: gere um valor aleatório (`node -e "console.log(require('crypto').randomBytes(24).toString('hex'))"`) e grave do mesmo jeito em `WhatsApp:TokenVerificacao`; o mesmo valor vai no campo "Verify token" do app da Meta.
  Chave que cifra as credenciais dos clientes no banco: gere com `dotnet run --project src\Seceletesc.Api -- gerar-chave-credenciais --pasta C:\seceletesc-chaves` e grave o conteúdo do arquivo em `WhatsApp:ChaveCredenciais`, sem exibir na tela:
  ```cmd
  set /p VALOR=<C:\seceletesc-chaves\chave-credenciais-whatsapp.txt
  dotnet user-secrets set "WhatsApp:ChaveCredenciais" "%VALOR%" --project src\Seceletesc.Api
  set VALOR=
  ```
  Os user-secrets ficam em texto na sua pasta de usuário (`%APPDATA%\Microsoft\UserSecrets`): servem só para desenvolvimento. Em produção, use as variáveis de ambiente ou o cofre do provedor. Sem a chave, as credenciais gravadas não podem ser lidas: guarde uma cópia em local seguro.
- **Cada empresa cliente (operador da plataforma, pela linha de comando; com a M3, pelo Embedded Signup).** São três etapas separadas, e cada uma fica no histórico da empresa com o operador e o motivo (o nome do operador é o informado no comando: ainda não é comprovado; a autenticação do operador vem antes de número real). **Antes de registrar**, confira no WhatsApp Manager da Meta que o número e o token são mesmo da WABA daquela empresa: o sistema ainda não confere isso sozinho (vem com a M3):
  1. **Conexão:** `conectar-conta --tenant ID --waba ID_DA_WABA --operador "Seu nome"`; `registrar-credencial --tenant ID --waba ID_DA_WABA --arquivo CAMINHO_DO_ARQUIVO_COM_O_TOKEN --operador "Seu nome"` (o token fica num arquivo que você mesmo cria e **apaga logo depois**; ele nunca vai na linha de comando nem aparece na tela); e `registrar-numero --tenant ID --phone-number-id ID_DO_NUMERO --waba ID_DA_WABA --operador "Seu nome"`.
  2. **Autorização operacional:** `autorizar-operacao --tenant ID --phone-number-id ID_DO_NUMERO --operador "Seu nome" --motivo "..."`.
  3. **Ativação do envio:** `habilitar-envio --tenant ID --phone-number-id ID_DO_NUMERO --operador "Seu nome" --motivo "..."`. Só é aceita com as etapas 1 e 2 cumpridas. **Nada sai** enquanto `WhatsApp:EnvioHabilitado` estiver desligado na API.

  Os comandos rodam com `dotnet run --project src\Seceletesc.Api -- COMANDO ...` e a variável `SECELETESC_CONEXAO_DONO`. Para desfazer: `desabilitar-envio`, `revogar-operacao` (também desliga o envio) e `desconectar-conta` (revoga a credencial, a autorização operacional e o envio dos números; ao reconectar, as três etapas são refeitas).
- **Como verificar:** depois do item B4, a Meta valida a URL do webhook (ela chama `GET /whatsapp/webhook` e recebe o desafio).
- **Custo:** criar a conta e o app não custa nada. As mensagens têm preço definido pela Meta e são pagas pelo cliente: tabela oficial em https://developers.facebook.com/docs/whatsapp/pricing
- **Obrigatório agora?** Não. **O envio continua desligado** até sua autorização explícita (`WhatsApp:EnvioHabilitado` e, por número, `habilitar-envio`).

### B4. Hospedagem com HTTPS (quando decidir publicar; pode esperar)

- **Por quê:** a Meta só chama webhooks em endereço público com HTTPS, e a API de supervisão exige HTTPS fora do ambiente local.
- **O que escolher:** um provedor que rode contêiner .NET e ofereça PostgreSQL gerenciado (ex.: Azure, AWS, Google Cloud, Render, Railway). A escolha tem custo mensal e é sua.
- **Informações necessárias depois:** conexões do banco para os papéis `app`, `worker`, `supervisao` e `dono` (migrações), chave pública de dados protegidos, segredos da Meta.
- **Como verificar:** `https://SEU-ENDERECO/saude` responde `{"status":"ok"}`.
- **Custo:** sim, mensal (depende do provedor). **Obrigatório agora?** Não.

### B5. Chaves de cifragem dos dados protegidos (antes de dados reais)

- **Por quê:** CPF, cartão etc. enviados pelos clientes são cifrados; só quem tem a chave privada lê o original.
- **Passo a passo:** `dotnet run --project src\Seceletesc.Api -- gerar-chaves --pasta C:\caminho\seguro\fora-do-projeto`
- **Onde configurar:** o conteúdo da chave **pública** vai em `DadosProtegidos:ChavePublicaPem` (user-secrets ou variável do servidor). A chave **privada** fica guardada com você (cofre de senhas); não vai para o servidor da API nem para o Git.
- **Como verificar:** o webhook registra mensagens (sem a chave pública, ele recusa).
- **Custo:** nenhum. **Obrigatório agora?** Só antes de receber mensagens reais.

### B6. GitHub (já feito, conferir)

- **Onde:** https://github.com/Goularts/Seceletesc/settings
- **Conferir:** "Branches" com proteção da `master` exigindo os 3 checks; "Code security" com secret scanning e push protection ligados.
- **Custo:** nenhum (repositório público). **Obrigatório agora?** Já está feito.

---

## C. Decisões funcionais que precisam da sua aprovação

| # | Decisão | Por que importa | Padrão atual |
|---|---|---|---|
| C1 | Provedor de IA (F5) | Sem ele, nenhuma resposta automática | Nenhum: tudo vai para humano |
| C2 | Assuntos que a IA responde sozinha no piloto e o conhecimento inicial | Define o que o chatbot pode dizer | Nenhum assunto configurado |
| C3 | Coexistência (app + API) ou só API | Muda como o humano responde e a retomada fora da janela | Desligada por ora (decisão 34b): nada sai fora da janela; aguarda o cliente |
| C4 | Fluxo de templates para retomada fora da janela | Hoje proibido | Proibido |
| C5 | Autenticação de produção (como as pessoas entram no painel) | Pré-requisito para publicar | Chave de API de desenvolvimento |
| C6 | Retenção de dados e LGPD (quanto tempo guardar histórico e dados cifrados) | Obrigação legal | Não definido |
| C7 | Quem pode ver o dado protegido original e o fluxo para CPF | Hoje ninguém vê pelo sistema | Bloqueado |
| C8 | Mensagens que não são texto (mídia, áudio) | Hoje vão para humano | Supervisão |
| C9 | Limites de atraso da fila (aviso e escalonamento) | Configuráveis por empresa | 5 e 15 minutos |

---

## D. Opcional ou futuro

| Item | Quando |
|---|---|
| Painel web de supervisão (F6) | Depois da F5 |
| Allowlist de IPs da Meta no proxy e log agregado do webhook | Na implantação |
| Monitoramento e alertas (uptime, erros) | Na implantação |
| Memória vetorial mais forte no Ruflo (modelo de vetores melhor) | Se a busca semântica atual não bastar |
| Configuração do Codex (MCP do Ruflo no Codex) | Se o Codex passar a ser usado no projeto; exige mudar configuração global do Codex |
