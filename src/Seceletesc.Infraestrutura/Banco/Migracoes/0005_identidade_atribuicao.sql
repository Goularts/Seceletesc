-- Pós-F3: identidade comprovada no banco e atribuição de conversas. Executado por seceletesc_dono.
-- Ver plano técnico, seções 4.9 e 4.12; invariante 26.
--
-- Antes (0004), o banco conferia o papel a partir do id de usuário que a API gravava na linha: uma API comprometida
-- podia gravar o id de um administrador. Agora cada transação da supervisão comprova a identidade apresentando a
-- chave do usuário ao banco (autenticar_transacao). Toda autoria gravada (aprovar, revogar, liberar, publicar,
-- responder, encerrar pendência, atribuir) precisa ser a do usuário comprovado NESTA transação.

-- ---------------------------------------------------------------- identidade por transação
-- Sem RLS e sem permissão para nenhum papel da aplicação: só as funções abaixo (SECURITY DEFINER) leem e gravam.
create table autenticacoes_transacao (
  xid         xid8 primary key,
  usuario_id  uuid not null,
  tenant_id   uuid not null,
  criada_em   timestamptz not null default now()
);
create index ix_autenticacoes_criada_em on autenticacoes_transacao (criada_em);
revoke all on autenticacoes_transacao from public;

-- Comprova a identidade na transação atual: a chave (em claro, o hash é calculado aqui) precisa ser de um usuário
-- ativo da empresa da transação. Uma transação tem uma única identidade.
create function autenticar_transacao(p_chave text) returns table (usuario_id uuid, tenant_id uuid, papel text)
language plpgsql volatile security definer set search_path = public, pg_temp as $$
declare
  u usuarios_supervisao%rowtype;
  existente uuid;
begin
  select * into u from usuarios_supervisao x
   where x.chave_sha256 = encode(sha256(convert_to(coalesce(p_chave, ''), 'UTF8')), 'hex') and x.ativo;
  if u.id is null or u.tenant_id <> tenant_atual() then
    raise exception 'Credencial inválida para esta empresa' using errcode = '28000';
  end if;
  select a.usuario_id into existente from autenticacoes_transacao a where a.xid = pg_current_xact_id();
  if existente is null then
    insert into autenticacoes_transacao (xid, usuario_id, tenant_id) values (pg_current_xact_id(), u.id, u.tenant_id);
    -- Limpeza: o xid8 nunca se repete, então registros antigos não valem para nada.
    delete from autenticacoes_transacao a where a.criada_em < now() - interval '1 day';
  elsif existente <> u.id then
    raise exception 'A transação já tem outra identidade' using errcode = '28000';
  end if;
  return query select u.id, u.tenant_id, u.papel;
end $$;

-- Usuário comprovado nesta transação (nulo se nenhum).
create function usuario_autenticado() returns uuid
language sql stable security definer set search_path = public, pg_temp as $$
  select a.usuario_id from autenticacoes_transacao a
   where a.xid = pg_current_xact_id_if_assigned() and a.tenant_id = tenant_atual()
$$;

-- As duas funções de autoria da 0004 passam a responder só pelo usuário comprovado nesta transação. Todos os
-- triggers que conferem papel (aprovação, status, rejeição, política, liberação, pendência, mensagem humana)
-- herdam a verificação.
create or replace function papel_supervisor(p_tenant uuid, p_usuario uuid) returns text
language sql stable security definer set search_path = public, pg_temp as $$
  select u.papel from usuarios_supervisao u
   where u.id = p_usuario and u.tenant_id = p_tenant and p_tenant = tenant_atual() and u.ativo
     and p_usuario = usuario_autenticado()
$$;

create or replace function supervisor_ativo(p_tenant uuid, p_usuario uuid) returns boolean
language sql stable security definer set search_path = public, pg_temp as $$
  select exists (select 1 from usuarios_supervisao u
                  where u.id = p_usuario and u.tenant_id = p_tenant and p_tenant = tenant_atual() and u.ativo
                    and p_usuario = usuario_autenticado())
$$;

-- Usuário ativo desta empresa, sem exigir que seja quem opera (para atribuir a conversa a outra pessoa).
create function usuario_ativo_da_empresa(p_tenant uuid, p_usuario uuid) returns boolean
language sql stable security definer set search_path = public, pg_temp as $$
  select exists (select 1 from usuarios_supervisao u
                  where u.id = p_usuario and u.tenant_id = p_tenant and p_tenant = tenant_atual() and u.ativo)
$$;

-- Toda escrita feita pelo papel da supervisão exige identidade comprovada na transação, inclusive as que não gravam
-- autoria (assumir, sugerir, registrar evento). Superusuário (administração do banco) fica de fora: a aplicação
-- nunca o usa (pg_has_role é verdadeiro para superusuário em qualquer papel).
create function exigir_identidade_supervisao() returns trigger
language plpgsql set search_path = public, pg_temp as $$
begin
  if pg_has_role(current_user, 'seceletesc_supervisao', 'member')
     and not exists (select 1 from pg_roles where rolname = current_user and rolsuper)
     and usuario_autenticado() is null then
    raise exception 'Operação da supervisão sem identidade comprovada na transação' using errcode = '42501';
  end if;
  return coalesce(new, old);
end $$;

create trigger tg_conversas_identidade before update on conversas
  for each row execute function exigir_identidade_supervisao();
create trigger tg_outbox_identidade before insert or update on outbox
  for each row execute function exigir_identidade_supervisao();
create trigger tg_itens_identidade before insert or update on itens_conhecimento
  for each row execute function exigir_identidade_supervisao();
create trigger tg_versoes_identidade before insert on versoes_conhecimento
  for each row execute function exigir_identidade_supervisao();
create trigger tg_rejeicoes_identidade before insert on rejeicoes_versao
  for each row execute function exigir_identidade_supervisao();
create trigger tg_politicas_identidade before insert on politicas_tenant
  for each row execute function exigir_identidade_supervisao();
create trigger tg_eventos_identidade before insert on eventos_historico
  for each row execute function exigir_identidade_supervisao();

-- Revisão pós-F3 (S1): o usuário registrado num evento da supervisão é o comprovado na transação, não o que a
-- aplicação escreve. O banco recusa evento da supervisão que cite outro usuário (ou nenhum).
create function conferir_usuario_do_evento() returns trigger
language plpgsql set search_path = public, pg_temp as $$
begin
  if pg_has_role(current_user, 'seceletesc_supervisao', 'member')
     and not exists (select 1 from pg_roles where rolname = current_user and rolsuper)
     and (new.dados->>'usuario') is distinct from usuario_autenticado()::text then
    raise exception 'Evento da supervisão precisa registrar o usuário comprovado na transação' using errcode = 'P0001';
  end if;
  return new;
end $$;

create trigger tg_eventos_usuario before insert on eventos_historico
  for each row execute function conferir_usuario_do_evento();

-- ---------------------------------------------------------------- autoria de versões propostas
alter table versoes_conhecimento no force row level security;
alter table versoes_conhecimento
  add column proposta_por uuid,
  add constraint fk_versao_proposta_por foreign key (tenant_id, proposta_por) references usuarios_supervisao (tenant_id, id);
alter table versoes_conhecimento force row level security;

create function proteger_autoria_versao() returns trigger
language plpgsql set search_path = public, pg_temp as $$
begin
  if not exists (select 1 from pg_roles where rolname = current_user and rolsuper) then
    -- Versão gravada pela supervisão: o autor é sempre o usuário comprovado (revisão pós-F3, S1). Outros papéis
    -- (sugestão da IA, F5) não têm autor humano.
    if pg_has_role(current_user, 'seceletesc_supervisao', 'member') then
      new.proposta_por := usuario_autenticado();
    elsif new.proposta_por is not null then
      raise exception 'Autor da versão precisa ser o usuário comprovado na transação' using errcode = 'P0001';
    end if;
  end if;
  return new;
end $$;

create trigger tg_versoes_autoria before insert on versoes_conhecimento
  for each row execute function proteger_autoria_versao();

-- ---------------------------------------------------------------- atribuição de conversas
-- Atendimentos que precisam de humano formam uma fila (sem responsável). O atendente pega da fila só para si;
-- supervisor e administrador atribuem, reatribuem e retiram. Liberar para a IA tira a atribuição.
alter table conversas no force row level security;
alter table conversas
  add column atribuida_a uuid,
  add constraint fk_conversa_atribuida_a foreign key (tenant_id, atribuida_a) references usuarios_supervisao (tenant_id, id);
alter table conversas force row level security;
create index ix_conversas_atribuida on conversas (tenant_id, atribuida_a);

create or replace function proteger_conversa() returns trigger
language plpgsql set search_path = public, pg_temp as $$
declare
  papel text;
  operador uuid;
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
  -- Atendente (revisão pós-F3, S4): só tira da IA uma conversa da fila (pendência de retomada) e não mexe no estado
  -- de conversa atribuída a outra pessoa.
  if new.estado <> old.estado and pg_has_role(current_user, 'seceletesc_supervisao', 'member') then
    operador := usuario_autenticado();
    if papel_supervisor(new.tenant_id, operador) = 'Atendente'
       and ((old.estado = 'Automatico' and not old.marcada_retomada_humana)
            or (old.atribuida_a is not null and old.atribuida_a <> operador)) then
      raise exception 'Atendente só assume atendimentos da fila ou atribuídos a ele' using errcode = 'P0001';
    end if;
  end if;
  if new.liberada_em is distinct from old.liberada_em and not pg_has_role(current_user, 'seceletesc_supervisao', 'member') then
    raise exception 'Liberação só pela supervisão humana' using errcode = 'P0001';
  end if;
  if old.estado = 'Humano' and new.estado = 'Automatico'
     and (new.liberada_em is null or new.liberada_em is not distinct from old.liberada_em) then
    raise exception 'Voltar ao modo automático exige registrar a liberação' using errcode = 'P0001';
  end if;
  -- Quem libera fica registrado e precisa ser supervisor ou administrador (atendente não libera).
  if new.estado = 'Humano' and old.estado <> 'Humano' then
    new.liberada_por := null;  -- cada liberação registra o próprio responsável
  elsif old.estado = 'Humano' and new.estado = 'Automatico' then
    papel := papel_supervisor(new.tenant_id, new.liberada_por);
    if papel is null or papel not in ('Supervisor', 'Administrador') then
      raise exception 'Liberar a conversa para a IA exige supervisor ou administrador' using errcode = 'P0001';
    end if;
  elsif new.liberada_por is distinct from old.liberada_por then
    raise exception 'Responsável pela liberação só muda ao liberar' using errcode = 'P0001';
  end if;
  if not old.marcada_retomada_humana and new.marcada_retomada_humana then
    -- Pendência reaberta: o encerramento anterior não vale para ela.
    new.retomada_encerrada_por := null;
    new.retomada_encerrada_em := null;
    new.retomada_motivo := null;
  elsif old.marcada_retomada_humana and not new.marcada_retomada_humana then
    if not pg_has_role(current_user, 'seceletesc_supervisao', 'member') then
      raise exception 'A pendência de retomada só é encerrada pela supervisão humana' using errcode = 'P0001';
    end if;
    papel := papel_supervisor(new.tenant_id, new.retomada_encerrada_por);
    if papel is null or papel not in ('Supervisor', 'Administrador')
       or coalesce(btrim(new.retomada_motivo), '') = '' then
      raise exception 'A pendência de retomada só é encerrada por supervisor ou administrador, com motivo' using errcode = 'P0001';
    end if;
    new.retomada_encerrada_em := now();
  elsif new.retomada_encerrada_por is distinct from old.retomada_encerrada_por
     or new.retomada_encerrada_em is distinct from old.retomada_encerrada_em
     or new.retomada_motivo is distinct from old.retomada_motivo then
    raise exception 'O registro de encerramento da pendência só muda ao encerrá-la' using errcode = 'P0001';
  end if;
  -- Atribuição. De volta à IA, a conversa sai da responsabilidade de quem a atendia. Conversa com a IA não recebe
  -- responsável (revisão pós-F3, G3/S7: recusar em vez de zerar em silêncio).
  if new.estado = 'Automatico' and old.estado <> 'Automatico' then
    new.atribuida_a := null;
  elsif new.estado = 'Automatico' and new.atribuida_a is not null then
    raise exception 'Conversa com a IA não tem responsável humano' using errcode = 'P0001';
  elsif new.atribuida_a is distinct from old.atribuida_a then
    if not pg_has_role(current_user, 'seceletesc_supervisao', 'member') then
      raise exception 'Atribuição só pela supervisão humana' using errcode = 'P0001';
    end if;
    operador := usuario_autenticado();
    papel := papel_supervisor(new.tenant_id, operador);
    if papel is null then
      raise exception 'Atribuição exige usuário ativo desta empresa' using errcode = 'P0001';
    end if;
    if papel = 'Atendente' then
      -- O atendente só pega da fila, para si mesmo.
      if old.atribuida_a is not null or new.atribuida_a is distinct from operador then
        raise exception 'Atendente só pega para si um atendimento da fila' using errcode = 'P0001';
      end if;
    elsif new.atribuida_a is not null and not usuario_ativo_da_empresa(new.tenant_id, new.atribuida_a) then
      raise exception 'Responsável precisa ser usuário ativo desta empresa' using errcode = 'P0001';
    end if;
  end if;
  return new;
end $$;

-- Mensagem humana: além da 0003, o atendente só responde conversa atribuída a ele.
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
    if papel_supervisor(new.tenant_id, new.autor_id) = 'Atendente' and cv.atribuida_a is distinct from new.autor_id then
      raise exception 'Atendente só responde conversa atribuída a ele' using errcode = 'P0001';
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

-- Revisão pós-F3 (S8): no momento do envio, o autor de uma resposta humana ainda está ativo e, se for atendente,
-- a conversa ainda é dele. Usado pelo worker, que não lê a tabela de usuários.
create function autor_ainda_pode_enviar(p_tenant uuid, p_conversa uuid, p_autor uuid) returns boolean
language sql stable security definer set search_path = public, pg_temp as $$
  select exists (
    select 1 from usuarios_supervisao u join conversas cv on cv.id = p_conversa and cv.tenant_id = p_tenant
     where u.id = p_autor and u.tenant_id = p_tenant and p_tenant = tenant_atual() and u.ativo
       and (u.papel <> 'Atendente' or cv.atribuida_a = u.id))
$$;

-- ---------------------------------------------------------------- permissões
revoke all on function autenticar_transacao(text), usuario_autenticado(), usuario_ativo_da_empresa(uuid, uuid),
  exigir_identidade_supervisao(), proteger_autoria_versao(), conferir_usuario_do_evento(),
  autor_ainda_pode_enviar(uuid, uuid, uuid) from public;
grant execute on function autor_ainda_pode_enviar(uuid, uuid, uuid) to seceletesc_worker;
grant execute on function autenticar_transacao(text), usuario_autenticado(), usuario_ativo_da_empresa(uuid, uuid) to seceletesc_supervisao;
grant execute on function usuario_autenticado() to seceletesc_app, seceletesc_worker;
grant update (atribuida_a) on conversas to seceletesc_supervisao;
