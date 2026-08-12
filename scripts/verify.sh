#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────────
# PoC 주장 1~4 자동 검증
#
#   ./scripts/verify.sh              검증만 수행
#   ./scripts/verify.sh --seed       검증 전에 데모 데이터를 먼저 생성
#   ./scripts/verify.sh --restart    주장 1을 위해 api 컨테이너를 실제로 재생성
#                                    (이 머신에서는 컨테이너 기동에 수 분 걸린다)
# ─────────────────────────────────────────────────────────────────────────────
set -u

cd "$(dirname "$0")/.." || exit 1

# shellcheck disable=SC1091
set -a; . ./.env; set +a

OS="https://localhost:9200"
API="http://localhost:8080"
ADMIN="admin:${OPENSEARCH_INITIAL_ADMIN_PASSWORD}"
C1="company1_admin:${COMPANY1_ADMIN_PASSWORD}"

DO_SEED=0
DO_RESTART=0
for arg in "$@"; do
  case "$arg" in
    --seed) DO_SEED=1 ;;
    --restart) DO_RESTART=1 ;;
  esac
done

PASS=0
FAIL=0

ok()   { printf '  \033[32m[PASS]\033[0m %s\n' "$1"; PASS=$((PASS+1)); }
bad()  { printf '  \033[31m[FAIL]\033[0m %s\n' "$1"; FAIL=$((FAIL+1)); }
note() { printf '         %s\n' "$1"; }
head_() { printf '\n\033[1m%s\033[0m\n' "$1"; }

osq() { # osq <credentials> <path> [body]
  if [ $# -ge 3 ]; then
    curl -sk -u "$1" -H 'Content-Type: application/json' "${OS}$2" -d "$3"
  else
    curl -sk -u "$1" "${OS}$2"
  fi
}

jq_() { python3 -c "import json,sys;d=json.load(sys.stdin);$1" 2>/dev/null; }

# ─────────────────────────────────────────────────────────────────────────────
# 0. 데모 데이터 생성
# ─────────────────────────────────────────────────────────────────────────────
if [ "$DO_SEED" = "1" ]; then
  head_ "0. 데모 데이터 생성"
  for company in company1 company2; do
    curl -s -o /dev/null -X POST "${API}/api/load-test?count=100" \
      -H "X-Company-Id: ${company}" -H "X-User-Id: user-001"

    # FLS 검증용 500 에러를 회사당 3건씩 확보한다 (진짜 스택트레이스가 필요하다)
    for _ in 1 2 3; do
      curl -s -o /dev/null -X POST "${API}/api/products/1/purchase?forceError=true" \
        -H "X-Company-Id: ${company}" -H "X-User-Id: user-001" \
        -H 'Content-Type: application/json' -d '{"quantity":1}'
    done
    note "${company}: 부하 100건 + 강제 500 3건 생성"
  done

  note "Fluent Bit flush 및 색인 대기..."
  sleep 8
  osq "$ADMIN" "/app-logs-*/_refresh" >/dev/null
fi

osq "$ADMIN" "/app-logs-*/_refresh" >/dev/null

# ─────────────────────────────────────────────────────────────────────────────
# 주장 1 — 컨테이너가 죽어도 로그는 남는다
# ─────────────────────────────────────────────────────────────────────────────
head_ "주장 1 — 컨테이너가 죽어도 로그는 남는다"

BEFORE=$(osq "$ADMIN" "/app-logs-*/_count" | jq_ "print(d['count'])")
note "현재 색인된 문서 수: ${BEFORE:-0}"

if [ "$DO_RESTART" = "1" ]; then
  note "api 컨테이너를 완전히 제거하고 재생성한다..."
  docker compose rm -sf api >/dev/null 2>&1
  docker compose up -d api >/dev/null 2>&1
  note "재생성 완료"
fi

STARTED_AT=$(docker inspect -f '{{.State.StartedAt}}' poc-api 2>/dev/null)
OLDEST=$(osq "$ADMIN" "/app-logs-*/_search?size=1&sort=@timestamp:asc&_source=@timestamp" \
  | jq_ "print(d['hits']['hits'][0]['_source']['@timestamp'])")

note "현재 api 컨테이너 기동 시각 : ${STARTED_AT:-?}"
note "가장 오래된 로그 문서 시각   : ${OLDEST:-?}"

# ISO-8601 비교. Docker 는 소수점 9자리를 쓰는데 파이썬 fromisoformat 은 3/6자리만 받는다.
ts_lt() {
  python3 -c "
import sys, re, datetime
def parse(s):
    s = re.sub(r'(\.\d{6})\d+', r'\1', s.strip())
    return datetime.datetime.fromisoformat(s.replace('Z', '+00:00'))
sys.exit(0 if parse('$1') < parse('$2') else 1)" 2>/dev/null
}

if [ -n "${OLDEST:-}" ] && [ -n "${STARTED_AT:-}" ] && ts_lt "$OLDEST" "$STARTED_AT"; then
  ok "현재 컨테이너보다 오래된 로그가 OpenSearch 에 남아 있다"
  note "→ 컨테이너 수명과 로그 수명이 분리되어 있다"
else
  bad "현재 컨테이너보다 오래된 로그를 찾지 못했다 (--restart 로 재생성 후 다시 실행)"
fi

# 대비 실험 — 컨테이너에 붙어 있는 로그는 어디까지 거슬러 올라가는가.
#
# Docker 20.10+ 의 dual logging 덕분에 fluentd 드라이버를 써도 docker logs 는 읽힌다.
# 다만 그 캐시는 "컨테이너에 붙어 있는" 것이라 컨테이너가 사라지면 함께 사라진다.
# 그래서 재생성 이후에는 OpenSearch 쪽 로그가 훨씬 더 과거까지 남아 있게 된다.
DOCKER_OLDEST=$(docker compose logs api --no-log-prefix 2>/dev/null \
  | head -1 | jq_ "print(d['@timestamp'])")
note "docker compose logs 로 볼 수 있는 가장 오래된 로그: ${DOCKER_OLDEST:-(없음)}"

if [ -n "${DOCKER_OLDEST:-}" ] && [ -n "${OLDEST:-}" ] && ts_lt "$OLDEST" "$DOCKER_OLDEST"; then
  ok "OpenSearch 가 컨테이너 로그보다 더 과거까지 보관하고 있다"
  note "→ 컨테이너를 갈아엎어도 그 이전 로그는 OpenSearch 에만 남는다"
else
  note "(api 컨테이너를 아직 재생성하지 않았다면 두 값이 같은 것이 정상이다)"
fi

# ─────────────────────────────────────────────────────────────────────────────
# 주장 2 — 회사별 로그 격리
# ─────────────────────────────────────────────────────────────────────────────
head_ "주장 2 — 회사별 로그 격리가 무료 기능으로 가능하다"

ADMIN_C1=$(osq "$ADMIN" "/app-logs-*/_count" '{"query":{"term":{"company_id":"company1"}}}' | jq_ "print(d['count'])")
ADMIN_C2=$(osq "$ADMIN" "/app-logs-*/_count" '{"query":{"term":{"company_id":"company2"}}}' | jq_ "print(d['count'])")
note "admin 이 보는 문서 — company1: ${ADMIN_C1:-0} / company2: ${ADMIN_C2:-0}"

if [ "${ADMIN_C1:-0}" -gt 0 ] && [ "${ADMIN_C2:-0}" -gt 0 ]; then
  ok "admin 은 두 회사 문서를 모두 본다"
else
  bad "두 회사 데이터가 모두 필요하다 (--seed 로 생성)"
fi

USER_TOTAL=$(osq "$C1" "/app-logs-*/_count" | jq_ "print(d['count'])")
note "company1_admin 이 전체 검색 시 보는 문서 수: ${USER_TOTAL:-0}"

if [ "${USER_TOTAL:-0}" = "${ADMIN_C1:-0}" ]; then
  ok "company1_admin 에게는 company1 문서만 보인다 (${USER_TOTAL} = ${ADMIN_C1})"
else
  bad "격리 실패: company1_admin=${USER_TOTAL:-0}, 기대값=${ADMIN_C1:-0}"
fi

# ★ 회사 관리자가 직접 다른 회사를 지정해서 검색해도 0건이어야 한다
CROSS=$(osq "$C1" "/app-logs-*/_count" '{"query":{"term":{"company_id":"company2"}}}' | jq_ "print(d['count'])")
if [ "${CROSS:-x}" = "0" ]; then
  ok "company1_admin 이 company_id:company2 를 직접 검색해도 0건"
  note "→ 애플리케이션이 아니라 저장소가 막는다. 쿼리를 조작해도 뚫리지 않는다"
else
  bad "교차 조회 차단 실패: ${CROSS:-?}건 조회됨"
fi

# ─────────────────────────────────────────────────────────────────────────────
# 주장 3 — 필드 레벨 보안 (FLS)
# ─────────────────────────────────────────────────────────────────────────────
head_ "주장 3 — 민감 필드를 역할별로 숨길 수 있다"

FLS_QUERY='{"size":1,"query":{"bool":{"filter":[{"term":{"company_id":"company1"}},{"exists":{"field":"error.stack_trace"}}]}}}'

ADMIN_DOC=$(osq "$ADMIN" "/app-logs-*/_search" "$FLS_QUERY")
DOC_ID=$(printf '%s' "$ADMIN_DOC" | jq_ "print(d['hits']['hits'][0]['_id'])")
DOC_IDX=$(printf '%s' "$ADMIN_DOC" | jq_ "print(d['hits']['hits'][0]['_index'])")
ADMIN_HAS=$(printf '%s' "$ADMIN_DOC" | jq_ "print('yes' if 'stack_trace' in d['hits']['hits'][0]['_source'].get('error',{}) else 'no')")

if [ "${ADMIN_HAS:-no}" = "yes" ]; then
  TRACE_LEN=$(printf '%s' "$ADMIN_DOC" | jq_ "print(len(d['hits']['hits'][0]['_source']['error']['stack_trace']))")
  ok "admin 에게는 error.stack_trace 가 보인다 (${TRACE_LEN}자)"
  note "문서: ${DOC_IDX}/${DOC_ID}"
else
  bad "스택트레이스가 있는 company1 문서를 찾지 못했다 (--seed 로 500 에러 생성)"
fi

if [ -n "${DOC_ID:-}" ]; then
  USER_HAS=$(osq "$C1" "/${DOC_IDX}/_doc/${DOC_ID}" \
    | jq_ "print('yes' if 'stack_trace' in d.get('_source',{}).get('error',{}) else 'no')")
  USER_KEYS=$(osq "$C1" "/${DOC_IDX}/_doc/${DOC_ID}" \
    | jq_ "print(','.join(sorted(d.get('_source',{}).get('error',{}).keys())))")

  if [ "${USER_HAS:-yes}" = "no" ]; then
    ok "company1_admin 에게는 같은 문서의 error.stack_trace 필드가 아예 없다"
    note "company1_admin 이 보는 error 하위 필드: ${USER_KEYS:-(없음)}"
  else
    bad "FLS 실패: company1_admin 에게 stack_trace 가 노출된다"
  fi
fi

# ─────────────────────────────────────────────────────────────────────────────
# 주장 4 — 앱은 백엔드를 몰라도 된다
# ─────────────────────────────────────────────────────────────────────────────
head_ "주장 4 — 앱은 로그 백엔드를 몰라도 된다"

# 주석에도 'OpenSearch' 라는 단어가 나오므로 PackageReference 줄만 본다.
if grep -E '<PackageReference' api/PocApi.csproj | grep -iE "opensearch|elasticsearch|nest" >/dev/null 2>&1; then
  bad "csproj 에 검색엔진 관련 패키지 참조가 있다"
else
  ok "PocApi.csproj 에 OpenSearch / Elasticsearch 패키지 참조가 없다"
  note "참조 패키지: $(grep -oE 'Include="[^"]+"' api/PocApi.csproj | sed 's/Include=//;s/"//g' | tr '\n' ' ')"
fi

if grep -rn "^using .*\(OpenSearch\|Elasticsearch\|Nest\)" api --include='*.cs' >/dev/null 2>&1; then
  bad "소스에 검색엔진 클라이언트 using 이 있다"
else
  ok "소스 어디에도 검색엔진 클라이언트 using 이 없다"
fi

# 가장 강한 증거: 빌드 산출물에 해당 어셈블리가 아예 없다
ASM=$(docker exec poc-api sh -c 'ls /app | grep -iE "opensearch|elastic|nest"' 2>/dev/null)
if [ -z "$ASM" ]; then
  ok "빌드 산출물(/app)에 OpenSearch 관련 어셈블리가 하나도 없다"
  note "→ 백엔드를 Loki 등으로 바꿔도 이 프로젝트는 한 글자도 안 바뀐다"
else
  bad "빌드 산출물에 관련 어셈블리가 있다: ${ASM}"
fi

note "참고: 소스의 'OpenSearch' 문자열은 전부 주석(설계 의도 기록)이다."
note "      실제 참조 여부는 위의 패키지 / using / 어셈블리 3가지로 판정한다."

# ─────────────────────────────────────────────────────────────────────────────
head_ "결과"
printf '  통과 %d / 실패 %d\n\n' "$PASS" "$FAIL"
[ "$FAIL" -eq 0 ]
