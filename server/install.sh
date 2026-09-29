#!/bin/bash
# LANFax faxd installer. Run as root; idempotent.
set -euo pipefail

if [ "$(id -u)" -ne 0 ]; then
    echo "install.sh must run as root" >&2
    exit 1
fi

HERE="$(cd "$(dirname "$0")" && pwd)"
ENV_FILE=/etc/faxserver/settings.env
AMI_CONF=/etc/asterisk/manager.d/faxd.conf
SHARE=/srv/fax
NEW_TOKEN=""
SWAP_MSG="swap already present"

gen_hex() {
    if command -v openssl >/dev/null 2>&1; then
        openssl rand -hex 16
    else
        head -c16 /dev/urandom | od -An -tx1 | tr -d ' \n'
    fi
}

# set KEY=value in settings.env: replace in place, else append
set_env() {
    local key="$1" val="$2"
    if grep -q "^${key}=" "$ENV_FILE"; then
        sed -i "s|^${key}=.*|${key}=${val}|" "$ENV_FILE"
    else
        echo "${key}=${val}" >> "$ENV_FILE"
    fi
}

get_env() {
    grep "^$1=" "$ENV_FILE" 2>/dev/null | head -n1 | cut -d= -f2- || true
}

# 0. swap
if [ -z "$(swapon --show 2>/dev/null)" ]; then
    if [ ! -f /swapfile ]; then
        dd if=/dev/zero of=/swapfile bs=1M count=512 status=none
    fi
    chmod 600 /swapfile
    mkswap /swapfile >/dev/null
    swapon /swapfile
    grep -q '^/swapfile ' /etc/fstab || echo '/swapfile none swap sw 0 0' >> /etc/fstab
    SWAP_MSG="created and enabled 512 MB /swapfile"
fi

# 1. user and groups
if ! id faxd >/dev/null 2>&1; then
    useradd --system --home /var/lib/faxd --shell /usr/sbin/nologin faxd
fi
for g in asterisk fax; do
    if getent group "$g" >/dev/null 2>&1; then
        usermod -aG "$g" faxd
    fi
done
install -d -o faxd -g faxd -m 0750 /var/lib/faxd

# 2. code (the running version is kept as faxd.prev for rollback)
install -d /opt/faxd
if [ -d /opt/faxd/faxd ]; then
    rm -rf /opt/faxd/faxd.prev
    cp -a /opt/faxd/faxd /opt/faxd/faxd.prev
fi
if command -v rsync >/dev/null 2>&1; then
    rsync -a --delete --exclude __pycache__ "$HERE/faxd/" /opt/faxd/faxd/
else
    rm -rf /opt/faxd/faxd
    cp -r "$HERE/faxd" /opt/faxd/faxd
    find /opt/faxd/faxd -name __pycache__ -type d -prune -exec rm -rf {} +
fi
chown -R root:root /opt/faxd

# put faxd.prev back in place of the new code; false when there is no previous version
rollback_code() {
    [ -d /opt/faxd/faxd.prev ] || return 1
    rm -rf /opt/faxd/faxd
    cp -a /opt/faxd/faxd.prev /opt/faxd/faxd
}

# smoke import before anything restarts: a package that does not even import never
# replaces the running service (which keeps the old code loaded until a restart)
if ! (cd / && PYTHONPATH=/opt/faxd python3 -B -c "import faxd.main"); then
    echo "ERROR: the new faxd package fails to import; previous code restored, service left running untouched" >&2
    rollback_code || echo "ERROR: no previous version to restore" >&2
    exit 1
fi

# settings.env must exist before we edit it
install -d /etc/faxserver
[ -f "$ENV_FILE" ] || install -m 0640 /dev/null "$ENV_FILE"
chgrp faxd "$ENV_FILE"
chmod 0640 "$ENV_FILE"

# 3. sudoers (only for a root-owned script nobody else can modify)
ARM_SCRIPT=/usr/local/bin/fax-arm.sh
if [ ! -f "$ARM_SCRIPT" ] || [ "$(stat -c %u "$ARM_SCRIPT")" != 0 ] || [ -n "$(find "$ARM_SCRIPT" -perm /022)" ]; then
    echo "refusing to grant sudo: $ARM_SCRIPT must exist, be owned by root and not be group/world writable" >&2
    stat -c '%U:%G %A %n' "$ARM_SCRIPT" >&2 || true
    exit 1
fi
TMP_SUDO="$(mktemp)"
echo "faxd ALL=(root) NOPASSWD: $ARM_SCRIPT" > "$TMP_SUDO"
chmod 0440 "$TMP_SUDO"
visudo -c -f "$TMP_SUDO" >/dev/null
install -m 0440 -o root -g root "$TMP_SUDO" /etc/sudoers.d/faxd
rm -f "$TMP_SUDO"
visudo -c >/dev/null

# 4. AMI user
AMI_SECRET=""
if [ -f "$AMI_CONF" ]; then
    AMI_SECRET="$(grep -m1 '^secret' "$AMI_CONF" | sed 's/^secret *= *//' | tr -d ' \r' || true)"
fi
[ -n "$AMI_SECRET" ] || AMI_SECRET="$(gen_hex)"
install -d /etc/asterisk/manager.d
cat > "$AMI_CONF" <<EOF
[faxd]
secret = ${AMI_SECRET}
deny=0.0.0.0/0.0.0.0
permit=127.0.0.1/255.255.255.255
read=system,call,user,reporting,dialplan
write=system,call,reporting
EOF
chown root:asterisk "$AMI_CONF"
chmod 0640 "$AMI_CONF"
asterisk -rx "manager reload" >/dev/null || echo "warning: asterisk manager reload failed" >&2
set_env FAXD_AMI_SECRET "$AMI_SECRET"
unset AMI_SECRET

# 5. token
if [ -z "$(get_env FAXD_TOKEN)" ]; then
    NEW_TOKEN="$(gen_hex)"
    set_env FAXD_TOKEN "$NEW_TOKEN"
fi

# 6. share dirs
install -d -o faxd -g faxd "$SHARE/queue-meta"

# 7. service
install -m 0644 "$HERE/faxd.service" /etc/systemd/system/faxd.service
systemctl daemon-reload
systemctl enable faxd >/dev/null 2>&1
systemctl restart faxd

HTTP_PORT="$(get_env FAXD_HTTP_PORT)"
HTTP_PORT="${HTTP_PORT:-8080}"
health() {
    local out
    for _ in 1 2 3 4 5 6 7 8 9 10; do
        if out="$(curl -fsS "http://127.0.0.1:${HTTP_PORT}/healthz" 2>/dev/null)"; then
            echo "$out"
            return 0
        fi
        sleep 1
    done
    echo "failed"
    return 1
}
HEALTH="$(health || true)"

# rollback: the new version did not come up healthy, so restore faxd.prev and restart it
if [ "$HEALTH" = "failed" ]; then
    echo "ERROR: faxd failed its health check after the restart; rolling back" >&2
    journalctl -u faxd -n 20 --no-pager >&2 || true
    if rollback_code; then
        systemctl restart faxd
        echo "ERROR: rolled back to /opt/faxd/faxd.prev; previous version is $(systemctl is-active faxd || true), healthz: $(health || true). The new version is NOT deployed." >&2
    else
        echo "ERROR: no /opt/faxd/faxd.prev to roll back to; faxd is down" >&2
    fi
    exit 1
fi

# 8. firewall
UFW_MSG="ufw not installed"
if command -v ufw >/dev/null 2>&1; then
    if ufw status | grep -q '^Status: active'; then
        ufw allow from 192.168.0.0/16 to any port "$HTTP_PORT" proto tcp >/dev/null
        UFW_MSG="ufw rule added for 192.168.0.0/16 -> $HTTP_PORT"
    else
        UFW_MSG="ufw inactive, no rule added"
    fi
fi

echo "==== faxd install summary ===="
if [ -n "$NEW_TOKEN" ]; then
    echo "FAXD_TOKEN (new, shown once): $NEW_TOKEN"
else
    echo "FAXD_TOKEN: unchanged (in $ENV_FILE)"
fi
echo "swap: $SWAP_MSG"
echo "firewall: $UFW_MSG"
echo "service: $(systemctl is-active faxd || true) / $(systemctl is-enabled faxd || true)"
echo "healthz: $HEALTH"
[ "$HEALTH" != "failed" ] || exit 1
