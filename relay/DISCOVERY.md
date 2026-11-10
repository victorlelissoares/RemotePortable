# Identidade e descoberta do Relay

O cliente não recebe nem armazena token compartilhado. Na primeira execução, cria uma chave ECDSA P-256 por dispositivo. O ID mostrado pela interface é o SHA-256 da chave pública. A chave privada fica protegida pelo DPAPI no Windows e em arquivo restrito ao usuário no Linux.

## Aprovação inicial

1. Inicie o aplicativo no computador. O Relay registra a chave pública como pendente e a interface mostra o ID que precisa de aprovação.
2. No diretório do Relay na VPS, aprove o ID:

   ```sh
   docker compose run --rm --no-deps relay approve ID_DO_DISPOSITIVO
   ```

3. Atualize a lista ou reinicie a conexão. O dispositivo aprovado poderá anunciar e descobrir hosts.

Para revogar:

```sh
docker compose run --rm --no-deps relay revoke ID_DO_DISPOSITIVO
```

As listas `pending-devices.json` e `approved-devices.json` ficam no volume Docker `relay-data`. Faça backup desse volume. Revogar impede novas autenticações; uma sessão já estabelecida termina quando a conexão cair.

## Protocolo novo

O cliente envia a chave pública para inscrição. Cada comando autenticado recebe um nonce aleatório e exige uma assinatura ECDSA vinculada à ação, ao dispositivo e ao host alvo. O Relay confere o ID derivado da chave e só permite que um dispositivo anuncie o próprio ID. `JOIN` continua exigindo a senha configurada no host e a aprovação local da sessão.

O administrador deve aprovar cada instalação nova. Não existe rota para baixar chaves privadas ou obter uma credencial global.

## Compatibilidade durante migração

Para permitir a atualização dos computadores sem indisponibilidade, o Relay aceita temporariamente o protocolo antigo quando `RELAY_TOKEN` está configurado no `.env` da VPS. Os novos clientes não usam esse segredo. Depois de atualizar e aprovar todos os dispositivos, remova `RELAY_TOKEN` do `.env` e recrie o serviço; o protocolo antigo ficará desativado. O token não deve voltar aos binários.

## Executar o Tunnel manualmente

```sh
./RemotePortable.Tunnel host remoteportable.cloud 47900 ID_DERIVADO 47890 FINGERPRINT_SHA256
./RemotePortable.Tunnel join remoteportable.cloud 47900 ID_DO_HOST 47891
```

O modo `host` usa o ID mostrado pelo próprio aplicativo. O modo `join` recebe o ID do host selecionado na lista.
