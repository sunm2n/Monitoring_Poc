# OpenSearch 로그 수집 PoC

클라우드 네이티브 ERP의 로그 수집·저장·조회 파이프라인이 실제로 어떻게 동작하는지,
특히 **회사(테넌트)별 권한 분리가 OpenSearch에서 어떻게 구현되는지**를 로컬 Docker 환경에서 확인한다.

> **범위 밖**: 디자인, 인증/인가 구현, 성능 튜닝, HA 구성, 프로덕션 보안.

---

## 이 PoC가 증명하려는 것

| # | 주장 | 검증 방법 | 결과 |
|---|---|---|:--:|
| 1 | 컨테이너가 죽어도 로그는 남는다 | api 컨테이너 재생성 후에도 이전 로그가 Dashboards에 조회됨 | [검증 기록](docs/verification.md) |
| 2 | 회사별 로그 격리가 **무료로** 가능하다 | `company1_admin`으로 company2 로그를 검색해도 0건 | [검증 기록](docs/verification.md) |
| 3 | 민감 필드를 역할별로 숨길 수 있다 (FLS) | 회사 관리자에게는 스택트레이스 필드가 아예 없음 | [검증 기록](docs/verification.md) |
| 4 | 앱은 백엔드를 몰라도 된다 | API 코드에 OpenSearch 관련 코드가 단 한 줄도 없음 | [검증 기록](docs/verification.md) |

> **4번이 가장 중요하다.** API 프로젝트에 OpenSearch SDK 의존성이 없다는 것 자체가,
> 나중에 Loki나 다른 백엔드로 교체 가능하다는 증거다.

---

## 아키텍처

```
┌──────────────┐
│  브라우저     │  index.html (버튼 나열, 디자인 X)
│              │  회사 선택 드롭다운 → X-Company-Id 헤더
└──────┬───────┘
       │ HTTP
┌──────▼────────────────────┐        ┌──────────────┐
│  api (.NET 10)            │───────▶│  mssql       │
│  - 상품 CRUD / 구매        │  EF    │  SQL Server  │
│  - Serilog → stdout JSON   │ Core   │  2022        │
│  - OpenSearch 의존성 없음   │        │  + AuditLogs │
└──────┬────────────────────┘        └──────────────┘
       │ Docker fluentd 로깅 드라이버 (24224)
┌──────▼────────────────────┐
│  fluent-bit               │  수집 · JSON 파싱 · 라우팅
└──────┬────────────────────┘
       │ HTTPS bulk
┌──────▼────────────────────┐        ┌──────────────────────┐
│  opensearch (단일 노드)     │◀───────│ opensearch-dashboards│
│  - Security 플러그인 ON     │        │  - admin             │
│  - app-logs-* 인덱스        │        │  - company1_admin    │
│  - DLS / FLS 적용           │        │  - company2_admin    │
└───────────────────────────┘        └──────────────────────┘
```

### 컴포넌트 선정 근거

| 컴포넌트 | 선택 | 이유 |
|---|---|---|
| 수집기 | **Fluent Bit 4.1** | Logstash 대비 1/10 리소스. OpenSearch + MSSQL이 이미 떠 있는 개발 PC에 JVM을 하나 더 올릴 이유가 없다 |
| 전송 방식 | **Docker fluentd 로깅 드라이버** | K8s의 DaemonSet 수집 구조를 로컬에서 가장 근접하게 재현. 앱은 stdout만 신경 쓴다 |
| DB | **MSSQL 2022** | 실제 제품 전제와 동일 |
| OpenSearch | **3.1 단일 노드 + Security ON** | Security를 끄면 PoC 목적의 절반(권한 분리)이 사라진다. **절대 비활성화하지 말 것** |

---

## 실행

### 사전 조건

- Docker Desktop **메모리 12GB 이상 권장** (Settings → Resources)
  - 8GB에서도 뜨지만 OpenSearch + Dashboards + MSSQL이 동시에 돌면 빠듯하다
- Apple Silicon이면 **Rosetta 활성화** (Settings → General → Use Rosetta for x86/amd64 emulation)
  - MSSQL 이미지는 amd64 단일 매니페스트라 에뮬레이션이 필수다
- Linux/WSL이면 `sudo sysctl -w vm.max_map_count=262144`

### 기동

```bash
cp .env.example .env
```

`.env` 를 열어 비밀번호를 채운다. **대문자 + 소문자 + 숫자 + 특수문자 포함 8자 이상**이어야 한다.
복잡도를 못 맞추면 MSSQL 과 OpenSearch 컨테이너가 **에러 메시지 없이 조용히 죽는다.**

```bash
docker compose up -d
```

기동 순서가 곧 설계다. compose가 알아서 다음 순서를 지킨다.

```
opensearch ──(healthy)──▶ os-setup ──(완료)──▶ fluent-bit ──▶ api
                 └───────▶ dashboards
mssql ─────(healthy)─────────────────────────────────────────▶ api
```

`os-setup`이 **완료된 뒤에야** `fluent-bit`이 뜬다.
인덱스 템플릿보다 첫 로그 문서가 먼저 들어가면 `company_id`가 `text`로 자동 매핑되고,
그 순간 DLS가 아무 에러 없이 조용히 죽는다.

> ⚠️ 이 머신 기준으로 **컨테이너 하나가 실제로 start 되기까지 수 분이 걸린다.**
> `docker compose up`이 멈춘 것처럼 보여도 실패가 아니다. `docker compose ps`로 상태를 확인할 것.

### 접속

| 대상 | 주소 | 계정 |
|---|---|---|
| 데모 화면 | http://localhost:8080 | — |
| OpenSearch Dashboards | http://localhost:5601 | 아래 표 참조 |
| OpenSearch API | https://localhost:9200 | `admin` |

| 계정 | 비밀번호 | 볼 수 있는 것 |
|---|---|---|
| `admin` | `.env` 의 `OPENSEARCH_INITIAL_ADMIN_PASSWORD` | 전부 (company1 + company2 + 스택트레이스) |
| `company1_admin` | `.env` 의 `COMPANY1_ADMIN_PASSWORD` | company1 문서만, 스택트레이스 필드 없음 |
| `company2_admin` | `.env` 의 `COMPANY2_ADMIN_PASSWORD` | company2 문서만, 스택트레이스 필드 없음 |

회사 계정은 `opensearch/setup-security.sh` 가 `.env` 값으로 생성한다.
`.env` 를 고쳤다면 `docker compose up os-setup` 을 다시 돌려야 반영된다.

### Dashboards 첫 설정

1. `admin`으로 로그인 → 테넌트 선택 화면에서 **Global** 선택
2. Management → Dashboards Management → Index patterns → Create index pattern
3. 패턴 `app-logs-*`, 시간 필드 `@timestamp`
4. Discover에서 조회

회사 계정으로 로그인할 때도 같은 절차를 각자의 테넌트에서 반복한다.

---

## 로그 스키마

**[docs/log-schema.md](docs/log-schema.md)가 이 PoC의 실질적 산출물이다.**
인프라는 갈아엎을 수 있지만 로그 스키마는 앱 전체에 퍼지므로 여기서 확정해야 한다.

앱·수집기·인덱스 템플릿·권한 스크립트가 모두 그 문서 하나를 참조한다.

---

## Loki + Grafana 비교 (선택)

같은 로그를 **두 백엔드로 동시에** 보내 나란히 비교할 수 있다.

```bash
docker compose -f docker-compose.yml -f docker-compose.loki.yml up -d
```

Grafana 는 http://localhost:3000 (`admin` / `.env` 의 `GRAFANA_ADMIN_PASSWORD`).
Loki 데이터소스는 프로비저닝으로 미리 꽂혀 있다.

`api/` 는 한 글자도 바뀌지 않는다. Fluent Bit 설정 파일 하나와 compose 오버레이 하나가 전부다.

실측 비교 결과는 **[docs/loki-comparison.md](docs/loki-comparison.md)** 에 있다. 요약하면:

| | OpenSearch | Loki OSS |
|---|:--:|:--:|
| 회사별 격리 | ✅ | ❌ 인증 계층 자체가 없다 |
| 필드 숨김 (FLS) | ✅ | ❌ 개념 없음 |
| 메모리 | 1.29 GiB | **219 MiB** |

기본 구성으로 돌아가려면 오버레이 없이 `docker compose up -d` 하면 된다.

## 대용량 검증

```bash
RESET=1 ./scripts/volume-test.sh 100000 1000000 5000000
```

단계적으로 올려 깨지는 지점을 찾는다. 측정 결과와 원인 분석은
**[docs/volume-test.md](docs/volume-test.md)** 에 있다. 요약하면:

| 단계 | 유실률 |
|---|---|
| 10만 / 100만 | **0%** |
| 500만 | **34.65%** |

유실은 백엔드가 못 받아서가 아니라 **수집기가 429 백프레셔를 기다리지 않고 청크를 버려서**
생겼다. 미해결 과제는 저장소 이슈로 등록해 뒀다.

문서당 **224 bytes** 로 수렴하고, **DLS 오버헤드는 사실상 0** 이었다 (436만 건 기준 47ms → 50ms).

## 검증

검증 절차와 실측 결과는 **[docs/verification.md](docs/verification.md)** 에 있다.

빠르게 확인하려면:

```bash
./scripts/verify.sh
```

---

## 디렉터리 구조

```
.
├── docker-compose.yml            # 전체 스택. 기동 순서가 곧 설계
├── docker-compose.stdout.yml     # api 를 json-file 드라이버로 되돌리는 오버라이드
├── docker-compose.loki.yml       # Loki + Grafana 를 병렬로 추가하는 오버레이
├── .env.example                  # 비밀번호 템플릿 (.env 는 커밋하지 않는다)
│
├── api/                          # .NET 10 API — OpenSearch 의존성 0
│   ├── Logging/
│   │   ├── LogSchema.cs          # 로그 필드명 상수
│   │   ├── SerilogSetup.cs       # 출력 JSON 스키마 결정
│   │   ├── Enrichers.cs          # 예외 → error.{type,message,stack_trace}
│   │   ├── AppLog.cs             # event + outcome 을 강제하는 로깅 헬퍼
│   │   └── CompanyContextMiddleware.cs
│   ├── Features/                 # 상품 CRUD / 구매 / 부하생성 / 감사로그
│   ├── Data/                     # EF Core + 시드 실행기
│   └── wwwroot/index.html        # 버튼만 나열한 데모 화면
│
├── db/init/01-seed.sql           # 스키마 + 시드 (스키마의 단일 진실)
├── fluent-bit/
│   ├── fluent-bit.conf           # 수집 · JSON 파싱 · OpenSearch 전송
│   └── fluent-bit-loki.conf      # 위를 @INCLUDE 하고 Loki 출력만 덧붙임
├── grafana/provisioning/         # Loki 데이터소스 자동 등록
├── opensearch/
│   ├── 01-index-template.json    # company_id 를 keyword 로 고정
│   ├── 02-ism-policy.json        # 7일 후 삭제
│   └── setup-security.sh         # 테넌트·역할·사용자 (멱등, POSIX sh)
├── scripts/
│   ├── verify.sh                 # 주장 1~4 자동 검증
│   └── volume-test.sh            # 대용량 단계별 측정 (유실률·처리량·용량)
└── docs/
    ├── log-schema.md             # ★ 로그 스키마 명세
    ├── verification.md           # 검증 절차와 실측 결과
    ├── loki-comparison.md        # OpenSearch vs Loki 실측 비교
    └── volume-test.md            # 대용량 측정 결과 + 원인 분석
```

---

## PoC 이후 검토 과제 (범위 밖)

이 PoC로 **답이 나오지 않는 것들**을 미리 명시해 둔다.

- **성능·용량 산정** — 단일 노드 PoC로는 알 수 없다. 실제 로그량 측정 후 별도 산정
- **HA 구성** — 마스터 3 + 데이터 노드 구성, 장애 시 동작
- **온프렘 납품 시나리오** — 고객사 서버 사양에서 단일 노드가 버티는지
- **로그 보존 비용** — 스토리지 증가 추이 실측 필요
- **개인정보 마스킹 규칙** — 앱 단 코딩 규칙 및 리뷰 체크리스트 수립
- **감사로그 위변조 방지** — WORM 스토리지 또는 해시 체인 방식 검토
- **Keycloak 연동 시 역할 매핑 운영** — 고객사 추가 시 역할 자동 생성 프로세스
