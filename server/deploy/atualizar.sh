#!/usr/bin/env bash
set -euo pipefail

ZIP="${1:-/root/publish.zip}"
DEST=/opt/bdkhourfarm
KEEP_RELEASES="${KEEP_RELEASES:-3}"
RELEASES=/opt/bdkhourfarm-releases

[ -f "$ZIP" ] || { echo "Não achei $ZIP. Envie com: scp .\\publish.zip root@SEU_IP:/root/"; exit 1; }
command -v unzip >/dev/null || apt-get install -y unzip

id bdk >/dev/null 2>&1 || useradd -r -m -d "$DEST" bdk

echo ">> Parando o farm..."
systemctl stop bdkhourfarm 2>/dev/null || true

echo ">> Copiando a versão nova (data/ é preservada)..."
set +e
unzip -o -q "$ZIP" -d "$DEST"
rc=$?
set -e
if [ "$rc" -ge 2 ]; then
  echo "Falha ao descompactar $ZIP (código $rc). O farm continua parado." >&2
  exit 1
fi
chown -R bdk:bdk "$DEST"
chmod +x "$DEST/BdkHourFarm"
mkdir -p "$DEST/data"
chown bdk:bdk "$DEST/data"
chmod 750 "$DEST"
chmod 700 "$DEST/data"

echo ">> Limpando versões antigas (guardando as $KEEP_RELEASES últimas)..."
mkdir -p "$RELEASES"
chmod 700 "$RELEASES"
if [ "$KEEP_RELEASES" -gt 0 ]; then
  case "$(readlink -f "$ZIP")" in
    "$RELEASES"/*) ;;
    *) cp "$ZIP" "$RELEASES/publish-$(date +%Y%m%d-%H%M%S).zip" ;;
  esac
fi
(ls -1t "$RELEASES"/publish-*.zip 2>/dev/null || true) | tail -n +$((KEEP_RELEASES + 1)) | xargs -r rm -f --
[ "$ZIP" = "/root/publish.zip" ] && rm -f "$ZIP"
journalctl --vacuum-size=100M >/dev/null 2>&1 || true
apt-get clean >/dev/null 2>&1 || true

echo ">> Atualizando o serviço (com proteções do systemd)..."
write_unit() {
  local hard="$1"
  cat > /etc/systemd/system/bdkhourfarm.service <<UNIT
[Unit]
Description=BDK Hour Farm (Steam)
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=bdk
WorkingDirectory=$DEST
Environment=BDK_DATA_DIR=$DEST/data
ExecStart=$DEST/BdkHourFarm run
Restart=on-failure
RestartSec=15
RestartPreventExitStatus=2
UMask=0077
UNIT
  if [ "$hard" = "1" ]; then
    cat >> /etc/systemd/system/bdkhourfarm.service <<UNIT
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=full
ProtectHome=true
ProtectKernelTunables=true
ProtectKernelModules=true
ProtectControlGroups=true
RestrictSUIDSGID=true
UNIT
  fi
  cat >> /etc/systemd/system/bdkhourfarm.service <<UNIT

[Install]
WantedBy=multi-user.target
UNIT
}

write_unit 1
systemctl daemon-reload
systemctl enable bdkhourfarm >/dev/null 2>&1 || true

echo ">> Iniciando..."
systemctl start bdkhourfarm || true
sleep 8
if ! systemctl is-active --quiet bdkhourfarm; then
  echo "!! Não subiu com as proteções extras. Tentando sem elas..."
  systemctl stop bdkhourfarm 2>/dev/null || true
  write_unit 0
  systemctl daemon-reload
  systemctl start bdkhourfarm || true
  sleep 8
fi
systemctl --no-pager --lines=15 status bdkhourfarm || true

echo
echo "Pronto. Token do painel:"
sudo -u bdk "$DEST/BdkHourFarm" token
