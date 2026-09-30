#!/usr/bin/env bash
# Throwaway OpenSSH server for the ssh/scp selftest: own host key and config in a temp dir,
# listening on 127.0.0.1:$2, accepting only the public key in $1 for the current user.
# Prints the temp dir; stop with: kill "$(cat <dir>/sshd.pid)"
set -euo pipefail
PUBKEY="$1"
PORT="${2:-2222}"
DIR="$(mktemp -d)"
ssh-keygen -q -t ed25519 -N '' -f "$DIR/host_ed25519"
cp "$PUBKEY" "$DIR/authorized_keys"
chmod 600 "$DIR/authorized_keys"
chmod 700 "$DIR"
cat > "$DIR/sshd_config" <<EOF
Port $PORT
ListenAddress 127.0.0.1
HostKey $DIR/host_ed25519
PidFile $DIR/sshd.pid
AuthorizedKeysFile $DIR/authorized_keys
PasswordAuthentication no
KbdInteractiveAuthentication no
PubkeyAuthentication yes
PermitRootLogin prohibit-password
StrictModes no
UsePAM no
AllowUsers $(id -un)
Subsystem sftp internal-sftp
EOF
SUDO=""
[ "$(id -u)" -ne 0 ] && SUDO="sudo"
$SUDO mkdir -p /run/sshd
$SUDO /usr/sbin/sshd -f "$DIR/sshd_config" -E "$DIR/sshd.log"
for _ in $(seq 50); do [ -s "$DIR/sshd.pid" ] && break; sleep 0.1; done
echo "$DIR"
