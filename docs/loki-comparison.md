# OpenSearch vs Loki + Grafana — 실측 비교

측정일: **2026-08-12**
구성: 동일한 Fluent Bit 파이프라인에서 **같은 로그를 두 백엔드로 fan-out**

```bash
docker compose -f docker-compose.yml -f docker-compose.loki.yml up -d
```

| 대상 | 주소 | 계정 |
|---|---|---|
| OpenSearch Dashboards | http://localhost:5601 | `admin` / `company1_admin` / `company2_admin` |
| Grafana | http://localhost:3000 | `admin` / `.env` 의 `GRAFANA_ADMIN_PASSWORD` |
| Loki API | http://localhost:3100 | **없음** (아래 참조) |

---

## 0. 비교가 공정한가 — 먼저 확인한 것

두 백엔드가 정말 같은 로그를 받는지부터 확인했다. 안 그러면 이후 비교가 전부 무의미하다.

| 백엔드 | 최근 10분 로그 건수 |
|---|---|
| OpenSearch | **88건** |
| Loki | **88건** (스트림 6개) |

`fluent-bit-loki.conf` 가 `fluent-bit.conf` 를 `@INCLUDE` 하고 Loki OUTPUT 하나만 덧붙이는 구조라,
INPUT · JSON 파싱 · 메타데이터 제거가 전부 같은 설정을 공유한다.
따라서 아래 차이는 전부 **백엔드 자체의 차이**다.

### 앱은 아무것도 바뀌지 않았다

```bash
# Loki 를 추가한 커밋(e077692)에서 api/ 변경 내역
$ git show --stat e077692 -- api/
(출력 없음)

# 그 커밋이 실제로 건드린 파일 전부
$ git show --stat e077692 --format="" --name-only
.env.example
README.md
docker-compose.loki.yml
docs/loki-comparison.md
fluent-bit/fluent-bit-loki.conf
fluent-bit/fluent-bit.conf
grafana/provisioning/datasources/loki.yml
```

이게 [주장 4](verification.md#주장-4--앱은-로그-백엔드를-몰라도-된다)의 실물 증명이다.
백엔드를 하나 더 붙이는 동안 `api/` 는 한 글자도 바뀌지 않았고, 재빌드도 하지 않았다.
바뀐 것은 Fluent Bit 설정 파일 하나와 compose 오버레이 하나뿐이다.

---

## 1. 결론 요약

| PoC 주장 | OpenSearch | Loki OSS + Grafana OSS |
|---|:--:|:--:|
| 1. 컨테이너가 죽어도 로그는 남는다 | ✅ | ✅ 동일 |
| 2. 회사별 로그 격리 | ✅ | ❌ **불가** |
| 3. 민감 필드 역할별 숨김 (FLS) | ✅ | ❌ **개념 자체가 없음** |
| 4. 앱은 백엔드를 몰라도 된다 | ✅ | ✅ 동일 |
| 리소스 사용량 | ⚠️ 힙에 비례 (아래 3절 정정 참조) | ✅ 수집량에 비례하지 않음 |
| 임의 필드 검색·집계 | ✅ 전 필드 | ⚠️ 라벨 외에는 전체 스캔 |

**한 줄 요약**: Loki 는 훨씬 가볍지만, **이 PoC 의 핵심인 회사별 권한 분리를 할 수 없다.**

> 리소스 항목은 초판에서 "6배 차이" 라고 적었다가 3절에서 정정했다. 배수는 힙 설정에 따라 달라지므로
> 의미가 없고, 실제 차이는 "OpenSearch 는 처리량을 메모리로 산다" 는 성질이다.

---

## 2. 권한 모델 — 여기서 결론이 갈린다

### 측정 결과

| 시나리오 | OpenSearch | Loki |
|---|---|---|
| 인증 없이 API 접근 | `HTTP 401` | **`HTTP 200`** |
| 인증 없이 company2 로그 조회 | `Unauthorized` | **로그 3건 그대로 반환** |
| `company1_admin` 으로 company2 조회 | **`{"count":0}`** | 해당 개념 없음 |
| 사용자·역할 API 존재 여부 | `_plugins/_security/api/*` | `HTTP 404` — **없음** |

실제로 실행한 명령이다.

```bash
# OpenSearch — 인증 없이는 아무것도 못 본다
$ curl -sk https://localhost:9200/app-logs-*/_count
Unauthorized

# Loki — 인증 없이 다른 회사 로그가 그대로 나온다
$ curl -sG http://localhost:3100/loki/api/v1/query_range \
    --data-urlencode 'query={company_id="company2"}'
→ company2 스트림 2개 / 로그 3건 조회됨
```

### 왜 이렇게 되는가

**Loki 에는 인증 계층이 없다.** 이건 버그가 아니라 설계다.
Loki 는 "인증·인가는 앞단의 게이트웨이가 하라"는 전제로 만들어졌다.

멀티테넌시는 있다. `auth_enabled: true` 로 두면 모든 요청에 `X-Scope-OrgID` 헤더를 요구하고,
테넌트별로 데이터를 물리적으로 분리해 저장한다. 문제는 그 헤더를 **누가 검증하는가**다.

```
[사용자] ──▶ [??? 직접 만들어야 하는 프록시] ──X-Scope-OrgID: company1──▶ [Loki]
                        ↑
              여기가 격리의 전부다.
              뚫리면 헤더를 바꿔서 아무 회사 로그나 볼 수 있다.
```

OpenSearch 는 필터가 **계정 자체에 붙어 있다.**

```
[company1_admin] ──▶ [OpenSearch Security]
                            │ 역할에 dls: {"term":{"company_id":"company1"}}
                            ▼
                     쿼리를 어떻게 조작해도 company1 밖으로 못 나간다
```

우리가 측정한 `{"count":0}` 이 그 결과다. 사용자가 `company_id:company2` 를 **직접 입력**해도 0건이다.
중간 계층이 없으니 뚫릴 중간 계층도 없다.

### Grafana 쪽은 해결해주지 않는다

Grafana OSS 의 권한은 **데이터소스 단위**까지다. 행·필드 단위 권한(Row/Field level)은
Grafana Enterprise 기능이다. 즉 Loki 데이터소스에 접근할 수 있는 사용자는 모든 회사 로그를 볼 수 있다.

대시보드에 `company_id="company1"` 필터를 걸어둘 수는 있다. 하지만 그건 **권한이 아니라 화면 설정**이고,
사용자가 패널의 쿼리를 편집하거나 Explore 로 들어가면 그대로 뚫린다.
`grafana/provisioning/datasources/loki.yml` 주석에 이 내용을 적어뒀다.

### FLS 는 등가물이 아예 없다

`error.stack_trace` 만 특정 역할에 숨기는 기능이 Loki 에 없다. 우회하려면:

- 수집 단계에서 스택트레이스가 있는 로그를 **별도 스트림/테넌트로 분리**한다
- 즉 "같은 로그를 역할별로 다르게 보여주기"가 아니라 **파이프라인을 쪼개는** 일이 된다
- 그러면 장애 조사할 때 회사 관리자와 개발자가 서로 다른 로그를 보게 된다

OpenSearch 는 역할에 한 줄이었다.

```json
"fls": ["~error.stack_trace"]
```

---

## 3. 리소스 — Loki 가 가볍지만, 배수를 말하려면 조건을 붙여야 한다

> ⚠️ **정정.** 이 절의 초판에 "메모리 1.29 GiB vs 219 MiB, 약 6배" 라고 적었는데 공정하지 않았다.
> 그때 OpenSearch 힙을 **512m 로 잡아둔 상태**였고, 대용량 검증을 위해 **2g 로 올리자 2.5 GiB** 가 됐다.
> 즉 그 배수는 측정값이 아니라 **설정 선택의 결과**였다. 아래는 두 설정을 나란히 놓은 것이다.

| 컨테이너 | 힙 512m 일 때 | 힙 2g 일 때 |
|---|---|---|
| `poc-opensearch` | 1.079 GiB | **2.525 GiB** |
| `poc-dashboards` | 214.7 MiB | 53.4 MiB |
| **OpenSearch 합계** | ≈ 1.29 GiB | **≈ 2.58 GiB** |
| `poc-loki` | 113.1 MiB | 214.3 MiB |
| `poc-grafana` | 105.5 MiB | 122.7 MiB |
| **Loki 합계** | ≈ 219 MiB | **≈ 337 MiB** |
| `poc-fluent-bit` | 4.7 MiB | 38.2 MiB |

### 정직한 서술

배수(6배 / 7.6배)는 의미가 없다. 실제 차이는 이것이다.

- **OpenSearch 는 목표 수집 속도에 비례하는 JVM 힙이 필요하다.**
  벌크 수집 상한이 `indexing_pressure.memory.limit` = **힙의 10%** 로 묶여 있어서,
  더 많이 받으려면 힙을 키워야 한다. 힙은 곧 메모리 요구량이다.
- **Loki 는 그런 요구가 없다.** 라벨만 색인하므로 수집량이 늘어도 메모리가 비례해 늘지 않는다.
  대신 수집 속도는 `ingestion_rate_mb` 라는 **설정값**으로 제한된다 (기본 4 MB/s).

즉 "Loki 가 6배 가볍다" 가 아니라 **"OpenSearch 는 처리량을 메모리로 사고, Loki 는 그렇지 않다"** 가 맞다.

> 🎯 **온프렘 납품에서는 여전히 이게 결정적일 수 있다.**
> 고객사가 4GB 램 VM 하나를 주는 상황이라면, OpenSearch 는 힙을 얼마로 잡든
> 그 안에서 수집 처리량 상한이 정해진다. Loki 는 같은 자리에서 더 여유롭다.
> 다만 **필요한 수집 속도를 먼저 산정해야** 어느 쪽이 맞는지 말할 수 있다.

측정 환경의 한계는 [volume-test.md](volume-test.md) 상단 경고를 참조.

### 저장 용량

| 백엔드 | 수치 |
|---|---|
| OpenSearch 인덱스 | 533건 / 191.4 KB |
| Loki 청크 디스크 | 120.0 KB |

> ⚠️ **이 숫자는 신뢰하지 말 것.** 533건은 용량 비교를 하기엔 터무니없이 적고,
> Loki 는 청크가 flush 되기 전까지 메모리에 있어 디스크 수치가 낮게 나온다.
> 구조적으로는 Loki 가 유리한 게 맞다(라벨만 색인하고 본문은 통째 압축) —
> 다만 **그 배수를 이 PoC 로 말할 수는 없다.** 실측하려면 실제 로그량으로 며칠 돌려야 한다.

---

## 4. 조회 모델 — 사고방식이 다르다

| | OpenSearch | Loki |
|---|---|---|
| 색인 대상 | **모든 필드** (역색인) | **라벨만** |
| 본문 | 색인됨 | 통째로 압축 저장 |
| 임의 필드 검색 | 바로 가능 | 라벨로 좁힌 뒤 전체 스캔 + 파싱 |
| 집계 | 전 필드 가능 | 라벨 위주, 나머지는 파싱 후 |

### 측정

```bash
# OpenSearch — trace_id 로 바로 검색된다 (역색인)
$ curl ... -d '{"query":{"exists":{"field":"trace_id"}}}'
→ 524건

# Loki — trace_id 는 라벨이 아니므로 라벨 셀렉터로는 안 된다
$ curl ... --data-urlencode 'query={trace_id!=""}'
→ status: success / error: None / 결과: 0건      ← ⚠️ 에러가 아니라 조용히 0건

# Loki — 라벨로 먼저 좁히고 본문을 파싱해야 한다
$ curl ... --data-urlencode 'query={service="poc-api"} | json | trace_id != ""'
→ 조회됨
```

> ⚠️ **Loki 의 가장 위험한 함정이 여기다.**
> 라벨이 아닌 필드를 라벨 셀렉터에 쓰면 **에러가 아니라 조용히 0건**이 나온다.
> 없는 라벨(`{nonexistent="x"}`)을 지정해도 마찬가지로 `success` + 0건이다.
> "로그가 안 남았나?" 하고 앱을 뒤지게 되는 종류의 함정이다.
> OpenSearch 에서 `company_id` 가 `text` 로 잘못 매핑됐을 때 DLS 가 조용히 죽는 것과 같은 성격의 사고다.

### 라벨 설계는 되돌리기 어려운 결정이다

우리가 라벨에 넣은 것과 넣지 않은 것:

| 라벨에 넣음 | 값 개수 | 근거 |
|---|---|---|
| `service` | 1 | 고정 |
| `env` | 1 | 고정 |
| `level` | 3 | Information / Warning / Error |
| `company_id` | 2 (실제로는 고객사 수) | **"회사 수는 유한하다"는 도메인 지식** |

| 라벨에 안 넣음 | 이유 |
|---|---|
| `trace_id` | 요청마다 유일 → 스트림 무한 증가 = 카디널리티 폭발 |
| `user_id` | 사용자 수만큼 |
| `http.path` | `/api/products/12/purchase` 처럼 ID가 박히면 사실상 무한 |

`company_id` 를 라벨에 넣을 수 있는 건 "고객사 수는 수백 규모"라는 사전 판단 덕분이다.
**이 판단이 틀리면 운영 중에 Loki 가 무너진다.** OpenSearch 에는 이런 사전 판단이 필요 없다 —
모든 필드를 색인하기 때문이고, 그 대가로 더 많은 메모리와 디스크를 쓴다.

참고로 Loki 3.x 는 `service_name`, `detected_level` 라벨을 자동으로 추가한다.
우리가 넣은 `service`, `level` 과 중복되므로 실제 운영에서는 한쪽을 정리하는 게 좋다.

---

## 5. 그래서 어느 쪽인가

정답은 **"무엇을 포기할 수 있는가"** 에 달려 있다.

### OpenSearch 를 골라야 하는 경우

- **회사별 로그 격리가 요건이다** ← 우리 제품이 여기 해당한다
- 민감 필드를 역할별로 숨겨야 한다
- 고객사 관리자에게 로그 조회 화면을 직접 열어줄 계획이다
- 로그로 임의 조건 검색·집계를 자유롭게 해야 한다

### Loki 를 골라야 하는 경우

- 로그를 **개발팀 내부에서만** 본다 (테넌트 격리가 필요 없다)
- 서버 리소스가 빈약하다 (온프렘 소규모 납품)
- 조회 패턴이 "라벨로 좁히고 최근 로그 훑기"로 정해져 있다
- 이미 Grafana 로 메트릭을 보고 있어 화면을 합치고 싶다

### 절충안 — 검토해볼 가치가 있다

| 안 | 내용 | 걸림돌 |
|---|---|---|
| Loki + 자체 게이트웨이 | 인증 프록시를 직접 만들어 `X-Scope-OrgID` 를 주입 | 격리의 무게가 전부 우리 코드에 실린다. 버그 하나가 곧 정보 유출 |
| 이중 구성 | 내부 운영용 Loki + 고객 노출용 OpenSearch | 저장소를 두 개 운영. 앱은 안 바뀜(주장 4 덕분) |
| Grafana Enterprise | 행·필드 단위 권한 사용 | **유료**. "무료로 가능하다"는 이 PoC 의 전제가 깨진다 |

> 📌 **이중 구성이 생각보다 현실적이다.** 앱이 백엔드를 모르기 때문에 Fluent Bit OUTPUT 을
> 두 개 두는 것만으로 끝난다 — 지금 이 PoC 가 정확히 그 상태로 돌고 있다.
> 개발팀은 가벼운 Grafana 로 보고, 고객사 관리자에게는 DLS/FLS 가 걸린 OpenSearch 만 열어주는 식이다.

---

## 6. 이 비교로 답이 나오지 않는 것

- **대용량에서의 성능·비용 배수** — 533건으로는 아무 말도 할 수 없다
- **쿼리 응답 속도** — 데이터가 적어 둘 다 즉시 응답한다. 의미 있는 비교가 아니다
- **Loki 클러스터 운영 난이도** — 단일 바이너리 모드만 봤다
- **X-Scope-OrgID 게이트웨이를 직접 만들 때의 실제 공수** — 미구현
- **Grafana Enterprise 의 행 단위 권한이 DLS 와 실제로 등가인지** — 미검증
