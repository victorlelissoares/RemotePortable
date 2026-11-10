# RemotePortable Relay

Relay TLS para encaminhar sessões entre dispositivos sem expor portas locais. A versão atual permite identidade individual por dispositivo e aprovação administrativa.

## Requisitos

- Docker Engine e Docker Compose.
- Porta TCP 47900 acessível na VPS.
- Certificado TLS em `relay/certs/relay.pfx`.
- `RELAY_CERT_PASSWORD` configurada no arquivo `relay/.env`.

O Compose cria o volume persistente `relay-data` para as listas de dispositivos aprovados e pendentes. A chave privada do Relay e as chaves dos clientes nunca são enviadas ao Tunnel.

```sh
cd relay
docker compose up -d --build
docker compose logs -f relay
```

O servidor não exige token para clientes novos. Enquanto computadores com a versão anterior ainda precisarem se conectar, mantenha temporariamente o `RELAY_TOKEN` antigo no `.env`; o Compose o fornece apenas ao processo Relay como compatibilidade. Remova essa variável depois que todos os dispositivos estiverem atualizados e aprovados.

## Aprovar ou revogar dispositivo

O primeiro acesso cria uma entrada pendente. O cliente exibe o ID derivado da chave pública. Confirme a identidade do computador e rode na VPS:

```sh
docker compose run --rm --no-deps relay approve ID_DO_DISPOSITIVO
docker compose run --rm --no-deps relay revoke ID_DO_DISPOSITIVO
```

Faça backup do volume `relay-data`; ele contém as chaves públicas autorizadas. A perda desse volume exige aprovar os dispositivos novamente.

## Atualização

Mantenha um backup dos dados e valide o Compose antes de atualizar. A ativação da autenticação nova é compatível com clientes antigos apenas enquanto `RELAY_TOKEN` estiver definido. Não remova o segredo da VPS até concluir a atualização dos clientes. A desativação do protocolo antigo é feita removendo `RELAY_TOKEN` do `.env` e recriando o contêiner.

## Segurança

- Cada comando novo usa desafio aleatório e assinatura ECDSA P-256 por dispositivo.
- O ID de host corresponde ao ID derivado da chave autenticada; não é um nome arbitrário.
- O tráfego remoto mantém TLS ponta a ponta e valida o fingerprint anunciado; a senha do host e a aprovação local continuam necessárias.
- Dispositivos pendentes não conseguem listar, anunciar ou encaminhar conexões.
- O modo antigo é apenas uma ponte de migração. Evite divulgar o segredo e remova-o ao terminar a atualização.
