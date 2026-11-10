# Relay sem token distribuído — especificação de migração

Status: **proposta em desenvolvimento; não habilitar em produção**.

## Objetivo

O cliente usa `remoteportable.cloud:47900` sem conhecer `RELAY_TOKEN`. O segredo do Relay permanece somente na VPS. Não disponibilizar endpoint para recuperar esse segredo: seria equivalente a publicá-lo.

## Identidade

- Na primeira execução, gerar chave privada Ed25519 (ou ECDSA P-256, conforme APIs .NET disponíveis), guardada em cofre do sistema operacional.
- Calcular `device_id` como identificador derivado da chave pública, evitando IDs arbitrários reutilizáveis.
- Enviar chave pública ao Relay por TLS para solicitar inscrição. O servidor grava pedido **pendente** sem dar acesso à descoberta nem a sessões.
- Operador aprova o fingerprint da chave pública na VPS, após verificar que é o dispositivo correto. O banco de identidades e as aprovações devem persistir em volume.
- Em cada conexão, servidor envia nonce aleatório com prazo de validade. Cliente assina nonce, ação, ID e dados da sessão; servidor verifica assinatura e autorização antes de LIST, REGISTER, HOST ou JOIN.
- Para JOIN, exigir autorização específica do controlador para o host ou código temporário concedido pelo host. A senha de acesso remoto e o consentimento do usuário continuam obrigatórios.
- O Relay deve limitar taxa de cadastro e tentativas de autenticação, limitar tamanho de mensagens e registrar eventos sem armazenar credenciais.

## Identidade TLS

O certificado do host precisa de identidade persistente e verificada: fingerprint anunciado em diretório não basta quando um agente não confiável pode registrar metadados. Vincular fingerprint de sessão à identidade aprovada e assinar a publicação, ou usar certificado persistente associado à chave do dispositivo.

## Migração

1. Implantar novo protocolo na VPS **em paralelo** ao protocolo atual, em porta ou versão distinta.
2. Implementar armazenamento persistente, fila de aprovação e endpoint administrativo local (não exposto publicamente).
3. Atualizar Tunnel, biblioteca compartilhada e interfaces Windows/Linux.
4. Testar registro, aprovação, revogação, reinício da VPS, reconexão, spoofing de ID e acesso não autorizado.
5. Só então remover o campo de token, migrar os dispositivos e desativar o protocolo antigo.
6. Rotacionar o token compartilhado atual após a migração.

## Restrições

Não há como autenticar automaticamente qualquer instalação nova sem um segredo prévio, aprovação de administrador ou outra prova de identidade. O token não deve ser incluído em binários, arquivos públicos, logs, argumentos de processo nem resposta HTTP.
