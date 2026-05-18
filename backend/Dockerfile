# ============================================================
#  UndeuxSales バックエンド Dockerfile（マルチターゲット）
#  - target: api        … ASP.NET Core Web API
#  - target: dataloader … 初期DBダンプ投入コンソールアプリ
# ============================================================

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY . .
RUN dotnet restore src/UndeuxSales.Api/UndeuxSales.Api.csproj \
 && dotnet restore src/UndeuxSales.DataLoader/UndeuxSales.DataLoader.csproj

RUN dotnet publish src/UndeuxSales.Api/UndeuxSales.Api.csproj \
        -c Release -o /app/api --no-restore /p:UseAppHost=false
RUN dotnet publish src/UndeuxSales.DataLoader/UndeuxSales.DataLoader.csproj \
        -c Release -o /app/dataloader --no-restore /p:UseAppHost=false

# ---- API ランタイム ----
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS api
# ヘルスチェック（docker compose）で使用する curl を導入する。
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/api ./
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "UndeuxSales.Api.dll"]

# ---- DataLoader ランタイム ----
FROM mcr.microsoft.com/dotnet/runtime:8.0 AS dataloader
WORKDIR /app
COPY --from=build /app/dataloader ./
USER $APP_UID
ENTRYPOINT ["dotnet", "UndeuxSales.DataLoader.dll"]
