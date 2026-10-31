# syntax=docker/dockerfile:1
# See https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

# This stage is used when running from VS in fast mode (Default for Debug configuration)
FROM mcr.microsoft.com/azure-functions/dotnet-isolated:4-dotnet-isolated10.0 AS base
WORKDIR /home/site/wwwroot
EXPOSE 80


# This stage is used to build the service project
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["src/JourneyMonitor.Functions/JourneyMonitor.Functions.csproj", "src/JourneyMonitor.Functions/"]
COPY ["src/JourneyMonitor.Core/JourneyMonitor.Core.csproj", "src/JourneyMonitor.Core/"]
COPY ["src/JourneyMonitor.Infrastructure/JourneyMonitor.Infrastructure.csproj", "src/JourneyMonitor.Infrastructure/"]
RUN dotnet restore "./src/JourneyMonitor.Functions/JourneyMonitor.Functions.csproj"
COPY . .
WORKDIR "/src/src/JourneyMonitor.Functions"
RUN dotnet build "./JourneyMonitor.Functions.csproj" -c $BUILD_CONFIGURATION -o /app/build

# This stage is used to publish the service project to be copied to the final stage
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./JourneyMonitor.Functions.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# This stage is used in production or when running from VS in regular mode (Default when not using the Debug configuration)
FROM base AS final
WORKDIR /home/site/wwwroot
COPY --from=publish /app/publish .
ENV AzureWebJobsScriptRoot=/home/site/wwwroot \
    AzureFunctionsJobHost__Logging__Console__IsEnabled=true

# Local docker compose only (target: with-azure-cli). Lets DefaultAzureCredential call `az` inside
# the container. Do not use this target when publishing to Azure Container Registry.
# Do not use `InstallAzureCLIDeb | bash`: on this Functions/Ubuntu 24.04 base it can exit 0
# without installing an `az` binary. Install the azure-cli package from Microsoft's apt repo.
FROM final AS with-azure-cli
USER root
# arrivia's corporate TLS-inspecting proxy re-signs outbound HTTPS (including aka.ms/packages.microsoft.com)
# with this root, so it needs to be trusted before curl can fetch the az-cli installer. Supplied as
# a BuildKit secret (not COPY'd) so it never lands in an image layer - `docker history` on the
# resulting image won't show it, and it can't leak if this image is ever published/reused outside
# this local-only target. `required=false` so the build still succeeds on networks that don't
# intercept TLS (the cert is simply not installed and az-cli's install proceeds over plain TLS).
RUN --mount=type=secret,id=corporate_ca,target=/usr/local/share/ca-certificates/corporate-ca.crt,required=false \
    apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl gnupg lsb-release \
    && if [ -s /usr/local/share/ca-certificates/corporate-ca.crt ]; then update-ca-certificates; fi \
    && mkdir -p /etc/apt/keyrings \
    && curl -fsSL https://packages.microsoft.com/keys/microsoft.asc | gpg --dearmor -o /etc/apt/keyrings/microsoft.gpg \
    && chmod go+r /etc/apt/keyrings/microsoft.gpg \
    && AZ_DIST="$(lsb_release -cs)" \
    && if ! curl -fsSL "https://packages.microsoft.com/repos/azure-cli/dists/${AZ_DIST}/Release" >/dev/null; then AZ_DIST=jammy; fi \
    && echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/microsoft.gpg] https://packages.microsoft.com/repos/azure-cli/ ${AZ_DIST} main" > /etc/apt/sources.list.d/azure-cli.list \
    && apt-get update \
    && apt-get install -y --no-install-recommends azure-cli \
    && command -v az \
    && az --version \
    && rm -rf /var/lib/apt/lists/*
ENV AZURE_CONFIG_DIR=/home/.azure

FROM final
