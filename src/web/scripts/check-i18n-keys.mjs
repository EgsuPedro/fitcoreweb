// Comprueba que toda clave de traducción usada en plantillas y código existe en español e inglés.
// Se ejecuta con `npm run check:i18n` (y en CI): una clave faltante se vería en pantalla como el texto crudo "a.b.c".
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');

function sourceFiles(dir) {
  return fs.readdirSync(dir, { withFileTypes: true }).flatMap((entry) => {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) return sourceFiles(full);
    return entry.name.endsWith('.ts') && !entry.name.endsWith('.spec.ts') && entry.name !== 'schema.d.ts' ? [full] : [];
  });
}

function keysOf(tree, prefix = '') {
  return Object.entries(tree).flatMap(([key, value]) =>
    typeof value === 'string' ? [`${prefix}${key}`] : keysOf(value, `${prefix}${key}.`),
  );
}

const KEY = '((?:[a-zA-Z]+\\.)+[a-zA-Z0-9_]+)';
const patterns = [
  new RegExp(`'${KEY}'\\s*\\|\\s*transloco`, 'g'),
  new RegExp(`translate\\('${KEY}'`, 'g'),
  new RegExp(`labelKey:\\s*'${KEY}'`, 'g'),
  new RegExp(`toast\\.success\\('${KEY}'`, 'g'),
  new RegExp(`errors\\.text\\('${KEY}'`, 'g'),
  // Claves de aviso pasadas como argumento a un método (p. ej. run(request, 'memberships.frozenToast')).
  new RegExp(`'((?:[a-zA-Z]+\\.)+[a-zA-Z0-9_]*Toast)'`, 'g'),
];

const used = new Set();
for (const file of sourceFiles(path.join(root, 'src', 'app'))) {
  const text = fs.readFileSync(file, 'utf8');
  for (const pattern of patterns) for (const match of text.matchAll(pattern)) used.add(match[1]);
}

let failed = false;
for (const lang of ['es', 'en']) {
  const tree = JSON.parse(fs.readFileSync(path.join(root, 'public', 'i18n', `${lang}.json`), 'utf8'));
  const known = new Set(keysOf(tree));
  const missing = [...used].filter((key) => !known.has(key)).sort();
  if (missing.length > 0) {
    failed = true;
    console.error(`Faltan en ${lang}.json (${missing.length}):\n  ${missing.join('\n  ')}`);
  }
}
if (used.size < 100) {
  failed = true;
  console.error(`Se encontraron solo ${used.size} claves: ¿cambió la forma de usar las traducciones?`);
}
if (failed) process.exit(1);
console.log(`OK: ${used.size} claves usadas, todas presentes en es y en.`);
