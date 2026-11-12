# syntax=docker/dockerfile:1

# ---- build -------------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

# Restore in its own layer: it only re-runs when package versions or project files change.
COPY global.json Directory.Build.props Directory.Build.targets Directory.Packages.props ./
COPY src/IdempotentApi/IdempotentApi.csproj src/IdempotentApi/
COPY src/IdempotentApi.Redis/IdempotentApi.Redis.csproj src/IdempotentApi.Redis/
COPY samples/PaymentsApi/PaymentsApi.csproj samples/PaymentsApi/
COPY samples/DemoDriver/DemoDriver.csproj samples/DemoDriver/
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages \
    dotnet restore samples/PaymentsApi/PaymentsApi.csproj && \
    dotnet restore samples/DemoDriver/DemoDriver.csproj

COPY .editorconfig ./
COPY src/ src/
COPY samples/PaymentsApi/ samples/PaymentsApi/
COPY samples/DemoDriver/ samples/DemoDriver/
# Framework-dependent IL (no apphost) runs unchanged on the glibc-based runtime image below.
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages \
    dotnet publish samples/PaymentsApi/PaymentsApi.csproj -c Release --no-restore -o /app/api -p:UseAppHost=false && \
    dotnet publish samples/DemoDriver/DemoDriver.csproj -c Release --no-restore -o /app/driver -p:UseAppHost=false

# ---- runtime -----------------------------------------------------------------------------------------
# Chiseled Ubuntu: no shell, no package manager, and a non-root "app" user (UID 1654) baked in.
# One image holds the sample API (default) and the demo driver (compose overrides the entrypoint).
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS runtime
LABEL org.opencontainers.image.title="idempotent-api-sample" \
      org.opencontainers.image.source="https://github.com/hekimcan206/idempotent-api" \
      org.opencontainers.image.licenses="MIT"
COPY --from=build --link /app/api /app/api
COPY --from=build --link /app/driver /app/driver
# The content root is the working directory, so it must be the API folder for appsettings.json to load.
WORKDIR /app/api
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "PaymentsApi.dll"]
