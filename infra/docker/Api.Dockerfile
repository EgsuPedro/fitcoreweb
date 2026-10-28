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
# Almacén privado de archivos (fotos de progreso): fuera de /app, solo lo lee y escribe la API. Un volumen nombrado hereda este dueño.
ENV FileStorage__RootPath=/var/lib/fitcore/files
RUN mkdir -p /var/lib/fitcore/files && chown -R $APP_UID /var/lib/fitcore && chmod -R 700 /var/lib/fitcore
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "FitCore.Api.dll"]
