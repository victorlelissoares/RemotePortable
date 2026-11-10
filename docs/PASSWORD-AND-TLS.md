# Senha persistente e fingerprint TLS automático

A interface Windows/Linux não exibe mais o campo manual de fingerprint TLS. Na conexão por dispositivo da lista, o aplicativo recebe o fingerprint anunciado pelo Relay e continua validando o certificado TLS do host. **Isso não equivale a identidade autenticada do host:** um participante que conhece o token compartilhado pode registrar outro fingerprint para um ID; para produção, implemente identidade persistente e verificação de chave previamente confiável.

## Senha

A senha configurada no campo **Senha** é salva automaticamente ao sair do campo, quando tem pelo menos 12 caracteres, e ao iniciar o compartilhamento. Na próxima execução ela é carregada automaticamente. O mesmo campo serve à senha de compartilhamento e à senha usada para controlar outra máquina: trocar a senha afeta ambas.

- Windows: DPAPI, escopo do usuário atual, em `%LOCALAPPDATA%/RemotePortable/password.dpapi`.
- Linux: Secret Service, acessado pelo comando `secret-tool`. Instale `libsecret-tools` e tenha um chaveiro desbloqueado (por exemplo GNOME Keyring/KWallet com Secret Service).

```bash
sudo apt install libsecret-tools gnome-keyring
```

Se o chaveiro não estiver disponível, o aplicativo avisa que não conseguiu salvar a senha. Não há fallback para texto puro.

## Limitações

A conexão direta por IP sem um dispositivo selecionado ainda exige uma identidade TLS previamente conhecida. A interface não permite mais digitar o fingerprint manualmente: utilize a lista do Relay. A publicação de fingerprints pelo Relay compartilhado não oferece proteção completa contra personificação.
