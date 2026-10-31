# Contexto de build: raíz del repositorio
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props .editorconfig FitCore.sln ./
COPY src/ src/
COPY tests/ tests/
RUN dotnet restore src/backend/FitCore.Worker/FitCore.Worker.csproj
RUN dotnet publish src/backend/FitCore.Worker/FitCore.Worker.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:9.0 AS final
WORKDIR /app
COPY --from=build /app .
ENV FileStorage__RootPath=/var/lib/fitcore/files
RUN mkdir -p /var/lib/fitcore/files && chown -R $APP_UID /var/lib/fitcore && chmod -R 700 /var/lib/fitcore
USER $APP_UID
ENTRYPOINT ["dotnet", "FitCore.Worker.dll"]
