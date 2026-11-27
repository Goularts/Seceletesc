# Seceletesc

Plataforma de atendimento inteligente por WhatsApp com supervisão humana e conhecimento controlado.

- Regras do produto: [`docs/business-rules/`](docs/business-rules/)
- Contexto e decisões: [`docs/project-context.md`](docs/project-context.md)
- Plano técnico: [`docs/desenvolvimento/plano-tecnico.md`](docs/desenvolvimento/plano-tecnico.md)
- Qualidade e testes: [`docs/desenvolvimento/qualidade.md`](docs/desenvolvimento/qualidade.md)

## Requisitos

- .NET SDK 10
- Docker (os testes de integração sobem um PostgreSQL real via Testcontainers)

## Para começar

- Estado real do projeto (o que funciona e o que não): [`docs/setup/estado-do-projeto.md`](docs/setup/estado-do-projeto.md)
- O que depende do administrador: [`docs/setup/GUIA-ADMINISTRADOR.md`](docs/setup/GUIA-ADMINISTRADOR.md)
- Portão de mudanças (Ruflo + Git + testes): [`docs/desenvolvimento/portao.md`](docs/desenvolvimento/portao.md)

## Demonstração local, sem serviços externos (CMD)

Com o Docker Desktop aberto:

```cmd
node ferramentas\demo\demo-local.cjs
```

Sobe o banco, aplica as migrações, cria uma empresa de demonstração, sobe a API, simula o WhatsApp com um webhook assinado e mostra o registro mascarado, a supervisão e a resposta humana na fila. Nada é enviado à Meta.

## Rodar os testes (CMD)

```cmd
dotnet test
```

Pelo portão (grava o resultado para a verificação de fim de tarefa): `node ferramentas\portao	estar.cjs`.

## Banco local (CMD)

```cmd
docker compose up -d --wait
```

O PostgreSQL local fica em `127.0.0.1:55432` (mude com a variável `SECELETESC_DB_PORTA`). O superusuário do container só provisiona os papéis; a aplicação usa `app_local`, as migrações usam `seceletesc_dono`.

## API de supervisão local (CMD)

Autenticação de desenvolvimento: cada usuário da supervisão tem uma chave de API, criada por linha de comando com a conexão do papel dono. A autenticação de produção ainda será definida.

`SENHA_LOCAL` é a de `SECELETESC_DB_SENHA_LOCAL` (veja o padrão em `docker-compose.yml`). Com o banco local no ar (se o volume é anterior à F2, recrie-o com `docker compose down -v` para criar o usuário `supervisao_local`):

```cmd
set "SECELETESC_CONEXAO_DONO=Host=127.0.0.1;Port=55432;Database=seceletesc;Username=seceletesc_dono;Password=SENHA_LOCAL"
dotnet run --project src\Seceletesc.Api -- migrar
dotnet run --project src\Seceletesc.Api -- criar-tenant --nome "Empresa de teste"
dotnet run --project src\Seceletesc.Api -- criar-supervisor --tenant ID_DO_TENANT --nome "Administração" --papel Administrador

set "ConnectionStrings__Supervisao=Host=127.0.0.1;Port=55432;Database=seceletesc;Username=supervisao_local;Password=SENHA_LOCAL"
dotnet run --project src\Seceletesc.Api
```

Em outro terminal:

```cmd
curl -H "Authorization: Bearer CHAVE" http://localhost:5224/supervisao/conversas
```

Documento OpenAPI (só em desenvolvimento): `GET /openapi/v1.json`. Exemplos de requisição: `docs/setup/api-local.http`.

Outros comandos: `gerar-chaves --pasta PASTA_FORA_DO_GIT` (chaves de cifragem dos dados protegidos) e os comandos do operador da plataforma para as contas do WhatsApp de cada empresa (`conectar-conta`, `registrar-credencial`, `registrar-numero`, `autorizar-operacao`, `habilitar-envio` e os inversos; rode o projeto sem argumentos válidos para ver o uso). Roteiro em `docs/setup/GUIA-ADMINISTRADOR.md`, seção B3.

Rotas (todas exigem a chave; o tenant vem do usuário):

| Rota | O que faz |
|---|---|
| `GET /supervisao/conversas?estado=` | lista as conversas da empresa |
| `GET /supervisao/conversas/{id}` | detalhe: mensagens (com dados protegidos mascarados), decisões e envios |
| `POST /supervisao/conversas/{id}/assumir` | humano assume; a IA fica muda |
| `POST /supervisao/conversas/{id}/liberar` | `{"versaoEsperada": n}`: devolve à IA |
| `POST /supervisao/conversas/{id}/responder` | `{"texto": "..."}`: resposta humana pelo mesmo número |
| `POST /supervisao/conversas/{id}/orientar` | `{"texto": "...", "assunto": "..."}`: vira sugestão REV |
| `GET /supervisao/politica` | política vigente |
| `PUT /supervisao/politica` | `{"versaoEsperada": n, "conteudo": {...}}`: publica nova versão |
| `POST /supervisao/conversas/{id}/atribuir` | `{"responsavel": "ID"}` ou `null` para devolver à fila (supervisor ou administrador) |
| `POST /supervisao/conversas/{id}/encerrar-retomada` | `{"motivo": "..."}`: encerra a pendência de retomada pelo celular |
| `GET /supervisao/conhecimento?status=` | lista o conhecimento da empresa |
| `GET /supervisao/conhecimento/{id}` | versões e auditoria |
| `POST /supervisao/conhecimento` | `{"assunto", "conteudo", "conversaOrigem"?, "sugeridas"?}`: nova sugestão (Rev) |
| `POST /supervisao/conhecimento/{id}/editar` | `{"versaoBase", "conteudo", "permissoes"?}`: nova versão proposta |
| `POST /supervisao/conhecimento/{id}/aprovar` | `{"versaoProposta", "permissoes": {"classificacoes", "escopoTipo", "escopoRef"?, "validoAte"?}}` |
| `POST /supervisao/conhecimento/{id}/rejeitar` | `{"motivo"}` |
| `POST /supervisao/conhecimento/{id}/revogar` | `{"motivo"}` (só administrador) |

Papéis (`--papel`, padrão `Atendente`; troca com `alterar-papel --usuario ID --papel PAPEL`): o atendente vê as conversas atribuídas a ele e a fila, pega da fila ao assumir, responde, orienta e sugere, e vê só conhecimento aprovado PUB e TMP; o supervisor também libera, encerra pendências e revisa e aprova conhecimento comum; o administrador também publica a política, aprova NEG, EXC, RES e CLI e revoga. Fora do ambiente de desenvolvimento, a API de supervisão só aceita HTTPS.
