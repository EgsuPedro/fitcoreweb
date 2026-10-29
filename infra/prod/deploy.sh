#!/usr/bin/env bash
# Despliega (o actualiza) FIT360 en esta máquina. Se ejecuta desde la copia del repositorio en el servidor:
#   ./infra/prod/deploy.sh               # la última versión de la rama main
#   ./infra/prod/deploy.sh <commit|tag>  # una versión concreta (también sirve para volver atrás)
# Hace: traer el código, construir las imágenes, aplicar migraciones (servicio «migrate»), reiniciar y esperar a que la API responda.
# Nota para volver atrás: las migraciones solo avanzan. Volver a un código más viejo con la base ya migrada puede no funcionar; en ese caso se
# restaura la copia de seguridad previa (docs/despliegue.md).
set -euo pipefail

cd "$(dirname "$0")/../.."
ref="${1:-origin/main}"
compose=(docker compose -f infra/prod/docker-compose.prod.yml --env-file infra/prod/.env.prod)

if [ ! -f infra/prod/.env.prod ]; then
  echo "Falta infra/prod/.env.prod (copie .env.prod.example y complete los valores)." >&2
  exit 1
fi

echo "==> Trayendo el código ($ref)"
git fetch --all --prune --tags
git checkout --detach "$ref"
echo "$(git rev-parse HEAD)" > infra/prod/.deployed

echo "==> Construyendo imágenes"
"${compose[@]}" build

echo "==> Aplicando migraciones y reiniciando"
"${compose[@]}" up -d --remove-orphans

echo "==> Esperando a que la API esté sana"
for i in $(seq 1 60); do
  state="$("${compose[@]}" ps --format '{{.Service}} {{.Health}}' api | awk '{print $2}')"
  if [ "$state" = "healthy" ]; then
    echo "API sana. Versión desplegada: $(cut -c1-12 infra/prod/.deployed)"
    docker image prune -f >/dev/null
    exit 0
  fi
  sleep 3
done

echo "La API no quedó sana a tiempo. Registros:" >&2
"${compose[@]}" logs --tail 80 migrate api >&2
exit 1
