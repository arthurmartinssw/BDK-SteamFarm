#!/usr/bin/env bash
# Confere a segurança da VPS e do BdkHourFarm. Só LÊ coisas: não altera nada.
# Uso (como root):  bash /root/auditar.sh
set -u

DEST=/opt/bdkhourfarm
DATA="$DEST/data"
ok()   { printf '  [ OK  ] %s\n' "$1"; }
warn() { printf '  [AVISO] %s\n' "$1"; WARNS=$((WARNS + 1)); }
WARNS=0

echo "== SSH =="
if command -v sshd >/dev/null; then
  cfg=$(sshd -T 2>/dev/null || true)
  echo "$cfg" | grep -qi '^passwordauthentication no' && ok "login por senha desligado" || warn "login por senha LIGADO (rode ssh-seguro.sh travar)"
  echo "$cfg" | grep -qi '^pubkeyauthentication yes'  && ok "login por chave ligado" || warn "login por chave desligado"
  root=$(echo "$cfg" | awk 'tolower($1)=="permitrootlogin"{print $2}')
  case "$root" in
    no) ok "login direto como root desligado" ;;
    prohibit-password|without-password) ok "root só entra por chave (ideal: criar um usuário comum com sudo e desligar o root)" ;;
    *) warn "PermitRootLogin = $root" ;;
  esac
else
  warn "sshd não encontrado"
fi

echo "== Rede =="
if command -v ufw >/dev/null && ufw status | grep -q "Status: active"; then
  ok "firewall (ufw) ativo"
else
  warn "ufw inativo. Se o provedor não tem firewall próprio: ufw allow 22/tcp; ufw allow 80/tcp; ufw allow 443/tcp; ufw enable"
fi
echo "  portas abertas:"
ss -tlnH 2>/dev/null | awk '{print "    " $4}' | sort -u
if ss -tlnH 2>/dev/null | awk '{print $4}' | grep -E '(^0\.0\.0\.0:8080$|^\*:8080$|^\[::\]:8080$)' >/dev/null; then
  warn "a porta 8080 do painel está exposta na internet (deveria ser só 127.0.0.1 atrás do Caddy)"
else
  ok "painel (8080) só acessível localmente"
fi

echo "== Serviço =="
systemctl is-active --quiet bdkhourfarm && ok "bdkhourfarm rodando" || warn "bdkhourfarm parado"
pid=$(systemctl show -p MainPID --value bdkhourfarm 2>/dev/null || echo 0)
if [ "${pid:-0}" != "0" ]; then
  u=$(ps -o user= -p "$pid" 2>/dev/null | tr -d ' ')
  [ "$u" = "bdk" ] && ok "roda como usuário sem privilégios (bdk)" || warn "roda como '$u' (deveria ser bdk)"
fi
if command -v systemd-analyze >/dev/null; then
  echo "  exposição systemd: $(systemd-analyze security bdkhourfarm --no-pager 2>/dev/null | tail -n 1)"
fi
systemctl is-active --quiet caddy && ok "Caddy (HTTPS) rodando" || warn "Caddy parado: o painel não tem HTTPS"

echo "== Arquivos =="
if [ -d "$DATA" ]; then
  mode=$(stat -c %a "$DATA")
  [ "$mode" = "700" ] && ok "data/ com permissão 700" || warn "data/ está com $mode (esperado 700)"
  loose=$(find "$DATA" -type f -perm /077 2>/dev/null | head -n 5)
  [ -z "$loose" ] && ok "arquivos de data/ só legíveis pelo dono" || { warn "arquivos legíveis por outros:"; echo "$loose" | sed 's/^/      /'; }
  if [ -f "$DATA/api-token.txt" ]; then
    age=$(( ( $(date +%s) - $(stat -c %Y "$DATA/api-token.txt") ) / 86400 ))
    [ "$age" -gt 180 ] && warn "token do painel tem $age dias (considere trocar pelo painel)" || ok "token do painel tem $age dias"
  fi
  [ -f "$DATA/backup-public.pem" ] && ok "chave pública de backup instalada" || warn "sem chave pública: backups e logs cifrados desligados"
  if ls "$DATA"/backups/backup-*.bdkb >/dev/null 2>&1; then
    last=$(ls -1t "$DATA"/backups/backup-*.bdkb | head -n 1)
    age=$(( ( $(date +%s) - $(stat -c %Y "$last") ) / 3600 ))
    [ "$age" -le 30 ] && ok "último backup há ${age} h" || warn "último backup há ${age} h (esperado menos de 24 h)"
  else
    warn "nenhum backup encontrado"
  fi
else
  warn "pasta $DATA não encontrada"
fi

echo "== Sistema =="
dpkg -s unattended-upgrades >/dev/null 2>&1 && ok "atualizações de segurança automáticas instaladas" || warn "unattended-upgrades ausente (apt install unattended-upgrades)"
dpkg -s fail2ban >/dev/null 2>&1 && ok "fail2ban instalado" || warn "fail2ban ausente (opcional: bloqueia quem insiste em errar o SSH)"
pending=$(apt list --upgradable 2>/dev/null | grep -c upgradable || true)
[ "${pending:-0}" -eq 0 ] && ok "sem atualizações pendentes" || warn "$pending pacote(s) para atualizar (apt update && apt upgrade)"
[ -f /var/run/reboot-required ] && warn "reinício do servidor pendente (reboot)" || ok "sem reinício pendente"
use=$(df --output=pcent / | tail -n 1 | tr -dc '0-9')
[ "${use:-0}" -lt 80 ] && ok "disco em ${use}%" || warn "disco em ${use}% (rode atualizar.sh para limpar, ou apague backups antigos)"

echo
[ "$WARNS" -eq 0 ] && echo "Tudo certo." || echo "$WARNS ponto(s) de atenção acima."
