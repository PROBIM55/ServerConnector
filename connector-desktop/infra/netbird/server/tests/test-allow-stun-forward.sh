#!/usr/bin/env bash
set -Eeuo pipefail

readonly ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
readonly SCRIPT="$ROOT/allow-stun-forward.sh"
readonly UNIT="$ROOT/structura-netbird-stun-forward.service"
readonly EXPECTED='-A DOCKER-USER -i eth0 -p udp -m conntrack --ctdir ORIGINAL --ctorigdst 109.73.194.38 --ctorigdstport 3479 -m comment --comment structura-netbird-stun -j RETURN'
readonly DROP='-A DOCKER-USER -i eth0 -j DROP'
readonly FIXTURE='-A DOCKER-USER -i eth0 -m conntrack --ctstate RELATED,ESTABLISHED -j RETURN
-A DOCKER-USER -i eth0 -p udp -m conntrack --ctorigdstport 3478 -j RETURN
-A DOCKER-USER -i eth0 -j DROP
-A DOCKER-USER -j RETURN'

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
fake="$tmp/iptables"
state="$tmp/rules"
calls="$tmp/calls"

cat > "$fake" <<'FAKE_IPTABLES'
#!/usr/bin/env bash
set -Eeuo pipefail
state="${FAKE_IPTABLES_STATE:?}"
calls="${FAKE_IPTABLES_CALLS:?}"
printf '%s\n' "$*" >> "$calls"
[[ "${1:-}" == '-w' ]] || exit 64
shift
case "${1:-}" in
  -S)
    [[ "${2:-}" == 'DOCKER-USER' && -f "$state" ]] || exit 1
    [[ "${FAKE_IPTABLES_WRONG_CHAIN:-0}" == '0' ]] || exit 1
    printf '%s\n' '-N DOCKER-USER'
    cat "$state"
    ;;
  -C)
    [[ "${2:-}" == 'DOCKER-USER' && -f "$state" ]] || exit 1
    shift 2
    expected="-A DOCKER-USER $*"
    grep -Fxq -- "$expected" "$state"
    ;;
  -I)
    [[ "${2:-}" == 'DOCKER-USER' && "${3:-}" =~ ^[1-9][0-9]*$ && -f "$state" ]] || exit 1
    index="$3"
    shift 3
    expected="-A DOCKER-USER $*"
    awk -v idx="$index" -v rule="$expected" 'NR == idx { print rule } { print } END { if (NR < idx - 1) exit 1 }' "$state" > "$state.next"
    mv "$state.next" "$state"
    ;;
  *) exit 64 ;;
esac
FAKE_IPTABLES
chmod +x "$fake"

fail() { printf 'FAIL: %s\n' "$*" >&2; exit 1; }
assert_eq() { [[ "$1" == "$2" ]] || fail "$3"; }
assert_contains() { grep -Fxq -- "$2" <<< "$1" || fail "$3"; }
run_script() {
  IPTABLES_BIN="$fake" FAKE_IPTABLES_STATE="$state" FAKE_IPTABLES_CALLS="$calls" bash "$SCRIPT"
}
reset_fixture() { printf '%s\n' "$FIXTURE" > "$state"; : > "$calls"; }

# Absent owned rule: insert exactly before DROP and preserve all existing rules.
reset_fixture
run_script
actual="$(cat "$state")"
assert_contains "$actual" "$EXPECTED" 'missing narrow exception after insert'
expected_before_drop="$(printf '%s\n' "${FIXTURE%%$DROP*}" "$EXPECTED" "$DROP" '-A DOCKER-USER -j RETURN' | sed '/^$/d')"
assert_eq "$actual" "$expected_before_drop" 'rule was not inserted before DROP or changed unrelated rules'
if grep -Eq '(^| )(-F|-D)( |$)' "$calls"; then fail 'script flushed or deleted a rule'; fi

# Existing exact rule: idempotent and no chain mutation.
before="$actual"
calls_before="$(wc -l < "$calls")"
run_script
assert_eq "$(cat "$state")" "$before" 'idempotent run changed the chain'
calls_after="$(wc -l < "$calls")"
[[ "$calls_after" -gt "$calls_before" ]] || fail 'idempotent run did not inspect the chain'
new_calls="$(tail -n +"$((calls_before + 1))" "$calls")"
if grep -Eq '^-w -I ' <<< "$new_calls"; then fail 'idempotent run inserted a duplicate'; fi

# Missing/wrong chain and missing expected DROP fail without modifying rules.
if IPTABLES_BIN="$fake" FAKE_IPTABLES_STATE="$tmp/missing-chain" FAKE_IPTABLES_CALLS="$calls" bash "$SCRIPT" >/dev/null 2>&1; then fail 'missing chain unexpectedly succeeded'; fi
[[ ! -e "$tmp/missing-chain" ]] || fail 'missing-chain fixture was modified'

reset_fixture
before="$(cat "$state")"
if IPTABLES_BIN="$fake" FAKE_IPTABLES_STATE="$state" FAKE_IPTABLES_CALLS="$calls" FAKE_IPTABLES_WRONG_CHAIN=1 bash "$SCRIPT" >/dev/null 2>&1; then fail 'wrong chain unexpectedly succeeded'; fi
assert_eq "$(cat "$state")" "$before" 'wrong-chain failure modified rules'

printf '%s\n' "${FIXTURE/$DROP/-A DOCKER-USER -i eth0 -j REJECT}" > "$state"
before="$(cat "$state")"
if run_script >/dev/null 2>&1; then fail 'missing expected DROP unexpectedly succeeded'; fi
assert_eq "$(cat "$state")" "$before" 'missing-DROP failure modified chain'

# A changed rule carrying our comment is a conflict, not a reason to repair it.
reset_fixture
sed -i "s|$DROP|-A DOCKER-USER -p udp -m comment --comment structura-netbird-stun -j RETURN\n$DROP|" "$state"
before="$(cat "$state")"
if run_script >/dev/null 2>&1; then fail 'unexpected owned rule unexpectedly succeeded'; fi
assert_eq "$(cat "$state")" "$before" 'owned-rule conflict modified chain'

# A foreign rule remains byte-for-byte in its original place.
reset_fixture
sed -i '2i -A DOCKER-USER -s 192.0.2.10 -j RETURN' "$state"
run_script
assert_contains "$(cat "$state")" '-A DOCKER-USER -s 192.0.2.10 -j RETURN' 'foreign rule was not preserved'
[[ "$(cat "$state")" == *$'\n-A DOCKER-USER -s 192.0.2.10 -j RETURN\n'* ]] || fail 'foreign rule moved'

grep -Fq 'After=docker.service structura-docker-user-firewall.service' "$UNIT" || fail 'unit does not order after Docker firewall loader'
grep -Fq 'PartOf=docker.service structura-docker-user-firewall.service' "$UNIT" || fail 'unit will not reapply with firewall loader'
printf 'PASS: insertion, idempotency, fail-closed chain/anchor/ownership checks, foreign-rule preservation, unit ordering\n'
