# Interface de dispositivos — Windows e Linux

As interfaces WinForms (Windows) e Avalonia (Linux) agora incluem a lista global de dispositivos conectados ao Relay, com atualização a cada 10 segundos e botão para conexão.

## Como utilizar

1. Instale o artefato da plataforma gerado pelo GitHub Actions. O pacote deve conter **RemotePortable** e **RemotePortable.Tunnel** na mesma pasta.
2. Abra a interface gráfica (no Linux, execute `RemotePortable.Linux` pelo gerenciador de arquivos, concedendo permissão de execução se necessário).
3. Informe o Relay (`remoteportable.cloud`) e o `RELAY_TOKEN` no campo **Token**.
4. No host, informe senha de 12+ caracteres e clique **Compartilhar** com **Usar Relay** marcado. O Tunnel será iniciado automaticamente, e o host aparecerá na lista.
5. No controlador, clique **Atualizar**, selecione o dispositivo e clique **Conectar selecionado**.
6. Informe a senha do host e seu fingerprint SHA-256 (64 caracteres), caso ainda não estejam preenchidos, antes de iniciar a conexão. O host exibirá um diálogo de aprovação.

## Estado e limitações

- A lista é global para todos os clientes que possuem o token compartilhado do Relay.
- O Relay não fornece contas ou permissões individuais; o controle remoto continua protegido por senha e aprovação.
- O fingerprint TLS do host **não é publicado automaticamente** pela integração atual; a lista pode conter um valor temporário de zeros. Nesse caso, obtenha o fingerprint diretamente do host por um canal confiável. Não desative a verificação TLS.
- Os executáveis ainda não possuem instalador, atalhos ou integração ao menu de aplicativos Linux. O Linux usa Avalonia, não terminal, para a interface.
- O Linux host requer sessão X11 e FFmpeg/xdotool/xdpyinfo.
- O token não é persistido no disco pela interface; precisa ser informado ao iniciar.
- Os builds e a conexão entre máquinas reais precisam ser validados antes de uso em produção.
