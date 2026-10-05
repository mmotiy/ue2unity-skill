#!/usr/bin/env bash
# Watches the remote transfer of "American City Packs Bundle ... .rar" on F:.
# The transfer tool writes to <name>.rar.### and should rename to <name>.rar on finish.
#
# Exit codes:
#   0 = transfer complete and archive verifies
#   2 = stalled: size frozen but archive integrity failed 3x in a row
#   3 = 24h watchdog timeout
#   4 = file vanished without appearing under its final name
#   5 = renamed to final name but archive does not verify

PRIMARY="/f/American City Packs Bundle 4.25 - 4.27, 5.0.rar.###"
FINAL="/f/American City Packs Bundle 4.25 - 4.27, 5.0.rar"
TAR=/c/Windows/System32/tar.exe
LOG="C:/Users/mmotiy/.zcode/workspace/default/citypack-work/monitor.log"

say() { echo "$(date '+%Y-%m-%d %H:%M:%S') $*" >> "$LOG"; }

# A RAR only counts as complete if it verifies. Prefer 7z (detects truncation,
# verifies every file CRC); libarchive tar cannot tell a truncated RAR from a
# finished one, so it is only a last-resort sanity pass.
SEVENZ="/c/Program Files/7-Zip/7z.exe"
verify() {
  local err=/tmp/rarverify.err
  if [ -f "$SEVENZ" ]; then
    if "$SEVENZ" t -y "$1" > "$err" 2>&1; then
      return 0
    fi
    say "verify(7z): $(tail -c 300 "$err" | tr '\n' ' ')"
    return 1
  fi
  "$TAR" -tf "$1" > /dev/null 2> "$err"
  local rc=$?
  if [ $rc -eq 0 ] && ! grep -qiE 'truncat|unexpected|damaged|crc|error' "$err"; then
    return 0
  fi
  say "verify(tar): rc=$rc err=$(head -c 300 "$err" | tr '\n' ' ')"
  return 1
}

say "=== monitor start ==="
say "primary=$PRIMARY"
say "final=$FINAL"

last=""; stable=0; failed=0; gone=0; ticks=0
while :; do
  ticks=$((ticks+1))

  if [ -f "$FINAL" ]; then
    say "final archive present -> verifying"
    if verify "$FINAL"; then
      say "RESULT=COMPLETE_RENAMED"
      exit 0
    fi
    say "RESULT=RENAMED_BUT_VERIFY_FAILED"
    exit 5
  fi

  if [ -f "$PRIMARY" ]; then
    gone=0
    key="$(stat -c '%s|%Y' "$PRIMARY" 2>/dev/null)"
    say "poll $key"
    if [ -n "$key" ] && [ "$key" = "$last" ]; then stable=$((stable+1)); else stable=0; fi
    last="$key"
    if [ $stable -ge 10 ]; then
      say "frozen ~10 min -> verifying archive (attempt $((failed+1)))"
      if verify "$PRIMARY"; then
        say "RESULT=COMPLETE_STABLE"
        exit 0
      fi
      failed=$((failed+1))
      if [ $failed -ge 3 ]; then
        say "RESULT=STALLED_3_FAILED_TESTS"
        exit 2
      fi
    fi
  else
    gone=$((gone+1))
    say "poll primary-missing ($gone)"
    if [ $gone -ge 3 ]; then
      say "RESULT=FILE_VANISHED"
      exit 4
    fi
  fi

  if [ $ticks -ge 1440 ]; then
    say "RESULT=WATCHDOG_24H"
    exit 3
  fi

  sleep 60
done
