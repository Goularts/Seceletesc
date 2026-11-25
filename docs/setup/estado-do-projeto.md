# Estado real do projeto (auditoria de 2026-11-25, F4.5)

Testes automatizados não são disponibilidade em produção. Abaixo, o que foi **executado de verdade** na máquina local, sem serviços externos (`node ferramentas\demo\demo-local.cjs`).

## Resumo

| Pergunta | Resposta |
|---|---|
| Roda localmente agora? | **Sim.** Backend .NET + PostgreSQL em Docker. Demonstração de ponta a ponta executada. |
| Banco operacional? | **Sim, localmente** (PostgreSQL 17 em Docker, porta 55432). Migrações 0001 a 0006 aplicadas. Não há banco online. |
| Swagger/OpenAPI? | **OpenAPI sim** (`/openapi/v1.json`, só em desenvolvimento, 17 rotas). Não há tela Swagger UI; dá para abrir o JSON em qualquer visualizador OpenAPI ou usar o arquivo `docs/setup/api-local.http`. |
| Interface web? | **Não.** Só backend (API). O painel é a F6. |
| Hospedagem online? | **Nenhuma.** Nada publicado. |
| Envia mensagens ao WhatsApp? | **Não.** O envio real existe no código, mas está desligado e sem credenciais. |

## O que funciona (executado na demonstração local)

| Item | Situação |
|---|---|
| API de supervisão (`/supervisao/...`): conversas, fila, atribuição, assumir, liberar, responder, orientar, política, conhecimento | **Funciona** localmente com chave de API de desenvolvimento |
| Webhook do WhatsApp (`/whatsapp/webhook`): verificação, assinatura, registro, idempotência | **Funciona** com mensagens **simuladas** (assinadas pelo script) |
| Dados protegidos: CPF mascarado no histórico e cifrado | **Funciona** (visto na demo: "meu CPF é [DADO PROTEGIDO:CPF]") |
| Fila da IA e decisão do backend | **Funciona**, mas o motor de IA é um **substituto que nunca responde**: toda mensagem vai para supervisão |
| Resposta humana pela API | **Fica na fila**, não sai (envio desligado) |
| Governança do conhecimento (sugerir, aprovar, revogar, papéis) | **Funciona** |
| Testes automatizados | 450 testes passam (domínio, arquitetura, integração com PostgreSQL real) |

## Real × simulado

| Integração | Situação |
|---|---|
| PostgreSQL | **Real** (local, Docker) |
| WhatsApp Cloud API (receber) | **Simulado**: o script assina e envia o webhook como a Meta faria |
| WhatsApp Cloud API (enviar) | **Código real, desligado**, sem token, sem número; testado só com HTTP falso |
| Coexistência (app + API) | **Não verificada**: depende do cadastro do número real |
| Motor de IA | **Não existe** (F5): substituto que falha fechado |
| Autenticação | **Desenvolvimento**: chave de API por usuário, criada por linha de comando. Não serve para produção |
| Painel web | **Não existe** (F6) |

## O que falta para o primeiro atendimento de ponta a ponta

1. **Motor de IA (F5):** escolher o provedor e implementar o adaptador. Sem isso, nenhuma resposta automática sai; tudo vai para humano.
2. **Conteúdo do piloto:** assuntos autônomos, conhecimento aprovado e textos (decisão funcional).
3. **WhatsApp real:** conta Meta, número de teste, app, token, webhook público com HTTPS (ver `GUIA-ADMINISTRADOR.md`).
4. **Hospedagem com HTTPS** e banco gerenciado.
5. **Autenticação de produção** da API de supervisão.
6. **Painel (F6)** ou, até lá, operação pela API (ferramenta como o arquivo `.http`).

Com os itens 1 e 2, já dá para demonstrar respostas automáticas localmente, ainda simulando o WhatsApp.
