# Segurança do BDK Hour Farm

Revisão feita sobre o código e os scripts deste repositório. Não substitui um teste de invasão profissional,
e a parte da VPS (firewall, SSH, atualizações) você confere rodando `server/deploy/auditar.sh`.

## O que o sistema protege e de quem

| Risco | Proteção |
|---|---|
| Alguém acessa o painel sem o token | HTTPS (Caddy), token de 144 bits comparado em tempo constante, atraso de 0,5 s por erro, aviso no Discord após 10 erros em 10 min |
| Token vazou | Botão "Gerar novo token" no painel: o antigo para de valer na hora |
| Roubo do disco ou da pasta `data/` | Sessão, webhook e chave da Steam cifrados (AES-256-GCM); modo "senha" opcional |
| Chave da Anthropic roubada da VPS | Guardada cifrada (`aikey.dat`, presa à máquina) e incluída no backup cifrado; só o nome do jogo, o texto da conquista e a pergunta vão à Anthropic |
| VPS invadida lendo backups e logs | Cifrados com chave pública: a privada só existe no seu PC |
| Senha da Steam | Nunca é gravada. Na renovação pelo painel, existe só na memória durante o login |
| Força bruta no desbloqueio | 5 erros travam por 5 minutos |
| Injeção de script no painel | CSP restritiva (`default-src 'none'`), sem recursos externos além das capas da Steam |
| Serviço comprometido | Roda como usuário `bdk`, com `NoNewPrivileges`, `ProtectSystem`, `PrivateTmp` e outros |

## Limites conhecidos (o que NÃO está coberto)

1. **Modo padrão ("machine")**: a chave de cifra vem da própria máquina. Protege contra vazamento só da pasta `data/`,
   não contra quem já controla a VPS. Para isso existe o modo `protect passphrase`.
2. **VPS totalmente comprometida (root)**: o atacante vê a sessão em uso na memória. Resposta: trocar a senha da Steam,
   remover dispositivos autorizados, trocar o token do painel e recriar a VPS.
3. **Renovar o login pelo painel** envia a senha da Steam pelo HTTPS até a VPS. Quem tem o token do painel **e** a senha
   da Steam pode fazer isso; só o token não basta. Se isso incomodar, use o comando `login` por SSH.
4. **Termos da Steam**: farm de horas está em área cinzenta e a conta pode ser limitada. Use conta secundária.
5. **Android**: confira no `AndroidManifest.xml` que `android:allowBackup="false"` e que não há `usesCleartextTraffic="true"`.
   O token fica no Keystore do aparelho, mas um celular desbloqueado e com o app aberto dá acesso ao farm.
6. **Dependências** (SteamKit2, .NET, Caddy): atualize de tempos em tempos. O `unattended-upgrades` cuida do sistema.
7. **Endereço sslip.io**: o endereço do painel é público e indexável por certificados; o token é o que protege, não o segredo do endereço.

## Rotina sugerida

- **Todo mês**: `bash /root/auditar.sh`; baixar um backup e abrir com o BdkTool (prova de que a chave privada funciona).
- **A cada 6 meses**: trocar o token do painel; renovar o login da Steam antes do vencimento (o aviso vem 14 dias antes).
- **A cada atualização do programa**: `bash /root/atualizar.sh` (guarda as 3 últimas versões para voltar atrás).

## Se algo der errado

- **Token vazou**: painel › Segurança › Gerar novo token; atualize o app.
- **Celular perdido**: gere novo token (invalida o app perdido) e, se quiser, a senha de desbloqueio do farm.
- **Suspeita de invasão da VPS**: troque a senha da Steam e remova os dispositivos; recrie a VPS; restaure o backup
  (`BdkTool backup`) e renove o login. Revise a chave SSH autorizada em `/root/.ssh/authorized_keys`.
- **Perdeu a chave privada ou a senha dela**: os backups antigos não abrem. Gere um par novo (`BdkTool keygen`) e troque
  a pública na VPS; os backups novos já valem.
