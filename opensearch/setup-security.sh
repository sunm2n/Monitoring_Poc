#!/bin/sh
# =============================================================================
# setup-security.sh
# -----------------------------------------------------------------------------
# OpenSearch 3.1 (Security 플러그인 ON, 단일 노드) 초기 구성 스크립트.
# 테넌트(회사) 별 DLS(문서 레벨 보안) / FLS(필드 레벨 보안) 격리를 무료 기능만으로
# 구현한다. company1_admin 계정은 company_id: company1 문서만 보이고,
# error.stack_trace 필드는 아예 보이지 않는다.
#
# 멱등(idempotent) 스크립트다. 컨테이너를 몇 번을 갈아엎어도 그냥 다시 실행하면 된다.
#   - PUT 기반 API(테넌트/역할/사용자/역할매핑/인덱스템플릿)는 OpenSearch 쪽에서
#     이미 upsert 의미론이라 재실행해도 안전하다.
#   - ISM 정책만 예외: 이미 존재하는 정책에 seq_no/primary_term 없이 PUT하면 409가
#     나므로, 존재 여부를 먼저 GET으로 확인하고 없을 때만 생성한다.
#
# 실행 환경 주의: 이 스크립트는 curlimages/curl 컨테이너(bash 없음, sh/busybox만
# 있음) 또는 macOS 호스트에서 실행될 수 있다. 그래서 POSIX sh만 쓴다.
#   - [[ ]], 배열, local, pipefail 전부 금지. [ ], 공백으로 나눈 word list, 전역
#     변수만 사용한다.
#   - set -eu 만 쓴다 (pipefail은 POSIX sh에 없다). curl 자체는 4xx/5xx에도 셸
#     종료코드를 0으로 반환하므로, 실패 판정은 -w '%{http_code}'로 받은 HTTP 상태
#     코드를 직접 비교해서 수행한다 (아래 call_api 참고).
# =============================================================================
set -eu

# -----------------------------------------------------------------------------
# 환경변수 / 기본값
# -----------------------------------------------------------------------------
OPENSEARCH_HOST="${OPENSEARCH_HOST:-https://opensearch:9200}"

: "${OPENSEARCH_INITIAL_ADMIN_PASSWORD:?OPENSEARCH_INITIAL_ADMIN_PASSWORD 환경변수가 필요합니다 (admin 비밀번호)}"
: "${COMPANY1_ADMIN_PASSWORD:?COMPANY1_ADMIN_PASSWORD 환경변수가 필요합니다}"
: "${COMPANY2_ADMIN_PASSWORD:?COMPANY2_ADMIN_PASSWORD 환경변수가 필요합니다}"

# 스크립트 파일이 있는 디렉터리를 기준으로 JSON 설정 파일을 찾는다.
# CONFIG_DIR 환경변수로 덮어쓸 수 있다 (예: 컨테이너 내부에서 마운트 경로가 다를 때).
SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
CONFIG_DIR="${CONFIG_DIR:-$SCRIPT_DIR}"

INDEX_TEMPLATE_FILE="${CONFIG_DIR}/01-index-template.json"
ISM_POLICY_FILE="${CONFIG_DIR}/02-ism-policy.json"

ADMIN_AUTH="admin:${OPENSEARCH_INITIAL_ADMIN_PASSWORD}"

# -----------------------------------------------------------------------------
# 공용 함수
# -----------------------------------------------------------------------------

# json_escape STR
# 문자열 안의 백슬래시(\)와 큰따옴표(")를 JSON 문자열 값 안에 안전하게 넣을 수
# 있도록 이스케이프한다. 비밀번호 등 외부(환경변수) 입력값을 JSON 바디에 꽂을 때
# 반드시 통과시킨다 (안 그러면 비밀번호에 " 나 \ 가 섞였을 때 JSON이 깨진다).
json_escape() {
  printf '%s' "$1" | sed 's/\\/\\\\/g; s/"/\\"/g'
}

# call_api METHOD PATH [BODY]
# curl로 OpenSearch REST API를 호출하고 HTTP 상태코드를 확인한다.
# 2xx가 아니면 응답 본문과 함께 에러 메시지를 찍고 스크립트를 종료(exit 1)한다.
#
# curl의 -w 포맷 문자열 안 "\n"은 셸이 아니라 curl 자신이 해석하는 이스케이프다
# (curl 매뉴얼: HTTP 컨텍스트에서 \n, \r, \t 지원). 그래서 작은따옴표로 감싼
# '\nHTTPSTATUS:%{http_code}' 를 그대로 넘기면, 셸의 개행 관련 함정
# ($(printf '\n')가 트레일링 개행이 잘려서 빈 문자열이 되는 문제 등) 없이
# 본문 마지막 줄에 상태코드를 안전하게 붙일 수 있다.
call_api() {
  method="$1"
  path="$2"
  body="${3:-}"

  if [ -n "$body" ]; then
    resp=$(curl -sk -u "$ADMIN_AUTH" -H 'Content-Type: application/json' \
      -X "$method" "${OPENSEARCH_HOST}/${path}" \
      -d "$body" \
      -w '\nHTTPSTATUS:%{http_code}')
  else
    resp=$(curl -sk -u "$ADMIN_AUTH" -H 'Content-Type: application/json' \
      -X "$method" "${OPENSEARCH_HOST}/${path}" \
      -w '\nHTTPSTATUS:%{http_code}')
  fi

  HTTP_STATUS=$(printf '%s' "$resp" | tail -n 1 | sed -n 's/^HTTPSTATUS://p')
  RESP_BODY=$(printf '%s' "$resp" | sed '$d')

  case "$HTTP_STATUS" in
    2??)
      return 0
      ;;
    *)
      echo "오류: $method $path 실패 (HTTP ${HTTP_STATUS:-000})" >&2
      echo "응답 본문: $RESP_BODY" >&2
      exit 1
      ;;
  esac
}

# call_api_file METHOD PATH FILE
# call_api와 동일하지만 바디를 JSON 파일에서 읽는다 (--data-binary @file, 원본
# 포맷 그대로 전송).
call_api_file() {
  method="$1"
  path="$2"
  file="$3"

  if [ ! -f "$file" ]; then
    echo "오류: 설정 파일을 찾을 수 없습니다: $file" >&2
    exit 1
  fi

  resp=$(curl -sk -u "$ADMIN_AUTH" -H 'Content-Type: application/json' \
    -X "$method" "${OPENSEARCH_HOST}/${path}" \
    --data-binary "@${file}" \
    -w '\nHTTPSTATUS:%{http_code}')

  HTTP_STATUS=$(printf '%s' "$resp" | tail -n 1 | sed -n 's/^HTTPSTATUS://p')
  RESP_BODY=$(printf '%s' "$resp" | sed '$d')

  case "$HTTP_STATUS" in
    2??)
      return 0
      ;;
    *)
      echo "오류: $method $path 실패 (HTTP ${HTTP_STATUS:-000})" >&2
      echo "응답 본문: $RESP_BODY" >&2
      exit 1
      ;;
  esac
}

# resource_exists PATH
# GET이 200이면 존재, 그 외(주로 404)는 존재하지 않음으로 취급한다.
# call_api와 달리 실패해도 exit하지 않는다 (존재 확인 자체가 목적).
resource_exists() {
  path="$1"
  status=$(curl -sk -o /dev/null -w '%{http_code}' -u "$ADMIN_AUTH" \
    -H 'Content-Type: application/json' -X GET "${OPENSEARCH_HOST}/${path}")
  [ "$status" = "200" ]
}

# -----------------------------------------------------------------------------
# [1/9] OpenSearch가 응답할 때까지 대기 (최대 5분)
# -----------------------------------------------------------------------------
echo "==> [1/9] OpenSearch 클러스터 응답 대기 중 (${OPENSEARCH_HOST})"

MAX_ATTEMPTS=60
SLEEP_SECONDS=5
attempt=0
ready=0

while [ "$attempt" -lt "$MAX_ATTEMPTS" ]; do
  status=$(curl -sk -o /dev/null -w '%{http_code}' -u "$ADMIN_AUTH" \
    "${OPENSEARCH_HOST}/_cluster/health" 2>/dev/null || echo "000")
  if [ "$status" = "200" ]; then
    ready=1
    break
  fi
  attempt=$((attempt + 1))
  echo "    대기 중... (${attempt}/${MAX_ATTEMPTS}, 마지막 HTTP 상태: ${status})"
  sleep "$SLEEP_SECONDS"
done

if [ "$ready" -ne 1 ]; then
  echo "오류: ${MAX_ATTEMPTS}회(약 $((MAX_ATTEMPTS * SLEEP_SECONDS / 60))분) 시도 후에도 OpenSearch가 응답하지 않습니다." >&2
  echo "       OPENSEARCH_HOST(${OPENSEARCH_HOST})와 admin 비밀번호를 확인하세요." >&2
  exit 1
fi
echo "    OpenSearch 응답 확인 완료."

# -----------------------------------------------------------------------------
# [2/9] 인덱스 템플릿 등록 — Fluent Bit이 첫 문서를 보내기 전에 반드시 먼저 등록
# -----------------------------------------------------------------------------
echo "==> [2/9] 인덱스 템플릿 등록 (app-logs-template)"
call_api_file PUT "_index_template/app-logs-template" "$INDEX_TEMPLATE_FILE"
echo "    등록 완료: $INDEX_TEMPLATE_FILE"

# -----------------------------------------------------------------------------
# [3/9] ISM 정책 등록 — 이미 있으면 건너뜀 (멱등성)
# -----------------------------------------------------------------------------
echo "==> [3/9] ISM 정책 등록 (app-logs-policy)"
if resource_exists "_plugins/_ism/policies/app-logs-policy"; then
  echo "    이미 존재함 - 건너뜀 (기존 정책을 그대로 유지)"
else
  call_api_file PUT "_plugins/_ism/policies/app-logs-policy" "$ISM_POLICY_FILE"
  echo "    등록 완료: $ISM_POLICY_FILE"
fi

# -----------------------------------------------------------------------------
# [4/9] 테넌트 생성
# -----------------------------------------------------------------------------
echo "==> [4/9] 테넌트 생성 (company1_tenant, company2_tenant)"
call_api PUT "_plugins/_security/api/tenants/company1_tenant" \
  '{"description": "company1 전용 Dashboards 테넌트"}'
call_api PUT "_plugins/_security/api/tenants/company2_tenant" \
  '{"description": "company2 전용 Dashboards 테넌트"}'
echo "    완료"

# -----------------------------------------------------------------------------
# [5/9] 역할 생성 (DLS + FLS)
# -----------------------------------------------------------------------------
echo "==> [5/9] 역할 생성 (company1_reader, company2_reader)"

# create_reader_role COMPANY_ID ROLE_NAME TENANT_NAME
#
# dls 필드는 "JSON 객체"가 아니라 "JSON 객체를 담은 문자열"이다. 즉 역할 문서
# 자체의 dls 값 타입은 string이고, 그 문자열 안에 term 쿼리 JSON이 이스케이프된
# 채로 들어간다:
#   "dls": "{\"term\": {\"company_id\": \"company1\"}}"
#
# 이걸 sh에서 안전하게 만들기 위해 heredoc(<<EOF, 따옴표 없는 델리미터)을 쓴다.
# 따옴표 없는(unquoted) heredoc은 변수 치환($company_id 등)은 수행하지만,
# 백슬래시는 오직 $, `, \, 그리고 줄바꿈 앞에서만 특별한 의미를 가진다.
# 즉 \" (백슬래시+큰따옴표)는 그 두 글자 그대로 보존된다 — 셸이 임의로 벗겨내지
# 않는다. 그래서 아래처럼 \" 를 그냥 리터럴로 타이핑하면 원하는 이스케이프된
# JSON 문자열이 그대로 만들어진다. (반대로 작은따옴표 heredoc(<<'EOF')을 쓰면
# 변수 치환이 안 되어 company_id를 파라미터화할 수 없다 — 그래서 회사별로
# 함수를 하나 두고 unquoted heredoc을 쓰는 절충안을 택했다.)
create_reader_role() {
  company_id="$1"
  role_name="$2"
  tenant_name="$3"

  body=$(cat <<EOF
{
  "cluster_permissions": ["cluster_composite_ops_ro"],
  "index_permissions": [
    {
      "index_patterns": ["app-logs-*"],
      "dls": "{\"term\": {\"company_id\": \"${company_id}\"}}",
      "fls": ["~error.stack_trace"],
      "allowed_actions": [
        "read",
        "indices:admin/mappings/get",
        "indices:monitor/settings/get"
      ]
    }
  ],
  "tenant_permissions": [
    {
      "tenant_patterns": ["${tenant_name}"],
      "allowed_actions": ["kibana_all_write"]
    }
  ]
}
EOF
)
  call_api PUT "_plugins/_security/api/roles/${role_name}" "$body"
}

create_reader_role "company1" "company1_reader" "company1_tenant"
create_reader_role "company2" "company2_reader" "company2_tenant"
echo "    완료 (DLS: company_id term 필터 / FLS: error.stack_trace 제외)"

# -----------------------------------------------------------------------------
# [6/9] 사용자 생성
# -----------------------------------------------------------------------------
echo "==> [6/9] 사용자 생성 (company1_admin, company2_admin)"

# 비밀번호는 환경변수(외부 입력)이므로 JSON에 넣기 전에 반드시 json_escape를
# 거친다 — 비밀번호에 " 나 \ 가 섞여도 JSON이 깨지지 않도록.
c1_pw_escaped=$(json_escape "$COMPANY1_ADMIN_PASSWORD")
c2_pw_escaped=$(json_escape "$COMPANY2_ADMIN_PASSWORD")

call_api PUT "_plugins/_security/api/internalusers/company1_admin" \
  "{\"password\": \"${c1_pw_escaped}\"}"
call_api PUT "_plugins/_security/api/internalusers/company2_admin" \
  "{\"password\": \"${c2_pw_escaped}\"}"
echo "    완료"

# -----------------------------------------------------------------------------
# [7/9] 역할 매핑
# -----------------------------------------------------------------------------
echo "==> [7/9] 역할 매핑 (company1_reader<-company1_admin, company2_reader<-company2_admin)"
call_api PUT "_plugins/_security/api/rolesmapping/company1_reader" \
  '{"users": ["company1_admin"]}'
call_api PUT "_plugins/_security/api/rolesmapping/company2_reader" \
  '{"users": ["company2_admin"]}'
echo "    완료"

# -----------------------------------------------------------------------------
# [8/9] kibana_user 내장 역할 매핑 — 누락하면 로그인은 되는데 화면이 빈다.
# PUT rolesmapping/kibana_user는 기존 매핑을 통째로 덮어쓰므로 두 계정을 한 번에
# 같이 넣는다 (따로따로 PUT하면 나중 것이 앞의 것을 지워버린다).
# -----------------------------------------------------------------------------
echo "==> [8/9] kibana_user 내장 역할에 두 계정 매핑"
call_api PUT "_plugins/_security/api/rolesmapping/kibana_user" \
  '{"users": ["company1_admin", "company2_admin"]}'
echo "    완료"

# -----------------------------------------------------------------------------
# [9/9] 검증 출력
# -----------------------------------------------------------------------------
echo "==> [9/9] 검증"
echo ""
echo "----------------------------------------------------------------------"
echo "생성된 리소스 확인"
echo "----------------------------------------------------------------------"

for role in company1_reader company2_reader; do
  if resource_exists "_plugins/_security/api/roles/${role}"; then
    echo "  [OK] 역할: ${role}"
  else
    echo "  [!!] 역할 확인 실패: ${role}"
  fi
done

for user in company1_admin company2_admin; do
  if resource_exists "_plugins/_security/api/internalusers/${user}"; then
    echo "  [OK] 사용자: ${user}"
  else
    echo "  [!!] 사용자 확인 실패: ${user}"
  fi
done

for tenant in company1_tenant company2_tenant; do
  if resource_exists "_plugins/_security/api/tenants/${tenant}"; then
    echo "  [OK] 테넌트: ${tenant}"
  else
    echo "  [!!] 테넌트 확인 실패: ${tenant}"
  fi
done

for mapping in company1_reader company2_reader kibana_user; do
  if resource_exists "_plugins/_security/api/rolesmapping/${mapping}"; then
    echo "  [OK] 역할 매핑: ${mapping}"
  else
    echo "  [!!] 역할 매핑 확인 실패: ${mapping}"
  fi
done

if resource_exists "_plugins/_ism/policies/app-logs-policy"; then
  echo "  [OK] ISM 정책: app-logs-policy"
else
  echo "  [!!] ISM 정책 확인 실패: app-logs-policy"
fi

echo ""
echo "----------------------------------------------------------------------"
echo "요약"
echo "----------------------------------------------------------------------"
echo "  company1_admin -> company1_reader -> company_id=company1 문서만 조회,"
echo "                    error.stack_trace 필드 숨김, company1_tenant 쓰기 가능"
echo "  company2_admin -> company2_reader -> company_id=company2 문서만 조회,"
echo "                    error.stack_trace 필드 숨김, company2_tenant 쓰기 가능"
echo "  두 계정 모두 kibana_user 매핑됨 (Dashboards 로그인 및 기본 화면 접근용)"
echo ""
echo "==> setup-security.sh 완료"
