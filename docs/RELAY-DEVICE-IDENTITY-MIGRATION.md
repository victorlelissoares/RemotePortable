# Migração do Relay para identidade individual

Status: implementação inicial pronta para validação em CI; produção continua na etapa de migração controlada dos clientes.

## Implementado nesta etapa

- Clientes Windows e Linux criam uma chave ECDSA P-256 por instalação; o ID é o SHA-256 da chave pública.
- A chave privada é protegida pelo DPAPI no Windows e por permissões privadas no perfil Linux.
- Inscrição pendente, aprovação e revogação administrativa por dispositivo.
- Assinatura de desafio por nonce para LIST, REGISTER, HOST e JOIN.
- Persistência das listas de aprovados e pendentes em volume Docker.
- Interfaces removem o campo de token. A senha do host e o consentimento local permanecem.
- Compatibilidade temporária para clientes antigos usando `RELAY_TOKEN` exclusivamente no ambiente do servidor.

## Atualização sem interromper os clientes antigos

1. Fazer backup do `.env`, certificado TLS e estado do Relay.
2. Atualizar o serviço com o Compose novo mantendo temporariamente `RELAY_TOKEN` no `.env`. O valor só habilita o protocolo antigo no Relay; os clientes novos não o recebem.
3. Publicar e instalar a versão nova em todos os computadores.
4. Aprovar cada ID derivado de chave pelo comando documentado em `relay/DISCOVERY.md`.
5. Testar descoberta, conexão, senha, consentimento, reinício do Relay e revogação.
6. Quando não houver mais clientes antigos, remover `RELAY_TOKEN` do `.env` e recriar o contêiner. A compatibilidade antiga fica desativada.

## Limitações a resolver antes do corte final

- A autorização de identidade é do Relay inteiro. As permissões por host ainda dependem da senha e do consentimento local; ACL por usuário/dispositivo não está implementada.
- A identidade do cliente é persistida no perfil do usuário; apagar o arquivo cria outro dispositivo que exige nova aprovação.
- Uma revogação bloqueia novas autenticações; sessões que já estão encaminhando são encerradas ao desconectar.
- Ainda é necessário validar em Windows/Linux reais e com o Relay da VPS antes de retirar o protocolo antigo.
