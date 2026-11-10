# Descoberta global de dispositivos — protocolo experimental

O Relay aceita conexões TLS em `remoteportable.cloud:47900` com token compartilhado.

## Segurança

A listagem global é **opt-in**: somente hosts registrados explicitamente aparecem. Qualquer cliente que possua o `RELAY_TOKEN` consegue listar IDs, nomes, plataformas e fingerprints dos dispositivos publicados. Isso **não** é uma autorização individual por conta e não permite controlar uma máquina sem a senha do host e sua aprovação local. Para ambientes com múltiplos usuários, substitua o token global por credenciais individuais antes de produção.

O registro expira após 45 segundos sem heartbeat, é removido ao encerrar a conexão e é mantido apenas em memória. A lista é limitada a 200 dispositivos.

## Comandos do Tunnel

```bash
# Consultar dispositivos anunciados
./RemotePortable.Tunnel list remoteportable.cloud 47900 SEU_RELAY_TOKEN

# Anunciar host (em outro terminal, enquanto o host e seu túnel estão ativos)
./RemotePortable.Tunnel register remoteportable.cloud 47900 computador-01 SEU_RELAY_TOKEN PC-Escritorio FINGERPRINT_SHA256

# Host tradicional (túnel de dados, ainda executado separadamente)
./RemotePortable.Tunnel host remoteportable.cloud 47900 computador-01 SEU_RELAY_TOKEN 47890

# Controlador tradicional
./RemotePortable.Tunnel join remoteportable.cloud 47900 computador-01 SEU_RELAY_TOKEN 47891
```

O ID deve conter 6–64 caracteres alfanuméricos ou hífen. O nome anunciado deve conter somente letras, números, ponto, hífen ou sublinhado. O fingerprint SHA-256 deve ter 64 caracteres hexadecimais.

## Protocolo de controle

Após autenticação TLS:
- `LIST TOKEN list-all\n`: retorna uma linha JSON com dispositivos ativos.
- `REGISTER TOKEN ID\nNOME PLATAFORMA FINGERPRINT\n`: retorna `OK`; mantém a conexão aberta.
- `PING\n` a cada 15 segundos: retorna `PONG`.
- `HOST`/`JOIN` continuam compatíveis com a versão anterior.

## Estado da implementação

Este incremento implementa registro, heartbeat, listagem autenticada e CLI multiplataforma. **Ainda não implementa a lista na interface gráfica nem conexão com um clique**; essas funções exigem integrar o ciclo de vida do túnel às interfaces Windows e Linux. Não confunda o protocolo experimental com a experiência completa do AnyDesk.
