// Mide la latencia de los endpoints de analítica contra el gimnasio «volumen» (cargado con analytics-volume.mjs). Solo lectura.
// Uso: node scripts/analytics-perf.mjs [--runs 5]
const api = 'http://localhost:8080/api/v1';
const runs = Number(process.argv[process.argv.indexOf('--runs') + 1]) || 5;
const login = await (await fetch(`${api}/auth/login`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ tenantSlug: 'volumen', email: 'dueno@volumen.test', password: 'Volumen-Clave-12345', useCookie: false }) })).json();
const auth = { authorization: `Bearer ${login.accessToken}` };
const today = new Date();
const iso = (d) => d.toISOString().slice(0, 10);
const back = (n) => iso(new Date(today.getTime() - n * 86400000));
const periods = { '30 días': 'preset=Last30Days', 'año (366 d)': `preset=Custom&from=${back(365)}&to=${iso(today)}` };
const endpoints = [
  '/analytics/dashboard', '/analytics/dashboard/comparison',
  '/reports/financial/collections?pageSize=100', '/reports/financial/payment-methods', '/reports/financial/outstanding-balances?pageSize=100',
  '/reports/financial/cash-registers', '/reports/financial/reversals',
  '/reports/members/summary', '/reports/members/acquisitions?pageSize=100', '/reports/members/retention?pageSize=100', '/reports/members/renewals?pageSize=100', '/reports/members/expirations?days=30&pageSize=100',
  '/reports/attendance?view=Summary', '/reports/attendance?view=Members&pageSize=100', '/reports/classes/occupancy?pageSize=100', '/reports/classes/cancellations',
  '/reports/branches/comparison',
];
const rows = [];
for (const [label, q] of Object.entries(periods)) {
  for (const e of endpoints) {
    const url = `${api}${e}${e.includes('?') ? '&' : '?'}${q}`;
    const times = [];
    let status = 0;
    let bytes = 0;
    for (let i = 0; i < runs; i++) {
      const t0 = performance.now();
      const res = await fetch(url, { headers: auth });
      const body = await res.arrayBuffer();
      times.push(performance.now() - t0);
      status = res.status;
      bytes = body.byteLength;
    }
    times.sort((a, b) => a - b);
    rows.push({ período: label, endpoint: e.split('?')[0], estado: status, 'primera ms': Math.round(times[0]), 'mediana ms': Math.round(times[Math.floor(times.length / 2)]), 'máx ms': Math.round(times[times.length - 1]), KB: Math.round(bytes / 1024) });
  }
}
console.table(rows);
