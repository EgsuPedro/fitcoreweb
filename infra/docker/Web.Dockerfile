# Contexto de build: raíz del repositorio
FROM node:24-alpine AS build
WORKDIR /app
COPY src/web/package.json src/web/package-lock.json ./
RUN npm ci
COPY src/web/ ./
# Dirección pública (para canónicas, Open Graph y sitemap). Sin ella quedan relativas.
ARG SITE_URL=
ENV SITE_URL=$SITE_URL
RUN npx ng build --configuration production && node scripts/finalize-seo.mjs

FROM nginx:1.29-alpine AS final
COPY infra/docker/nginx.conf /etc/nginx/conf.d/default.conf
COPY infra/docker/security-headers.inc /etc/nginx/conf.d/security-headers.inc
COPY --from=build /app/dist/fitcore-web/browser /usr/share/nginx/html
EXPOSE 80
