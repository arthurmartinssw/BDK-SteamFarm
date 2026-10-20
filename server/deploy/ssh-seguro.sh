#!/usr/bin/env bash
# Passa o acesso SSH da VPS de senha para chave.
# Uso (como root):
#   bash ssh-seguro.sh chave    -> instala a chave pública enviada em /root/*.pub
#   bash ssh-seguro.sh travar   -> desliga o login por senha (só depois de testar a chave!)
set -euo pipefail

cmd="${1:-}"
AK=/root/.ssh/authorized_keys
CONF=/etc/ssh/sshd_config.d/00-bdk-seguro.conf

reload_ssh() {
  systemctl reload ssh 2>/dev/null || systemctl reload sshd
}

case "$cmd" in
  chave)
    shopt -s nullglob
    pubs=(/root/*.pub)
    [ "${#pubs[@]}" -gt 0 ] || { echo "Não achei nenhum arquivo .pub em /root. Envie com scp antes." >&2; exit 1; }
    mkdir -p /root/.ssh
    chmod 700 /root/.ssh
    touch "$AK"
    for f in "${pubs[@]}"; do
      line="$(tr -d '\r' < "$f" | head -n1)"
      case "$line" in
        ssh-ed25519\ *|ssh-rsa\ *|ecdsa-sha2-*) ;;
        *) echo "$f não parece uma chave pública. Pulei." >&2; continue ;;
      esac
      grep -qxF "$line" "$AK" || echo "$line" >> "$AK"
      echo "Chave instalada: $f"
    done
    chmod 600 "$AK"
    echo
    echo "Agora abra OUTRO terminal e teste:  ssh root@SEU_IP"
    echo "Se entrar sem pedir a senha da VPS, volte aqui e rode:  bash ssh-seguro.sh travar"
    ;;

  travar)
    if ! grep -Eq '^(ssh-ed25519|ssh-rsa|ecdsa-sha2-)' "$AK" 2>/dev/null; then
      echo "Não há nenhuma chave em $AK. Rode primeiro:  bash ssh-seguro.sh chave" >&2
      exit 1
    fi
    cat > "$CONF" <<'CFG'
PasswordAuthentication no
KbdInteractiveAuthentication no
PermitRootLogin prohibit-password
PubkeyAuthentication yes
CFG
    if ! sshd -t; then
      rm -f "$CONF"
      echo "Configuração inválida; desfeito. Nada mudou." >&2
      exit 1
    fi
    reload_ssh
    if sshd -T 2>/dev/null | grep -qi '^passwordauthentication yes'; then
      rm -f "$CONF"
      reload_ssh
      echo "Outra configuração ainda permite senha; desfeito. Nada mudou." >&2
      exit 1
    fi
    echo "Pronto: login por senha desligado. Só entra quem tem a chave."
    echo "Se um dia perder a chave, use o console web da vStack (ele não depende do SSH)."
    ;;

  *)
    echo "Uso: bash ssh-seguro.sh chave | travar" >&2
    exit 1
    ;;
esac
