# Multi-stage build for the demo FakeAppInsights (contract v5 section 2). Build context: Portal/ (see docker-compose.yml,
# profile "demo"). The project references no platform project, so only its own folder is copied.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["demo/FakeAppInsights/FakeAppInsights.csproj", "demo/FakeAppInsights/"]
RUN dotnet restore "demo/FakeAppInsights/FakeAppInsights.csproj"
COPY demo/FakeAppInsights/ demo/FakeAppInsights/
RUN dotnet publish "demo/FakeAppInsights/FakeAppInsights.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 8080
COPY --from=build /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "FakeAppInsights.dll"]
