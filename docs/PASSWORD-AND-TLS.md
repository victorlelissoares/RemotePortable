# Senha persistente e fingerprint TLS automático

A interface Windows/Linux não exibe mais o campo manual de fingerprint TLS. O Relay autentica cada dispositivo com chave ECDSA persistente, desafio aleatório e assinatura. O host só pode anunciar o ID derivado da própria chave. O TLS da sessão remota continua validado pelo fingerprint publicado, e a senha e a aprovação local do host continuam obrigatórias.

## Senha

A senha configurada no campo **Senha** é salva automaticamente ao sair do campo, quando tem pelo menos 12 caracteres, e ao iniciar o compartilhamento. Na próxima execução ela é carregada automaticamente. O mesmo campo serve à senha de compartilhamento e à senha usada para controlar outra máquina: trocar a senha afeta ambas.

- Windows: DPAPI, escopo do usuário atual, em `%LOCALAPPDATA%/RemotePortable/password.dpapi`.
- Linux: Secret Service, acessado pelo comando `secret-tool`. Instale `libsecret-tools` e tenha um chaveiro desbloqueado (por exemplo GNOME Keyring/KWallet com Secret Service).

```bash
sudo apt install libsecret-tools gnome-keyring
```

Se o chaveiro não estiver disponível, o aplicativo avisa que não conseguiu salvar a senha. Não há fallback para texto puro.

## Limitações

A conexão direta por IP sem um dispositivo selecionado ainda exige uma identidade TLS previamente conhecida. A interface não permite digitar o fingerprint manualmente: utilize a lista do Relay. Durante a atualização, o Relay pode manter temporariamente o protocolo antigo se `RELAY_TOKEN` ainda estiver definido no servidor; os clientes novos nunca usam esse token.
