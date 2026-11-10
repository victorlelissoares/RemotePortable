# Interface de dispositivos — Windows e Linux

As interfaces WinForms (Windows) e Avalonia (Linux) agora incluem a lista global de dispositivos conectados ao Relay, com atualização a cada 10 segundos e botão para conexão.

## Como utilizar

1. Instale o artefato da plataforma gerado pelo GitHub Actions. O pacote deve conter **RemotePortable** e **RemotePortable.Tunnel** na mesma pasta.
2. Abra a interface gráfica (no Linux, execute `RemotePortable.Linux` pelo gerenciador de arquivos, concedendo permissão de execução se necessário).
3. No primeiro acesso, o Relay coloca a identidade do dispositivo em aprovação pendente. Copie o ID mostrado e peça ao administrador do Relay para aprová-lo.
4. No host, informe senha de 12+ caracteres e clique **Compartilhar** com **Usar Relay** marcado. O Tunnel será iniciado automaticamente, e o host aparecerá na lista.
5. No controlador, clique **Atualizar**, selecione o dispositivo e clique **Conectar selecionado**. O controlador também precisa ser aprovado uma vez.
6. Informe a senha do host. O host exibirá um diálogo de aprovação para cada sessão.

## Estado e limitações

- O ID do dispositivo é derivado da chave pública; não o altere manualmente.
- O Relay exige aprovação por dispositivo. O controle remoto também exige a senha do host e aprovação local a cada sessão.
- O fingerprint TLS do host **não é publicado automaticamente** pela integração atual; a lista pode conter um valor temporário de zeros. Nesse caso, obtenha o fingerprint diretamente do host por um canal confiável. Não desative a verificação TLS.
- Os executáveis ainda não possuem instalador, atalhos ou integração ao menu de aplicativos Linux. O Linux usa Avalonia, não terminal, para a interface.
- O Linux host requer sessão X11 e FFmpeg/xdotool/xdpyinfo.
- A chave privada do dispositivo é guardada no DPAPI do usuário no Windows e em arquivo com permissões privadas no Linux.
- Os builds e conexões entre máquinas reais precisam ser validados antes de uso em produção.
