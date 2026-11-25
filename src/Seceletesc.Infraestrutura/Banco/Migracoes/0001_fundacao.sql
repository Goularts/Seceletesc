-- F0 Fundação: tenants, clientes, histórico append-only, RLS.
-- Executado pelo papel seceletesc_dono (dono das tabelas, sem superusuário e sem BYPASSRLS).
-- Os papéis são criados antes, por Banco/Provisionamento/provisionar-papeis.sql.
-- Ver docs/desenvolvimento/plano-tecnico.md, seções 4.5 e 4.7.

do $$
begin
  if not exists (select 1 from pg_roles where rolname = 'seceletesc_app')
     or not exists (select 1 from pg_roles where rolname = 'seceletesc_worker') then
    raise exception 'Papéis não provisionados: rode Banco/Provisionamento/provisionar-papeis.sql antes das migrações.';
  end if;
  if exists (select 1 from pg_roles where rolname = current_user and (rolsuper or rolbypassrls)) then
    raise exception 'Migrações devem rodar com o papel dono (sem superusuário e sem BYPASSRLS), não com %.', current_user;
  end if;
end $$;

create table tenants (
  id         uuid primary key,
  nome       text not null,
  criado_em  timestamptz not null default now()
);

-- Mapeamento número do WhatsApp → tenant. Sem RLS (é consultado antes de existir tenant),
-- sem dados sensíveis e sem acesso direto: só pela função resolver_tenant_por_phone_number_id.
create table tenant_numeros_whatsapp (
  phone_number_id  text primary key,
  tenant_id        uuid not null references tenants(id)
);

create table clientes (
  id         uuid primary key,
  tenant_id  uuid not null references tenants(id),
  wa_id      text not null,
  criado_em  timestamptz not null default now(),
  unique (tenant_id, wa_id)
);

-- Histórico append-only (invariante 15).
create table eventos_historico (
  id           bigint generated always as identity primary key,
  tenant_id    uuid not null references tenants(id),
  conversa_id  uuid null,
  tipo         text not null,
  dados        jsonb not null default '{}'::jsonb,
  ocorrido_em  timestamptz not null default now()
);

create index ix_eventos_historico_tenant_conversa on eventos_historico (tenant_id, conversa_id, id);

create function bloquear_alteracao_historico() returns trigger
language plpgsql set search_path = public, pg_temp as $$
begin
  raise exception 'eventos_historico é append-only: % não é permitido', tg_op
    using errcode = 'P0001';
end $$;

create trigger tg_historico_sem_update_delete
  before update or delete on eventos_historico
  for each row execute function bloquear_alteracao_historico();

create trigger tg_historico_sem_truncate
  before truncate on eventos_historico
  for each statement execute function bloquear_alteracao_historico();

-- Quem insere não escolhe id nem horário: impede evento retroativo e OVERRIDING SYSTEM VALUE.
create function carimbar_evento_historico() returns trigger
language plpgsql security definer set search_path = public, pg_temp as $$
begin
  new.id := nextval(pg_get_serial_sequence('public.eventos_historico', 'id'));
  new.ocorrido_em := now();
  return new;
end $$;

create trigger tg_historico_carimbo
  before insert on eventos_historico
  for each row execute function carimbar_evento_historico();

-- Tenant atual da transação. Sem tenant definido, falha (nunca devolve "tudo"):
-- current_setting sem missing_ok gera erro 42704 se a variável nunca foi definida na sessão;
-- se já foi definida e a transação acabou, volta '' e a conversão para uuid falha (22P02).
create function tenant_atual() returns uuid
language sql stable set search_path = public, pg_temp as $$
  select current_setting('app.tenant_id')::uuid
$$;

-- RLS forçado: vale também para o dono das tabelas (que não é superusuário).
alter table tenants enable row level security;
alter table tenants force row level security;
create policy tenants_isolamento on tenants
  using (id = tenant_atual())
  with check (id = tenant_atual());

alter table clientes enable row level security;
alter table clientes force row level security;
create policy clientes_isolamento on clientes
  using (tenant_id = tenant_atual())
  with check (tenant_id = tenant_atual());

alter table eventos_historico enable row level security;
alter table eventos_historico force row level security;
create policy eventos_isolamento on eventos_historico
  using (tenant_id = tenant_atual())
  with check (tenant_id = tenant_atual());

-- Resolução do tenant a partir do webhook, antes de existir tenant na transação.
-- SECURITY DEFINER mínimo: devolve só o id. Roda como o dono, que não é superusuário.
create function resolver_tenant_por_phone_number_id(p_phone_number_id text) returns uuid
language sql stable security definer set search_path = public, pg_temp as $$
  select tenant_id from tenant_numeros_whatsapp where phone_number_id = p_phone_number_id
$$;

-- Permissões mínimas. Funções têm EXECUTE para PUBLIC por padrão: retirar.
revoke all on tenants, tenant_numeros_whatsapp, clientes, eventos_historico from public;
revoke all on function resolver_tenant_por_phone_number_id(text) from public;
revoke all on function carimbar_evento_historico() from public;
revoke all on function bloquear_alteracao_historico() from public;
revoke all on function tenant_atual() from public;

grant usage on schema public to seceletesc_app, seceletesc_worker;
grant select on tenants to seceletesc_app, seceletesc_worker;
grant select, insert on clientes to seceletesc_app;
grant select on clientes to seceletesc_worker;
grant select, insert on eventos_historico to seceletesc_app, seceletesc_worker;
grant execute on function tenant_atual() to seceletesc_app, seceletesc_worker;
grant execute on function resolver_tenant_por_phone_number_id(text) to seceletesc_app;
