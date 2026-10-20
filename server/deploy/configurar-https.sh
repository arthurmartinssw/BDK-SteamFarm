#!/usr/bin/env bash
# Coloca o painel do farm em HTTPS com o Caddy (certificado gratuito automático).
# Uso (como root):  bash /root/configurar-https.sh            -> usa SEU-IP.sslip.io
#                   bash /root/configurar-https.sh meu.dominio -> usa um domínio seu
set -euo pipefail

IP=$(ip -4 route get 1.1.1.1 | awk '{for (i = 1; i < NF; i++) if ($i == "src") print $(i + 1)}')
DOMAIN="${1:-${IP//./-}.sslip.io}"
PORT=8080

echo ">> Domínio: $DOMAIN"

if ! curl -fsS "http://127.0.0.1:$PORT/api/health" >/dev/null 2>&1; then
  echo "AVISO: o farm não está respondendo em 127.0.0.1:$PORT (o painel fica atrás do Caddy)."
  echo "       Rode primeiro:  bash /root/atualizar.sh   e confira:  systemctl status bdkhourfarm"
fi

if ! command -v caddy >/dev/null; then
  echo ">> Instalando o Caddy..."
  apt-get update
  apt-get install -y caddy
fi

cat > /etc/caddy/Caddyfile <<CADDY
$DOMAIN {
    encode gzip
    reverse_proxy 127.0.0.1:$PORT
}
CADDY

# Se o firewall do Ubuntu estiver ligado, libera HTTP/HTTPS (a porta 22 do SSH continua liberada).
if command -v ufw >/dev/null && ufw status | grep -q "Status: active"; then
  ufw allow 80/tcp
  ufw allow 443/tcp
fi

systemctl enable caddy >/dev/null
systemctl restart caddy

echo ">> Aguardando o certificado (até 60 s)..."
for _ in $(seq 1 30); do
  if curl -fsS "https://$DOMAIN/api/health" >/dev/null 2>&1; then
    TOKEN=$(sudo -u bdk /opt/bdkhourfarm/BdkHourFarm token 2>/dev/null | tail -n 1 || true)
    echo
    echo "Painel funcionando!"
    echo "Endereço: https://$DOMAIN"
    [ -n "$TOKEN" ] && echo "Link com acesso já configurado (não compartilhe):" && echo "  https://$DOMAIN/#token=$TOKEN"
    exit 0
  fi
  sleep 2
done

echo
echo "O HTTPS ainda não respondeu. Confira:"
echo "  - se as portas 80 e 443 estão liberadas no painel do provedor da VPS;"
echo "  - o log do Caddy:  journalctl -u caddy -n 50 --no-pager"
echo "  - se o farm está rodando:  systemctl status bdkhourfarm"
exit 1
