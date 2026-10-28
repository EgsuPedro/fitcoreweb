// Convierte los resultados .trx de dotnet test en anotaciones de GitHub Actions (::error) para ver qué prueba falló y por qué
// sin abrir el registro completo. Uso: node scripts/ci-annotate-trx.mjs <carpeta>
import fs from 'node:fs';
import path from 'node:path';

const dir = process.argv[2] ?? 'TestResults';
if (!fs.existsSync(dir)) {
  console.log(`No hay resultados en ${dir}`);
  process.exit(0);
}

const escape = (s) => s.replace(/%/g, '%25').replace(/\r/g, '%0D').replace(/\n/g, '%0A');
let failed = 0;
for (const file of fs.readdirSync(dir).filter((f) => f.endsWith('.trx'))) {
  const xml = fs.readFileSync(path.join(dir, file), 'utf8');
  for (const match of xml.matchAll(/<UnitTestResult\b[^>]*testName="([^"]*)"[^>]*outcome="Failed"[^>]*>([\s\S]*?)<\/UnitTestResult>/g)) {
    failed++;
    const message = /<Message>([\s\S]*?)<\/Message>/.exec(match[2])?.[1] ?? '';
    const trace = /<StackTrace>([\s\S]*?)<\/StackTrace>/.exec(match[2])?.[1] ?? '';
    const text = `${message}\n${trace}`.replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&quot;/g, '"').replace(/&amp;/g, '&').slice(0, 1500);
    console.log(`::error title=Prueba fallida: ${escape(match[1])}::${escape(text)}`);
  }
}

console.log(failed === 0 ? 'Ninguna prueba fallida en los .trx.' : `${failed} prueba(s) fallida(s).`);
