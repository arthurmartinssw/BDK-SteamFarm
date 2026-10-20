# BDK Hour Farm

Farm de horas para Steam que roda 24 h numa VPS, pausa sozinho quando você joga em outro PC e volta depois.
Painel web, app Android, avisos no Discord, agenda de horários e backup/logs cifrados.

| Pasta | O que é |
|---|---|
| `server/` | O programa da VPS (.NET 8, SteamKit2) com o painel web embutido. Veja `server/README.md`. |
| `tools/BdkTool/` | Ferramenta do **seu PC**: gera as chaves e abre backups e logs cifrados. |
| `app/` | Código do app Android (Kotlin/Compose). Veja `app/LEIA-ME.md`. |
| `site/` | Site de apresentação (estático, pronto para a Vercel). |

## Compilar

```powershell
# servidor (gera o pacote para a VPS)
cd server
dotnet publish -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -o publish

# ferramenta para o PC (Windows)
cd ..\tools\BdkTool
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

## Segurança, em resumo

- A senha da Steam nunca é salva; só a sessão, cifrada (AES-256-GCM), com modo "senha" opcional.
- Backups e logs são cifrados com chave pública (RSA-4096 + AES-256-GCM). A privada fica só no seu PC.
- Painel com HTTPS, token e limite de tentativas; serviço sem privilégios; SSH só por chave.

## Neste repositório não entra

Nada de `data/`, tokens, chaves `.pem`, keystores do Android nem backups (veja o `.gitignore`).
Antes de qualquer `git add`, confira com `git status` que nenhum segredo apareceu.

> Projeto independente, sem afiliação com a Valve. Farm de horas está numa área cinzenta dos termos da Steam: use por sua conta e risco, de preferência com uma conta secundária.
