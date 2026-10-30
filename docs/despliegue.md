# Despliegue de FIT360 (demo pública / primer cliente)

Objetivo: tener **un enlace** (`https://…`) para que un cliente vea los avances, y poder **actualizarlo** desde git sin tocar nada a mano. Todo corre en **una sola máquina** con Docker Compose, igual que en desarrollo, pero con las medidas de producción. Archivos: [`infra/prod/`](../infra/prod/) y [`.github/workflows/deploy.yml`](../.github/workflows/deploy.yml).

> **Datos:** mientras sea una máquina gratuita y sin garantía de disponibilidad, use **solo datos de práctica**. El sistema guarda datos de salud (nutrición, evaluaciones, fotos), que la ley de protección de datos de Ecuador (LOPDP) trata como sensibles. Para clientes reales: máquina de pago, copias fuera del servidor probadas y contrato de tratamiento de datos.

## Cómo queda armado
```
Internet ──443──▶ Caddy (HTTPS automático) ──/api/*──▶ API  ──▶ PostgreSQL (rol limitado + RLS)
                        └────────── resto ──────────▶ Web (Angular, nginx)      ▲
                                             Worker ───────────────────────────┘
        migrate (una vez por despliegue, con el propietario de la base)      backup (diario)
```
- Solo Caddy abre puertos (80 y 443). La base, la API y la web **no** son accesibles desde internet.
- La API y el Worker conocen solo el rol limitado `fitcore_app`. Las migraciones las aplica el servicio `migrate` y termina.
- `/health/*`, Swagger y OpenAPI no se exponen al exterior.

## Paso 1 — Crear la máquina (Oracle Cloud «Always Free»)
1. Cree una cuenta en <https://www.oracle.com/cloud/free/> (pide una tarjeta solo para verificar; no cobra la capa gratuita). **Elija bien la región de origen: no se puede cambiar.** Para Ecuador, Santiago (Chile) o São Paulo.
2. *Compute → Instances → Create instance*:
   - Imagen: **Ubuntu 24.04** (arm64). Forma: **VM.Standard.A1.Flex** (Ampere), 2 CPU y 12 GB de memoria alcanzan para la demo.
   - Red: asigne IP pública. Luego, en *Networking → Reserved public IPs*, **reserve una IP** y asóciela a la instancia (así no cambia si se reinicia).
   - Clave SSH: descargue la privada (`.key`) y guárdela; sin ella no hay acceso.
3. Abra los puertos: en la *Security List* de la subred agregue entradas de entrada **TCP 80** y **TCP 443** (y UDP 443) desde `0.0.0.0/0`. El 22 (SSH) ya está.
4. Si dice «Out of capacity», pruebe otra hora u otro dominio de disponibilidad. Oracle puede reclamar máquinas gratuitas casi inactivas; la demo con uso normal no suele alcanzar ese umbral, pero no hay garantía.

> Plan B (de pago, unos 4–6 USD al mes): un VPS pequeño (Hetzner, DigitalOcean, Vultr). Desde el paso 2 es idéntico.

## Paso 2 — Preparar la máquina
Conéctese: `ssh -i clave.key ubuntu@IP`. Instale Docker y git y habilite el cortafuegos del sistema:
```bash
sudo apt-get update && sudo apt-get install -y git ca-certificates curl
curl -fsSL https://get.docker.com | sudo sh
sudo usermod -aG docker $USER && newgrp docker
# En Ubuntu de Oracle el cortafuegos del sistema también bloquea: abra 80 y 443.
sudo iptables -I INPUT 6 -m state --state NEW -p tcp --dport 80 -j ACCEPT
sudo iptables -I INPUT 6 -m state --state NEW -p tcp --dport 443 -j ACCEPT
sudo iptables -I INPUT 6 -m state --state NEW -p udp --dport 443 -j ACCEPT
sudo netfilter-persistent save
```

## Paso 3 — El dominio gratis
1. Cree una cuenta en <https://www.duckdns.org> y un subdominio, por ejemplo `fit360-demo` → `fit360-demo.duckdns.org`. Apúntelo a la **IP reservada** del paso 1.
2. Ese es su `DOMAIN` (sin `https://`). Caddy pedirá el certificado a Let's Encrypt solo, al arrancar.
3. Más adelante, con un dominio propio: cambie el DNS y `DOMAIN` en `.env.prod` y ejecute el despliegue. No hay nada más que tocar (el sistema usa rutas relativas).

## Paso 4 — Primer despliegue
```bash
# Clave de solo lectura del repositorio (si es privado): genere una y agréguela en GitHub → Settings → Deploy keys
ssh-keygen -t ed25519 -f ~/.ssh/fit360_deploy -N ""      # luego: cat ~/.ssh/fit360_deploy.pub  → pegar en GitHub
printf 'Host github.com\n  IdentityFile ~/.ssh/fit360_deploy\n' >> ~/.ssh/config
git clone git@github.com:SU_USUARIO/SU_REPO.git ~/fit360 && cd ~/fit360

cp infra/prod/.env.prod.example infra/prod/.env.prod
nano infra/prod/.env.prod            # complete DOMAIN y todas las claves; genere cada una con:  openssl rand -hex 24
chmod 600 infra/prod/.env.prod

./infra/prod/deploy.sh               # construye, migra, arranca y espera a que la API esté sana (la primera vez tarda varios minutos)
```
Abra `https://SU_DOMINIO`. Entre con el administrador de plataforma de su `.env.prod`.

## Paso 5 — Datos de práctica para la demo
Desde **su PC** (PowerShell), contra el enlace público; el cliente verá un gimnasio con datos:
```powershell
./scripts/demo-seed.ps1 -ApiUrl 'https://SU_DOMINIO/api/v1' -PlatformEmail 'EL_CORREO' -PlatformPassword 'LA_CLAVE' -DemoPassword 'UnaClaveDeDemo2026x' -Slug demo -Domain demo.fit360.test
```
Le crea el gimnasio `demo` con un usuario por rol. Envíe al cliente: el enlace, el identificador del gimnasio (`demo`), el correo del propietario y la clave. **No** le dé el acceso de plataforma.
(`analytics-demo-data.ps1` agrega alumnos, pagos y asistencias; también acepta `-ApiUrl`. Su opción `-ConDatosPasados` necesita Docker en la misma máquina: omítala en el servidor.)

## Paso 6 — Actualizar (cada vez que haya cambios)
Opción manual, siempre disponible: `ssh` al servidor y `cd ~/fit360 && ./infra/prod/deploy.sh`.

Opción automática (recomendada): al subir a `main` y pasar el CI, GitHub despliega solo.
1. Genere una clave solo para despliegues **en su PC**: `ssh-keygen -t ed25519 -f deploy_ci -N ""`. Agregue `deploy_ci.pub` a `~/.ssh/authorized_keys` del servidor.
2. En GitHub → *Settings → Secrets and variables → Actions*:
   - Secretos: `DEPLOY_HOST` (la IP), `DEPLOY_USER` (`ubuntu`), `DEPLOY_SSH_KEY` (contenido de `deploy_ci`), `DEPLOY_KNOWN_HOSTS` (salida de `ssh-keyscan -t ed25519 IP`).
   - Variable: `DEPLOY_ENABLED` = `true`.
   - *Environments → production*: opcional, agregue «Required reviewers» si quiere aprobar cada despliegue.
3. Para desplegar otra versión (o volver atrás): *Actions → Deploy → Run workflow* e indique el commit o etiqueta.

**Volver atrás:** `./infra/prod/deploy.sh <commit>`. Las migraciones solo avanzan: si la versión nueva cambió el esquema, volver al código viejo puede fallar; en ese caso se restaura la copia de seguridad previa (ver abajo).

## Copias de seguridad
- El servicio `backup` guarda cada día (2:00 hora de Ecuador) la base (`db-*.dump`) y los archivos (`files-*.tar.gz`) en el volumen `backups`, y conserva 14 días.
- Una copia solo en la misma máquina **no** protege si se pierde la máquina. Descárguelas fuera con regularidad:
  ```bash
  # desde su PC
  scp -i clave.key "ubuntu@IP:/var/lib/docker/volumes/fit360-prod_backups/_data/*" ./respaldos/
  ```
  (o con `rclone` hacia Backblaze B2 / Cloudflare R2, ambos con capa gratuita).
- **Restaurar** (en una instalación nueva o tras un daño):
  ```bash
  cd ~/fit360 && C="docker compose -f infra/prod/docker-compose.prod.yml --env-file infra/prod/.env.prod"
  $C up -d postgres && $C run --rm migrate          # crea el rol fitcore_app y el esquema
  $C stop api worker caddy
  docker cp respaldos/db-AAAAMMDD-HHMM.dump $($C ps -q postgres):/tmp/restore.dump
  $C exec postgres pg_restore -U fitcore -d fitcore --clean --if-exists --no-owner /tmp/restore.dump
  # archivos privados (si también se perdieron):
  docker run --rm -v fit360-prod_fitcore_files:/files -v "$PWD/respaldos":/in alpine sh -c "cd /files && tar xzf /in/files-AAAAMMDD-HHMM.tar.gz"
  $C up -d
  ```
  **Pruebe la restauración** al menos una vez antes de depender de las copias.

## Endurecimiento: lo que ya está y lo que falta
Ya está:
- HTTPS obligatorio con certificado automático y HSTS; cookie de sesión `Secure`, `HttpOnly`, `SameSite=Strict`.
- Solo Caddy expuesto; la API rechaza encabezados `Host` que no sean el dominio (`AllowedHosts`).
- Sin Swagger/OpenAPI ni rutas de salud públicas; tope de 12 MB por petición.
- La API y el Worker corren con un rol sin privilegios de superusuario y sujeto a Row-Level Security; no tienen credenciales de propietario.
- Registros en JSON con rotación (10 MB × 5 por servicio); límite de intentos de inicio de sesión por IP real (vía `X-Forwarded-For` de Caddy).
- Secretos solo en `.env.prod` (fuera de git).

Falta o conviene hacer:
- **Alertas:** un monitor externo gratuito (UptimeRobot, Better Stack) que consulte `https://SU_DOMINIO/` cada 5 minutos y avise por correo.
- **Actualizaciones del sistema operativo:** `sudo apt-get install -y unattended-upgrades`.
- **SSH:** deshabilitar contraseñas (en Oracle ya viene solo con clave) y, si puede, restringir el 22 a su IP.
- **Sitio público:** en producción el build de la web recibe `SITE_URL=https://DOMINIO` (ya lo pasa el compose) para las direcciones canónicas, `robots.txt` y `sitemap.xml`. Los textos legales son un borrador: reemplácelos antes de abrir al público (`src/web/src/app/features/public/legal-texts.ts` y `Legal__*`). `CONTACT_NOTIFY_EMAIL` avisa al equipo comercial de cada contacto.
- **Correo:** el sistema ya envía por SMTP (variables `SMTP_*` de `.env.prod`; sin `SMTP_HOST` no envía nada). Falta elegir proveedor y verificar SPF/DKIM del dominio para que no caiga en spam.
- **Restauración probada y copias fuera de la máquina** antes de tener clientes reales.
- **Cobros:** sin proveedor de pagos configurado, los pagos comerciales se registran a mano.
