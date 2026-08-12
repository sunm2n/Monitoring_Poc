#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────────
# 대용량 검증 — 단계적으로 올려 깨지는 지점을 찾는다.
#
#   ./scripts/volume-test.sh                       기본 단계 (10만 → 100만 → 500만)
#   ./scripts/volume-test.sh 1000000 5000000       단계를 직접 지정
#   RATE=20000 ./scripts/volume-test.sh 1000000    생성 속도 제한 (건/초)
#   DAYS_BACK=7 ./scripts/volume-test.sh 1000000   과거 7일에 분산 (날짜별 인덱스 생성)
#   RESET=1 ./scripts/volume-test.sh               시작 전 app-logs-* 인덱스 삭제
#
# 측정 항목
#   - 유실률       : 앱이 만든 건수 vs 색인된 건수 (run_id 로 대조)
#   - 파이프라인 처리량 : 색인 완료까지 걸린 시간
#   - 문서당 바이트  : 용량 산정의 기준값
#   - 질의 응답시간  : DLS 있음/없음, Loki 라벨 vs 전체 스캔
#   - 힙 사용률     : 어디서 마르는지
#
# 결과는 docs/volume-test.md 에 누적 기록된다.
# ─────────────────────────────────────────────────────────────────────────────
set -u

cd "$(dirname "$0")/.." || exit 1
# shellcheck disable=SC1091
set -a; . ./.env; set +a

OS="https://localhost:9200"
API="http://localhost:8080"
LOKI="http://localhost:3100"
ADMIN="admin:${OPENSEARCH_INITIAL_ADMIN_PASSWORD}"
C1="company1_admin:${COMPANY1_ADMIN_PASSWORD}"

STAGES=("$@")
[ ${#STAGES[@]} -eq 0 ] && STAGES=(100000 1000000 5000000)

RATE="${RATE:-0}"
DAYS_BACK="${DAYS_BACK:-0}"
RESET="${RESET:-0}"
REPORT="docs/volume-test.md"

# 파이프라인이 비워질 때까지 기다리는 최대 시간(초).
# 500만 건이면 색인에 수 분 걸릴 수 있다.
DRAIN_MAX="${DRAIN_MAX:-1800}"

bold() { printf '\n\033[1m%s\033[0m\n' "$1"; }
info() { printf '  %s\n' "$1"; }

osq() {
  if [ $# -ge 3 ]; then curl -sk -u "$1" -H 'Content-Type: application/json' "${OS}$2" -d "$3"
  else curl -sk -u "$1" "${OS}$2"; fi
}

jq_() { python3 -c "import json,sys;d=json.load(sys.stdin);$1" 2>/dev/null; }

# 밀리초 단위로 명령 실행 시간을 재고, 표준출력은 버린다.
timed_ms() {
  local start end
  start=$(python3 -c 'import time;print(time.time())')
  "$@" >/dev/null 2>&1
  end=$(python3 -c 'import time;print(time.time())')
  python3 -c "print(f'{(${end}-${start})*1000:.0f}')"
}

check_prereq() {
  curl -sf -o /dev/null "${API}/healthz" || { echo "api 가 응답하지 않는다"; exit 1; }
  osq "$ADMIN" "/_cluster/health" | grep -q status || { echo "opensearch 가 응답하지 않는다"; exit 1; }

  HEAP_MAX=$(osq "$ADMIN" "/_cat/nodes?h=heap.max" | tr -d ' ')
  info "OpenSearch 힙 최대: ${HEAP_MAX}"
  case "$HEAP_MAX" in
    *gb|*GB) ;;
    *) echo ""
       echo "  ⚠️  힙이 1GB 미만이다 (${HEAP_MAX}). 수백만 건을 넣으면 세그먼트 병합 중"
       echo "     힙이 말라 색인이 느려지거나 죽는다. .env 의 OPENSEARCH_JAVA_OPTS 를"
       echo "     -Xms2g -Xmx2g 로 올리고 opensearch 를 재생성한 뒤 다시 실행할 것."
       echo "";;
  esac

  if curl -sf -o /dev/null "${LOKI}/ready"; then
    LOKI_ON=1; info "Loki: 감지됨 → 양쪽 비교 진행"
  else
    LOKI_ON=0; info "Loki: 없음 → OpenSearch 만 측정"
  fi
}

# 색인 건수가 더 이상 늘지 않을 때까지 기다린다.
# 고정 sleep 으로는 대용량에서 언제 끝났는지 알 수 없다.
drain() {
  local run_id="$1" expected="$2"
  local prev=-1 same=0 elapsed=0 n

  while [ "$elapsed" -lt "$DRAIN_MAX" ]; do
    osq "$ADMIN" "/app-logs-*/_refresh" >/dev/null
    n=$(osq "$ADMIN" "/app-logs-*/_count" "{\"query\":{\"term\":{\"run_id\":\"${run_id}\"}}}" | jq_ "print(d['count'])")
    n=${n:-0}

    [ "$n" -ge "$expected" ] && { echo "$n"; return; }

    if [ "$n" = "$prev" ]; then
      same=$((same+1))
      # 10초간 증가가 없으면 파이프라인이 멈춘 것으로 본다 (= 유실 확정)
      [ "$same" -ge 10 ] && { echo "$n"; return; }
    else
      same=0
    fi

    prev="$n"
    sleep 1
    elapsed=$((elapsed+1))
  done

  echo "${n:-0}"
}

# ─────────────────────────────────────────────────────────────────────────────
bold "대용량 검증 준비"
check_prereq
info "단계: ${STAGES[*]}"
info "생성 속도 제한: $([ "$RATE" = "0" ] && echo '무제한' || echo "${RATE}/초")"
info "날짜 분산: $([ "$DAYS_BACK" = "0" ] && echo '오늘만' || echo "최근 ${DAYS_BACK}일")"

if [ "$RESET" = "1" ]; then
  info "기존 app-logs-* 인덱스를 삭제한다 (문서당 바이트를 깨끗하게 재려면 필요)"
  osq "$ADMIN" "/app-logs-*" >/dev/null
  curl -sk -u "$ADMIN" -X DELETE "${OS}/app-logs-*" >/dev/null
  sleep 2
fi

mkdir -p docs
{
  echo ""
  echo "## 실행: $(date '+%Y-%m-%d %H:%M:%S')"
  echo ""
  echo "힙 최대 \`${HEAP_MAX}\` / 속도제한 \`$([ "$RATE" = "0" ] && echo unlimited || echo "$RATE")\` / 날짜분산 \`${DAYS_BACK}일\`"
  echo ""
  echo "| 단계 | 생성 | 색인 | 유실 | 유실률 | 생성속도 | 총 소요 | 실효 처리량 | 문서당 | 인덱스 총량 | 힙 |"
  echo "|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|"
} >> "$REPORT"

for STAGE in "${STAGES[@]}"; do
  RUN_ID="v$(date +%s)"
  bold "단계: ${STAGE} 건 (run_id=${RUN_ID})"

  # ── 생성 ────────────────────────────────────────────────────────────────
  GEN=$(curl -s -X POST "${API}/api/volume-test?count=${STAGE}&ratePerSec=${RATE}&daysBack=${DAYS_BACK}&runId=${RUN_ID}" \
    -H "X-Company-Id: company1" -H "X-User-Id: volume" --max-time 3600)

  GEN_COUNT=$(printf '%s' "$GEN" | jq_ "print(d['generated'])")
  GEN_SEC=$(printf '%s' "$GEN" | jq_ "print(d['elapsedSec'])")
  GEN_RATE=$(printf '%s' "$GEN" | jq_ "print(d['actualRatePerSec'])")

  if [ -z "${GEN_COUNT:-}" ]; then
    info "생성 실패. 응답: $(printf '%s' "$GEN" | head -c 200)"
    break
  fi
  info "생성: ${GEN_COUNT}건 / ${GEN_SEC}초 (약 ${GEN_RATE}건/초)"

  # ── 색인 대기 ───────────────────────────────────────────────────────────
  info "색인 완료 대기 중..."
  DRAIN_START=$(python3 -c 'import time;print(time.time())')
  INDEXED=$(drain "$RUN_ID" "$GEN_COUNT")
  DRAIN_END=$(python3 -c 'import time;print(time.time())')
  DRAIN_SEC=$(python3 -c "print(f'{${DRAIN_END}-${DRAIN_START}:.1f}')")

  LOST=$((GEN_COUNT - INDEXED))
  LOSS_PCT=$(python3 -c "print(f'{${LOST}/${GEN_COUNT}*100:.2f}')")
  # ⚠️ 처리량은 반드시 (생성시간 + 대기시간) 으로 나눠야 한다.
  #    대기시간만으로 나누면 "생성 중에 이미 색인된 분량"이 분자에 남아 있어
  #    실제보다 2~7배 부풀려진다. (초판 지표가 이 오류를 갖고 있었다)
  TOTAL_SEC=$(python3 -c "print(f'{${GEN_SEC}+${DRAIN_SEC}:.1f}')")
  PIPE_RATE=$(python3 -c "print(int(${INDEXED}/max(${TOTAL_SEC},0.001)))")

  info "색인: ${INDEXED}건 / 유실 ${LOST}건 (${LOSS_PCT}%)"
  info "파이프라인 처리량: 약 ${PIPE_RATE}건/초"

  # ── 용량 · 힙 ───────────────────────────────────────────────────────────
  # ⚠️ flush 를 강제하지 않으면 store.size 가 갱신되지 않아 "문서당 41 bytes" 같은
  #    불가능한 값이 나온다. 세그먼트가 아직 디스크에 안 내려간 상태의 수치이기 때문이다.
  osq "$ADMIN" "/app-logs-*/_flush" >/dev/null
  sleep 3
  osq "$ADMIN" "/app-logs-*/_refresh" >/dev/null
  SIZE_B=$(osq "$ADMIN" "/_cat/indices/app-logs-*?h=store.size&bytes=b" | awk '{s+=$1} END {print s+0}')
  DOCS_ALL=$(osq "$ADMIN" "/_cat/indices/app-logs-*?h=docs.count" | awk '{s+=$1} END {print s+0}')
  PER_DOC=$(python3 -c "print(f'{${SIZE_B}/max(${DOCS_ALL},1):.0f}')")
  SIZE_H=$(python3 -c "print(f'{${SIZE_B}/1e6:.1f} MB')")
  HEAP_PCT=$(osq "$ADMIN" "/_cat/nodes?h=heap.percent" | tr -d ' ')
  IDX_COUNT=$(osq "$ADMIN" "/_cat/indices/app-logs-*?h=index" | wc -l | tr -d ' ')

  info "인덱스 ${IDX_COUNT}개 / 총 ${DOCS_ALL}건 / ${SIZE_H} / 문서당 ${PER_DOC} bytes / 힙 ${HEAP_PCT}%"

  {
    printf '| %s | %s | %s | %s | %s%% | %s/s | %ss | %s/s | %sB | %s | %s%% |\n' \
      "$STAGE" "$GEN_COUNT" "$INDEXED" "$LOST" "$LOSS_PCT" "$GEN_RATE" \
      "$TOTAL_SEC" "$PIPE_RATE" "$PER_DOC" "$SIZE_H" "$HEAP_PCT"
  } >> "$REPORT"
done

# ─────────────────────────────────────────────────────────────────────────────
bold "질의 응답시간 측정 (현재 데이터 규모 기준)"

TOTAL_DOCS=$(osq "$ADMIN" "/_cat/indices/app-logs-*?h=docs.count" | awk '{s+=$1} END {print s+0}')
info "대상 문서 수: ${TOTAL_DOCS}"

Q_TERM='{"query":{"term":{"company_id":"company1"}}}'
Q_AGG='{"size":0,"aggs":{"by_event":{"terms":{"field":"event"},"aggs":{"by_outcome":{"terms":{"field":"outcome"}}}}}}'
Q_TRACE='{"size":1,"query":{"exists":{"field":"error.stack_trace"}}}'

MS_ADMIN=$(timed_ms curl -sk -u "$ADMIN" -H 'Content-Type: application/json' "${OS}/app-logs-*/_count" -d "$Q_TERM")
MS_DLS=$(timed_ms curl -sk -u "$C1" "${OS}/app-logs-*/_count")
MS_AGG=$(timed_ms curl -sk -u "$ADMIN" -H 'Content-Type: application/json' "${OS}/app-logs-*/_search" -d "$Q_AGG")
MS_FLS=$(timed_ms curl -sk -u "$C1" -H 'Content-Type: application/json' "${OS}/app-logs-*/_search" -d "$Q_TRACE")

info "admin  term 카운트          : ${MS_ADMIN} ms"
info "DLS    전체 카운트(필터 적용) : ${MS_DLS} ms"
info "admin  event×outcome 집계    : ${MS_AGG} ms"
info "FLS    스택트레이스 제외 조회 : ${MS_FLS} ms"

{
  echo ""
  echo "**질의 응답시간** (문서 ${TOTAL_DOCS}건 기준)"
  echo ""
  echo "| 질의 | 계정 | 소요 |"
  echo "|---|---|---:|"
  echo "| \`term company_id\` 카운트 | admin | ${MS_ADMIN} ms |"
  echo "| 전체 카운트 (DLS 필터 적용) | company1_admin | ${MS_DLS} ms |"
  echo "| \`event\` × \`outcome\` 집계 | admin | ${MS_AGG} ms |"
  echo "| 스택트레이스 조회 (FLS 적용) | company1_admin | ${MS_FLS} ms |"
} >> "$REPORT"

if [ "${LOKI_ON:-0}" = "1" ]; then
  bold "Loki 측정"
  LOKI_DISK=$(docker exec poc-loki du -sk /loki 2>/dev/null | awk '{printf "%.1f MB", $1/1024}')
  info "Loki 디스크: ${LOKI_DISK}"

  START_NS=$(python3 -c "import time;print(int((time.time()-3600)*1e9))")
  MS_LABEL=$(timed_ms curl -sG "${LOKI}/loki/api/v1/query_range" \
    --data-urlencode 'query=sum(count_over_time({company_id="company1"}[1h]))' \
    --data-urlencode "start=${START_NS}")
  MS_JSON=$(timed_ms curl -sG "${LOKI}/loki/api/v1/query_range" \
    --data-urlencode 'query=sum(count_over_time({company_id="company1"} | json | outcome="failure" [1h]))' \
    --data-urlencode "start=${START_NS}")

  info "라벨만 사용 (색인됨)      : ${MS_LABEL} ms"
  info "| json 파싱 (전체 스캔)   : ${MS_JSON} ms"

  {
    echo ""
    echo "**Loki** — 디스크 ${LOKI_DISK}"
    echo ""
    echo "| 질의 | 소요 |"
    echo "|---|---:|"
    echo "| 라벨만 사용 (색인 대상) | ${MS_LABEL} ms |"
    echo "| \`\| json\` 파싱 (전체 스캔) | ${MS_JSON} ms |"
  } >> "$REPORT"
fi

bold "완료"
info "결과가 ${REPORT} 에 기록되었다"
