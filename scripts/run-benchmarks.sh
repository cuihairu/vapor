#!/usr/bin/env bash
# Runs the performance baseline suites (Release) and prints the measured
# numbers via the test output. Transcribe the printed baselines into
# docs/performance.md — that file is the authoritative record; the assertions
# in the benchmark tests only guard against order-of-magnitude regressions.
#
# Usage:
#   ./scripts/run-benchmarks.sh            # both benchmark projects
#   ./scripts/run-benchmarks.sh -f <filter> # extra --filter argument
set -euo pipefail

export DOTNET_ROLL_FORWARD=Major

FILTER="FullyQualifiedName~Performance"
EXTRA_FILTER=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    -f|--filter)
      EXTRA_FILTER="$2"
      shift 2
      ;;
    -h|--help)
      sed -n '2,9p' "$0" | sed 's/^# \{0,1\}//'
      exit 0
      ;;
    *)
      echo "unknown argument: $1 (use -h for help)" >&2
      exit 2
      ;;
  esac
done

ARGS=(--filter "$FILTER")
if [[ -n "$EXTRA_FILTER" ]]; then
  ARGS+=(--filter "$EXTRA_FILTER")
fi

echo "==> ControlPlane benchmarks"
dotnet test tests/Vapor.ControlPlane.Tests -c Release --nologo \
  --logger "console;verbosity=detailed" "${ARGS[@]}" -- RunConfiguration.MaxCpuCount=2

echo "==> Steam.Core benchmarks"
dotnet test tests/Vapor.Steam.Core.Tests -c Release --nologo \
  --logger "console;verbosity=detailed" "${ARGS[@]}" -- RunConfiguration.MaxCpuCount=2

echo "==> done. Record the printed numbers in docs/performance.md"
