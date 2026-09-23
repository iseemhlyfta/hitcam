#!/usr/bin/env bash
# Runs xcodebuild with the given arguments, keeps the full log, and turns compiler errors and
# failed tests into GitHub annotations (readable without downloading logs).
# Usage: xcodebuild.sh <log-file> <xcodebuild args...>
set -uo pipefail
log="$1"; shift

xcodebuild "$@" > "$log" 2>&1
status=$?

grep -E "Executed [0-9]+ test|\*\* (TEST|BUILD) (SUCCEEDED|FAILED)" "$log" | tail -n 5 || true

if [ "$status" -ne 0 ]; then
  root="${GITHUB_WORKSPACE:-$PWD}/"
  # "/path/File.swift:12:5: error: message"
  grep -E "^/.+:[0-9]+:[0-9]+: error: " "$log" | sort -u | head -n 40 | while IFS= read -r line; do
    file="${line%%:*}"; rest="${line#*:}"
    ln="${rest%%:*}"; rest="${rest#*:}"
    col="${rest%%:*}"; msg="${rest#*: error: }"
    echo "::error file=${file#$root},line=$ln,col=$col::$msg"
  done
  # Test failures: "/path/Tests.swift:10: error: -[Suite test] : XCTAssert... failed"
  grep -E "^/.+:[0-9]+: error: " "$log" | sort -u | head -n 20 | while IFS= read -r line; do
    echo "::error::${line#$root}"
  done
  # Anything else (linker, signing, missing SDK).
  grep -E "^(error|ld|clang): |error: " "$log" | grep -vE "^/" | sort -u | head -n 10 | while IFS= read -r line; do
    echo "::error::$line"
  done
  tail -n 30 "$log"
fi
exit "$status"
