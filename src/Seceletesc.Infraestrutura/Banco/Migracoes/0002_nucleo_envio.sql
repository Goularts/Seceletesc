-- F1b Núcleo de envio: conversas, mensagens, dados protegidos, política, conhecimento, decisões, outbox e fila.
-- Executado por seceletesc_dono. Ver docs/desenvolvimento/plano-tecnico.md, seções 4.1 a 4.7.

do $$
begin
  if not exists (select 1 from pg_roles where rolname = 'seceletesc_supervisao') then
    raise exception 'Papel seceletesc_supervisao não provisionado: rode Banco/Provisionamento/provisionar-papeis.sql.';
  end if;
end $$;

-- Tabelas imutáveis: nenhum UPDATE/DELETE/TRUNCATE, nem pelo dono.
create function bloquear_alteracao_imutavel() returns trigger
language plpgsql set search_path = public, pg_temp as $$
begin
  raise exception '% é imutável: % não é permitido', tg_table_name, tg_op using errcode = 'P0001';
end $$;

-- ---------------------------------------------------------------- conversas (4.1)
create table conversas (
  id                          uuid primary key,
  tenant_id                   uuid not null references tenants(id),
  cliente_id                  uuid not null references clientes(id),
  estado                      text not null check (estado in ('Automatico', 'TransferenciaPendente', 'Humano')),
  versao_estado               bigint not null check (versao_estado >= 1),
  ultima_mensagem_cliente_em  timestamptz,
  liberada_em                 timestamptz,
  marcada_retomada_humana     boolean not null default false,
  criada_em                   timestamptz not null default now(),
  unique (tenant_id, cliente_id)
);

-- A versão do estado só cresce: nenhum código consegue "voltar" uma conversa para uma versão antiga.
create function exigir_versao_crescente() returns trigger
language plpgsql set search_path = public, pg_temp as $$
begin
  if new.versao_estado < old.versao_estado
     or (new.estado <> old.estado and new.versao_estado <= old.versao_estado) then
    raise exception 'versao_estado deve crescer a cada transição' using errcode = 'P0001';
  end if;
  return new;
end $$;

create trigger tg_conversas_versao before update on conversas
  for each row execute function exigir_versao_crescente();

-- ---------------------------------------------------------------- mensagens (append-only, sem dados protegidos)
create table mensagens (
  id               uuid primary key,
  tenant_id        uuid not null references tenants(id),
  conversa_id      uuid not null references conversas(id),
  direcao          text not null check (direcao in ('entrada', 'saida')),
  texto_mascarado  text not null,
  wamid            text,
  ocorrida_em      timestamptz not null,
  criada_em        timestamptz not null default now(),
  unique (tenant_id, wamid)
);
create index ix_mensagens_conversa on mensagens (tenant_id, conversa_id, ocorrida_em);
create trigger tg_mensagens_imutaveis before update or delete on mensagens
  for each row execute function bloquear_alteracao_imutavel();
create trigger tg_mensagens_sem_truncate before truncate on mensagens
  for each statement execute function bloquear_alteracao_imutavel();

-- ---------------------------------------------------------------- dados protegidos (invariante 23)
-- Valor original cifrado (AES-GCM com chave por registro, envolvida por RSA-OAEP). A aplicação só grava;
-- só a supervisão lê, e só quem tem a chave privada decifra.
create table dados_protegidos (
  id             uuid primary key,
  tenant_id      uuid not null references tenants(id),
  conversa_id    uuid not null references conversas(id),
  mensagem_id    uuid not null references mensagens(id),
  categoria      text not null,
  chave_cifrada  bytea not null,
  nonce          bytea not null,
  cifrado        bytea not null,
  tag            bytea not null,
  criado_em      timestamptz not null default now()
);
create trigger tg_dados_protegidos_imutaveis before update or delete on dados_protegidos
  for each row execute function bloquear_alteracao_imutavel();

-- ---------------------------------------------------------------- política por tenant (4.6), versões imutáveis
create table politicas_tenant (
  tenant_id  uuid not null references tenants(id),
  versao     int not null check (versao >= 1),
  conteudo   jsonb not null,
  criada_em  timestamptz not null default now(),
  primary key (tenant_id, versao)
);
create trigger tg_politicas_imutaveis before update or delete on politicas_tenant
  for each row execute function bloquear_alteracao_imutavel();

-- ---------------------------------------------------------------- conhecimento (4.6)
create table itens_conhecimento (
  id              uuid primary key,
  tenant_id       uuid not null references tenants(id),
  assunto         text not null,
  status          text not null check (status in ('Rev', 'Aprovado', 'Rejeitado', 'Revogado')),
  versao_vigente  int not null check (versao_vigente >= 1),
  criado_em       timestamptz not null default now()
);

create table versoes_conhecimento (
  item_id         uuid not null references itens_conhecimento(id),
  versao          int not null check (versao >= 1),
  tenant_id       uuid not null references tenants(id),
  conteudo        text not null,
  classificacoes  text[] not null,
  escopo_tipo     text not null check (escopo_tipo in ('Empresa', 'Cliente', 'Proposta', 'Atendimento')),
  escopo_ref      uuid,
  valido_ate      timestamptz,
  origem          text not null,
  criada_em       timestamptz not null default now(),
  primary key (item_id, versao)
);
create trigger tg_versoes_imutaveis before update or delete on versoes_conhecimento
  for each row execute function bloquear_alteracao_imutavel();

-- Invariantes 16 e 17: fora da supervisão humana, conhecimento só nasce como sugestão (Rev) e não muda de status.
create function proteger_status_conhecimento() returns trigger
language plpgsql set search_path = public, pg_temp as $$
begin
  if pg_has_role(current_user, 'seceletesc_supervisao', 'member') then
    return new;
  end if;
  if tg_op = 'INSERT' and new.status <> 'Rev' then
    raise exception 'Somente a supervisão humana publica conhecimento: sugestões nascem com status Rev' using errcode = 'P0001';
  end if;
  if tg_op = 'UPDATE' then
    raise exception 'Somente a supervisão humana altera conhecimento' using errcode = 'P0001';
  end if;
  return new;
end $$;

create trigger tg_conhecimento_status before insert or update on itens_conhecimento
  for each row execute function proteger_status_conhecimento();

-- ---------------------------------------------------------------- decisões de envio (imutáveis, 4.3/4.7)
create table decisoes_envio (
  id                uuid primary key,
  tenant_id         uuid not null references tenants(id),
  conversa_id       uuid not null references conversas(id),
  versao_estado     bigint not null,
  tipo              text not null check (tipo in ('resposta', 'aviso_transferencia')),
  resultado         text not null check (resultado in ('aprovada', 'bloqueada')),
  motivo            text,
  detalhe           text,
  politica_versao   int,
  itens_entregues   jsonb not null default '[]'::jsonb,
  fontes_citadas    jsonb not null default '[]'::jsonb,
  criada_em         timestamptz not null default now(),
  check ((resultado = 'aprovada') = (motivo is null))
);
create trigger tg_decisoes_imutaveis before update or delete on decisoes_envio
  for each row execute function bloquear_alteracao_imutavel();

-- ---------------------------------------------------------------- outbox (4.4)
create table outbox (
  id             uuid primary key,
  tenant_id      uuid not null references tenants(id),
  conversa_id    uuid not null references conversas(id),
  decisao_id     uuid not null unique references decisoes_envio(id),
  texto          text not null,
  status         text not null check (status in ('pendente', 'enviando', 'enviada', 'cancelada', 'incerta')),
  expira_em      timestamptz not null,
  lease_ate      timestamptz,
  tentativas     int not null default 0,
  wamid          text,
  motivo         text,
  criada_em      timestamptz not null default now(),
  atualizada_em  timestamptz not null default now()
);

-- Restrição central: nenhuma mensagem automática entra na fila sem uma decisão APROVADA
-- da mesma conversa, tomada sobre a versão ATUAL do estado. Pular o validador é impossível.
create function exigir_decisao_aprovada() returns trigger
language plpgsql set search_path = public, pg_temp as $$
declare
  d decisoes_envio%rowtype;
  versao_atual bigint;
begin
  select * into d from decisoes_envio where id = new.decisao_id;
  select versao_estado into versao_atual from conversas where id = new.conversa_id;
  if d.id is null or d.resultado <> 'aprovada' or d.conversa_id <> new.conversa_id
     or d.tenant_id <> new.tenant_id or versao_atual is null or d.versao_estado <> versao_atual then
    raise exception 'Mensagem automática sem decisão aprovada sobre a versão atual da conversa' using errcode = 'P0001';
  end if;
  if new.status <> 'pendente' then
    raise exception 'Mensagem entra na fila como pendente' using errcode = 'P0001';
  end if;
  return new;
end $$;

create trigger tg_outbox_exige_decisao before insert on outbox
  for each row execute function exigir_decisao_aprovada();

-- Estado terminal não volta atrás.
create function proteger_estado_terminal_outbox() returns trigger
language plpgsql set search_path = public, pg_temp as $$
begin
  if old.status in ('enviada', 'cancelada', 'incerta') then
    raise exception 'Mensagem em estado terminal (%) não pode mudar', old.status using errcode = 'P0001';
  end if;
  if new.texto <> old.texto or new.decisao_id <> old.decisao_id or new.conversa_id <> old.conversa_id then
    raise exception 'Conteúdo da mensagem na fila é imutável' using errcode = 'P0001';
  end if;
  new.atualizada_em := now();
  return new;
end $$;

create trigger tg_outbox_terminal before update on outbox
  for each row execute function proteger_estado_terminal_outbox();

-- ---------------------------------------------------------------- fila de envio (sem RLS, só ids)
-- O worker atende todos os tenants. A fila não tem dados de negócio e só é acessível por funções.
create table fila_envio (
  outbox_id      uuid primary key references outbox(id),
  tenant_id      uuid not null,
  disponivel_em  timestamptz not null default now()
);

create function enfileirar_envio() returns trigger
language plpgsql security definer set search_path = public, pg_temp as $$
begin
  insert into fila_envio (outbox_id, tenant_id) values (new.id, new.tenant_id);
  return new;
end $$;

create trigger tg_outbox_enfileira after insert on outbox
  for each row execute function enfileirar_envio();

create function desenfileirar_envio() returns trigger
language plpgsql security definer set search_path = public, pg_temp as $$
begin
  if new.status in ('enviada', 'cancelada', 'incerta') then
    delete from fila_envio where outbox_id = new.id;
  end if;
  return new;
end $$;

create trigger tg_outbox_desenfileira after update on outbox
  for each row execute function desenfileirar_envio();

-- Reserva o próximo item disponível por um tempo (lease). Devolve só ids.
create function reservar_envio(p_lease_segundos int) returns table (outbox_id uuid, tenant_id uuid)
language sql security definer set search_path = public, pg_temp as $$
  update fila_envio f
     set disponivel_em = now() + make_interval(secs => p_lease_segundos)
   where f.outbox_id = (
     select outbox_id from fila_envio
      where disponivel_em <= now()
      order by disponivel_em
      for update skip locked
      limit 1)
  returning f.outbox_id, f.tenant_id
$$;

-- ---------------------------------------------------------------- RLS
alter table conversas enable row level security;            alter table conversas force row level security;
alter table mensagens enable row level security;            alter table mensagens force row level security;
alter table dados_protegidos enable row level security;     alter table dados_protegidos force row level security;
alter table politicas_tenant enable row level security;     alter table politicas_tenant force row level security;
alter table itens_conhecimento enable row level security;   alter table itens_conhecimento force row level security;
alter table versoes_conhecimento enable row level security; alter table versoes_conhecimento force row level security;
alter table decisoes_envio enable row level security;       alter table decisoes_envio force row level security;
alter table outbox enable row level security;               alter table outbox force row level security;

create policy conversas_isolamento on conversas using (tenant_id = tenant_atual()) with check (tenant_id = tenant_atual());
create policy mensagens_isolamento on mensagens using (tenant_id = tenant_atual()) with check (tenant_id = tenant_atual());
create policy dados_protegidos_isolamento on dados_protegidos using (tenant_id = tenant_atual()) with check (tenant_id = tenant_atual());
create policy politicas_isolamento on politicas_tenant using (tenant_id = tenant_atual()) with check (tenant_id = tenant_atual());
create policy itens_isolamento on itens_conhecimento using (tenant_id = tenant_atual()) with check (tenant_id = tenant_atual());
create policy versoes_isolamento on versoes_conhecimento using (tenant_id = tenant_atual()) with check (tenant_id = tenant_atual());
create policy decisoes_isolamento on decisoes_envio using (tenant_id = tenant_atual()) with check (tenant_id = tenant_atual());
create policy outbox_isolamento on outbox using (tenant_id = tenant_atual()) with check (tenant_id = tenant_atual());

-- ---------------------------------------------------------------- permissões
revoke all on conversas, mensagens, dados_protegidos, politicas_tenant, itens_conhecimento, versoes_conhecimento,
              decisoes_envio, outbox, fila_envio from public;
revoke all on function bloquear_alteracao_imutavel(), exigir_versao_crescente(), proteger_status_conhecimento(),
                       exigir_decisao_aprovada(), proteger_estado_terminal_outbox(), enfileirar_envio(),
                       desenfileirar_envio(), reservar_envio(int) from public;

grant usage on schema public to seceletesc_supervisao;
grant execute on function tenant_atual() to seceletesc_supervisao;

-- Aplicação (pipeline): grava a conversa e decide; nunca lê dados protegidos nem altera conhecimento.
grant select, insert, update on conversas to seceletesc_app;
grant select, insert on mensagens to seceletesc_app;
grant insert on dados_protegidos to seceletesc_app;
grant select on politicas_tenant to seceletesc_app;
grant select, insert on itens_conhecimento, versoes_conhecimento to seceletesc_app;
grant select, insert on decisoes_envio to seceletesc_app;
grant select, insert on outbox to seceletesc_app;

-- Worker: revalida e envia; só altera o andamento da mensagem na fila e a marcação de retomada.
grant select, update (marcada_retomada_humana) on conversas to seceletesc_worker;
grant select, insert on mensagens to seceletesc_worker;
grant select on politicas_tenant, itens_conhecimento, versoes_conhecimento, decisoes_envio to seceletesc_worker;
grant select, update (status, lease_ate, tentativas, wamid, motivo, atualizada_em) on outbox to seceletesc_worker;
grant execute on function reservar_envio(int) to seceletesc_worker;

-- Supervisão humana (F2/F3).
grant select, update on conversas to seceletesc_supervisao;
grant select on mensagens, dados_protegidos, decisoes_envio, outbox to seceletesc_supervisao;
grant select, insert on politicas_tenant to seceletesc_supervisao;
grant select, insert, update on itens_conhecimento to seceletesc_supervisao;
grant select, insert on versoes_conhecimento to seceletesc_supervisao;
grant select on tenants, clientes, eventos_historico to seceletesc_supervisao;
grant insert on eventos_historico to seceletesc_supervisao;
