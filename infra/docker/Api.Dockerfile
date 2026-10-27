# Contexto de build: raíz del repositorio
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props .editorconfig FitCore.sln ./
COPY src/ src/
COPY tests/ tests/
RUN dotnet restore src/backend/FitCore.Api/FitCore.Api.csproj
RUN dotnet publish src/backend/FitCore.Api/FitCore.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "FitCore.Api.dll"]
