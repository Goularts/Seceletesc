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

### B3. Conta da Meta e WhatsApp (quando decidir conectar; pode esperar)

- **Por quê:** receber e enviar mensagens reais pela WhatsApp Cloud API.
- **Onde:** Meta Business (https://business.facebook.com) e Meta for Developers (https://developers.facebook.com/apps). Guia oficial: https://developers.facebook.com/docs/whatsapp/cloud-api/get-started
- **Passo a passo:**
  1. Crie ou use o portfólio empresarial em business.facebook.com e faça a verificação da empresa (Configurações > Central de segurança).
  2. Em developers.facebook.com/apps, crie um app do tipo "Business" e adicione o produto **WhatsApp**.
  3. Use primeiro o **número de teste** que a Meta oferece no próprio app (não use o número real ainda).
  4. Crie um **usuário do sistema** (Configurações do negócio > Usuários do sistema) com as permissões `whatsapp_business_messaging` e `whatsapp_business_management` e gere um token permanente.
  5. Anote, **sem enviar para ninguém**: o App Secret (Configurações do app > Básico), o token do usuário do sistema e o `phone_number_id` do número de teste.
- **Onde configurar no projeto (no seu terminal, nunca no chat):** use o CMD (o PowerShell grava o histórico de comandos em arquivo). Para cada segredo, o valor é colado num prompt e não fica escrito no comando:
  ```cmd
  cd C:\dev\Seceletesc
  set /p VALOR=Cole o App Secret e tecle Enter: 
  dotnet user-secrets set "WhatsApp:AppSecret" "%VALOR%" --project src\Seceletesc.Api
  set /p VALOR=Cole o token do usuario do sistema e tecle Enter: 
  dotnet user-secrets set "WhatsApp:TokenAcesso" "%VALOR%" --project src\Seceletesc.Api
  set VALOR=
  ```
  Token de verificação do webhook: gere um valor aleatório (`node -e "console.log(require('crypto').randomBytes(24).toString('hex'))"`) e grave do mesmo jeito em `WhatsApp:TokenVerificacao`; o mesmo valor vai no campo "Verify token" do app da Meta.
  Os user-secrets ficam em texto na sua pasta de usuário (`%APPDATA%\Microsoft\UserSecrets`): servem só para desenvolvimento. Em produção, use as variáveis de ambiente ou o cofre do provedor.
  O `phone_number_id` não é segredo: registre-o com `dotnet run --project src\Seceletesc.Api -- registrar-numero --tenant ID_DA_EMPRESA --phone-number-id ID_DA_META`.
- **Como verificar:** depois do item B4, a Meta valida a URL do webhook (ela chama `GET /whatsapp/webhook` e recebe o desafio).
- **Custo:** criar a conta e o app não custa nada. As mensagens têm preço definido pela Meta: consulte a tabela oficial em https://developers.facebook.com/docs/whatsapp/pricing antes de usar números reais.
- **Obrigatório agora?** Não. **O envio continua desligado** até sua autorização explícita (`WhatsApp:EnvioHabilitado`).

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
| C3 | Coexistência (app + API) ou só API | Muda como o humano responde e a retomada fora da janela | Sem coexistência: nada sai fora da janela; aguarda o cliente |
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
