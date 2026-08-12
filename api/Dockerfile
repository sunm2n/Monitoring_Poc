# syntax=docker/dockerfile:1

# ─────────────────────────────────────────────────────────────────────────────
# .NET 10 API.
# 이 이미지 안에는 로그 수집기도, OpenSearch 클라이언트도 없다.
# 앱은 stdout 에 JSON 을 뱉고, 수집은 Docker 로깅 드라이버가 컨테이너 바깥에서 처리한다.
# (K8s 에서 DaemonSet 수집기가 하는 일과 같은 구조)
# ─────────────────────────────────────────────────────────────────────────────

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# csproj 만 먼저 복사해서 restore 레이어를 캐싱한다 — 코드만 고칠 때 재빌드가 빨라진다.
COPY PocApi.csproj ./
RUN dotnet restore

COPY . ./
RUN dotnet publish -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./

EXPOSE 8080
ENTRYPOINT ["dotnet", "PocApi.dll"]
