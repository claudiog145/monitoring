# Multi-stage build for the demo DemoGenerator (contract v5 section 2). Build context: Portal/ (see docker-compose.yml,
# profile "demo"). The project references no platform project, so only its own folder is copied.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["demo/DemoGenerator/DemoGenerator.csproj", "demo/DemoGenerator/"]
RUN dotnet restore "demo/DemoGenerator/DemoGenerator.csproj"
COPY demo/DemoGenerator/ demo/DemoGenerator/
# The Kusto schema lives next to the packs, outside the project folder; copied so the project can link it.
COPY demo/kusto/ demo/kusto/
RUN dotnet publish "demo/DemoGenerator/DemoGenerator.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 8080
COPY --from=build /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "DemoGenerator.dll"]
