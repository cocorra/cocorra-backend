# ============================================
# Stage 1: Build & Publish
# ============================================
# Tests run in CI (.github/workflows/deploy.yml, job "test") before this image is built.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy only the project files the API needs, then restore: this layer stays cached
# until a .csproj changes, so source-only edits skip the NuGet restore.
COPY Cocorra.DAL/Cocorra.DAL.csproj Cocorra.DAL/
COPY Cocorra.BLL/Cocorra.BLL.csproj Cocorra.BLL/
COPY Cocorra.API/Cocorra.API.csproj Cocorra.API/
RUN dotnet restore Cocorra.API/Cocorra.API.csproj

# Copy all source code
COPY . .

# Publish the API project in Release mode
RUN dotnet publish Cocorra.API/Cocorra.API.csproj \
    -c Release \
    -o /app/publish \
    --no-restore

# ============================================
# Stage 2: Runtime
# ============================================
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Install curl for health checks
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*

# Copy published output from build stage
COPY --from=build /app/publish .

# Run as the image's built-in non-root "app" user (UID 1654). Files mounted into the
# container, such as firebase-config.json, must be readable by that UID.
USER $APP_UID

# Expose port 8080 (ASP.NET 10 default)
EXPOSE 8080

# Health check: /health includes a database connectivity check. start-period covers
# startup migrations.
HEALTHCHECK --interval=30s --timeout=5s --start-period=60s --retries=3 \
    CMD curl -fsS http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "Cocorra.API.dll"]
