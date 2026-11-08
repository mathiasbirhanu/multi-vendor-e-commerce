# MarketHub API — multi-stage build (.NET 9)
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY src/MarketHub.Shared ./src/MarketHub.Shared
COPY src/Modules ./src/Modules
COPY src/MarketHub.Api ./src/MarketHub.Api
RUN dotnet restore src/MarketHub.Api/MarketHub.Api.csproj && \
    dotnet publish src/MarketHub.Api/MarketHub.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "MarketHub.Api.dll"]
