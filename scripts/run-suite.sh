#!/usr/bin/env bash
#
# Runs the whole measurement suite and collects it into one report.
#
#   ./scripts/run-suite.sh [quick|standard|full]
#
# quick     ~6 min   smoke test: is the harness working, are the numbers plausible
# standard  ~35 min  the defaults; enough samples and repeats to be quotable
# full      ~90 min  more repeats, longer soak, higher ramp
#
# Everything lands in LoadTestReports/<profile>-<timestamp>/, including NBomber's own
# reports, a JSON summary per run, and REPORT.md.

set -uo pipefail

cd "$(dirname "$0")/.."

PROFILE="${1:-standard}"
STAMP="$(date +%Y%m%d-%H%M%S)"
OUT="LoadTestReports/${PROFILE}-${STAMP}"
LOGS="${OUT}/logs"

case "$PROFILE" in
  quick)
    SESSIONS=10000;  WORKING=1000; DURATION=15;  WARMUP=4; REPEAT=2
    RPS=500;         LOGIN_RPS=10; LOGOUT_RPS=2
    LOGOUT_RPS_ISO=100
    RAMP_FROM=250;   RAMP_TO=3000;  RAMP_STEPS=5; RAMP_SECS=10
    SOAK_LIFETIME=1; SOAK_DURATION=150; SOAK_SESSIONS=10000
    ;;
  standard)
    SESSIONS=100000; WORKING=5000; DURATION=30;  WARMUP=8; REPEAT=3
    RPS=1000;        LOGIN_RPS=10; LOGOUT_RPS=1
    LOGOUT_RPS_ISO=200
    RAMP_FROM=500;   RAMP_TO=8000;  RAMP_STEPS=6; RAMP_SECS=15
    SOAK_LIFETIME=1; SOAK_DURATION=300; SOAK_SESSIONS=20000
    ;;
  full)
    SESSIONS=250000; WORKING=10000; DURATION=60; WARMUP=10; REPEAT=5
    RPS=1000;        LOGIN_RPS=10;  LOGOUT_RPS=1
    LOGOUT_RPS_ISO=300
    RAMP_FROM=500;   RAMP_TO=16000; RAMP_STEPS=8; RAMP_SECS=20
    SOAK_LIFETIME=2; SOAK_DURATION=600; SOAK_SESSIONS=50000
    ;;
  *)
    echo "Unknown profile '$PROFILE'. Use quick, standard or full." >&2
    exit 1
    ;;
esac

# Injected Redis latency, so the round trip costs what a managed instance one availability
# zone away costs instead of what loopback costs.
LATENCY=1
JITTER=1

mkdir -p "$LOGS"

STEP=0
# Two BenchmarkDotNet runs, then per strategy: mix, request, login, logout, ramp, failure,
# soak. Two strategies.
TOTAL=16

bar() {
  local done=$1 total=$2 width=36
  local filled=$(( done * width / total ))
  printf '\r  [%s%s] %d/%d  %s' \
    "$(printf '#%.0s' $(seq 1 $filled 2>/dev/null))" \
    "$(printf '.%.0s' $(seq 1 $(( width - filled )) 2>/dev/null))" \
    "$done" "$total" "${3:-}"
}

step() {
  STEP=$(( STEP + 1 ))
  echo
  echo "=============================================================================="
  printf '[%2d/%2d] %s\n' "$STEP" "$TOTAL" "$1"
  echo "=============================================================================="
  bar "$(( STEP - 1 ))" "$TOTAL" "$1"
  echo
}

run_load() {
  # run_load <log-name> <args...>
  local name=$1; shift

  # Tee so the run is watchable live and still captured for the report. NBomber draws its
  # own live progress bar; --output writes the JSON the report is built from.
  if ! dotnet run -c Release --project API.LoadTests -- "$@" \
      --output "${OUT}/${name}.json" 2>&1 | tee "${LOGS}/${name}.log"; then
    echo "  !! ${name} failed - see ${LOGS}/${name}.log" >&2
  fi
}

echo "Profile: $PROFILE"
echo "Output:  $OUT"
echo

# ---------------------------------------------------------------------------------------
# Dependencies
# ---------------------------------------------------------------------------------------
echo "Bringing up Redis and Toxiproxy..."
docker compose up -d --wait >/dev/null || { echo "docker compose failed" >&2; exit 1; }
docker compose exec -T redis redis-cli flushall >/dev/null

echo "Building..."
dotnet build -c Release >/dev/null || { echo "build failed" >&2; exit 1; }

# ---------------------------------------------------------------------------------------
# BenchmarkDotNet: per-operation cost, isolated, no HTTP
# ---------------------------------------------------------------------------------------
# Cleared, or results from an earlier run get copied into this report as if they belonged to it.
rm -rf BenchmarkDotNet.Artifacts

step "BenchmarkDotNet - one operation at a time"
dotnet run -c Release --project API.Benchmarks -- \
  --filter '*TokenRevocationBenchmarks*' 2>&1 | tee "${LOGS}/bdn-single.log"

step "BenchmarkDotNet - 100 operations in flight"
dotnet run -c Release --project API.Benchmarks -- \
  --filter '*ConcurrentTokenRevocation*' 2>&1 | tee "${LOGS}/bdn-concurrent.log"

if [ -d BenchmarkDotNet.Artifacts/results ]; then
  mkdir -p "${OUT}/benchmarkdotnet"
  cp BenchmarkDotNet.Artifacts/results/* "${OUT}/benchmarkdotnet/" 2>/dev/null
fi

# ---------------------------------------------------------------------------------------
# Load tests, per strategy
# ---------------------------------------------------------------------------------------
for STRATEGY in denylist allowlist; do

  COMMON="--strategy $STRATEGY --sessions $SESSIONS --working-set $WORKING
          --warmup $WARMUP --redis-latency $LATENCY --redis-jitter $JITTER"

  step "$STRATEGY - production mix ($RPS:$LOGIN_RPS:$LOGOUT_RPS), x$REPEAT"
  run_load "${STRATEGY}-mix" --mode mix $COMMON \
    --rps "$RPS" --login-rps "$LOGIN_RPS" --logout-rps "$LOGOUT_RPS" \
    --duration "$DURATION" --repeat "$REPEAT"

  step "$STRATEGY - authenticated requests only, x$REPEAT (percentile-grade)"
  run_load "${STRATEGY}-request" --mode request $COMMON \
    --rps "$RPS" --duration "$DURATION" --repeat "$REPEAT"

  step "$STRATEGY - logins only, x$REPEAT (percentile-grade)"
  run_load "${STRATEGY}-login" --mode login $COMMON \
    --rps "$RPS" --duration "$DURATION" --repeat "$REPEAT"

  step "$STRATEGY - logouts only at $LOGOUT_RPS_ISO/s, x$REPEAT (percentile-grade)"
  run_load "${STRATEGY}-logout" --mode logout $COMMON \
    --rps "$LOGOUT_RPS_ISO" --duration "$DURATION" --repeat "$REPEAT"

  step "$STRATEGY - saturation ramp $RAMP_FROM to $RAMP_TO req/s"
  run_load "${STRATEGY}-ramp" --mode ramp $COMMON \
    --ramp-from "$RAMP_FROM" --ramp-to "$RAMP_TO" \
    --ramp-steps "$RAMP_STEPS" --ramp-step-seconds "$RAMP_SECS"

  step "$STRATEGY - failure modes (unreachable, stalled, out of memory)"
  run_load "${STRATEGY}-failure" --mode failure $COMMON \
    --rps "$RPS" --ramp-step-seconds "$RAMP_SECS"

  step "$STRATEGY - soak, ${SOAK_DURATION}s over ${SOAK_LIFETIME}-minute tokens"
  run_load "${STRATEGY}-soak" --mode mix --strategy "$STRATEGY" \
    --sessions "$SOAK_SESSIONS" --working-set "$WORKING" \
    --token-lifetime "$SOAK_LIFETIME" --duration "$SOAK_DURATION" \
    --warmup "$WARMUP" --sample-interval 15 \
    --rps $(( RPS / 4 )) --login-rps "$LOGIN_RPS" --logout-rps 5 \
    --redis-latency "$LATENCY" --redis-jitter "$JITTER"

done

bar "$TOTAL" "$TOTAL" "done"
echo

# ---------------------------------------------------------------------------------------
# Report
# ---------------------------------------------------------------------------------------
echo
echo "Assembling report..."
python3 scripts/build-report.py "$OUT" > "${OUT}/REPORT.md" \
  && echo "  ${OUT}/REPORT.md" \
  || echo "  !! report generation failed; raw JSON and logs are in ${OUT}" >&2

echo
echo "Done. Everything is under ${OUT}/"
