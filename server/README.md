# BdkHourFarm (SteamKit2)

Farm de horas **sem cliente Steam e sem Windows**. Entra na conta direto na rede da Steam
(como o ArchiSteamFarm faz) e mantém uma lista de jogos "rodando".

## O que ele faz quando você joga em outro PC

1. Você clica em **Jogar** no outro PC e a Steam pergunta se quer desconectar a outra sessão.
   **Essa pergunta é da Steam e continua aparecendo**; nenhum programa consegue impedi-la.
   Você clica em OK.
2. A Steam avisa o farm que a conta passou a jogar em outro lugar. O farm **fica parado**
   (não tenta retomar nem derruba você).
3. Quando você fecha o jogo, a Steam avisa de novo. O farm espera `ResumeDelaySeconds`
   (padrão 20 s), confere que continua livre e **volta a farmar a mesma lista**.

Você não precisa mais abrir a VPS para parar/ligar o farm.

## Compilar

Precisa do SDK do .NET 8 ou mais novo (só na máquina onde você compila).

    dotnet build -c Release

Para a VPS, gere um executável que não precisa de .NET instalado:

    # VPS x86-64
    dotnet publish -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o publish

    # VPS ARM (ex.: Oracle Ampere)
    dotnet publish -c Release -r linux-arm64 --self-contained -p:PublishSingleFile=true -o publish

Depois do primeiro build que funcionar, troque `Version="3.*"` do SteamKit2 no
`BdkHourFarm.csproj` pela versão exata que foi restaurada.

## Primeiro uso

1. Login (uma vez; pede usuário, senha e Steam Guard). A senha **não** é salva, só um token:

       ./BdkHourFarm login

2. Rode uma vez para gerar o `data/config.json`, edite com seus AppIDs e rode de novo:

       ./BdkHourFarm run

   Opções do `config.json`:

   | Campo | O que faz |
   |---|---|
   | `AppIds` | Jogos a farmar (até 32). |
   | `StartupWaitSeconds` | Espera após o login para a Steam avisar se já há jogo em outro PC. |
   | `ResumeDelaySeconds` | Espera antes de voltar a farmar depois que o outro jogo fecha. |
   | `PersonaState` | `Online`, `Invisible`, `Offline`, `Away`, `Busy` ou `Snooze`. |

Os arquivos `config.json` e `session.json` ficam em `data/` ao lado do executável
(ou na pasta definida em `BDK_DATA_DIR`). **Proteja o `session.json`**: quem tiver esse
arquivo consegue entrar na sua conta até o token expirar ou ser revogado.

## Painel web (monitorar pelo celular)

O farm agora traz um painel embutido: estado (farmando/pausado), conta, tempo ligado,
horas farmadas por jogo, pausas, reconexões, dias até o login vencer, log recente e um
botão para pausar/retomar.

- Por padrão ele escuta só em `127.0.0.1:8080` (dentro da VPS). O `deploy/configurar-https.sh`
  instala o Caddy, que publica o painel em HTTPS (`https://SEU-IP-COM-TRACOS.sslip.io`).
- Todas as rotas `/api/...` exigem um token. Veja com `BdkHourFarm token`. Ele fica salvo
  só no navegador onde você digitou.
- Link de acesso rápido (não compartilhe): `https://SEU-DOMINIO/#token=SEU_TOKEN`.
- Horas totais na Steam (opcional): preencha `SteamWebApiKey` no `config.json` com uma
  chave de https://steamcommunity.com/dev/apikey. O perfil precisa ter os detalhes de jogo públicos.
- A pausa pelo painel vale até você tocar em Retomar ou até o programa reiniciar.

### Hospedar o painel na Vercel (opcional)

O mesmo `Web/index.html` funciona hospedado fora da VPS:

1. Suba só o `Web/index.html` num repositório do GitHub e importe na Vercel (sem build).
2. No `config.json` da VPS, coloque `"ApiAllowedOrigin": "https://SEU-PROJETO.vercel.app"`
   e reinicie o farm.
3. No painel da Vercel, abra a engrenagem e preencha o endereço do farm
   (`https://SEU-IP-COM-TRACOS.sslip.io`) e o token.

A VPS continua precisando de HTTPS: um site HTTPS não consegue chamar `http://IP:8080`.

## Atualizar a VPS para uma versão nova

No Windows (na pasta do projeto):

    dotnet publish -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o publish
    Compress-Archive -Path .\publish\* -DestinationPath .\publish.zip -Force
    scp .\publish.zip .\deploy\atualizar.sh .\deploy\configurar-https.sh root@SEU_IP:/root/

Na VPS:

    bash /root/atualizar.sh

## Na VPS (systemd)

    sudo useradd -r -m -d /opt/bdkhourfarm bdk
    sudo cp -r publish/* /opt/bdkhourfarm/
    sudo chown -R bdk:bdk /opt/bdkhourfarm
    sudo -u bdk /opt/bdkhourfarm/BdkHourFarm login      # uma vez, via SSH
    sudo -u bdk /opt/bdkhourfarm/BdkHourFarm run        # gera config.json; edite e Ctrl+C
    sudo cp bdkhourfarm.service /etc/systemd/system/
    sudo systemctl enable --now bdkhourfarm
    journalctl -u bdkhourfarm -f                        # acompanhar o log

Alternativa: fazer o `login` no seu PC e copiar o `data/session.json` para a VPS
(`chmod 600`). Se a Steam recusar o token vindo de outro IP, faça o login direto na VPS.

## Segurança da conta

- A sessão (refresh token) fica **criptografada** (AES-256-GCM) em `data/`, nunca em texto puro; logs mostram a conta mascarada (`st******`).
- Modo padrão (`machine`): a chave vem da própria máquina. Protege contra vazamento só da pasta `data/` (backup, snapshot, envio sem querer). **Não** protege contra quem já é root na VPS.
- Modo forte (`passphrase`): `sudo -u bdk /opt/bdkhourfarm/BdkHourFarm protect passphrase`. A senha nunca fica no disco; depois de cada reinício o painel mostra "Farm bloqueado" e você digita a senha para começar. Voltar: `protect machine`.
- Em caso de invasão: troque a senha da Steam e remova os dispositivos autorizados (isso invalida os tokens). O ideal é usar uma conta secundária.
- SSH por chave: `deploy/ssh-seguro.sh` (`chave` instala a chave pública, `travar` desliga a senha).
- O serviço roda com usuário sem privilégios e proteções do systemd (`atualizar.sh` aplica).

## Painel: jogos e histórico

- **Escolher jogos**: lista a biblioteca (precisa da chave da Steam, colada no painel e guardada criptografada) e salva em `data/games.json`, que tem prioridade sobre o `config.json`. Máximo 32.
- **Histórico**: gráficos por dia, linha do tempo de 7 dias e lista de sessões com o motivo de cada parada.

## Agenda de horários

No painel (seção "Agenda"): ligue "Usar agenda", escolha o fuso (padrão America/Sao_Paulo) e adicione horários com dias da semana, início e fim. Um horário como 22:00 → 08:00 vira a meia-noite. Fora dos horários o farm pausa sozinho e volta quando o próximo horário começa. Fica em `data/schedule.json`. No app dá para ligar/desligar a agenda e ver o próximo horário. Desligada ou sem horários = farma o tempo todo.

## Avisos no Discord

No painel (seção "Avisos no Discord") ou no app, cole a URL de um webhook (Discord: Configurações do servidor › Integrações › Webhooks › Novo webhook › Copiar URL) e use "Enviar teste". A URL fica criptografada em `data/webhook.dat`.
Avisos: farm iniciado, conexão caiu por mais de 3 min, voltou, bloqueado aguardando senha, programa parando, login recusado, login perto de vencer (menos de 14 dias), pausa/retomada pelo painel e pausa/retomada por jogar em outro PC (os dois últimos podem ser desligados no painel).

## Avisos

- **Desligue o farm antigo** (a VM com o BDK Steam Hour Farm) antes de usar este. Os dois
  usam a mesma conta; enquanto o antigo estiver farmando, este fica pausado.
- O token dura cerca de 200 dias (o programa avisa quando faltar menos de 14). Depois,
  rode `login` de novo.
- A Steam às vezes manda avisos de estado falsos. O `ResumeDelaySeconds` existe para isso;
  se ainda for derrubado cedo demais, aumente o valor (ex.: 60).
- Se o login der "RateLimitExceeded" ou a Steam pedir confirmação, espere um pouco antes de tentar de novo.
- A cada 30 minutos o log mostra uma linha `Status:` (farmando, pausado ou sem conexão).

## Backup e logs cifrados

Para que nem uma VPS invadida entregue seus backups e logs, tudo é cifrado com uma **chave pública**:
o servidor só tem a pública (que só tranca); a **privada fica no seu PC** e é a única que abre.

1. No seu PC, gere o par de chaves (uma vez): `BdkTool keygen`. Guarde `bdk-private.pem` **e a senha** em lugar seguro
   (gerenciador de senhas + cópia offline). Perdeu qualquer um dos dois = backups e logs ilegíveis para sempre.
2. Envie só a pública: `scp bdk-public.pem root@SEU_IP:/opt/bdkhourfarm/data/backup-public.pem`, depois
   `chown bdk:bdk /opt/bdkhourfarm/data/backup-public.pem && systemctl restart bdkhourfarm`.
   O log do serviço mostra a "impressão" da chave; confira com `BdkTool fingerprint`.
3. Pronto:
   - **Backup**: um por dia em `data/backups/` (os de mais de 7 dias são apagados; ajuste em `BackupKeepDays`, mínimo 7), também sob demanda no painel (card "Backup cifrado") ou com
     `BdkHourFarm backup`. Baixe pelo painel ou com `scp`, e abra com `BdkTool backup arquivo.bdkb`.
   - **Logs**: gravados em `data/logs/*.bdklog` (30 dias). Leia com `BdkTool logs pasta-ou-arquivo`.
     Com `"LogToConsole": false` no `config.json`, o journald também deixa de guardar o log em texto puro.

**Restaurar** em uma VPS nova: instale o programa, rode `BdkTool backup` no PC, copie os arquivos extraídos para `data/`
(sessão, histórico, agenda, webhook, token), ajuste o dono (`chown -R bdk:bdk data`), inicie o serviço e apague a pasta extraída:
ela contém segredos em texto. O programa volta a criptografar tudo com a chave da nova máquina.

Segurança: a chave pública só pode ser trocada por arquivo (via SSH), nunca pelo painel/API. Assim, quem roubar o token do painel
não consegue redirecionar os backups futuros para a própria chave.

### Espaço em disco

- Backups: apagados automaticamente depois de `BackupKeepDays` dias (padrão e mínimo: 7). O mais recente nunca é apagado.
  O painel tem o botão "Apagar os antigos" para limpar na hora.
- Atualizações: o `atualizar.sh` guarda as `KEEP_RELEASES` (padrão 3) últimas versões em `/opt/bdkhourfarm-releases` para você
  poder voltar atrás (`bash /root/atualizar.sh /opt/bdkhourfarm-releases/publish-AAAAMMDD-HHMMSS.zip`). As mais antigas são
  apagadas, e o `publish.zip` enviado também. Para não guardar nada: `KEEP_RELEASES=0 bash /root/atualizar.sh`.
- O journal do sistema é limitado a 100 MB a cada atualização.
