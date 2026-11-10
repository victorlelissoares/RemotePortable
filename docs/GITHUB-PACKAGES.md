# Pacotes de distribuição no GitHub Packages (GHCR)

Os workflows da branch `master` publicam os bundles desktop como **artefatos OCI**, não imagens Docker executáveis:

- `ghcr.io/victorlelissoares/remoteportable-windows:latest` — arquivo ZIP com RemotePortable.exe e RemotePortable.Tunnel.exe.
- `ghcr.io/victorlelissoares/remoteportable-linux:latest` — arquivo tar.gz com aplicativo Avalonia e Tunnel.

Cada build também recebe a tag imutável por commit `sha-<SHA completo>`. O GitHub Actions utiliza `GITHUB_TOKEN` e permissão `packages: write`; não é necessário configurar PAT.

## Baixar os pacotes com ORAS

Instale [ORAS CLI](https://oras.land/docs/installation/) e execute:

```bash
oras pull ghcr.io/victorlelissoares/remoteportable-linux:latest
oras pull ghcr.io/victorlelissoares/remoteportable-windows:latest
```

Se o pacote for privado, faça `oras login ghcr.io` com credenciais que possuam `read:packages`.

## Visibilidade e downloads

Após o primeiro publish bem-sucedido, os pacotes devem aparecer em **Packages** no perfil/repositório GitHub. A visibilidade inicial pode ser privada; ajuste nas configurações de cada pacote, caso queira download público. Os arquivos ZIP/tar.gz também ficam disponíveis como **Actions artifacts**, sujeitos à retenção do GitHub.

**Atenção:** pacotes GHCR são artefatos OCI, portanto a interface GitHub Packages não fornece a mesma experiência de download direto de executáveis que GitHub Releases. Para downloads por navegador, prefira publicar também em Releases.
