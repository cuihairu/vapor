#!/usr/bin/env bash
# Serial per-project coverage collection with per-report validation and retry.
#
# The all-at-once `dotnet test Vapor.sln` run intermittently loses coverage
# silently, and serial collection does not immunize against it — the
# 2026-09-22 gate rehearsal corrupted 2 of 10 serial project runs. Both
# failure shapes are invisible to the exit code, so every report is validated
# and the project retried until a valid one lands:
#   - empty report (231 bytes): the datacollector's session-end report write
#     races testhost shutdown; historically "one random project" per
#     all-at-once run.
#   - all-zero report: full module closure listed with every hits="0" — the
#     testhost's hit buffer never reached the collector. Vapor.Agent.Tests
#     passed 102/102 while its report recorded nothing (both probe reruns
#     were healthy, so it is a race, not a deterministic flag effect).
# A report is valid when it parses and shows at least one covered line;
# corrupt ones are deleted so the gate never reads them.
#
# Vapor.E2E.Tests is the one project collected WITHOUT the coverage
# collector: it spawns real ControlPlane/Agent child processes, which inherit
# the profiler environment and corrupt the collector session — its report is
# structurally empty (231 bytes on every attempt, two in a row on
# 2026-09-22), and it contributes no unique lines anyway (its product
# coverage lives in the child processes coverlet cannot see).
#
# Extra arguments are forwarded to every `dotnet test` invocation (CI adds
# the blame-hang and ContinuousIntegrationBuild flags it used to put on the
# all-at-once run).
set -u
cd "$(dirname "$0")/.."
root="TestResults/coverage-serial"
rm -rf "$root"
mkdir -p "$root"

# Echo the valid report paths under $1; delete the corrupt ones. Returns
# success when at least one valid report exists.
validate_reports() {
	local dir=$1 found=0 f ok
	for f in "$dir"/*/coverage.cobertura.xml; do
		[ -f "$f" ] || continue
		ok=0
		python3 - "$f" << 'PY' && ok=1
import sys
import xml.etree.ElementTree as ET

try:
	root = ET.parse(sys.argv[1]).getroot()
	covered = any(
		int(line.get("hits", "0")) > 0
		for cls in root.iter("class")
		for line in cls.iter("line")
		if line.get("branch") != "true"
	)
	sys.exit(0 if covered else 1)
except Exception:
	sys.exit(1)
PY
		if [ "$ok" = 1 ]; then
			echo "$f"
			found=1
		else
			rm -f "$f"
			echo "  dropped corrupt coverage report: $f"
		fi
	done
	[ "$found" = 1 ]
}

fail=0
for proj in tests/*/*.Tests.csproj; do
	name=$(basename "$proj" .csproj)
	attempt=0
	while :; do
		attempt=$((attempt + 1))
		echo "=== $name (attempt $attempt)"
		dir="$root/$name/TestResults"
		log="$root/$name.attempt$attempt.log"
		collect=(--collect "XPlat Code Coverage")
		# See header: spawned children corrupt the collector session, so E2E
		# runs without it — and with no report expected, there is nothing to
		# validate; passing tests are the only criterion.
		validate=1
		[ "$name" = "Vapor.E2E.Tests" ] && { collect=(); validate=0; }
		if ! VAPOR_TEST_REDIS=localhost:6379 dotnet test "$proj" -c Release --no-build --nologo \
			--settings tests/coverlet.runsettings \
			--results-directory "$dir" "${collect[@]}" "$@" >"$log" 2>&1; then
			echo "PROJECT FAILED: $name (see $log)"
			fail=1
			break
		fi
		if [ "$validate" = 0 ] || validate_reports "$dir" > /dev/null; then
			break
		fi
		if [ "$attempt" -ge 3 ]; then
			echo "NO VALID COVERAGE REPORT after $attempt attempts: $name"
			fail=1
			break
		fi
	done
done
exit "$fail"
