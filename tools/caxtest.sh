#!/usr/bin/env bash
# Finds out what TIA Portal's CAx export carries, by sending a real project's hardware round trip:
#
#   copy of the project  --hw export-->  A.aml  --hw import-->  empty project  --hw export-->  B.aml
#
# and comparing at each step: two exports of the same project (is the file stable?), the stations
# before and after (did they all come back?), each CPU's attributes before and after, a hardware
# compile of the rebuilt project, and A.aml against B.aml (what was lost on the way).
#
# Run it from Git Bash on a machine with a licensed TIA Portal, against an archive or a project:
#
#   bash caxtest.sh "C:\projects\Line3.zap20"
#   bash caxtest.sh "C:\projects\Line3\Line3.ap20"
#   bash caxtest.sh "C:\projects\Line3_V19.zap19" --upgrade
#
# It never touches the original: an archive is retrieved into a temp folder, a project folder is
# copied there first. Everything runs in a headless TIA Portal of its own, next to any portals or
# sessions you have open, and that portal closes at the end. Nothing goes near a PLC.
#
# The report, both AML files and the attribute dumps go to Desktop\tia-cli-fieldtest\caxtest-<time>.
# Send that folder back.

set -u

SOURCE=${1:-}
UPGRADE=
[ "${2:-}" = "--upgrade" ] && UPGRADE=--upgrade
KEEP=${KEEP:-}

if [ -z "$SOURCE" ]; then
  echo "usage: bash caxtest.sh <project.apNN | archive.zapNN> [--upgrade]" >&2
  exit 2
fi

command -v tia > /dev/null || { echo "tia is not on PATH. Install tia-cli first." >&2; exit 2; }
SOURCE_UNIX=$(cygpath -u "$SOURCE")
[ -e "$SOURCE_UNIX" ] || { echo "Not found: $SOURCE" >&2; exit 2; }

STAMP=$(date +%Y%m%d-%H%M%S)
# Where fieldtest.ps1 writes too: the real Desktop, which may be redirected to OneDrive.
DESKTOP=$(powershell -NoProfile -Command "[Environment]::GetFolderPath('Desktop')" 2>/dev/null | tr -d '\r')
[ -z "$DESKTOP" ] && DESKTOP="$USERPROFILE\\Desktop"
OUT=${OUT_DIR:-$(cygpath -u "$DESKTOP")/tia-cli-fieldtest}/caxtest-$STAMP
# Short on purpose: TIA Portal refuses project folders longer than 143 characters.
WORK=$(cygpath -u "$(cygpath -w "${TEMP:-/tmp}")")/cax-$RANDOM
mkdir -p "$OUT" "$WORK"
REPORT=$OUT/report.md
win() { cygpath -w "$1"; }

PASS=0; FAIL=0; INFO=0
{
  echo "# CAx round trip - $STAMP"
  echo
  echo "- **source**: \`$SOURCE\`"
  echo "- **machine**: $COMPUTERNAME"
  echo "- **tia**: $(tia --version 2>&1 | tr -d '\r')"
  echo
} > "$REPORT"

# run <name> <expected exit or "any"> <tia args...>: runs one command and records it.
run() {
  local name=$1 expect=$2; shift 2
  local started=$SECONDS out code verdict
  out=$(tia "$@" < /dev/null 2>&1); code=$?
  if [ "$expect" = any ]; then verdict=info; INFO=$((INFO+1))
  elif [ "$code" = "$expect" ]; then verdict=pass; PASS=$((PASS+1))
  else verdict=FAIL; FAIL=$((FAIL+1)); fi
  printf '   [%s] %s (exit %s, %ss)\n' "$verdict" "$name" "$code" "$((SECONDS-started))"
  {
    echo "### $name [$verdict]"
    echo
    echo "\`tia $*\` - exit $code, expected $expect, $((SECONDS-started))s"
    echo
    echo '```'
    printf '%s\n' "$out" | tr -d '\r'
    echo '```'
    echo
  } >> "$REPORT"
  LAST_OUT=$out; LAST_EXIT=$code
}

# check <name> <0 for pass> <detail>: a verdict this script reaches itself.
check() {
  local name=$1 ok=$2 detail=$3 verdict
  if [ "$ok" = 0 ]; then verdict=pass; PASS=$((PASS+1)); else verdict=FAIL; FAIL=$((FAIL+1)); fi
  printf '   [%s] %s\n' "$verdict" "$name"
  { echo "### $name [$verdict]"; echo; echo '```'; printf '%s\n' "$detail"; echo '```'; echo; } >> "$REPORT"
}

note() { printf '\n== %s\n' "$1"; printf '## %s\n\n' "$1" >> "$REPORT"; }

# An AML file without what changes on every export even when the hardware does not: the file name
# it was written under, the time it was written, and the element IDs - fresh GUIDs every time, so
# they are renumbered in order of first appearance. References between elements use the same IDs,
# so renumbering all of them consistently keeps the file meaningful. Whether TIA still imports such
# a file is checked below; if it does, tia can write AML that git diffs cleanly.
normalize() {
  perl -pe '
    s/FileName="[^"]*"/FileName=""/;
    s/<LastWritingDateTime>[^<]*</<LastWritingDateTime></;
    # The WriterID names TIA Portal itself and is the same in every export.
    s/([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})/$id{lc $1} \/\/= sprintf("00000000-0000-0000-0000-%012d", ++$n)/ge unless /<WriterID>/;
  ' "$1" > "$2"
}

# compare <name> <a> <b>: raw differences are information, differences after normalize are the finding.
compare() {
  local name=$1 a=$2 b=$3
  if cmp -s "$a" "$b"; then
    check "$name, byte for byte" 0 'identical'
  else
    INFO=$((INFO+1)); printf '   [info] %s: %s raw diff lines\n' "$name" "$(diff "$a" "$b" | grep -c '^[<>]')"
    { echo "### $name, raw [info]"; echo; echo '```'; diff "$a" "$b" | head -30; echo '```'; echo; } >> "$REPORT"
  fi
  normalize "$a" "$WORK/na.aml"; normalize "$b" "$WORK/nb.aml"
  if cmp -s "$WORK/na.aml" "$WORK/nb.aml"; then
    check "$name, ignoring file name, time and IDs" 0 'identical'
  else
    diff "$WORK/na.aml" "$WORK/nb.aml" > "$OUT/$(echo "$name" | tr ' ' '-').diff"
    check "$name, ignoring file name, time and IDs" 1 "$(grep -c '^[<>]' "$OUT/$(echo "$name" | tr ' ' '-').diff") diff lines; first 60:
$(head -60 "$OUT/$(echo "$name" | tr ' ' '-').diff")"
  fi
}

# Station names from 'tia devices --json': the "name" of each object at the top level.
stations() {
  tia devices --json < /dev/null 2>/dev/null | tr -d '\r' |
    awk '/^  \{/ { inside = 1; next } /^  \}/ { inside = 0; next }
         inside && /^    "name": / { sub(/^    "name": "/, ""); sub(/",?$/, ""); print }'
}

# Every CPU attribute, as tia prints it, one file per station.
dump_attrs() {
  local dir=$1; mkdir -p "$dir"
  while IFS= read -r dev; do
    [ -z "$dev" ] && continue
    tia device attrs "$dev" < /dev/null 2>&1 | tr -d '\r' > "$dir/$dev.txt"
  done < <(stations)
}

cleanup() {
  note 'Cleanup'
  [ -n "${TIA_PORTAL:-}" ] && run 'session stop' 0 session stop --attach "$TIA_PORTAL"
  # Only ever the folder this script made: a temp path ending in cax-<number>.
  case "$WORK" in
    */cax-[0-9]*) if [ -z "$KEEP" ]; then rm -rf -- "$WORK"; else echo "   kept $(win "$WORK")"; fi ;;
  esac
  { echo "---"; echo; echo "**$PASS passed, $FAIL failed, $INFO informational**"; } >> "$REPORT"
  printf '\n%s passed, %s failed, %s informational\nSend this folder back:\n  %s\n' "$PASS" "$FAIL" "$INFO" "$(win "$OUT")"
}
trap cleanup EXIT

# ------------------------------------------------------------------ a portal of its own
note 'Portal'
run 'session start --new --headless' 0 session start --new --headless
PORTAL=$(printf '%s\n' "$LAST_OUT" | tr -d '\r' | awk '/^  pid / { print $2; exit }')
if [ -z "$PORTAL" ]; then echo "The session did not start; see $REPORT" >&2; exit 1; fi
# From here on every command goes to this portal, whatever else is running.
export TIA_PORTAL=$PORTAL

# ------------------------------------------------------------------ the original, as a copy
note 'Original project (a copy)'
case "$SOURCE_UNIX" in
  *.zap*) run 'retrieve the archive' 0 project retrieve "$SOURCE" "$(win "$WORK/a")" $UPGRADE ;;
  *.ap*)  mkdir -p "$WORK/a"
          cp -r -- "$(dirname "$SOURCE_UNIX")" "$WORK/a/" || { echo "Could not copy the project folder." >&2; exit 1; }
          run 'open the copy' 0 project open "$(win "$WORK/a/$(basename "$(dirname "$SOURCE_UNIX")")/$(basename "$SOURCE_UNIX")")" $UPGRADE ;;
  *)      echo "Expected a .apNN project or a .zapNN archive: $SOURCE" >&2; exit 2 ;;
esac
[ "$LAST_EXIT" = 0 ] || exit 1

run 'stations before' 0 devices
DEVICES_A=$LAST_OUT
dump_attrs "$OUT/attrs-before"

# The rebuilds are made under the same name: the project name is written into the AML, and a
# different one would show up as the only difference.
PNAME=$(tia project info --json < /dev/null 2>/dev/null | tr -d '\r' | awk -F'"' '/^  "name": /{ print $4; exit }')
[ -n "$PNAME" ] || PNAME=CaxProbe

# Compiled here too, so errors in the rebuild can be told apart from ones the project already had.
while IFS= read -r dev; do
  [ -z "$dev" ] && continue
  run "compile $dev (original)" any compile "$dev"
done < <(stations)

run 'hw export' 0 hw export "$(win "$OUT/A.aml")"
# Under the same name, in another folder: the name is written into the file.
mkdir -p "$WORK/again"
run 'hw export again' 0 hw export "$(win "$WORK/again/A.aml")"
compare 'two exports' "$OUT/A.aml" "$WORK/again/A.aml"

while IFS= read -r dev; do
  [ -z "$dev" ] && continue
  run "hw export --device $dev" 0 hw export "$(win "$OUT/A-$dev.aml")" --device "$dev"
done < <(stations)

run 'close the original' 0 project close

# ------------------------------------------------------------------ rebuilt from the AML alone
note 'Empty project, hardware imported'
run 'project new' 0 project new "$(win "$WORK/b")" "$PNAME"
[ "$LAST_EXIT" = 0 ] || exit 1
run 'hw import' 0 hw import "$(win "$OUT/A.aml")"

run 'stations after' 0 devices
DEVICES_B=$LAST_OUT
if [ "$DEVICES_A" = "$DEVICES_B" ]; then
  check 'the same stations came back' 0 'identical device tables'
else
  check 'the same stations came back' 1 "$(diff <(printf '%s\n' "$DEVICES_A") <(printf '%s\n' "$DEVICES_B"))"
fi

dump_attrs "$OUT/attrs-after"
if diff -r "$OUT/attrs-before" "$OUT/attrs-after" > "$OUT/attrs.diff"; then
  check 'CPU attributes survived' 0 'identical attribute dumps'
else
  check 'CPU attributes survived' 1 "$(head -80 "$OUT/attrs.diff")"
fi

# A hardware compile is the honest test of whether the rebuilt stations are usable. The program is
# empty, so any error here is about the hardware.
while IFS= read -r dev; do
  [ -z "$dev" ] && continue
  run "compile $dev (rebuild)" any compile "$dev"
done < <(stations)

mkdir -p "$WORK/rebuilt"
run 'hw export of the rebuild' 0 hw export "$(win "$WORK/rebuilt/A.aml")"
cp -- "$WORK/rebuilt/A.aml" "$OUT/B.aml"
compare 'original and rebuild' "$OUT/A.aml" "$WORK/rebuilt/A.aml"

# ------------------------------------------------------------------ a normalized file
note 'Empty project, normalized AML imported'
# If TIA takes renumbered IDs, tia can export AML that only changes when the hardware does.
normalize "$OUT/A.aml" "$OUT/A-normalized.aml"
run 'close the rebuild' 0 project close
run 'project new' 0 project new "$(win "$WORK/c")" "$PNAME"
run 'hw import of the normalized file' 0 hw import "$(win "$OUT/A-normalized.aml")"
run 'stations after the normalized import' 0 devices
if [ "$DEVICES_A" = "$LAST_OUT" ]; then
  check 'the normalized file brings the same stations back' 0 'identical device tables'
else
  check 'the normalized file brings the same stations back' 1 "$(diff <(printf '%s\n' "$DEVICES_A") <(printf '%s\n' "$LAST_OUT"))"
fi
