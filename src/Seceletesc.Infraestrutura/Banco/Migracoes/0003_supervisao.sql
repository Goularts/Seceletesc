-- F2 Supervisão humana: usuários da supervisão, mensagem humana pela API e permissões do papel de supervisão.
-- Executado por seceletesc_dono. Ver docs/desenvolvimento/plano-tecnico.md, seção 4.9.

-- ---------------------------------------------------------------- usuários da supervisão
-- Autenticação de desenvolvimento por chave de API (decisão pendente: autenticação definitiva).
-- Só o hash SHA-256 da chave é guardado; a chave tem 256 bits aleatórios, então um hash rápido basta.
-- Sem RLS: é consultada antes de existir tenant na transação. Sem acesso direto: só por autenticar_supervisor.
create table usuarios_supervisao (
  id            uuid primary key,
  tenant_id     uuid not null references tenants(id),
  nome          text not null,
  chave_sha256  text not null unique check (chave_sha256 ~ '^[0-9a-f]{64}$'),
  ativo         boolean not null default true,
  criado_em     timestamptz not null default now(),
  unique (tenant_id, id)
);

-- SECURITY DEFINER mínimo: devolve só o usuário e o tenant de uma chave ativa.
create function autenticar_supervisor(p_chave_sha256 text) returns table (usuario_id uuid, tenant_id uuid)
language sql stable security definer set search_path = public, pg_temp as $$
  select u.id, u.tenant_id from usuarios_supervisao u where u.chave_sha256 = p_chave_sha256 and u.ativo
$$;

-- O autor de uma mensagem humana existe, é desta empresa e está ativo. SECURITY DEFINER mínimo: a supervisão
-- não lê a tabela de usuários.
create function supervisor_ativo(p_tenant uuid, p_usuario uuid) returns boolean
language sql stable security definer set search_path = public, pg_temp as $$
  select exists (select 1 from usuarios_supervisao u where u.id = p_usuario and u.tenant_id = p_tenant and u.ativo)
$$;

-- ---------------------------------------------------------------- mensagem humana no outbox
-- Mensagem automática: tem decisão aprovada (0002). Mensagem humana: não tem decisão, tem autor.
-- A validação das novas restrições varre o outbox de TODOS os tenants. Com RLS forçado, o dono veria só um
-- tenant (ou falharia sem tenant). O FORCE sai e volta na mesma transação da migração (DDL é transacional).
alter table outbox no force row level security;
alter table outbox alter column decisao_id drop not null;
alter table outbox add column autor_id uuid;
alter table outbox add constraint ck_outbox_origem check ((decisao_id is null) = (autor_id is not null));
alter table outbox add constraint fk_outbox_autor foreign key (tenant_id, autor_id) references usuarios_supervisao (tenant_id, id);
alter table outbox force row level security;

-- Mesma restrição central da 0002 para mensagens automáticas. Mensagem humana só entra pela supervisão,
-- com autor, em conversa sob controle humano: a aplicação e o worker não conseguem criar uma.
create or replace function exigir_decisao_aprovada() returns trigger
language plpgsql set search_path = public, pg_temp as $$
declare
  d decisoes_envio%rowtype;
  cv conversas%rowtype;
begin
  if new.status <> 'pendente' then
    raise exception 'Mensagem entra na fila como pendente' using errcode = 'P0001';
  end if;
  select * into cv from conversas where id = new.conversa_id;
  if new.decisao_id is null then
    if not pg_has_role(current_user, 'seceletesc_supervisao', 'member') then
      raise exception 'Mensagem humana só pela supervisão' using errcode = 'P0001';
    end if;
    if new.autor_id is null
       or cv.id is null or cv.tenant_id <> new.tenant_id or cv.estado <> 'Humano'
       or not supervisor_ativo(new.tenant_id, new.autor_id)
       -- Validade limitada: a API usa 10 minutos. O limite do banco é folgado porque os testes usam relógio próprio.
       or new.expira_em > now() + interval '2 days' then
      raise exception 'Mensagem humana só pela supervisão, com autor, em conversa sob controle humano' using errcode = 'P0001';
    end if;
    return new;
  end if;
  select * into d from decisoes_envio where id = new.decisao_id;
  if new.autor_id is not null
     or d.id is null or d.resultado <> 'aprovada' or d.conversa_id <> new.conversa_id
     or d.tenant_id <> new.tenant_id or cv.id is null or d.versao_estado <> cv.versao_estado
     or d.texto_sha256 <> encode(sha256(convert_to(new.texto, 'UTF8')), 'hex')
     or (d.tipo = 'resposta' and cv.estado <> 'Automatico')
     or (d.tipo = 'aviso_transferencia' and cv.estado <> 'TransferenciaPendente') then
    raise exception 'Mensagem automática sem decisão aprovada sobre a versão atual da conversa' using errcode = 'P0001';
  end if;
  return new;
end $$;

-- Máquina de estados (0002) com mais uma trava: voltar de Humano para Automatico exige registrar a liberação
-- (liberada_em novo). Sem isso, a IA passaria a tratar mensagens recebidas durante o controle humano.
create or replace function proteger_conversa() returns trigger
language plpgsql set search_path = public, pg_temp as $$
begin
  if new.tenant_id <> old.tenant_id or new.cliente_id <> old.cliente_id or new.id <> old.id then
    raise exception 'tenant, cliente e id da conversa são imutáveis' using errcode = 'P0001';
  end if;
  if new.versao_estado < old.versao_estado
     or (new.estado <> old.estado and new.versao_estado <= old.versao_estado) then
    raise exception 'versao_estado deve crescer a cada transição' using errcode = 'P0001';
  end if;
  if new.estado <> old.estado
     and not pg_has_role(current_user, 'seceletesc_supervisao', 'member')
     and not (old.estado = 'Automatico' and new.estado = 'TransferenciaPendente') then
    raise exception 'Transição % -> % só pela supervisão humana', old.estado, new.estado using errcode = 'P0001';
  end if;
  if new.liberada_em is distinct from old.liberada_em and not pg_has_role(current_user, 'seceletesc_supervisao', 'member') then
    raise exception 'Liberação só pela supervisão humana' using errcode = 'P0001';
  end if;
  if old.estado = 'Humano' and new.estado = 'Automatico'
     and (new.liberada_em is null or new.liberada_em is not distinct from old.liberada_em) then
    raise exception 'Voltar ao modo automático exige registrar a liberação' using errcode = 'P0001';
  end if;
  return new;
end $$;

-- Autor também é imutável depois de entrar na fila.
create or replace function proteger_estado_terminal_outbox() returns trigger
language plpgsql set search_path = public, pg_temp as $$
begin
  if old.status in ('enviada', 'cancelada', 'incerta') then
    raise exception 'Mensagem em estado terminal (%) não pode mudar', old.status using errcode = 'P0001';
  end if;
  if new.texto <> old.texto or new.decisao_id is distinct from old.decisao_id or new.conversa_id <> old.conversa_id
     or new.autor_id is distinct from old.autor_id then
    raise exception 'Conteúdo da mensagem na fila é imutável' using errcode = 'P0001';
  end if;
  new.atualizada_em := now();
  return new;
end $$;

-- ---------------------------------------------------------------- permissões
revoke all on usuarios_supervisao from public;
revoke all on function autenticar_supervisor(text) from public;
revoke all on function supervisor_ativo(uuid, uuid) from public;
grant execute on function autenticar_supervisor(text), supervisor_ativo(uuid, uuid) to seceletesc_supervisao;

-- A supervisão só altera o que as transições dela precisam. Não mexe na última mensagem do cliente
-- (que define a janela de 24h) nem nos demais campos.
revoke update on conversas from seceletesc_supervisao;
grant update (estado, versao_estado, liberada_em) on conversas to seceletesc_supervisao;

-- A supervisão grava mensagens humanas na fila (o worker é quem envia).
grant insert on outbox to seceletesc_supervisao;
