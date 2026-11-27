-- M1, ajustes da revisão (docs/desenvolvimento/revisoes/2026-11-27-m1.md). Executado por seceletesc_dono.

-- S4/G8: desconectar a conta também revoga a autorização operacional dos números (padrão conservador; o Victor pode
-- mudar, ver "Decisões pendentes"). Ao reconectar, as três etapas são refeitas: nova credencial, nova autorização e
-- nova habilitação do envio, cada uma registrada.
create or replace function plataforma_desconectar_conta(p_tenant uuid, p_waba text, p_operador text, p_motivo text) returns void
language plpgsql set search_path = public, pg_temp as $$
declare
  numero record;
begin
  perform exigir_operacao_plataforma(p_tenant, p_operador, p_motivo, true);
  update contas_whatsapp set situacao = 'desconectada', desconectada_em = now()
   where waba_id = p_waba and tenant_id = p_tenant and situacao = 'conectada';
  if not found then
    raise exception 'Conta (WABA) conectada não encontrada nesta empresa' using errcode = 'P0001';
  end if;
  update credenciais_whatsapp set revogada_em = now() where waba_id = p_waba and revogada_em is null;
  for numero in
    select phone_number_id, envio_habilitado, operacao_autorizada_em is not null as autorizado
      from tenant_numeros_whatsapp
     where waba_id = p_waba and tenant_id = p_tenant and (envio_habilitado or operacao_autorizada_em is not null)
     for update
  loop
    update tenant_numeros_whatsapp
       set envio_habilitado = false,
           envio_alterado_em = case when numero.envio_habilitado then now() else envio_alterado_em end,
           envio_alterado_por = case when numero.envio_habilitado then p_operador else envio_alterado_por end,
           envio_motivo = case when numero.envio_habilitado then 'conta desconectada: ' || p_motivo else envio_motivo end,
           operacao_autorizada_em = null, operacao_autorizada_por = null, operacao_motivo = 'conta desconectada: ' || p_motivo
     where phone_number_id = numero.phone_number_id;
    if numero.envio_habilitado then
      insert into eventos_historico (tenant_id, conversa_id, tipo, dados)
      values (p_tenant, null, 'envio_whatsapp_desabilitado', jsonb_build_object('phone_number_id', numero.phone_number_id, 'operador', p_operador, 'motivo', 'conta desconectada'));
    end if;
    if numero.autorizado then
      insert into eventos_historico (tenant_id, conversa_id, tipo, dados)
      values (p_tenant, null, 'operacao_whatsapp_revogada', jsonb_build_object('phone_number_id', numero.phone_number_id, 'operador', p_operador, 'motivo', 'conta desconectada'));
    end if;
  end loop;
  insert into eventos_historico (tenant_id, conversa_id, tipo, dados)
  values (p_tenant, null, 'conta_whatsapp_desconectada', jsonb_build_object('waba_id', p_waba, 'operador', p_operador, 'motivo', p_motivo));
end $$;
revoke all on function plataforma_desconectar_conta(uuid, text, text, text) from public;

-- S7 (menor privilégio, decisão 24): nenhuma rota da supervisão usa as contas; o atendente não precisa ver WABA nem
-- business_id. Volta quando houver uma tela que precise, com filtro por papel.
revoke select on contas_whatsapp from seceletesc_supervisao;
