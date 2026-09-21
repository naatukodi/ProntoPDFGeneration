FROM mcr.microsoft.com/dotnet/sdk:8.0-bookworm-slim AS build
WORKDIR /src

# Cache package restore separately from application source changes.
COPY ValuationApp.csproj ./
RUN dotnet restore ValuationApp.csproj

COPY . ./
RUN dotnet publish ValuationApp.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim AS final
WORKDIR /app

# Native font support for SkiaSharp and QuestPDF rendering.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libfontconfig1 fonts-liberation \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish ./

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID

ENTRYPOINT ["dotnet", "ValuationApp.dll"]
