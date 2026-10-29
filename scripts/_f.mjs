import fs from 'node:fs';
import { execSync } from 'node:child_process';
const p = execSync('find tests/FitCore.Api.IntegrationTests -name s.trx').toString().trim().split('\n')[0];
const t = fs.readFileSync(p, 'utf8');
const re = /<UnitTestResult[^>]*testName="([^"]+)"[^>]*outcome="Failed"[\s\S]*?<Message>([\s\S]*?)<\/Message>/g;
let m; while ((m = re.exec(t))) console.log(m[1].split('.').pop(), '\n  ', m[2].replace(/&#xD;|&#xA;/g, ' ').slice(0, 700));
