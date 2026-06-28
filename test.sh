#!/usr/bin/env bash
#
# test.sh — scan (and optionally fix) truncated agent models in wp_player_agents.
#
# Background:
#   A valid agent model looks like  "ctm_st6/ctm_st6_variantn"  ->  dir/file
#   where the filename ALWAYS starts with the directory name.
#   Old web/legacy rows lost the leading path chars, e.g.
#       "ctm_st6/ctm_st6_variantn"  ->  "st6/ctm_st6_variantn"
#       "tm_professional/..._varf5" ->  "rofessional/..._varf5"
#   The filename is intact, so the real model is reconstructable:
#       real dir = first two "_"-separated tokens of the filename.
#
# Usage:
#   ./test.sh                 # scan only, report suspect rows (read-only)
#   ./test.sh --fix           # also UPDATE suspect rows to the healed value
#
# DB connection via env (or edit defaults below):
#   DB_HOST DB_PORT DB_USER DB_PASS DB_NAME

set -euo pipefail

DB_HOST="${DB_HOST:-23.88.127.11}"
DB_PORT="${DB_PORT:-3306}"
DB_USER="${DB_USER:-root}"
DB_PASS="${DB_PASS:-ngwRaH7JjRECekLFBPcY}"
DB_NAME="${DB_NAME:-cs2skins}"

FIX=0
[[ "${1:-}" == "--fix" ]] && FIX=1

mysql_q() {
    mysql --host="$DB_HOST" --port="$DB_PORT" --user="$DB_USER" \
          ${DB_PASS:+--password="$DB_PASS"} --database="$DB_NAME" \
          --batch --raw --skip-column-names -e "$1"
}

# Returns the healed model for a stored value, or the value unchanged if it looks valid/empty.
heal() {
    local v="$1"
    [[ -z "$v" || "$v" == "NULL" || "$v" != */* ]] && { printf '%s' "$v"; return; }
    local dir="${v%%/*}" file="${v#*/}"
    # already valid: filename starts with the directory
    [[ "$file" == "$dir"* ]] && { printf '%s' "$v"; return; }
    # reconstruct dir = first two "_"-tokens of the filename
    local t1 t2 rest
    IFS='_' read -r t1 t2 rest <<< "$file"
    [[ -z "$t1" || -z "$t2" ]] && { printf '%s' "$v"; return; }
    printf '%s/%s' "${t1}_${t2}" "$file"
}

echo "Scanning wp_player_agents on $DB_HOST:$DB_PORT/$DB_NAME ..."
echo

suspect=0
fixed=0
while IFS=$'\t' read -r steamid ct t; do
    [[ -z "${steamid:-}" ]] && continue
    new_ct="$(heal "$ct")"
    new_t="$(heal "$t")"
    if [[ "$new_ct" != "$ct" || "$new_t" != "$t" ]]; then
        suspect=$((suspect+1))
        echo "steamid=$steamid"
        [[ "$new_ct" != "$ct" ]] && echo "  CT: '$ct'  ->  '$new_ct'"
        [[ "$new_t"  != "$t"  ]] && echo "  T : '$t'   ->  '$new_t'"
        if [[ "$FIX" == "1" ]]; then
            esc_ct="${new_ct//\'/\'\'}"; esc_t="${new_t//\'/\'\'}"
            mysql_q "UPDATE wp_player_agents SET agent_ct='${esc_ct}', agent_t='${esc_t}' WHERE steamid='${steamid//\'/\'\'}';"
            fixed=$((fixed+1))
            echo "  -> fixed"
        fi
    fi
done < <(mysql_q "SELECT steamid, COALESCE(agent_ct,''), COALESCE(agent_t,'') FROM wp_player_agents;")

echo
echo "Suspect rows: $suspect"
[[ "$FIX" == "1" ]] && echo "Fixed rows:   $fixed" || echo "(run with --fix to apply UPDATEs)"
