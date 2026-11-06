# RemotePortable Relay (MVP experimental)

O modo Relay cria um túnel TCP entre duas máquinas por meio de uma VPS. O aplicativo Windows original **não foi modificado**: o túnel encaminha o protocolo TLS já utilizado pelo RemotePortable. Não há descoberta automática de IDs nem integração com a interface nesta versão.

## Requisitos

- VPS Linux com Docker e domínio DNS apontando para o IP público.
- Certificado TLS válido para o domínio, exportado como PKCS#12 (PFX).
- Porta TCP **47900** acessível na VPS, conforme as regras de rede.
- .NET 8 SDK para compilar o cliente de túnel no Windows.
- O mesmo segredo `RELAY_TOKEN` nos dois clientes e no servidor. Trate-o como credencial de infraestrutura, não como senha da sessão.

## Servidor

Na VPS, dentro da pasta `relay`:

```bash
cp .env.example .env
# Edite .env: RELAY_TOKEN com 64 caracteres aleatórios e RELAY_CERT_PASSWORD.
mkdir -p certs
# Coloque o certificado válido e chave privada no arquivo certs/relay.pfx
docker compose up -d --build
docker compose logs -f relay
```

Exemplo para exportar um certificado Let's Encrypt já emitido:

```bash
openssl pkcs12 -export -out certs/relay.pfx \
  -inkey /etc/letsencrypt/live/relay.exemplo.com/privkey.pem \
  -in /etc/letsencrypt/live/relay.exemplo.com/fullchain.pem
```

Use a senha informada na exportação em `RELAY_CERT_PASSWORD`. Renove o PFX e reinicie o container após renovar o certificado. Não versione `.env`, `.pfx` nem chaves privadas.

## Compilar o túnel (Windows)

```powershell
dotnet publish .\relay\RemotePortable.Tunnel\RemotePortable.Tunnel.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o .\relay\publish-tunnel
```

## Máquina controlada

1. Abra o RemotePortable original, defina senha forte e clique **Compartilhar** (porta 47890).
2. Copie o fingerprint TLS apresentado.
3. Abra um terminal na pasta do executável do túnel:

```powershell
.\RemotePortable.Tunnel.exe host relay.exemplo.com 47900 computador-01 SEU_RELAY_TOKEN 47890
```

O túnel mantém uma conexão de saída para o Relay e aguarda um controlador. O ID `computador-01` deve ser único e ter 6–64 caracteres alfanuméricos ou hífens.

## Máquina controladora

Inicie o túnel:

```powershell
.\RemotePortable.Tunnel.exe join relay.exemplo.com 47900 computador-01 SEU_RELAY_TOKEN 47891
```

No RemotePortable original, use:
- IP: `127.0.0.1`
- Porta: `47891`
- Senha: a senha definida na máquina controlada
- Fingerprint TLS: o SHA-256 exibido na máquina controlada

Clique **Controlar**. A máquina controlada deverá aprovar o acesso. O fingerprint muda quando o compartilhamento é reiniciado.

## Segurança e limitações

- O túnel usa TLS com validação do certificado público da VPS; não desabilite essa validação.
- O RemotePortable mantém uma segunda camada TLS ponta a ponta com verificação manual do fingerprint. O Relay encaminha bytes e não recebe o conteúdo descriptografado da sessão.
- O token compartilhado concede permissão para registrar e acessar IDs no Relay. **Não exponha o token** nem execute um Relay público sem proteção contra abuso, limites de conexões e observabilidade.
- MVP: uma sessão por ID, uma conexão por vez; sem cadastro de usuários, controle de acesso por usuário, métricas, limite de banda ou multiplexação.
- O host re-registra a cada desconexão e a cada 30 minutos sem sessão. O cliente controlador aguarda conexão local.
- Uma VPS com firewall restritivo ou uma rede corporativa pode bloquear a conexão de saída na porta 47900. Respeite as políticas da rede.
- O código foi publicado como implementação inicial; é necessário validar build e teste ponta a ponta antes de uso real.
