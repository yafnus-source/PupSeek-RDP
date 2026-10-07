<?php
/**
 * /monitor/dashboard.php — защищённый дашборд событий.
 * Читает /monitor/data/dashboard_events.json на сервере.
 */
require __DIR__ . '/config.php';

session_name($SESSION_NAME);
session_start();

// Проверка сессии + таймаут
if (!isset($_SESSION['auth']) || $_SESSION['auth'] !== true) {
    header('Location: index.php');
    exit;
}
if (isset($_SESSION['ts']) && (time() - $_SESSION['ts']) > $SESSION_TTL) {
    session_unset(); session_destroy();
    header('Location: index.php');
    exit;
}
$_SESSION['ts'] = time(); // продлеваем сессию при активности

// Чтение данных событий
$events = array();
if (is_file($JSON_PATH)) {
    $raw = @file_get_contents($JSON_PATH);
    if ($raw !== false) {
        $decoded = json_decode($raw, true);
        if (is_array($decoded)) $events = $decoded;
    }
}
$eventsJson = json_encode($events, JSON_UNESCAPED_UNICODE | JSON_INVALID_UTF8_SUBSTITUTE);

// Чтение данных Zabbix Agent
$zabbixPath = __DIR__ . '/data/zabbix_agent_history.json';
$zabbixData = array();
if (is_file($zabbixPath)) {
    $raw = @file_get_contents($zabbixPath);
    if ($raw !== false) {
        $decoded = json_decode($raw, true);
        if (is_array($decoded)) $zabbixData = $decoded;
    }
}
$zabbixJson = json_encode($zabbixData, JSON_UNESCAPED_UNICODE | JSON_INVALID_UTF8_SUBSTITUTE);

// Чтение адресной книги для IP
$bookPath = __DIR__ . '/data/addressbook.json';
$bookData = array();
if (is_file($bookPath)) {
    $raw = @file_get_contents($bookPath);
    if ($raw !== false) {
        $decoded = json_decode($raw, true);
        if (is_array($decoded)) $bookData = $decoded;
    }
}
$bookJson = json_encode($bookData, JSON_UNESCAPED_UNICODE | JSON_INVALID_UTF8_SUBSTITUTE);
?>
<!DOCTYPE html>
<html lang="ru">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Монитор WOW2 — Дашборд событий · PupSeek 3.9</title>
<link href="https://fonts.googleapis.com/css2?family=Roboto:wght@400;500;700&display=swap" rel="stylesheet">
<script src="https://cdn.tailwindcss.com"></script>
<script src="https://unpkg.com/lucide@latest"></script>
<style>
  :root{--bg:#F6F1E7;--card:#FFFFFF;--line:#E8DDD0;--line2:#DDD5C8;--fg:#2B241E;--muted:#9A8F86;--muted2:#C2B8AD;--olive:#8FA98B;--oliveD:#6E8A6E;--oliveL:#E9F0E8;--rose:#D8AFA3;--roseD:#B87E73;--roseL:#F9E8E5;--ink:#2B241E}
  *{box-sizing:border-box;margin:0;padding:0}
  body{font-family:'Roboto',sans-serif;background:var(--bg);color:var(--fg)}
  .mono{font-family:'Roboto',sans-serif}
  .page{
  max-width: none;
  width: 100%;
  margin: 0;
  padding: 24px 12px 18px;
  .hdr{display:flex;align-items:center;gap:14px;padding:14px 16px;background:var(--card);border:1px solid var(--line);border-radius:10px;box-shadow:0 6px 18px -12px rgba(0,0,0,.1)}
  .mark{width:42px;height:42px;border-radius:10px;display:grid;place-items:center;color:#fff;font-weight:900;background:linear-gradient(135deg,var(--olive),var(--oliveD));box-shadow:0 6px 14px -8px rgba(111,138,107,.5)}
  .ttl{font-size:18px;font-weight:900;letter-spacing:-.02em}
  .sub{font-size:12px;color:var(--muted);margin-top:2px}
  .live{display:inline-flex;align-items:center;gap:6px;padding:6px 10px;border-radius:10px;font-size:11px;font-weight:800;letter-spacing:.04em;background:var(--oliveL);color:var(--oliveD);border:1px solid #C8D9C6}
  .live i{width:7px;height:7px;border-radius:50%;background:var(--olive);display:inline-block;animation:pulse 1.6s infinite}
  @keyframes pulse{0%,100%{box-shadow:0 0 0 0 rgba(143,169,139,.45)}50%{box-shadow:0 0 0 6px rgba(143,169,139,0)}}
  .bento{display:grid;grid-template-columns:1.15fr .85fr;gap:14px}
  @media(max-width:900px){.bento{grid-template-columns:1fr}}
  .card{background:var(--card);border:1px solid var(--line);border-radius:10px;box-shadow:0 4px 12px -10px rgba(0,0,0,.08);overflow:hidden}
  .card-h{padding:10px 14px;border-bottom:1px solid var(--line);display:flex;align-items:center;justify-content:space-between;background:var(--card)}
  .card-h b{font-size:12px;letter-spacing:.06em;text-transform:uppercase;color:var(--muted)}
  .kpi{padding:12px;display:grid;grid-template-columns:repeat(3,1fr);gap:8px}
  @media(max-width:640px){.kpi{grid-template-columns:1fr}}
  .kpi-item{padding:10px;border-radius:10px;border:1px solid var(--line);background:var(--bg)}
  .kpi-item.dark{background:var(--fg);color:var(--card);border-color:var(--fg)}
  .kpi-num{font-size:22px;font-weight:900;letter-spacing:-.03em}
  .kpi-lab{font-size:11px;font-weight:700;letter-spacing:.06em;text-transform:uppercase;opacity:.7;margin-top:2px}
  .hosts{display:grid;grid-template-columns:repeat(3,1fr);gap:8px;margin-top:10px}
  @media(max-width:900px){.hosts{grid-template-columns:repeat(2,1fr)}}
  @media(max-width:560px){.hosts{grid-template-columns:1fr}}
  .host{padding:14px;border-radius:10px;background:var(--card);border:1px solid var(--line);display:flex;align-items:center;gap:12px;cursor:pointer;transition:all .15s}
  .host:hover{transform:translateY(-1px);box-shadow:0 6px 14px -8px rgba(0,0,0,.12);border-color:var(--line2)}
  .host.on{background:var(--olive);border-color:var(--olive);color:#fff}
  .host.on .muted{color:rgba(255,255,255,.75)}
  .host.crit{background:var(--roseL);border-color:#E9C2BC}
  .host.crit.on{background:var(--rose);border-color:var(--rose);color:#fff}
  .dot{width:8px;height:8px;border-radius:50%;flex-shrink:0}
  .table-head{padding:14px 16px;border-bottom:1px solid var(--line);display:flex;align-items:center;justify-content:space-between;background:var(--card);position:sticky;top:0;z-index:1}
  .controls{display:flex;gap:8px;align-items:center;flex-wrap:wrap}
  .seg{display:flex;gap:4px;padding:3px;background:var(--bg);border:1px solid var(--line);border-radius:10px}
  .seg button{padding:6px 12px;border-radius:10px;font-size:12px;font-weight:700;border:1px solid transparent;cursor:pointer}
  .seg button.on{background:var(--fg);color:#fff}
  .search{position:relative}
  .search input{width:260px;max-width:42vw;padding:9px 12px 9px 34px;border-radius:10px;border:1px solid var(--line);background:var(--bg);font-size:13px;outline:none}
  .search input:focus{border-color:var(--olive);background:#fff;box-shadow:0 0 0 3px rgba(143,169,139,.18)}
  .search svg{position:absolute;left:10px;top:50%;transform:translateY(-50%);color:var(--muted)}
  table{width:100%;min-width:1600px;border-collapse:collapse;table-layout:fixed}
  thead th{position:sticky;top:0;background:var(--card);z-index:1;text-align:left;font-size:11px;font-weight:800;letter-spacing:.06em;text-transform:uppercase;color:var(--muted);padding:10px 12px;border-bottom:1px solid var(--line);white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
  tbody td{padding:10px 12px;font-size:13px;border-bottom:1px solid #F0E8DD;vertical-align:middle;overflow:hidden}
  tbody tr:hover td{background:#FDF8EE}
  .tag{padding:4px 8px;border-radius:10px;font-size:11px;font-weight:700;border:1px solid var(--line);background:#F0F0F0;color:var(--fg);display:inline-block;max-width:100%;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
  .lvl{padding:4px 8px;border-radius:10px;font-size:11px;font-weight:800;display:inline-flex;align-items:center;gap:6px}
  .lvl.crit{background:var(--roseL);color:var(--roseD);border:1px solid #E9C2BC}
  .lvl.err{background:var(--roseL);color:var(--roseD);border:1px solid #E9C2BC}
  .bar{height:3px;background:var(--olive);width:0;transition:width .6s ease;border-radius:10px}
  .stat{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:16px 16px 14px;box-shadow:0 6px 18px -12px rgba(0,0,0,.1);position:relative;overflow:hidden}
  .stat.mint{background:#F2F2F2;border-color:#E9C2BC}
  .stat.rose{background:#F2F2F2;border-color:#E9C2BC}
  .stat.dark{background:#3A332E;color:#F2EAD5;border-color:#4A423C}
  @media(max-width:700px){.analytics-row{grid-template-columns:1fr}}
  .host-tag{padding:5px 8px;border-radius:10px;font-size:11px;font-weight:700;background:#F0F0F0;border:1px solid var(--line);color:var(--fg)}
</style>
</head>
<body>
<div class="page">
  <div class="hdr">
    <div class="mark">◉</div>
    <div>
      <div class="ttl">Монитор событий <span style="background:var(--olive);color:#fff;padding:2px 7px;border-radius:10px;font-size:10px;vertical-align:middle;margin-left:6px">WOW2 3.9</span></div>
      <div class="sub"><span id="hdrHosts">6 хостов</span> · <span id="hdrTotal">596 событий</span> · <span class="mono">Win32_NTLogEvent</span> · опрос 30с</div>
    </div>
    <div style="margin-left:auto;display:flex;gap:8px;align-items:center">
      <div class="seg" id="viewSeg">
        <button class="on" onclick="switchView('events')" id="viewEvents">События</button>
        <button onclick="switchView('zabbix')" id="viewZabbix">Дашборд Z</button>
      </div>
      <span class="mono" style="padding:7px 10px;border-radius:10px;background:var(--oliveL);border:1px solid #C8D9C6;color:var(--oliveD);font-size:11px;font-weight:800;display:inline-flex;align-items:center;gap:6px"><span style="width:7px;height:7px;border-radius:50%;background:var(--olive);display:inline-block"></span> LIVE</span>
      <button onclick="refreshBusy()" style="padding:9px 14px;border-radius:10px;background:var(--olive);color:#fff;font-weight:800;font-size:12px;border:1px solid var(--olive);cursor:pointer">Обновить</button>
    </div>
  </div>
  <div id="bar" class="bar" style="margin-top:10px"></div>

  <div id="eventsPanel">
  <div class="bento">
    <div>
      <div class="card">
        <div class="card-h"><b>Сводка</b><span class="mono" style="font-size:11px;color:var(--muted)">2 дня · лимит 500</span></div>
        <div class="kpi">
          <div class="kpi-item">
            <div class="kpi-lab">Всего</div>
            <div class="kpi-num mono" id="kTotal">596</div>
            <div class="mono" style="font-size:11px;color:var(--muted)">событий</div>
          </div>
          <div class="kpi-item" style="background:var(--roseL);border-color:#E9C2BC">
            <div class="kpi-lab" style="color:var(--roseD)">Critical</div>
            <div class="kpi-num mono" id="kCrit" style="color:var(--roseD)">0</div>
            <div class="mono" style="font-size:11px;color:var(--roseD)">требует внимания</div>
          </div>
          <div class="kpi-item" style="background:var(--oliveL);border-color:#C8D9C6">
            <div class="kpi-lab" style="color:var(--oliveD)">Error</div>
            <div class="kpi-num mono" id="kErr" style="color:var(--oliveD)">596</div>
            <div class="mono" style="font-size:11px;color:var(--oliveD)">ошибки служб</div>
          </div>
        </div>
        <div style="padding:0 16px 16px;display:flex;gap:6px;flex-wrap:wrap" id="topIds"></div>
      </div>
      <div class="hosts" id="hostGrid"></div>
    </div>
    <div class="card" style="display:flex;flex-direction:column">
      <div class="card-h"><b>Динамика</b><span class="mono" style="font-size:11px;color:var(--muted)">последние 2 дня</span></div>
      <div style="padding:16px">
        <div id="dynBars" style="height:96px;display:flex;align-items:end;gap:4px"></div>
        <div class="mono" style="display:flex;justify-content:space-between;font-size:11px;color:var(--muted);margin-top:8px"><span id="dynFrom">31.08</span><span id="dynTo">02.09 18:31</span></div>
        <div style="margin-top:14px;padding:12px;border-radius:10px;background:var(--bg);border:1px solid var(--line)">
          <div class="mono" style="font-size:11px;font-weight:800;letter-spacing:.06em;color:var(--muted)">Топ источники</div>
          <div id="topSrc" class="mono" style="margin-top:8px;display:flex;gap:6px;flex-wrap:wrap"></div>
        </div>
      </div>
    </div>
  </div>

  <!-- ANALYTICS BENTO -->
  <div class="analytics-row" style="display:grid;grid-template-columns:1fr 1fr;gap:10px;margin-top:10px">
    <div class="card">
      <div class="card-h"><b>Доля по источникам</b><span class="mono" style="font-size:11px;color:var(--muted)">топ-5</span></div>
      <div style="padding:16px;display:flex;gap:16px;align-items:center">
        <div id="donut" style="width:110px;height:110px;border-radius:50%;background:conic-gradient(var(--line) 0 100%);position:relative;display:grid;place-items:center;flex-shrink:0"><span id="donutCenter" class="mono" style="width:64px;height:64px;border-radius:50%;background:var(--card);display:grid;place-items:center;font-weight:800;font-size:12px">—</span></div>
        <div id="donutLegend" class="mono" style="font-size:11px;display:flex;flex-direction:column;gap:6px;flex:1"></div>
      </div>
    </div>
    <div class="card">
      <div class="card-h"><b>AI-сводка</b><span class="mono" style="font-size:11px;color:var(--muted)">по фильтру</span></div>
      <div id="aiSummary" class="mono" style="padding:14px;font-size:13px;line-height:1.5;color:var(--fg)">—</div>
    </div>

  </div>

  <div class="card" style="margin-top:10px;overflow:hidden">
    <div class="table-head">
      <div class="controls">
        <div class="seg" id="levelSeg"></div>
        <div class="seg" id="logSeg"></div>
        <div class="seg" id="periodSeg"></div>
        <input type="datetime-local" id="dateFrom" style="padding:6px 8px;border-radius:10px;border:1px solid var(--line);background:var(--card);font-size:11px;display:none">
        <input type="datetime-local" id="dateTo" style="padding:6px 8px;border-radius:10px;border:1px solid var(--line);background:var(--card);font-size:11px;display:none">
        <label class="mono" style="display:inline-flex;align-items:center;gap:6px;padding:6px 10px;border-radius:10px;border:1px solid var(--line);background:var(--card);font-size:12px;font-weight:700;cursor:pointer"><input type="checkbox" id="groupToggle"> Группировать</label>
        
      </div>
      <div class="search">
        <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><circle cx="11" cy="11" r="8"/><path d="m21 21-4.3-4.3"/></svg>
        <input id="q" list="qList" placeholder="Поиск по сообщению, источнику, ID, хосту…">
        <datalist id="qList"></datalist>
      </div>
    </div>
    <div style="overflow:auto;max-height:48vh" class="scrollbar-thin">
        <table style="width:100%;border-collapse:separate;border-spacing:0;table-layout:fixed">
        <colgroup>
          <col style="width:108px">
          <col style="width:110px">
          <col style="width:380px">
          <col style="width:72px">
          <col style="width:178px">
          <col style="width:auto">
          <col style="width:62px">
        </colgroup>
        <thead>
          <tr>
            <th>Время</th>
            <th>Уровень</th>
            <th>Источник</th>
            <th>ID</th>
            <th>Хост</th>
            <th>Сообщение</th>
            <th></th>
          </tr>
        </thead>
        <tbody id="tbody"></tbody>
      </table>
      <div id="empty" style="display:none;padding:40px;text-align:center">
        <div style="width:48px;height:48px;border-radius:10px;background:var(--bg);border:1px solid var(--line);display:grid;place-items:center;margin:0 auto 10px">◯</div>
        <div style="font-weight:800">Нет событий</div>
        <div style="font-size:13px;color:var(--muted);margin-top:4px">Смените период или сбросьте фильтры</div>
      </div>
    </div>
    <div style="display:flex;align-items:center;justify-content:space-between;padding:10px 16px;background:var(--bg);border-top:1px solid var(--line)">
      <span class="mono" style="font-size:11px;color:var(--muted)">Показано до 500 · всего <b id="stTotal2" style="color:var(--fg)">596</b></span>
      <span class="mono" style="font-size:11px;color:var(--muted)">Обновлено: <b id="stNow" style="color:var(--fg)">—</b> · <b id="stNext" style="color:var(--fg)">—</b></span>
    </div>
  </div>
  </div><!-- /eventsPanel -->

  <div id="zabbixPanel" style="display:none">
    <div class="card" style="overflow:hidden">
      <div class="card-h">
        <b>Дашборд Z — Zabbix Agent</b>
        <div style="display:flex;gap:8px;align-items:center">
          <input id="zSearch" placeholder="Фильтр по хосту…" style="padding:7px 12px;border-radius:10px;border:1px solid var(--line);background:var(--bg);font-size:12px;width:200px;outline:none">
          <span class="mono" style="font-size:11px;color:var(--muted)" id="zStatus">—</span>
        </div>
      </div>
      <div style="display:flex;gap:4px;padding:8px 16px;background:var(--bg);border-bottom:1px solid var(--line);flex-wrap:wrap" id="zHostBar"></div>
      <div style="overflow:auto;max-height:70vh">
        <table style="width:100%;min-width:1100px;border-collapse:collapse;table-layout:fixed">
          <colgroup>
            <col style="width:168px">
            <col style="width:110px">
            <col style="width:67px">
            <col style="width:72px">
            <col style="width:98px">
            <col style="width:80px">
            <col style="width:auto">
            <col style="width:100px">
            <col style="width:80px">
          </colgroup>
          <thead>
            <tr>
              <th>Хост</th>
              <th>IP</th>
              <th style="text-align:center">Агент</th>
              <th style="text-align:right">CPU %</th>
              <th style="text-align:right">RAM free %</th>
              <th style="text-align:right">Темп °C</th>
              <th>Диски</th>
              <th>Uptime</th>
              <th>Версия</th>
            </tr>
          </thead>
          <tbody id="zTbody"></tbody>
        </table>
        <div id="zEmpty" style="display:none;padding:40px;text-align:center">
          <div style="width:48px;height:48px;border-radius:10px;background:var(--bg);border:1px solid var(--line);display:grid;place-items:center;margin:0 auto 10px">◯</div>
          <div style="font-weight:800">Нет данных</div>
          <div style="font-size:13px;color:var(--muted);margin-top:4px">Откройте Дашборд Z в PupSeek RDP для сбора данных</div>
        </div>
      </div>
    </div>
  </div>

</div>

<!-- === Zabbix Side Detail Panel === -->
<div id="zDetailOverlay" style="display:none;position:fixed;inset:0;background:rgba(43,36,30,.32);backdrop-filter:blur(3px);z-index:40" onclick="closeZDetail()"></div>
<div id="zDetail" style="position:fixed;top:0;right:0;bottom:0;width:440px;max-width:94vw;background:var(--card);border-left:1px solid var(--line);box-shadow:-16px 0 40px rgba(43,36,30,.14);z-index:41;transform:translateX(100%);transition:transform .28s cubic-bezier(.4,0,.2,1);display:flex;flex-direction:column;overflow:hidden">
  <div style="padding:16px 16px 12px;border-bottom:1px solid var(--line);display:flex;align-items:center;gap:12px;background:linear-gradient(180deg,var(--card),var(--bg))">
    <div id="zDIcon" style="width:36px;height:36px;border-radius:10px;display:grid;place-items:center;font-weight:900;color:#fff;background:var(--olive);flex-shrink:0">◉</div>
    <div style="flex:1;min-width:0">
      <div id="zDHost" style="font-weight:900;font-size:15px;letter-spacing:-.02em;white-space:nowrap;overflow:hidden;text-overflow:ellipsis">—</div>
      <div id="zDSub" class="mono" style="font-size:11px;color:var(--muted);white-space:nowrap;overflow:hidden;text-overflow:ellipsis">—</div>
    </div>
    <span id="zDPill" style="padding:4px 10px;border-radius:10px;font-size:11px;font-weight:800;border:1px solid var(--line);background:var(--bg);color:var(--muted)">—</span>
    <button onclick="closeZDetail()" style="width:30px;height:30px;border-radius:10px;border:1px solid var(--line);background:var(--card);cursor:pointer;display:grid;place-items:center;font-size:14px">✕</button>
  </div>
  <div style="flex:1;overflow:auto;padding:14px;display:flex;flex-direction:column;gap:12px" class="scrollbar-thin">
    <!-- KPIs -->
    <div style="display:grid;grid-template-columns:1fr 1fr;gap:8px">
      <div class="card" style="padding:10px 12px"><div class="mono" style="font-size:10px;letter-spacing:.06em;color:var(--muted);font-weight:800">CPU</div><div id="zDKpiCpu" style="font-size:20px;font-weight:900;margin-top:2px">—</div><div id="zDKpiCpuBar" style="height:4px;border-radius:10px;background:var(--bg);margin-top:6px;overflow:hidden"><div style="height:100%;width:0;background:var(--olive);transition:width .4s"></div></div></div>
      <div class="card" style="padding:10px 12px"><div class="mono" style="font-size:10px;letter-spacing:.06em;color:var(--muted);font-weight:800">RAM FREE</div><div id="zDKpiRam" style="font-size:20px;font-weight:900;margin-top:2px">—</div><div id="zDKpiRamBar" style="height:4px;border-radius:10px;background:var(--bg);margin-top:6px;overflow:hidden"><div style="height:100%;width:0;background:var(--olive);transition:width .4s"></div></div></div>
      <div class="card" style="padding:10px 12px"><div class="mono" style="font-size:10px;letter-spacing:.06em;color:var(--muted);font-weight:800">UPTIME</div><div id="zDKpiUp" style="font-size:16px;font-weight:900;margin-top:4px">—</div><div id="zDKpiUpSub" class="mono" style="font-size:11px;color:var(--muted);margin-top:2px">—</div></div>
      <div class="card" style="padding:10px 12px"><div class="mono" style="font-size:10px;letter-spacing:.06em;color:var(--muted);font-weight:800">АГЕНТ / ОС</div><div id="zDKpiVer" class="mono" style="font-size:13px;font-weight:800;margin-top:4px">—</div><div id="zDKpiVerSub" class="mono" style="font-size:11px;color:var(--muted);margin-top:2px">—</div></div>
    </div>
    <!-- History sparkline -->
    <div class="card" style="padding:12px">
      <div style="display:flex;align-items:center;justify-content:space-between"><b style="font-size:11px;letter-spacing:.06em;color:var(--muted)">ИСТОРИЯ 2 ДНЯ</b><span class="mono" style="font-size:11px;color:var(--muted)" id="zDHistInfo">—</span></div>
      <div style="display:flex;gap:8px;margin-top:8px">
        <div style="flex:1"><div class="mono" style="font-size:10px;color:var(--muted);font-weight:700">CPU %</div><div id="zDHistCpu" style="height:54px;display:flex;align-items:end;gap:2px;margin-top:4px"></div></div>
        <div style="flex:1"><div class="mono" style="font-size:10px;color:var(--muted);font-weight:700">RAM free %</div><div id="zDHistRam" style="height:54px;display:flex;align-items:end;gap:2px;margin-top:4px"></div></div>
      </div>
    </div>
    <!-- Disks -->
    <div class="card" style="padding:12px">
      <b style="font-size:11px;letter-spacing:.06em;color:var(--muted)">ДИСКИ</b>
      <div id="zDDisks" style="margin-top:8px;display:flex;flex-direction:column;gap:8px"></div>
    </div>
    <!-- Network -->
    <div class="card" style="padding:12px">
      <div style="display:flex;align-items:center;justify-content:space-between"><b style="font-size:11px;letter-spacing:.06em;color:var(--muted)">СЕТЬ</b><span class="mono" style="font-size:11px;color:var(--muted)" id="zDNetInfo">—</span></div>
      <div id="zDNet" style="margin-top:8px;display:flex;flex-direction:column;gap:6px"></div>
    </div>
    <!-- Services -->
    <div class="card" style="padding:12px">
      <div style="display:flex;align-items:center;justify-content:space-between"><b style="font-size:11px;letter-spacing:.06em;color:var(--muted)">СЛУЖБЫ</b><span class="mono" style="font-size:11px;color:var(--muted)" id="zDSvcInfo">—</span></div>
      <div id="zDSvc" style="margin-top:8px;display:flex;flex-wrap:wrap;gap:6px"></div>
    </div>
  </div>
  <div style="padding:10px 14px;border-top:1px solid var(--line);display:flex;gap:8px;background:var(--bg)">
    <button onclick="if(window.filterByHost) filterByHost(document.getElementById('zDHost').textContent)" style="flex:1;padding:8px 12px;border-radius:10px;border:1px solid var(--line);background:var(--card);font-weight:700;font-size:12px;cursor:pointer">Фильтр в Событиях</button>
    <button onclick="closeZDetail()" style="padding:8px 14px;border-radius:10px;background:var(--olive);color:#fff;font-weight:800;font-size:12px;border:1px solid var(--olive);cursor:pointer">Закрыть</button>
  </div>
</div>

<script>
const FALLBACK = <?php echo $eventsJson; ?>;
const ZABBIX_RAW = <?php echo $zabbixJson; ?>;
const BOOK_RAW = <?php echo $bookJson; ?>;
let DATA_SRC = FALLBACK;
let level="Все", logFilter="Все", period="2 дня", selectedHost="", q="";
function hostCounts(){const m={}; DATA_SRC.forEach(e=>{m[e.computer]=(m[e.computer]||0)+1}); return m}
function buildLogSeg(){
  const el=document.getElementById("logSeg"); if(!el) return; el.innerHTML="";
  ["Все","System","Application","Security"].forEach(v=>{
    const b=document.createElement("button"); b.textContent=v;
    const on=logFilter===v;
    b.style.cssText="padding:6px 10px;border-radius:10px;font-size:11px;font-weight:700;border:1px solid "+(on?"var(--olive)":"var(--line)")+";background:"+(on?"var(--olive)":"var(--card)")+";color:"+(on?"#fff":"var(--muted)")+";cursor:pointer";
    b.onclick=()=>{ logFilter=v; buildLogSeg(); apply(); };
    el.appendChild(b);
  });
}
function buildSeg(){
  const lv=document.getElementById("levelSeg"); lv.innerHTML="";
  [["Все","Все"],["Critical","Critical"],["Error","Error"]].forEach(([l,v])=>{
    const b=document.createElement("button"); b.textContent=l;
    const on=level===v;
    b.style.cssText="padding:6px 12px;border-radius:10px;font-size:12px;font-weight:800;border:1px solid "+(on?"var(--olive)":"var(--line)")+";background:"+(on?"var(--olive)":"var(--card)")+";color:"+(on?"#fff":"var(--muted)")+";cursor:pointer";
    b.onclick=()=>{level=v; buildSeg(); apply();}; lv.appendChild(b);
  });
  const pp=document.getElementById("periodSeg"); pp.innerHTML="";
  ["30 мин","1 час","24 часа","2 дня","Кастом"].forEach(p=>{
    const b=document.createElement("button"); b.textContent=p;
    const on=period===p;
    b.style.cssText="padding:6px 12px;border-radius:10px;font-size:12px;font-weight:800;border:1px solid "+(on?"var(--fg)":"var(--line)")+";background:"+(on?"var(--fg)":"var(--card)")+";color:"+(on?"#fff":"var(--muted)")+";cursor:pointer";
    b.onclick=()=>{period=p; buildSeg(); updateDatePicker(); buildAutocomplete(); apply();}; pp.appendChild(b);
  });
}
function buildDynamics(){
  const cont=document.getElementById("dynBars"); if(!cont) return;
  // 7 buckets for last 2 days (approx 7*~7 hours)
  const buckets=7;
  const now=Date.now();
  const periodMs=2*24*60*60*1000;
  const bucketMs=periodMs/buckets;
  const from=now-periodMs;
  const counts=new Array(buckets).fill(0);
  // overall counts
  DATA_SRC.forEach(e=>{
    const t=new Date(e.time).getTime();
    if(t<from) return;
    const idx=Math.min(buckets-1, Math.floor((t-from)/bucketMs));
    counts[idx]++;
  });
  const max=Math.max(...counts,1);
  cont.innerHTML="";
  counts.forEach((c,i)=>{
    const h=Math.max(8, Math.round(c/max*100));
    const bar=document.createElement("span");
    bar.style.cssText=`flex:1;height:${h}%;background:${c===max?'var(--oliveD)':c>max*0.6?'var(--olive)':c>max*0.3?'#C8D9C6':'var(--line)'};border-radius:10px;transition:height .3s`;
    bar.title=`${c} событий`;
    cont.appendChild(bar);
  });
  const fromD=new Date(from), toD=new Date(now);
  const pad=n=>String(n).padStart(2,"0");
  const fromEl=document.getElementById("dynFrom"); if(fromEl) fromEl.textContent=`${pad(fromD.getDate())}.${pad(fromD.getMonth()+1)}`;
  const toEl=document.getElementById("dynTo"); if(toEl) toEl.textContent=`${pad(toD.getDate())}.${pad(toD.getMonth()+1)} ${pad(toD.getHours())}:${pad(toD.getMinutes())}`;
}
function buildHosts(){
  const grid=document.getElementById("hostGrid"); grid.innerHTML="";
  const counts=hostCounts();
  const hosts=Object.keys(counts).sort((a,b)=>counts[b]-counts[a]);
  hosts.forEach(h=>{
    const n=counts[h]; const active=selectedHost===h; const crit=DATA_SRC.filter(e=>e.computer===h&&e.level==="Critical").length;
    const div=document.createElement("div"); div.className="host"+(active?" on":"")+(crit&&!active?" crit":"");
    // per-host 7-bucket sparkline
    const hCounts=new Array(7).fill(0);
    const fromH=Date.now()-2*24*60*60*1000;
    const bucketH=(2*24*60*60*1000)/7;
    DATA_SRC.filter(e=>e.computer===h).forEach(e=>{
      const t=new Date(e.time).getTime();
      if(t<fromH) return;
      const idx=Math.min(6, Math.floor((t-fromH)/bucketH));
      hCounts[idx]++;
    });
    const hMax=Math.max(...hCounts,1);
    let spark=`<div style="display:flex;align-items:end;gap:2px;height:22px;margin-left:10px">`;
    hCounts.forEach(c=>{
      const hh=Math.max(4, Math.round(c/hMax*100));
      spark+=`<span style="flex:1;width:3px;height:${hh}%;background:${c===hMax?'var(--olive)':'#E8DDD0'};border-radius:10px;display:inline-block"></span>`;
    });
    spark+=`</div>`;
    div.innerHTML=`<div><div style="font-weight:800;font-size:13px">${h}</div><div class="mono muted" style="font-size:11px"><span style="color:#8FA98B">${n} событий</span>${crit?` · Critical ${crit}`:""}</div></div>${spark}<div style="margin-left:auto;font-weight:900">${crit?"●":"○"}</div>`;
    div.onclick=()=>{selectedHost=active?"":h; buildHosts(); apply();};
    div.ondblclick=()=>{selectedHost=""; buildHosts(); apply();};
    grid.appendChild(div);
  });
  if(!hosts.length) grid.innerHTML='<div style="color:var(--muted)">Нет хостов</div>';
  // update dynamics main chart
  buildDynamics();
  const srcCnt={}; DATA_SRC.forEach(e=>{srcCnt[e.source]=(srcCnt[e.source]||0)+1});
  const top=Object.entries(srcCnt).sort((a,b)=>b[1]-a[1]).slice(0,4);
  const topEl=document.getElementById("topSrc"); if(topEl){ topEl.innerHTML=""; top.forEach(([s,c])=>{ const span=document.createElement("span"); span.style.cssText="padding:5px 8px;border-radius:10px;background:#fff;border:1px solid var(--line);font-size:11px;font-weight:700"; span.textContent=`${s} · ${c}`; topEl.appendChild(span); }); }
  const total=DATA_SRC.length, critAll=DATA_SRC.filter(e=>e.level==="Critical").length, errAll=DATA_SRC.filter(e=>e.level==="Error").length;
  const kTotalEl=document.getElementById("kTotal"); if(kTotalEl) kTotalEl.textContent=total;
  const kCritEl=document.getElementById("kCrit"); if(kCritEl) kCritEl.textContent=critAll;
  const kErrEl=document.getElementById("kErr"); if(kErrEl) kErrEl.textContent=errAll;
  const eids={}; DATA_SRC.forEach(e=>{eids[e.eventId]=(eids[e.eventId]||0)+1});
  const sorted=Object.entries(eids).sort((a,b)=>b[1]-a[1]);
  const topIds=document.getElementById("topIds"); if(topIds){ topIds.innerHTML=""; sorted.slice(0,3).forEach(([id,c])=>{ const s=document.createElement("span"); s.className="mono"; s.style.cssText="padding:5px 8px;border-radius:10px;background:#fff;border:1px solid var(--line);font-size:11px;font-weight:700"; s.textContent=`${id} ×${c}`; topIds.appendChild(s); });}
}
document.getElementById("q").addEventListener("input",e=>{q=e.target.value.toLowerCase(); apply();});
document.getElementById("dateFrom")?.addEventListener("change", apply);
document.getElementById("dateTo")?.addEventListener("change", apply);
function updateDatePicker(){
  const fromEl=document.getElementById("dateFrom"), toEl=document.getElementById("dateTo");
  const show=period==="Кастом";
  if(fromEl) fromEl.style.display=show?"inline-block":"none";
  if(toEl) toEl.style.display=show?"inline-block":"none";
}
function buildAutocomplete(){
  const dl=document.getElementById("qList"); if(!dl) return; dl.innerHTML="";
  const ids=[...new Set(DATA_SRC.map(e=>String(e.eventId)))].slice(0,20);
  const srcs=[...new Set(DATA_SRC.map(e=>e.source))].slice(0,20);
  const hosts=[...new Set(DATA_SRC.map(e=>e.computer))].slice(0,20);
  [...ids, ...srcs, ...hosts].forEach(v=>{
    const opt=document.createElement("option"); opt.value=v; dl.appendChild(opt);
  });
}
function cutoffFor(p){
  if(p==="30 мин")return 30*60*1000;
  if(p==="1 час")return 60*60*1000;
  if(p==="24 часа")return 24*60*60*1000;
  if(p==="Кастом"){
    const fromEl=document.getElementById("dateFrom"), toEl=document.getElementById("dateTo");
    if(fromEl && fromEl.value && toEl && toEl.value){
      return new Date(toEl.value).getTime() - new Date(fromEl.value).getTime();
    }
    return 2*24*60*60*1000;
  }
  return 2*24*60*60*1000;
}
function getCutoffTime(){
  if(period==="Кастом"){
    const fromEl=document.getElementById("dateFrom");
    if(fromEl && fromEl.value) return new Date(fromEl.value).getTime();
  }
  return Date.now() - cutoffFor(period);
}
function filterByHost(host){ selectedHost=(selectedHost===host?"":host); buildHosts(); apply(); }
function esc(s){return String(s||"").replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]))}
let groupDup=false;
let currentModalEvent=null;
document.getElementById("groupToggle")?.addEventListener("change", e=>{ groupDup=e.target.checked; apply(); });
function openHostCard(host){
  // Mock AD info
  const info=`Хост: ${host}\nОС: Windows Server 2019\nIP: 10.0.1.${Math.floor(Math.random()*200+10)}\nOU: Contoso/Servers`;
  if(confirm(info+"\n\nОткрыть в PupSeek (фильтр по хосту)?")){
    filterByHost(host);
    window.scrollTo({top: document.getElementById("tbody").offsetTop-100, behavior:"smooth"});
  }
}
function apply(){
  const cutoff=getCutoffTime();
  let rows=DATA_SRC.filter(e=>{
    const t=new Date(e.time).getTime();
    if(t<cutoff) return false;
    if(level!=="Все" && e.level!==level) return false;
    if(logFilter!=="Все" && (e.logName||"")!==logFilter) return false;
    if(selectedHost && e.computer!==selectedHost) return false;
    if(q && !((e.message||"").toLowerCase().includes(q) || (e.source||"").toLowerCase().includes(q) || String(e.eventId).includes(q) || (e.computer||"").toLowerCase().includes(q))) return false;
    return true;
  });
  // grouping
  if(groupDup){
    const map={};
    rows.forEach(e=>{
      const k=e.eventId+"|"+e.source+"|"+e.computer;
      if(!map[k]) map[k]={...e, _cnt:0, _first:e.time};
      map[k]._cnt++;
      if(new Date(e.time) > new Date(map[k]._first)) map[k]= {...e, _cnt: map[k]._cnt, _first: e.time};
    });
    rows=Object.values(map).sort((a,b)=>new Date(b._first)-new Date(a._first));
  }

  rows.sort((a,b)=>new Date(b.time||b._first)-new Date(a.time||a._first)); rows=rows.slice(0,500);
  const tb=document.getElementById("tbody"); tb.innerHTML="";
  document.getElementById("empty").style.display=rows.length?"none":"block";
  const frag=document.createDocumentFragment();
  rows.forEach(e=>{
    const tr=document.createElement("tr");
    const d=new Date(e.time); const pad=n=>String(n).padStart(2,"0");
    const ts=pad(d.getDate())+"."+pad(d.getMonth()+1)+" "+pad(d.getHours())+":"+pad(d.getMinutes());
    const lvl=e.level==="Critical"?`<span class="lvl crit"><span style="width:7px;height:7px;border-radius:50%;background:var(--rose);display:inline-block"></span>Critical</span>`:
            e.level==="Error"?`<span class="lvl" style="background:var(--roseL);color:var(--roseD);border:1px solid #E9C2BC"><span style="width:7px;height:7px;border-radius:50%;background:var(--rose);display:inline-block"></span>Error</span>`:
            `<span class="lvl" style="background:var(--oliveL);color:var(--oliveD);border:1px solid #C8D9C6">${esc(e.level)}</span>`;
    const hostClick = `onclick="if(window.filterByHost) filterByHost('${esc(e.computer)}')" style="cursor:pointer" title="Открыть в PupSeek — фильтр по хосту"`;
        const cntBadge = e._cnt && e._cnt>1 ? ` <span style="padding:2px 6px;border-radius:999px;background:var(--oliveL);border:1px solid #C8D9C6;font-size:10px;font-weight:800;color:var(--oliveD)">×${e._cnt}</span>` : "";
    tr.innerHTML=`<td class="mono" style="color:var(--muted);white-space:nowrap">${ts}</td><td style="white-space:nowrap">${lvl}${cntBadge}</td><td class="hidden lg:table-cell mono" style="color:var(--muted);max-width:340px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap" title="${esc(e.source)}">${esc(e.source)}</td><td class="mono" style="font-weight:800"><a href="#" onclick="event.stopPropagation(); openDocs('${esc(e.eventId)}'); return false;" style="color:var(--olive);text-decoration:none;border-bottom:1px dashed var(--olive)">${esc(e.eventId)}</a></td><td><span class="host-tag" onclick="event.stopPropagation(); openHostCard('${esc(e.computer)}')" style="cursor:pointer">${esc(e.computer)}</span></td><td><span style="display:-webkit-box;-webkit-line-clamp:2;-webkit-box-orient:vertical;overflow:hidden;white-space:normal;word-break:break-word;line-height:1.35;width:100%;max-width:100%" title="${esc(e.message)}">${esc(e.message)}</span></td><td style="text-align:right;display:flex;gap:4px;justify-content:flex-end"><button class="msgBtn" style="width:28px;height:28px;border-radius:10px;border:1px solid var(--line);background:var(--bg);cursor:pointer;display:grid;place-items:center">›</button></td>`;
    const btn=tr.querySelector('.msgBtn'); if(btn) btn.onclick=()=>{ currentModalEvent=e; openModal(e.message); document.getElementById('modalRaw').textContent=JSON.stringify(e, null, 2); };
    [...tr.children].forEach(td=>{td.style.padding="11px 12px"; td.style.overflow="hidden";});
    tr.style.borderBottom="1px solid #F0E8DD";
    tr.tabIndex=0;
    tr.style.cursor="pointer";
    tr.onmouseenter=()=>tr.style.background="#FDF8EE";
    tr.onmouseleave=()=>tr.style.background="transparent";
    tr.onclick=(ev)=>{ if(ev.target.closest('button')) return; openModal(e.message); };
    tr.onkeydown=(ev)=>{ if(ev.key==='Enter'){ openModal(e.message); } };
    tr.querySelector('.host-tag').onclick=(ev)=>{ ev.stopPropagation(); filterByHost(e.computer); };
    frag.appendChild(tr);
  });
  tb.appendChild(frag);
  const hdrTotal=document.getElementById("hdrTotal"); if(hdrTotal) hdrTotal.textContent=DATA_SRC.length+" событий";
  const hdrHosts=document.getElementById("hdrHosts"); if(hdrHosts) hdrHosts.textContent=Object.keys(hostCounts()).length+" хоста";
  const now2=new Date(), pad3=n=>String(n).padStart(2,"0");
  const stNow=document.getElementById("stNow"); if(stNow) stNow.textContent=pad3(now2.getHours())+":"+pad3(now2.getMinutes());
  const nxt=new Date(now2.getTime()+30*60*1000); const stNext=document.getElementById("stNext"); if(stNext) stNext.textContent=pad3(nxt.getHours())+":"+pad3(nxt.getMinutes());
}
function buildHeatmap(){
  const c=document.getElementById("heatmap"); if(!c) return;
  c.innerHTML="";
  // header row
  const header=document.createElement("div"); header.style.display="contents";
  header.innerHTML='<div></div>'+Array.from({length:24},(_,h)=>`<div style="text-align:center;color:var(--muted)">${h}</div>`).join("");
  c.appendChild(header);
  const days=["31.08","01.09","02.09"];
  const now=Date.now();
  // build 3x24 grid
  days.forEach((label,di)=>{
    const rowLabel=document.createElement("div"); rowLabel.textContent=label; rowLabel.style.color="var(--muted)"; rowLabel.style.display="grid"; rowLabel.style.placeItems="center"; c.appendChild(rowLabel);
    for(let h=0;h<24;h++){
      const cell=document.createElement("div"); cell.style.height="14px"; cell.style.borderRadius="4px"; cell.style.border="1px solid var(--line)";
      // count events for this day/hour
      const cnt=DATA_SRC.filter(e=>{
        const d=new Date(e.time);
        const day=`${String(d.getDate()).padStart(2,"0")}.${String(d.getMonth()+1).padStart(2,"0")}`;
        return day===label && d.getHours()===h;
      }).length;
      const intensity=Math.min(1, cnt/8);
      cell.style.background=intensity===0?"var(--card)":`rgba(143,169,139,${0.2+intensity*0.8})`;
      cell.title=`${label} ${h}:00 — ${cnt}`;
      c.appendChild(cell);
    }
  });
}
function buildDonut(){
  const el=document.getElementById("donut"); if(!el) return;
  const srcCnt={}; DATA_SRC.forEach(e=>{srcCnt[e.source]=(srcCnt[e.source]||0)+1});
  const top=Object.entries(srcCnt).sort((a,b)=>b[1]-a[1]).slice(0,5);
  const total=DATA_SRC.length||1;
  let acc=0;
  const colors=["#8FA98B","#D8AFA3","#C9BFB5","#A8B8A5","#E8DDD0"];
  let grad=top.map(([s,c],i)=>{
    const pct=c/total*100;
    const start=acc; acc+=pct;
    return `${colors[i%colors.length]} ${start}% ${acc}%`;
  }).join(", ");
  el.style.background=`conic-gradient(${grad})`;
  const center=document.getElementById("donutCenter"); if(center) center.textContent=DATA_SRC.length;
  const legend=document.getElementById("donutLegend"); if(legend){ legend.innerHTML=""; top.forEach(([s,c],i)=>{
    const row=document.createElement("div"); row.style.display="flex"; row.style.alignItems="center"; row.style.gap="6px";
    row.innerHTML=`<span style="width:10px;height:10px;border-radius:50%;background:${colors[i%colors.length]};display:inline-block"></span> <span style="flex:1;white-space:nowrap;overflow:hidden;text-overflow:ellipsis">${s}</span> <b>${c}</b>`;
    legend.appendChild(row);
  });}
}
function buildTimeline(){
  const cont=document.getElementById("timeline"); if(!cont) return;
  cont.innerHTML="";
  const buckets=24;
  const now=Date.now(), from=now-2*24*60*60*1000, bucketMs=(2*24*60*60*1000)/buckets;
  const crit=new Array(buckets).fill(0), err=new Array(buckets).fill(0);
  DATA_SRC.forEach(e=>{
    const t=new Date(e.time).getTime(); if(t<from) return;
    const idx=Math.min(buckets-1, Math.floor((t-from)/bucketMs));
    if(e.level==="Critical") crit[idx]++; else err[idx]++;
  });
  const max=Math.max(...crit, ...err, 1);
  for(let i=0;i<buckets;i++){
    const col=document.createElement("div"); col.style.flex="1"; col.style.display="flex"; col.style.flexDirection="column"; col.style.justifyContent="end"; col.style.gap="1px"; col.style.height="100%";
    const cH=Math.round(crit[i]/max*100), eH=Math.round(err[i]/max*100);
    col.innerHTML=`<div title="Critical ${crit[i]}" style="height:${cH}%;background:var(--rose);border-radius:6px 6px 0 0"></div><div title="Error ${err[i]}" style="height:${eH}%;background:var(--olive);border-radius:6px 6px 0 0"></div>`;
    cont.appendChild(col);
  }
}
function buildAiSummary(){
  const el=document.getElementById("aiSummary"); if(!el) return;
  const total=DATA_SRC.length;
  const byHost={}; DATA_SRC.forEach(e=>{byHost[e.computer]=(byHost[e.computer]||0)+1});
  const topHost=Object.entries(byHost).sort((a,b)=>b[1]-a[1])[0];
  const topId=Object.entries(DATA_SRC.reduce((a,e)=>{a[e.eventId]=(a[e.eventId]||0)+1; return a;},{})).sort((a,b)=>b[1]-a[1])[0];
  const topSrc=Object.entries(DATA_SRC.reduce((a,e)=>{a[e.source]=(a[e.source]||0)+1; return a;},{})).sort((a,b)=>b[1]-a[1])[0];
  let todo="";
  if(topId && String(topId[0])==="7031") todo="Перезапустите службу Agentless Management: <code>Restart-Service -Name Agentless*</code> или <code>sc stop/start</code>.";
  else if(topId && String(topId[0])==="1054") todo="Проверьте DNS: <code>nslookup</code>, <code>ipconfig /flushdns</code>, <code>gpresult /h</code>.";
  else if(topId && String(topId[0])==="3") todo="Проверьте Kerberos: время на хостах, <code>klist purge</code>, SPN.";
  else if(topId) todo=`Проверьте источник <b>${topSrc?topSrc[0]:"—"}</b> и ID ${topId[0]} в docs.microsoft.com.`;
  else todo="—";
  el.innerHTML=`За 2 дня <b>${total}</b> событий, больше всего у <b>${topHost?topHost[0]:"—"}</b> (${topHost?topHost[1]:0}). Частая ошибка <b>ID ${topId?topId[0]:"—"}</b> ×${topId?topId[1]:0}.<br><b>Что делать:</b> ${todo} Для Critical — <code>sfc /scannow</code> и проверка диска.`;
}
function buildCorr(){
  const el=document.getElementById("corr"); if(!el) return;
  // find pairs that co-occur within 5 min on same host
  const pairs={};
  const sorted=[...DATA_SRC].sort((a,b)=>new Date(a.time)-new Date(b.time));
  for(let i=0;i<sorted.length-1;i++){
    for(let j=i+1;j<sorted.length && j<i+5;j++){
      if(sorted[i].computer===sorted[j].computer && Math.abs(new Date(sorted[i].time)-new Date(sorted[j].time))<5*60*1000 && sorted[i].eventId!==sorted[j].eventId){
        const key=[sorted[i].eventId, sorted[j].eventId].sort().join("↔");
        pairs[key]=(pairs[key]||0)+1;
      }
    }
  }
  const top=Object.entries(pairs).sort((a,b)=>b[1]-a[1]).slice(0,3);
  if(!top.length){ el.textContent="Корреляций не найдено — события разрознены."; return; }
  el.innerHTML=top.map(([k,c])=>`• <b>${k}</b> — ${c} раз вместе`).join("<br>");
}
let lastCritCount = DATA_SRC.filter(e=>e.level==="Critical").length;
let autoRefreshId=null;
function startAutoRefresh(){
  if(autoRefreshId) clearInterval(autoRefreshId);
  autoRefreshId=setInterval(()=>{
    const prevCrit=lastCritCount;
    // In real hosting, would fetch new JSON via fetch('data/dashboard_events.json')
    // For file://, just re-apply to simulate
    apply();
    const newCrit=DATA_SRC.filter(e=>e.level==="Critical").length;
    if(newCrit>prevCrit){
      lastCritCount=newCrit;
      // sound on new Critical
      try{
        const ctx=new (window.AudioContext||window.webkitAudioContext)();
        const o=ctx.createOscillator(); const g=ctx.createGain();
        o.type='sine'; o.frequency.value=880; o.connect(g); g.connect(ctx.destination);
        g.gain.setValueAtTime(0.12, ctx.currentTime);
        o.start(); g.gain.exponentialRampToValueAtTime(0.001, ctx.currentTime+0.25);
        setTimeout(()=>{o.stop(); ctx.close();}, 300);
      }catch(e){}
      // flash LIVE
      const liveEl=document.querySelector('.live'); if(liveEl){ liveEl.style.transform='scale(1.05)'; setTimeout(()=>liveEl.style.transform='', 300); }
    } else {
      lastCritCount=newCrit;
    }
    // update LIVE indicator
    const bar=document.getElementById("bar"); if(bar){ bar.style.width="100%"; setTimeout(()=>bar.style.width="0", 600); }
  }, 30000);
}
function refreshBusy(){
  const bar=document.getElementById("bar"); bar.style.width="30%"; bar.style.transition="none";
  requestAnimationFrame(()=>{ bar.style.transition="width .7s ease"; bar.style.width="100%"; setTimeout(()=>{bar.style.width="0"; apply();}, 720);});
}
// wrap apply to also refresh analytics
const _origApply=apply;
apply=function(){
  _origApply();
  buildDonut(); buildAiSummary();
  // save filters to URL and localStorage
  try{
    const params=new URLSearchParams({level, period, host:selectedHost, q});
    history.replaceState(null,"","?"+params.toString());
    localStorage.setItem("wow2_filters", JSON.stringify({level, period, host:selectedHost}));
  }catch(e){}
}
function loadFilters(){
  try{
    const p=new URLSearchParams(location.search);
    if(p.get("level")) level=p.get("level");
    if(p.get("period")) period=p.get("period");
    if(p.get("host")) selectedHost=p.get("host");
    if(p.get("q")) q=p.get("q");
    const saved=JSON.parse(localStorage.getItem("wow2_filters")||"null");
    if(saved && !p.toString()){
      level=saved.level||level; period=saved.period||period; selectedHost=saved.host||selectedHost;
    }
    const qEl=document.getElementById("q"); if(qEl && q) qEl.value=q;
  }catch(e){}
}
function exportCsv(){
  const rows=DATA_SRC.filter(e=>{
    const t=new Date(e.time).getTime();
    const cutoff=Date.now() - (period==="30 мин"?30*60*1000:period==="1 час"?60*60*1000:period==="24 часа"?24*60*60*1000:2*24*60*60*1000);
    if(t<cutoff) return false;
    if(level!=="Все" && e.level!==level) return false;
    if(logFilter!=="Все" && (e.logName||"")!==logFilter) return false;
    if(selectedHost && e.computer!==selectedHost) return false;
    if(q && !((e.message||"").toLowerCase().includes(q) || (e.source||"").toLowerCase().includes(q))) return false;
    return true;
  });
  const header="time,level,source,eventId,computer,message\n";
  const csv=header+rows.map(e=>`"${e.time}","${e.level}","${e.source}",${e.eventId},"${e.computer}","${String(e.message).replace(/"/g,'""').replace(/\r?\n/g,' ')}"`).join("\n");
  const blob=new Blob([csv],{type:"text/csv;charset=utf-8;"});
  const url=URL.createObjectURL(blob);
  const a=document.createElement("a"); a.href=url; a.download="events.csv"; a.click(); URL.revokeObjectURL(url);
}
loadFilters();
buildSeg(); buildLogSeg(); buildHosts(); buildAutocomplete(); updateDatePicker(); apply(); startAutoRefresh();
if(!document.getElementById("exportBtn")){
  const controls=document.querySelector(".table-head .controls");
  if(controls){
    const btn=document.createElement("button"); btn.id="exportBtn"; btn.textContent="Экспорт CSV";
    btn.style.cssText="padding:7px 12px;border-radius:10px;border:1px solid var(--line);background:var(--card);font-weight:700;font-size:12px;cursor:pointer";
    btn.onclick=exportCsv; controls.appendChild(btn);
  }
}

/* === Zabbix Agent Dashboard Z === */
let zData=[];
let zSelectedHost="";
let zFilter="";
const zBookMap={};
if(Array.isArray(BOOK_RAW)){
  BOOK_RAW.forEach(e=>{
    const h=(e.Computer||e.host||"").toLowerCase();
    if(h){
      const ips=(e.IPs||e.ips||"").split(/[,;]/).map(s=>s.trim()).filter(Boolean);
      zBookMap[h]={ip:ips[0]||"", fio:e.FIO||e.fio||"", user:e.User||e.user||""};
    }
  });
}
function switchView(v){
  const ep=document.getElementById("eventsPanel");
  const zp=document.getElementById("zabbixPanel");
  const bE=document.getElementById("viewEvents");
  const bZ=document.getElementById("viewZabbix");
  if(v==="zabbix"){
    ep.style.display="none"; zp.style.display="block";
    bE.classList.remove("on"); bZ.classList.add("on");
    buildZabbixData();
  } else {
    ep.style.display="block"; zp.style.display="none";
    bZ.classList.remove("on"); bE.classList.add("on");
  }
}
function buildZabbixData(){
  if(!Array.isArray(ZABBIX_RAW)||!ZABBIX_RAW.length){ zData=[]; renderZHostBar(); renderZTable(); return; }
  const cutoff=Date.now()-2*24*60*60*1000;
  const recent=ZABBIX_RAW.filter(x=>new Date(x.time).getTime()>=cutoff);
  const map={};
  recent.forEach(x=>{ const k=(x.host||"").toLowerCase(); if(!map[k]||new Date(x.time)>new Date(map[k].time)) map[k]=x; });
  zData=Object.values(map).sort((a,b)=>(a.host||"").localeCompare(b.host||""));
  renderZHostBar(); renderZTable();
}
function renderZHostBar(){
  const bar=document.getElementById("zHostBar"); bar.innerHTML="";
  if(!zData.length){ bar.innerHTML='<span style="font-size:12px;color:var(--muted)">Нет хостов</span>'; return; }
  zData.forEach(d=>{
    const alive=d.agentPing&&(!d.error);
    const active=zSelectedHost===d.host;
    const b=document.createElement("button");
    b.style.cssText="padding:5px 12px;border-radius:10px;font-size:11px;font-weight:700;border:1px solid "+(active?"var(--olive)":"var(--line)")+";background:"+(active?"var(--olive)":alive?"var(--oliveL)":"var(--card)")+";color:"+(active?"#fff":alive?"var(--oliveD)":"var(--muted)")+";cursor:pointer;transition:all .15s";
    b.textContent=d.host;
    b.onclick=()=>{ zSelectedHost=active?"":d.host; renderZHostBar(); renderZTable(); };
    bar.appendChild(b);
  });
}
function zEsc(s){return String(s||"").replace(/&/g,"&amp;").replace(/</g,"&lt;").replace(/>/g,"&gt;").replace(/"/g,"&quot;")}
function renderZTable(){
  const tb=document.getElementById("zTbody"); tb.innerHTML="";
  const empty=document.getElementById("zEmpty");
  const status=document.getElementById("zStatus");
  let list=zData;
  if(zSelectedHost) list=list.filter(x=>x.host===zSelectedHost);
  if(zFilter) list=list.filter(x=>(x.host||"").toLowerCase().includes(zFilter.toLowerCase()));
  const total=zData.length, alive=zData.filter(x=>x.agentPing&&!x.error).length;
  if(status) status.textContent="Всего: "+total+" · OK: "+alive+" · ERR: "+(total-alive);
  if(!list.length){ empty.style.display="block"; return; }
  empty.style.display="none";
  const cpuW=80,cpuC=95,ramW=15,ramC=5,diskW=20,diskC=10,tempW=70,tempC=85;
  list.forEach(d=>{
    const bk=zBookMap[(d.host||"").toLowerCase()]||{};
    const ip=bk.ip||"";
    const agentOk=d.agentPing&&(!d.error);
    const cpuCol=d.cpuLoad>=cpuC?"var(--roseD)":d.cpuLoad>=cpuW?"#D97706":"var(--fg)";
    const ramCol=d.memPFree<=ramC?"var(--roseD)":d.memPFree<=ramW?"#D97706":"var(--fg)";
    const tempCol=d.tempC!=null?(d.tempC>=tempC?"var(--roseD)":d.tempC>=tempW?"#D97706":"var(--fg)"):"var(--muted)";
    let uptime="—";
    const _up=d.uptimeSec!=null?d.uptimeSec:d.uptime;
    if(_up>0){
      const dd=Math.floor(_up/86400), hh=Math.floor((_up%86400)/3600);
      uptime=dd>0?dd+"д "+hh+"ч":hh+"ч";
    }
    let disksHtml="—";
    if(d.disks&&d.disks.length){
      disksHtml=d.disks.map(function(dk){
        const dc=dk.pfree<=diskC?"var(--roseD)":dk.pfree<=diskW?"#D97706":"var(--fg)";
        const dbg=dk.pfree<=diskC?"var(--roseL)":dk.pfree<=diskW?"#FEF3C7":"var(--oliveL)";
        return '<span style="display:inline-block;margin:1px 3px;padding:2px 6px;border-radius:6px;font-size:10px;font-weight:700;color:'+dc+';background:'+dbg+'">'+zEsc(dk.mount)+' '+dk.pfree.toFixed(0)+'%</span>';
      }).join("");
    }
    const tr=document.createElement("tr");
    tr.style.borderBottom="1px solid #F0E8DD";
    tr.style.cursor="pointer";
    tr.onmouseenter=()=>tr.style.background="#FDF8EE";
    tr.onmouseleave=()=>tr.style.background="";
    tr.onclick=()=>openZDetail(d.host);
    tr.title="Открыть детально →";
    tr.innerHTML='<td style="padding:12px 14px;font-weight:700">'+zEsc(d.host)+'</td><td style="padding:12px 14px;color:var(--muted)">'+zEsc(ip)+'</td><td style="padding:12px 14px;text-align:center"><span style="padding:3px 8px;border-radius:10px;font-size:11px;font-weight:800;'+(agentOk?"background:var(--oliveL);color:var(--oliveD);border:1px solid #C8D9C6":"background:var(--roseL);color:var(--roseD);border:1px solid #E9C2BC")+'">'+(agentOk?"OK":"ERR")+'</span></td><td style="padding:12px 14px;text-align:right;font-weight:700;color:'+cpuCol+'">'+d.cpuLoad.toFixed(1)+'</td><td style="padding:12px 14px;text-align:right;font-weight:700;color:'+ramCol+'">'+d.memPFree.toFixed(1)+'</td><td style="padding:12px 14px;text-align:right;font-weight:700;color:'+tempCol+'">'+(d.tempC!=null?d.tempC.toFixed(0)+"°":"—")+'</td><td style="padding:12px 14px">'+disksHtml+'</td><td style="padding:12px 14px;color:var(--muted)">'+uptime+'</td><td style="padding:12px 14px;color:var(--muted)">'+zEsc(d.agentVersion||"")+'</td>';
    tb.appendChild(tr);
  });
}
/* === Z Detail Panel === */
function zFmtBytes(b){ if(b==null) return "—"; const u=["Б","КБ","МБ","ГБ","ТБ"]; let i=0, v=b; while(v>=1024&&i<u.length-1){v/=1024;i++;} return v.toFixed(i===0?0:i===1?0:1)+" "+u[i]; }
function zFmtBps(b){ if(!b) return "0"; if(b>=1e9) return (b/1e9).toFixed(1)+" Гбит/с"; if(b>=1e6) return (b/1e6).toFixed(1)+" Мбит/с"; if(b>=1e3) return (b/1e3).toFixed(0)+" Кбит/с"; return b+" бит/с"; }
function openZDetail(host){
  const rec = zData.find(x=>x.host===host) || ZABBIX_RAW.filter(x=>x.host===host).sort((a,b)=>new Date(b.time)-new Date(a.time))[0];
  if(!rec) return;
  const bk=zBookMap[(rec.host||"").toLowerCase()]||{};
  document.getElementById("zDHost").textContent=rec.host;
  document.getElementById("zDSub").textContent=(bk.ip||"")+(bk.fio?" · "+bk.fio:"")+" · "+new Date(rec.time).toLocaleString("ru-RU");
  const pill=document.getElementById("zDPill");
  const ok=rec.agentPing&&!rec.error;
  pill.textContent=ok?"● ONLINE":"● OFFLINE";
  pill.style.background=ok?"var(--oliveL)":"var(--roseL)"; pill.style.color=ok?"var(--oliveD)":"var(--roseD)"; pill.style.borderColor=ok?"#C8D9C6":"#E9C2BC";
  document.getElementById("zDIcon").style.background=ok?"var(--olive)":"var(--rose)"; document.getElementById("zDIcon").textContent=ok?"◉":"◎";
  // KPIs
  const cpuEl=document.getElementById("zDKpiCpu"); cpuEl.textContent=(rec.cpuLoad!=null?rec.cpuLoad.toFixed(1)+"%":"—");
  cpuEl.style.color=rec.cpuLoad>=95?"var(--roseD)":rec.cpuLoad>=80?"#D97706":"var(--fg)";
  document.querySelector("#zDKpiCpuBar div").style.width=Math.min(100,rec.cpuLoad||0)+"%";
  document.querySelector("#zDKpiCpuBar div").style.background=rec.cpuLoad>=95?"var(--rose)":rec.cpuLoad>=80?"#D97706":"var(--olive)";
  const ramEl=document.getElementById("zDKpiRam"); ramEl.textContent=(rec.memPFree!=null?rec.memPFree.toFixed(1)+"%":"—");
  ramEl.style.color=rec.memPFree<=5?"var(--roseD)":rec.memPFree<=15?"#D97706":"var(--fg)";
  document.querySelector("#zDKpiRamBar div").style.width=(100-(rec.memPFree||0))+"%";
  document.querySelector("#zDKpiRamBar div").style.background=rec.memPFree<=5?"var(--rose)":rec.memPFree<=15?"#D97706":"var(--olive)";
  const _up=rec.uptimeSec!=null?rec.uptimeSec:rec.uptime;
  let upTxt="—", upSub="";
  if(_up>0){ const d=Math.floor(_up/86400), h=Math.floor((_up%86400)/3600), m=Math.floor((_up%3600)/60); upTxt=d>0?d+"д "+h+"ч":h>0?h+"ч "+m+"м":m+"м"; upSub="с "+new Date(Date.now()-_up*1000).toLocaleDateString("ru-RU"); }
  document.getElementById("zDKpiUp").textContent=upTxt; document.getElementById("zDKpiUpSub").textContent=upSub;
  document.getElementById("zDKpiVer").textContent=rec.agentVersion||"—";
  document.getElementById("zDKpiVerSub").textContent=rec.error?("⚠ "+rec.error): (rec.agentPing?"Агент отвечает":"Нет связи");
  document.getElementById("zDKpiVerSub").style.color=rec.error?"var(--roseD)":"var(--muted)";
  // Disks
  const dDiv=document.getElementById("zDDisks"); dDiv.innerHTML="";
  if(rec.disks&&rec.disks.length){
    rec.disks.forEach(dk=>{
      const pct=dk.pfree!=null?dk.pfree:(dk.total?dk.free/dk.total*100:0);
      const usedPct=100-pct;
      const col=pct<=10?"var(--rose)":pct<=20?"#D97706":"var(--olive)";
      const bg=pct<=10?"var(--roseL)":pct<=20?"#FEF3C7":"var(--oliveL)";
      const row=document.createElement("div");
      row.style.cssText="padding:8px 10px;border:1px solid var(--line);border-radius:10px;background:var(--card)";
      row.innerHTML='<div style="display:flex;justify-content:space-between;align-items:center"><b style="font-size:12px">'+zEsc(dk.mount)+'</b><span style="font-size:11px;font-weight:800;color:'+(pct<=10?"var(--roseD)":pct<=20?"#92400E":"var(--oliveD)")+'">'+pct.toFixed(1)+'% свободно</span></div><div style="height:6px;border-radius:10px;background:var(--bg);margin-top:6px;overflow:hidden"><div style="height:100%;width:'+usedPct.toFixed(1)+'%;background:'+col+'"></div></div><div class="mono" style="display:flex;justify-content:space-between;font-size:11px;color:var(--muted);margin-top:4px"><span>'+zFmtBytes(dk.free)+' свободно</span><span>'+zFmtBytes(dk.total)+' всего</span></div>';
      dDiv.appendChild(row);
    });
  } else dDiv.innerHTML='<div class="mono" style="font-size:12px;color:var(--muted);padding:8px">Нет данных о дисках</div>';
  // Network
  const nDiv=document.getElementById("zDNet"); nDiv.innerHTML="";
  const netInfo=document.getElementById("zDNetInfo");
  let nets=(rec.netIfs||[]).filter(n=>n.totalBps>0).sort((a,b)=>b.totalBps-a.totalBps).slice(0,4);
  // fallback: if totalBps are huge cumulative, try to pick the real adapter (non-virtual)
  if(!nets.length && rec.netIfs) nets=rec.netIfs.filter(n=>!/Loopback|ISATAP|Kernel|WFP|QoS/.test(n.name)).slice(0,3);
  netInfo.textContent=nets.length?nets.length+" интерфейса":"нет трафика";
  if(nets.length){
    const maxBps=Math.max(...nets.map(n=>n.totalBps),1);
    nets.forEach(n=>{
      const short=n.name.replace("Microsoft ","").replace(" Hyper-V "," ").replace(" Network Adapter","").trim().slice(0,34);
      const pct=Math.max(4, Math.round(n.totalBps/maxBps*100));
      const row=document.createElement("div");
      row.style.cssText="padding:6px 8px;border:1px solid var(--line);border-radius:10px;background:var(--bg);display:flex;align-items:center;gap:10px";
      row.innerHTML='<div style="flex:1;min-width:0"><div style="font-size:11px;font-weight:700;white-space:nowrap;overflow:hidden;text-overflow:ellipsis">'+zEsc(short)+'</div><div style="height:4px;border-radius:10px;background:var(--card);margin-top:4px;overflow:hidden"><div style="height:100%;width:'+pct+'%;background:var(--olive)"></div></div></div><div class="mono" style="text-align:right;flex-shrink:0"><div style="font-size:11px;font-weight:800">'+zFmtBps(n.totalBps)+'</div><div style="font-size:10px;color:var(--muted)">↓ '+zFmtBps(n.inBps)+' ↑ '+zFmtBps(n.outBps)+'</div></div>';
      nDiv.appendChild(row);
    });
  } else nDiv.innerHTML='<div class="mono" style="font-size:12px;color:var(--muted);padding:6px">Нет активных интерфейсов</div>';
  // Services
  const sDiv=document.getElementById("zDSvc"); sDiv.innerHTML="";
  const sInfo=document.getElementById("zDSvcInfo");
  const svcs=rec.services||[];
  const crit=svcs.filter(s=>s.isCritical);
  const bad=crit.filter(s=>String(s.state)!=="0" && String(s.state)!=="4");
  sInfo.textContent=crit.length? (bad.length? "⚠ "+bad.length+" проблема":"✓ все OK") : "нет данных";
  sInfo.style.color=bad.length?"var(--roseD)":"var(--muted)";
  if(crit.length){
    crit.slice(0,24).forEach(s=>{
      const st=String(s.state);
      const isOk=st==="0"||st==="4";
      const span=document.createElement("span");
      span.style.cssText="padding:4px 8px;border-radius:10px;font-size:11px;font-weight:700;border:1px solid "+(isOk?"#C8D9C6":"#E9C2BC")+";background:"+(isOk?"var(--oliveL)":"var(--roseL)")+";color:"+(isOk?"var(--oliveD)":"var(--roseD)")+";display:inline-flex;align-items:center;gap:4px";
      span.innerHTML=(isOk?"●":"■")+" "+zEsc(s.name);
      span.title=(s.displayName||s.name)+" · state="+st+" startup="+s.startup;
      sDiv.appendChild(span);
    });
    if(crit.length>24){ const more=document.createElement("span"); more.className="mono"; more.style.cssText="padding:4px 8px;font-size:11px;color:var(--muted)"; more.textContent="+"+(crit.length-24)+" ещё"; sDiv.appendChild(more); }
  } else sDiv.innerHTML='<span class="mono" style="font-size:12px;color:var(--muted)">Службы не отслеживаются</span>';
  // History sparkline for this host
  const hist=ZABBIX_RAW.filter(x=>x.host===host && new Date(x.time).getTime()>=Date.now()-2*24*60*60*1000).sort((a,b)=>new Date(a.time)-new Date(b.time));
  document.getElementById("zDHistInfo").textContent=hist.length+" точек";
  const cpuHist=document.getElementById("zDHistCpu"); cpuHist.innerHTML="";
  const ramHist=document.getElementById("zDHistRam"); ramHist.innerHTML="";
  if(hist.length>1){
    const cpuMax=Math.max(...hist.map(h=>h.cpuLoad||0),1);
    const ramMax=100;
    hist.slice(-20).forEach(h=>{
      const cH=Math.max(3, Math.round((h.cpuLoad||0)/cpuMax*100));
      const rH=Math.max(3, Math.round((h.memPFree||0)/ramMax*100));
      const cBar=document.createElement("div"); cBar.style.cssText="flex:1;height:"+cH+"%;background:"+(h.cpuLoad>=80?"var(--rose)":h.cpuLoad>=50?"#D97706":"var(--olive)")+";border-radius:6px;opacity:.9"; cBar.title=new Date(h.time).toLocaleString()+" CPU "+(h.cpuLoad||0).toFixed(1)+"%"; cpuHist.appendChild(cBar);
      const rBar=document.createElement("div"); rBar.style.cssText="flex:1;height:"+rH+"%;background:"+(h.memPFree<=10?"var(--rose)":h.memPFree<=20?"#D97706":"var(--olive)")+";border-radius:6px;opacity:.9"; rBar.title=new Date(h.time).toLocaleString()+" RAM free "+(h.memPFree||0).toFixed(1)+"%"; ramHist.appendChild(rBar);
    });
  } else {
    cpuHist.innerHTML='<span class="mono" style="font-size:11px;color:var(--muted)">мало данных</span>';
    ramHist.innerHTML='<span class="mono" style="font-size:11px;color:var(--muted)">мало данных</span>';
  }
  document.getElementById("zDetailOverlay").style.display="block";
  document.getElementById("zDetail").style.transform="translateX(0)";
  document.body.style.overflow="hidden";
}
function closeZDetail(){
  document.getElementById("zDetail").style.transform="translateX(100%)";
  document.getElementById("zDetailOverlay").style.display="none";
  document.body.style.overflow="";
}
document.addEventListener("keydown",e=>{ if(e.key==="Escape") closeZDetail(); });
document.getElementById("zSearch").addEventListener("input",function(e){ zFilter=e.target.value; renderZTable(); });
</script>

<div id="msgModal" style="display:none;position:fixed;inset:0;z-index:50;align-items:center;justify-content:center;padding:20px">
  <div style="position:absolute;inset:0;background:rgba(0,0,0,.4);backdrop-filter:blur(4px)" onclick="closeModal()"></div>
  <div style="position:relative;background:var(--card);border:1px solid var(--line);border-radius:10px;max-width:720px;width:100%;max-height:80vh;overflow:auto;box-shadow:0 20px 40px -12px rgba(0,0,0,.25)">
    <div style="padding:16px 16px 12px;border-bottom:1px solid var(--line);display:flex;align-items:center;justify-content:space-between">
      <b style="font-size:13px">Сообщение</b>
      <button onclick="closeModal()" style="width:28px;height:28px;border-radius:10px;border:1px solid var(--line);background:var(--bg);cursor:pointer">✕</button>
    </div>
    <div id="modalText" style="padding:16px;white-space:pre-wrap;word-break:break-word;font-size:13px;line-height:1.5;color:var(--fg)"></div>
    <details style="margin:0 16px 12px;border:1px solid var(--line);border-radius:10px;overflow:hidden"><summary class="mono" style="padding:8px 12px;background:var(--bg);cursor:pointer;font-size:11px;font-weight:700">Сырой XML / JSON</summary><pre id="modalRaw" style="padding:12px;white-space:pre-wrap;word-break:break-word;font-size:11px;max-height:240px;overflow:auto;background:var(--card)"></pre></details>
    <div style="padding:0 16px 12px;display:flex;gap:8px">
      <button onclick="openHostCard(currentModalEvent.computer)" style="padding:6px 10px;border-radius:10px;border:1px solid var(--line);background:var(--bg);font-size:12px;font-weight:700;cursor:pointer">Открыть хост в PupSeek</button>
      <button onclick="window.open('eventvwr.msc','_blank')" style="padding:6px 10px;border-radius:10px;border:1px solid var(--line);background:var(--bg);font-size:12px;font-weight:700;cursor:pointer">Открыть eventvwr.msc</button>
    </div>
    <div style="padding:12px 16px;border-top:1px solid var(--line);display:flex;gap:8px;justify-content:flex-end">
      <button onclick="translateModal()" style="padding:7px 12px;border-radius:10px;border:1px solid var(--line);background:var(--bg);font-size:12px;font-weight:700;cursor:pointer">RU</button>
      <button onclick="navigator.clipboard.writeText(document.getElementById('modalText').textContent)" style="padding:7px 12px;border-radius:10px;border:1px solid var(--line);background:var(--bg);font-size:12px;font-weight:700;cursor:pointer">Копировать</button>
      <button onclick="closeModal()" style="padding:7px 12px;border-radius:10px;background:var(--olive);color:#fff;font-weight:700;font-size:12px;border:1px solid var(--olive);cursor:pointer">Закрыть</button>
    </div>
  </div>
</div>
<script>
let originalModalText="";
function openModal(text){
  originalModalText=text;
  document.getElementById('modalText').textContent=text;
  document.getElementById('msgModal').style.display='flex';
  // Reset RU button
  const ruBtn=document.querySelector('#msgModal button[onclick="translateModal()"]');
  if(ruBtn) ruBtn.textContent="RU";
}
function translateModal(){
  const el=document.getElementById('modalText');
  const btn=document.querySelector('#msgModal button[onclick="translateModal()"]');
  if(el.textContent===originalModalText){
    // Simple mock translation: just add prefix and keep original (in real app, would call API)
    el.textContent="[Перевод] " + originalModalText;
    if(btn) btn.textContent="EN";
  } else {
    el.textContent=originalModalText;
    if(btn) btn.textContent="RU";
  }
}
function openDocs(eventId){
  window.open(`https://learn.microsoft.com/en-us/search/?terms=Event+ID+${eventId}`, '_blank');
}
function closeModal(){ document.getElementById('msgModal').style.display='none'; }
document.addEventListener('keydown', e=>{ if(e.key==='Escape') closeModal(); });
</script>

</body>
</html>
