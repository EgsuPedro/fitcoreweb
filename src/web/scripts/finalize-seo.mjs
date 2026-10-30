// Paso final de la compilación del sitio: resuelve la dirección pública en el HTML prerenderizado y genera robots.txt y sitemap.xml.
// Uso: SITE_URL=https://su-dominio.com node scripts/finalize-seo.mjs   (sin SITE_URL las direcciones quedan relativas y no se genera el mapa del sitio).
import { readdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

const dist = fileURLToPath(new URL('../dist/fitcore-web/browser/', import.meta.url));
const siteUrl = (process.env.SITE_URL ?? '').trim().replace(/\/+$/, '');
const token = '__SITE_URL__';

if (siteUrl && !/^https?:\/\/[^\s"'<>]+$/.test(siteUrl)) {
  console.error(`SITE_URL no es una dirección válida: ${siteUrl}`);
  process.exit(1);
}

function* htmlFiles(dir) {
  for (const name of readdirSync(dir)) {
    const path = join(dir, name);
    if (statSync(path).isDirectory()) {
      yield* htmlFiles(path);
    } else if (name.endsWith('.html')) {
      yield path;
    }
  }
}

const publicPaths = [];
let replaced = 0;
for (const file of htmlFiles(dist)) {
  const html = readFileSync(file, 'utf8');
  if (html.includes(token)) {
    writeFileSync(file, html.replaceAll(token, siteUrl));
    replaced++;
  }
  const relative = file.slice(dist.length).replaceAll('\\', '/');
  if (relative === 'index.html') {
    publicPaths.push('/');
  } else if (relative.endsWith('/index.html')) {
    publicPaths.push('/' + relative.slice(0, -'/index.html'.length));
  }
}

const robots = ['User-agent: *', 'Allow: /', 'Disallow: /api/'];
if (siteUrl) {
  robots.push(`Sitemap: ${siteUrl}/sitemap.xml`);
  const urls = publicPaths
    .sort()
    .map((p) => `  <url><loc>${siteUrl}${p === '/' ? '/' : p}</loc></url>`)
    .join('\n');
  writeFileSync(join(dist, 'sitemap.xml'), `<?xml version="1.0" encoding="UTF-8"?>\n<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">\n${urls}\n</urlset>\n`);
} else {
  console.warn('SITE_URL no definido: no se genera sitemap.xml y las direcciones canónicas quedan relativas.');
}
writeFileSync(join(dist, 'robots.txt'), robots.join('\n') + '\n');
console.log(`SEO final: ${replaced} páginas con dirección resuelta, ${publicPaths.length} públicas${siteUrl ? ', sitemap.xml y robots.txt generados' : ''}.`);
