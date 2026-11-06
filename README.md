# RemotePortable v0.2 — DXGI + H.264 (Windows)

Protótipo de acesso remoto **assistido** por LAN/VPN. O host captura a tela com **FFmpeg `ddagrab` (DXGI Desktop Duplication)** e codifica em **H.264 via Media Foundation (`h264_mf`)**. O controlador decodifica MPEG-TS/H.264 com FFmpeg e desenha os quadros em WinForms. Mouse/teclado continuam no canal TLS.

## Requisitos

- Windows 10/11 x64; .NET SDK 8 **apenas para compilar**.
- O script de compilação baixa automaticamente o FFmpeg para Windows x64 (distribuição Gyan.dev) e verifica os recursos `ddagrab` e `h264_mf`. É necessária internet **somente na primeira compilação**. A versão baixada pode mudar; para builds reproduzíveis, fixe versão e SHA-256.
- GPU/driver com suporte à captura DXGI; encoder H.264 Media Foundation disponível.
- Tela principal em resolução fixa durante a sessão.

Valide a sua build do FFmpeg no PowerShell:

```powershell
.\ffmpeg.exe -filters | findstr ddagrab
.\ffmpeg.exe -encoders | findstr h264_mf
.\ffmpeg.exe -hide_banner -f lavfi -i "ddagrab=output_idx=0:framerate=30" -t 3 -vf "hwdownload,format=bgra,format=nv12" -c:v h264_mf -f null NUL
```

Se o último comando falhar, sua combinação de FFmpeg, GPU e encoder não é compatível com esta versão. Não há fallback JPEG automático.

## Compilar

No Windows, dê dois cliques em **`compilar.cmd`** (ou execute `./build.ps1` no PowerShell). O script baixa, extrai, verifica e incorpora `ffmpeg.exe` automaticamente. Requer .NET SDK 8 e internet no computador usado para compilar.

Depois da compilação, copie **somente `publish/RemotePortable.exe`** para cada computador. O FFmpeg é incorporado como recurso no executável. Na primeira execução que usar vídeo, ele é extraído automaticamente para `%LOCALAPPDATA%\RemotePortable\bin\<sha256>\ffmpeg.exe`. Isso **não instala** o FFmpeg no sistema, mas cria um arquivo temporário persistente no perfil do usuário, necessário para iniciar o processo de codificação/decodificação. O cache é verificado com SHA-256 e reutilizado em execuções posteriores. O `.exe` final será maior (possivelmente centenas de MB). Antivírus ou políticas corporativas podem bloquear a extração/execução.

O build ainda requer internet para baixar o FFmpeg na primeira vez e .NET SDK 8. A distribuição possui licença própria; consulte https://www.ffmpeg.org/legal.html e https://www.gyan.dev/ffmpeg/builds/.

## Usar

1. Na máquina remota: senha de 12+ caracteres, FPS (comece com 30), **Compartilhar**.
2. Na máquina controladora: IP, porta TCP 47890, senha e fingerprint SHA-256 do certificado do host; **Controlar**.
3. Na máquina remota: aprove a solicitação; **Encerrar** interrompe a sessão.
4. Use apenas LAN/VPN confiável; **não exponha** a porta diretamente à internet.

## Limitações e riscos

- Implementação **não compilada/testada neste ambiente** (sem SDK .NET e sem Windows). A compatibilidade FFmpeg/Media Foundation deve ser validada em Windows.
- Apenas monitor principal. Sem áudio, clipboard, transferência de arquivos, UAC ou tela de login.
- Não há reconexão automática, fallback de codec, ajuste adaptativo de bitrate, limitação de tentativas nem auditoria de segurança.
- O pipeline de decodificação usa BGRA e `Bitmap`, o que ainda adiciona cópias CPU; para menor latência, usar D3D11 compartilhado na renderização.
- A dimensão do vídeo é anunciada pela resolução da tela primária: mudar resolução ou escala durante a sessão exige reconectar.
- A captura por DXGI e codificação H.264 não garantem latência menor sem medição; `h264_mf` depende do hardware e do FFmpeg utilizado.
- Certificado TLS efêmero: fingerprint muda ao reiniciar o host.
- Código de protótipo, não recomendado para produção sem testes de segurança e robustez.

## Correção v0.4 — estabilidade do listener

- O loop de compartilhamento é executado em uma thread de trabalho, sem bloquear a UI WinForms.
- O listener permanece ativo entre sessões; erros de cliente são registrados e não encerram o compartilhamento.
- O pedido de autorização é encaminhado à UI de forma assíncrona.
- Logs: `%LOCALAPPDATA%\RemotePortable\remoteportable.log`.
- `build.ps1` utiliza o pacote FFmpeg do BtbN/GitHub e carrega `System.IO.Compression.FileSystem` para Windows PowerShell 5.1.

Compile com `compilar.cmd`. Verifique a porta com `netstat -ano | findstr :47890` e procure `LISTENING` enquanto estiver compartilhando.

**Observação:** alterações revisadas estaticamente; compilação e teste em Windows ainda pendentes.

## v0.5 — diagnóstico e recuperação do listener

- O loop TCP agora tem um supervisor que reinicia o `TcpListener` após encerramento inesperado, com espera progressiva de até 10 segundos.
- Cada início e encerramento do listener registra PID, geração e motivo no arquivo `%LOCALAPPDATA%\RemotePortable\remoteportable.log`.
- O botão **Encerrar** registra a pilha de chamadas para investigar encerramentos involuntários.
- Sessões com erro continuam isoladas do loop principal.

**Validação no Windows:** abra **Compartilhar**, monitore `netstat -ano -p TCP | findstr :47890` por alguns minutos e examine o log caso a porta desapareça. Se o aplicativo encerrar ou o supervisor parar, envie o log completo. Este código não foi compilado/testado neste ambiente.
