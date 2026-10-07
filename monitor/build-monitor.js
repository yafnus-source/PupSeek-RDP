#!/usr/bin/env node
/**
 * build-monitor.js — генерирует monitor-wow2.html из dashboard.php + актуальных JSON-данных.
 *
 * Использование:
 *   node build-monitor.js
 *
 * Читает:
 *   dashboard.php                  → шаблон (берет HTML после ?>)
 *   data/dashboard_events.json     → FALLBACK
 *   data/zabbix_agent_history.json → ZABBIX_RAW
 *   data/addressbook.json          → BOOK_RAW
 *   data/webmon_urls.json          → WEB_URLS
 *   data/webmon_history.json       → WEB_HIST
 *
 * Пишет:
 *   monitor-wow2.html  (статическая версия для file://)
 */
const fs = require('fs');
const path = require('path');

const DIR = __dirname;
const TEMPLATE = path.join(DIR, 'dashboard.php');
const OUTPUT   = path.join(DIR, 'monitor-wow2.html');
const DATA_DIR = path.join(DIR, 'data');

function readJson(name) {
  const p = path.join(DATA_DIR, name);
  if (!fs.existsSync(p)) { console.warn(`  ⚠ ${name} not found, using []`); return []; }
  try { return JSON.parse(fs.readFileSync(p, 'utf8')); }
  catch (e) { console.error(`  ✗ ${name}: ${e.message}`); return []; }
}

(function build() {
  console.log('build-monitor: reading data…');

  const events = readJson('dashboard_events.json');
  console.log(`  events: ${events.length}`);

  let zabbix = readJson('zabbix_agent_history.json');
  // dashboard.php JS expects uptimeSec (нормализуем) + время в ISO
  // оставляем оба поля для совместимости
  zabbix = zabbix.map(z => {
    if (z.uptime !== undefined && z.uptimeSec === undefined) z.uptimeSec = z.uptime;
    if (z.uptimeSec !== undefined && z.uptime === undefined) z.uptime = z.uptimeSec;
    return z;
  });
  console.log(`  zabbix: ${zabbix.length}`);

  const book = readJson('addressbook.json');
  console.log(`  addressbook: ${book.length}`);

  const webUrls = readJson('webmon_urls.json');
  console.log(`  webUrls: ${webUrls.length}`);
  const webHist = readJson('webmon_history.json');
  console.log(`  webHist: ${webHist.length}`);

  // Read dashboard.php template
  let php = fs.readFileSync(TEMPLATE, 'utf8');
  // Remove PHP header up to first ?> (including session/auth logic)
  const split = php.indexOf('?>');
  if (split === -1) { console.error('No ?> found in dashboard.php'); process.exit(1); }
  let html = php.slice(split + 2);

  // Replace PHP echo placeholders with actual JSON
  html = html.replace('<?php echo $eventsJson; ?>', JSON.stringify(events));
  html = html.replace('<?php echo $zabbixJson; ?>', JSON.stringify(zabbix));
  html = html.replace('<?php echo $bookJson; ?>', JSON.stringify(book));
  html = html.replace('<?php echo $webUrlsJson; ?>', JSON.stringify(webUrls));
  html = html.replace('<?php echo $webHistJson; ?>', JSON.stringify(webHist));

  // Clean up any remaining PHP tags (unlikely)
  html = html.replace(/<\?php[\s\S]*?\?>/g, '');

  // Optional: patch title to indicate static build
  // html = html.replace('WOW2 3.9', 'WOW2 3.9 static');

  fs.writeFileSync(OUTPUT, html, 'utf8');
  console.log(`build-monitor: wrote ${OUTPUT} (${(fs.statSync(OUTPUT).size / 1024).toFixed(0)} KB)`);
  // validate
  const scripts = html.match(/<script>([\s\S]*?)<\/script>/g) || [];
  let ok = true;
  scripts.forEach((s,i)=>{
    const code = s.replace(/<\/?script>/g,'');
    try { new Function(code); } catch(e){ console.error(`  ✗ Script ${i+1} syntax error: ${e.message}`); ok=false; }
  });
  if(ok) console.log(`  ✓ JS syntax OK (${scripts.length} blocks)`);
})();
