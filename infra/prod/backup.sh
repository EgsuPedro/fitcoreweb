#!/bin/sh
# Copias de seguridad automáticas de FIT360: base de datos (pg_dump, formato custom) y archivos privados (tar.gz).
# Corre dentro del servicio «backup» del compose de producción. Hace una copia al iniciar (si no hay una reciente) y luego una diaria a la
# hora BACKUP_HOUR_UTC. Conserva los últimos BACKUP_KEEP_DAYS días. Las copias quedan en el volumen «backups» (/backups).
# Una copia que solo está en la misma máquina NO protege contra perder la máquina: descárguela fuera (docs/despliegue.md, sección Copias).
set -eu

keep="${BACKUP_KEEP_DAYS:-14}"
hour="${BACKUP_HOUR_UTC:-7}"

backup_once() {
  stamp="$(date -u +%Y%m%d-%H%M)"
  # Se escribe con nombre temporal y se renombra al terminar: una copia a medias nunca parece completa.
  pg_dump --format=custom --no-owner --file="/backups/.db-$stamp.part"
  mv "/backups/.db-$stamp.part" "/backups/db-$stamp.dump"
  tar -czf "/backups/.files-$stamp.part" -C /files .
  mv "/backups/.files-$stamp.part" "/backups/files-$stamp.tar.gz"
  find /backups -maxdepth 1 -type f \( -name 'db-*.dump' -o -name 'files-*.tar.gz' \) -mtime "+$keep" -delete
  echo "$(date -u +%FT%TZ) copia $stamp lista ($(du -sh /backups | cut -f1) en total)"
}

recent="$(find /backups -maxdepth 1 -name 'db-*.dump' -mmin -1200 | head -n 1)"
if [ -z "$recent" ]; then
  backup_once
fi

while true; do
  now="$(date -u +%s)"
  target="$(date -u -d "today ${hour}:00" +%s)"
  if [ "$target" -le "$now" ]; then
    target="$(date -u -d "tomorrow ${hour}:00" +%s)"
  fi
  sleep "$((target - now))"
  backup_once || echo "$(date -u +%FT%TZ) ERROR: la copia falló" >&2
done
