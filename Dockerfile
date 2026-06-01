# ============================================
# Stage 1: Build & Publish
# ============================================
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy solution and project files first for layer caching
COPY Cocorra.sln ./
COPY Cocorra.DAL/Cocorra.DAL.csproj Cocorra.DAL/
COPY Cocorra.BLL/Cocorra.BLL.csproj Cocorra.BLL/
COPY Cocorra.API/Cocorra.API.csproj Cocorra.API/
COPY Cocorra.Tests/Cocorra.Tests.csproj Cocorra.Tests/

# Restore all dependencies (cached unless .csproj files change)
RUN dotnet restore Cocorra.sln

# Copy all source code
COPY . .

# Run tests to validate before publish
RUN dotnet test Cocorra.Tests/Cocorra.Tests.csproj --no-restore --verbosity minimal

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

# Create directory for user uploads (images, voice files)
RUN mkdir -p /app/wwwroot/Uploads/img/Profiles \
             /app/wwwroot/Uploads/img/Rooms \
             /app/wwwroot/Uploads/voice

# Copy published output from build stage
COPY --from=build /app/publish .

# Expose port 8080 (ASP.NET 10 default)
EXPOSE 8080

# Health check
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD curl -f http://localhost:8080/ || exit 1

ENTRYPOINT ["dotnet", "Cocorra.API.dll"]
