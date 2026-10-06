#!/usr/bin/env bash
set -Eeuo pipefail

# Narrow exception for the self-hosted NetBird STUN UDP port. Docker-published
# traffic follows DNAT/FORWARD and is filtered by DOCKER-USER, not UFW INPUT.
readonly IPTABLES_BIN="${IPTABLES_BIN:-/usr/sbin/iptables}"
readonly CHAIN='DOCKER-USER'
readonly EXPECTED_DROP='-A DOCKER-USER -i eth0 -j DROP'
readonly OWNED_MARKER='structura-netbird-stun'

die() {
  printf '[netbird-stun-forward] %s\n' "$*" >&2
  exit 1
}

[[ "$EUID" -eq 0 ]] || die 'must run as root'
[[ "$IPTABLES_BIN" == /* && -x "$IPTABLES_BIN" ]] || die 'trusted absolute iptables executable is unavailable'

readonly -a RULE_ARGS=(
  -i eth0
  -p udp
  -m conntrack
  --ctdir ORIGINAL
  --ctorigdst 109.73.194.38
  --ctorigdstport 3479
  -m comment
  --comment structura-netbird-stun
  -j RETURN
)

chain_rules="$("$IPTABLES_BIN" -w -S "$CHAIN")" || die "cannot read $CHAIN chain"
rule_number=0
drop_number=0
drop_count=0
owned_number=0
owned_count=0

while IFS= read -r line; do
  [[ "$line" == "-A $CHAIN "* ]] || continue
  rule_number=$((rule_number + 1))

  if [[ "$line" == "$EXPECTED_DROP" ]]; then
    drop_count=$((drop_count + 1))
    drop_number=$rule_number
  elif [[ "$line" == *'-j DROP'* ]]; then
    die 'unexpected DROP rule in DOCKER-USER; refusing to guess insertion order'
  fi

  if [[ "$line" == *"$OWNED_MARKER"* ]]; then
    owned_count=$((owned_count + 1))
    owned_number=$rule_number
  fi
done <<< "$chain_rules"

[[ "$drop_count" -eq 1 ]] || die "expected exactly one $EXPECTED_DROP rule; found $drop_count"
[[ "$owned_count" -le 1 ]] || die 'duplicate owned rules found; refusing to alter the chain'

if [[ "$owned_count" -eq 1 ]]; then
  [[ "$owned_number" -lt "$drop_number" ]] || die 'owned rule is after the DROP rule'
  "$IPTABLES_BIN" -w -C "$CHAIN" "${RULE_ARGS[@]}" || die 'owned comment exists but rule differs from the expected exception'
  printf '[netbird-stun-forward] already present before expected DROP\n'
  exit 0
fi

# Insert at the current DROP position, which places this exact exception before
# the fail-closed rule without flushing, deleting, or rebuilding any rule.
"$IPTABLES_BIN" -w -I "$CHAIN" "$drop_number" "${RULE_ARGS[@]}" || die 'failed to insert the STUN exception'

chain_rules="$("$IPTABLES_BIN" -w -S "$CHAIN")" || die "cannot verify $CHAIN after insertion"
rule_number=0
drop_number=0
owned_number=0
drop_count=0
owned_count=0
while IFS= read -r line; do
  [[ "$line" == "-A $CHAIN "* ]] || continue
  rule_number=$((rule_number + 1))
  if [[ "$line" == "$EXPECTED_DROP" ]]; then
    drop_count=$((drop_count + 1))
    drop_number=$rule_number
  elif [[ "$line" == *'-j DROP'* ]]; then
    die 'unexpected DROP rule appeared during insertion'
  fi
  if [[ "$line" == *"$OWNED_MARKER"* ]]; then
    owned_count=$((owned_count + 1))
    owned_number=$rule_number
  fi
done <<< "$chain_rules"

[[ "$drop_count" -eq 1 && "$owned_count" -eq 1 && "$owned_number" -lt "$drop_number" ]] ||
  die 'post-insert chain verification failed; inspect the narrow rule manually'
"$IPTABLES_BIN" -w -C "$CHAIN" "${RULE_ARGS[@]}" || die 'inserted rule was not found by iptables -C'
printf '[netbird-stun-forward] inserted narrow UDP 3479 exception before expected DROP\n'
