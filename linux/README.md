# RemotePortable Linux (X11) — primeira implementação

Aplicativo gráfico em .NET 8 + Avalonia, com os modos **Compartilhar (host)** e **Controlar (cliente)**. O protocolo TLS, autenticação por senha, aprovação local e vídeo MPEG-TS/H.264 são compatíveis com o RemotePortable Windows existente. O Windows WinForms foi preservado.

## Requisitos

- Linux x86_64 com **sessão X11** (ou XWayland com acesso autorizado à tela X11). O host não suporta captura da sessão Wayland nativa, que requer PipeWire/portal.
- FFmpeg com **libx264**, `xdpyinfo` (pacote x11-utils) e `xdotool` no host; FFmpeg no cliente.
- Bibliotecas do Avalonia: libx11-6, libice6, libsm6, libfontconfig1 e bibliotecas gráficas usuais.
- Acesso explícito à sessão gráfica do usuário; não execute como root.

Ubuntu/Debian:

```bash
sudo apt update
sudo apt install -y ffmpeg xdotool x11-utils libx11-6 libice6 libsm6 libfontconfig1 libgbm1 libegl1 libgl1-mesa-dri
echo "$XDG_SESSION_TYPE"
echo "$DISPLAY"
```

Se a sessão for Wayland, faça login em uma sessão **Ubuntu on Xorg/X11** antes de hospedar a tela.

## Executar

Baixe o artefato `RemotePortable-Linux-x64` no GitHub Actions `Linux Desktop and Tunnel`:

```bash
mkdir -p ~/RemotePortable-Linux
tar -xzf RemotePortable-Linux-x64.tar.gz -C ~/RemotePortable-Linux
cd ~/RemotePortable-Linux
./RemotePortable.Linux
```

Ou compile:

```bash
dotnet run --project linux/RemotePortable.Linux/RemotePortable.Linux.csproj
```

**Host:** informe senha com 12+ caracteres, porta 47890, FPS e clique **Compartilhar**. Copie o fingerprint SHA-256 exibido e envie ao controlador por um canal confiável. Uma solicitação de autorização aparecerá para cada sessão.

**Controlador:** informe IP/porta (ou `127.0.0.1:47891` ao usar o Tunnel), senha e fingerprint SHA-256 do **host**. Clique **Controlar**.

## Relay da Hostinger

O Tunnel existente é um projeto .NET 8 independente e pode ser compilado para Linux. Baixe `RemotePortable-Tunnel-Linux-x64` do mesmo workflow e execute:

```bash
# No computador remoto, com Compartilhar ativo na porta 47890
./RemotePortable.Tunnel host remoteportable.cloud 47900 computador-01 SEU_RELAY_TOKEN 47890

# No controlador, em outro computador
./RemotePortable.Tunnel join remoteportable.cloud 47900 computador-01 SEU_RELAY_TOKEN 47891
```

Use um identificador de 6 a 64 caracteres, alfanumérico ou hífen, igual nos dois lados. No cliente GUI, conecte a `127.0.0.1:47891`, com a senha e fingerprint do host.

## Limitações e segurança

- **Linux host: X11 apenas.** PipeWire/Wayland, múltiplos monitores, áudio e transferência de arquivos ainda não foram implementados.
- O teclado implementa um subconjunto de teclas compatível com os códigos VK do Windows; layouts especiais e atalhos complexos ainda precisam de mapeamento.
- Controle de mouse/teclado no host usa `xdotool` e depende de permissões da sessão X11.
- A aprovação local é obrigatória para cada conexão; não há acesso silencioso ou serviço de inicialização automática.
- O certificado do host é temporário, gerado a cada inicialização. O cliente deve validar seu SHA-256 antes de conectar.
- O Relay não substitui a autenticação ponta a ponta: mantenha o token do Relay e a senha do host em segredo.
- O projeto ainda requer validação em máquinas Linux reais, especialmente da latência de vídeo e do comportamento do FFmpeg. Verifique o job de CI antes de usar o artefato.
