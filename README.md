# Seceletesc

Plataforma de atendimento inteligente por WhatsApp com supervisão humana e conhecimento controlado.

- Regras do produto: [`docs/business-rules/`](docs/business-rules/)
- Contexto e decisões: [`docs/project-context.md`](docs/project-context.md)
- Plano técnico: [`docs/desenvolvimento/plano-tecnico.md`](docs/desenvolvimento/plano-tecnico.md)
- Qualidade e testes: [`docs/desenvolvimento/qualidade.md`](docs/desenvolvimento/qualidade.md)

## Requisitos

- .NET SDK 10
- Docker (os testes de integração sobem um PostgreSQL real via Testcontainers)

## Rodar os testes (CMD)

```cmd
dotnet test
```

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
dotnet run --project src\Seceletesc.Api -- criar-supervisor --tenant ID_DO_TENANT --nome "Recepção"

set "ConnectionStrings__Supervisao=Host=127.0.0.1;Port=55432;Database=seceletesc;Username=supervisao_local;Password=SENHA_LOCAL"
dotnet run --project src\Seceletesc.Api
```

Em outro terminal:

```cmd
curl -H "Authorization: Bearer CHAVE" http://localhost:5224/supervisao/conversas
```

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
