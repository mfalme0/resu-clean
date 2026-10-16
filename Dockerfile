# syntax=docker/dockerfile:1
#
# resu-clean in a container.
#
# Three stages, because the toolchain and the runtime have nothing in common: Node builds the SPA
# into the folder the backend expects, the SDK compiles, and the ASP.NET runtime image runs the
# result. The runtime stage carries no Node and no SDK.
#
# One thing to get right, and it is the whole reason this file has a VOLUME line: /data holds the
# SQLite database, your resumes, your verified profile, your encrypted keys and every generated kit.
# Without a volume it all disappears on the next `docker compose down`, and there is no backup
# because the tool never made one.

# ---- 1. The SPA --------------------------------------------------------------
FROM node:22-alpine AS web

WORKDIR /build/web
COPY web/package.json web/package-lock.json ./
RUN npm ci --no-audit --no-fund

COPY web/ ./
# vite writes to ../server/wwwroot, which is where the backend picks static web assets up from.
RUN mkdir -p /build/server/wwwroot && npm run build

# ---- 2. The backend ----------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build

WORKDIR /build
# Restore against the project file alone first, so a source-only change does not re-download packages.
COPY server/ResuClean.csproj server/
RUN dotnet restore server/ResuClean.csproj

COPY server/ server/
COPY --from=web /build/server/wwwroot server/wwwroot

RUN dotnet publish server/ResuClean.csproj -c Release -o /app --no-restore --nologo

# ---- 3. Runtime --------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime

# curl is here for the health check below and nothing else. The runtime image ships no HTTP client,
# and a health check that runs `dotnet --info` would report a healthy container for a process that
# has already stopped answering — which is worse than having no health check at all, because it
# looks like coverage.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app ./

# Data lives here, and only here. Created and owned before dropping privileges so the container can
# run as a normal user rather than root.
RUN mkdir -p /data && chown -R app:app /data /app

# The app binds to 127.0.0.1 by default, which is right for a desktop and useless in a container:
# Docker reaches the container by its IP, so nothing published would ever answer. Binding to
# 0.0.0.0 inside the container is only safe because the compose file publishes to 127.0.0.1 on the
# host. If you publish on 0.0.0.0 there, set RESUCLEAN_API_KEY or the whole network can read your
# profile and spend your model credits.
ENV RESUCLEAN_HOST=0.0.0.0 \
    RESUCLEAN_PORT=5177 \
    RESUCLEAN_DATA_DIR=/data \
    RESUCLEAN_SWAGGER=false \
    DOTNET_gcServer=0 \
    DOTNET_TieredPGO=1

EXPOSE 5177

# /api/health is a real endpoint and answers "ok", so this fails only when the app genuinely cannot
# serve. It also catches the commonest container mistake: Schema.sql missing from the publish output,
# which throws on the first request that touches the database rather than at startup.
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
  CMD curl -fsS "http://127.0.0.1:${RESUCLEAN_PORT}/api/health" || exit 1

VOLUME ["/data"]

USER app
ENTRYPOINT ["dotnet", "ResuClean.dll"]