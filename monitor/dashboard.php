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

// Чтение данных
$events = array();
if (is_file($JSON_PATH)) {
    $raw = @file_get_contents($JSON_PATH);
    if ($raw !== false) {
        $decoded = json_decode($raw, true);
        if (is_array($decoded)) $events = $decoded;
    }
}
$eventsJson = json_encode($events, JSON_UNESCAPED_UNICODE | JSON_INVALID_UTF8_SUBSTITUTE);
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
  .page{max-width:none;width:100%;margin:0;padding:24px 12px 40px}
  .hdr{display:flex;align-items:center;gap:14px;padding:14px 16px;background:var(--card);border:1px solid var(--line);border-radius:10px;box-shadow:0 6px 18px -12px rgba(0,0,0,.1)}
  .mark{width:42px;height:42px;border-radius:10px;display:grid;place-items:center;color:#fff;font-weight:900;background:linear-gradient(135deg,var(--olive),var(--oliveD));box-shadow:0 6px 14px -8px rgba(111,138,107,.5)}
  .ttl{font-size:18px;font-weight:900;letter-spacing:-.02em}
  .sub{font-size:12px;color:var(--muted);margin-top:2px}
  .live{display:inline-flex;align-items:center;gap:6px;padding:6px 10px;border-radius:10px;font-size:11px;font-weight:800;letter-spacing:.04em;background:var(--oliveL);color:var(--oliveD);border:1px solid #C8D9C6}
  .live i{width:7px;height:7px;border-radius:50%;background:var(--olive);display:inline-block;animation:pulse 1.6s infinite}
  @keyframes pulse{0%,100%{box-shadow:0 0 0 0 rgba(143,169,139,.45)}50%{box-shadow:0 0 0 6px rgba(143,169,139,0)}}
  .bento{display:grid;grid-template-columns:1.15fr .85fr;gap:14px}
  @media(max-width:900px){.bento{grid-template-columns:1fr}}
  .card{background:var(--card);border:1px solid var(--line);border-radius:10px;box-shadow:0 6px 18px -12px rgba(0,0,0,.1);overflow:hidden}
  .card-h{padding:14px 16px;border-bottom:1px solid var(--line);display:flex;align-items:center;justify-content:space-between;background:var(--card)}
  .card-h b{font-size:12px;letter-spacing:.06em;text-transform:uppercase;color:var(--muted)}
  .kpi{padding:16px;display:grid;grid-template-columns:repeat(3,1fr);gap:12px}
  @media(max-width:640px){.kpi{grid-template-columns:1fr}}
  .kpi-item{padding:14px;border-radius:10px;border:1px solid var(--line);background:var(--bg)}
  .kpi-item.dark{background:var(--fg);color:var(--card);border-color:var(--fg)}
  .kpi-num{font-size:28px;font-weight:900;letter-spacing:-.03em}
  .kpi-lab{font-size:11px;font-weight:700;letter-spacing:.06em;text-transform:uppercase;opacity:.7;margin-top:2px}
  .hosts{display:grid;grid-template-columns:repeat(3,1fr);gap:12px;margin-top:14px}
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
      <span class="mono" style="padding:7px 10px;border-radius:10px;background:var(--oliveL);border:1px solid #C8D9C6;color:var(--oliveD);font-size:11px;font-weight:800;display:inline-flex;align-items:center;gap:6px"><span style="width:7px;height:7px;border-radius:50%;background:var(--olive);display:inline-block"></span> LIVE</span>
      <button onclick="refreshBusy()" style="padding:9px 14px;border-radius:10px;background:var(--olive);color:#fff;font-weight:800;font-size:12px;border:1px solid var(--olive);cursor:pointer">Обновить</button>
    </div>
  </div>
  <div id="bar" class="bar" style="margin-top:10px"></div>

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
  <div class="analytics-row" style="display:grid;grid-template-columns:1fr 1fr;gap:14px;margin-top:14px">
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

  <div class="card" style="margin-top:14px;overflow:hidden">
    <div class="table-head">
      <div class="controls">
        <div class="seg" id="levelSeg"></div>
        <div class="seg" id="periodSeg"></div>
      </div>
      <div class="search">
        <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><circle cx="11" cy="11" r="8"/><path d="m21 21-4.3-4.3"/></svg>
        <input id="q" placeholder="Поиск по сообщению, источнику, ID, хосту…">
      </div>
    </div>
    <div style="overflow:auto;max-height:62vh" class="scrollbar-thin">
      <table style="width:100%;border-collapse:separate;border-spacing:0;table-layout:fixed">
        <colgroup>
          <col style="width:108px">
          <col style="width:110px">
          <col style="width:380px">
          <col style="width:72px">
          <col style="width:148px">
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


</div>

<script>
const FALLBACK = <?php echo $eventsJson; ?>;
let DATA_SRC = FALLBACK;
let level="Все", period="2 дня", selectedHost="", q="";
function hostCounts(){const m={}; DATA_SRC.forEach(e=>{m[e.computer]=(m[e.computer]||0)+1}); return m}
function buildSeg(){
  const lv=document.getElementById("levelSeg"); lv.innerHTML="";
  [["Все","Все"],["Critical","Critical"],["Error","Error"]].forEach(([l,v])=>{
    const b=document.createElement("button"); b.textContent=l;
    const on=level===v;
    b.style.cssText="padding:6px 12px;border-radius:10px;font-size:12px;font-weight:800;border:1px solid "+(on?"var(--olive)":"var(--line)")+";background:"+(on?"var(--olive)":"var(--card)")+";color:"+(on?"#fff":"var(--muted)")+";cursor:pointer";
    b.onclick=()=>{level=v; buildSeg(); apply();}; lv.appendChild(b);
  });
  const pp=document.getElementById("periodSeg"); pp.innerHTML="";
  ["30 мин","1 час","24 часа","2 дня"].forEach(p=>{
    const b=document.createElement("button"); b.textContent=p;
    const on=period===p;
    b.style.cssText="padding:6px 12px;border-radius:10px;font-size:12px;font-weight:800;border:1px solid "+(on?"var(--fg)":"var(--line)")+";background:"+(on?"var(--fg)":"var(--card)")+";color:"+(on?"#fff":"var(--muted)")+";cursor:pointer";
    b.onclick=()=>{period=p; buildSeg(); apply();}; pp.appendChild(b);
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
function cutoffFor(p){ if(p==="30 мин")return 30*60*1000; if(p==="1 час")return 60*60*1000; if(p==="24 часа")return 24*60*60*1000; return 2*24*60*60*1000; }
function esc(s){return String(s||"").replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]))}
function apply(){
  const nowMs=Date.now(), cutoff=nowMs - cutoffFor(period);
  let rows=DATA_SRC.filter(e=>{
    const t=new Date(e.time).getTime();
    if(t<cutoff) return false;
    if(level!=="Все" && e.level!==level) return false;
    if(selectedHost && e.computer!==selectedHost) return false;
    if(q && !((e.message||"").toLowerCase().includes(q) || (e.source||"").toLowerCase().includes(q) || String(e.eventId).includes(q) || (e.computer||"").toLowerCase().includes(q))) return false;
    return true;
  });
  rows.sort((a,b)=>new Date(b.time)-new Date(a.time)); rows=rows.slice(0,500);
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
    tr.innerHTML=`<td class="mono" style="color:var(--muted);white-space:nowrap">${ts}</td><td>${lvl}</td><td class="hidden lg:table-cell mono" style="color:var(--muted);max-width:340px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap" title="${esc(e.source)}">${esc(e.source)}</td><td class="mono" style="font-weight:800">${esc(e.eventId)}</td><td><span class="host-tag">${esc(e.computer)}</span></td><td><span style="display:-webkit-box;-webkit-line-clamp:2;-webkit-box-orient:vertical;overflow:hidden;white-space:normal;word-break:break-word;line-height:1.35;width:100%;max-width:100%" title="${esc(e.message)}">${esc(e.message)}</span></td><td style="text-align:right"><button class="msgBtn" style="width:28px;height:28px;border-radius:10px;border:1px solid var(--line);background:var(--bg);cursor:pointer;display:grid;place-items:center">›</button></td>`;
    tr.querySelector('.msgBtn').onclick=()=>openModal(e.message);
    [...tr.children].forEach(td=>{td.style.padding="11px 12px"; td.style.overflow="hidden";});
    tr.style.borderBottom="1px solid #F0E8DD";
    tr.onmouseenter=()=>tr.style.background="#FDF8EE";
    tr.onmouseleave=()=>tr.style.background="transparent";
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
  el.innerHTML=`За 2 дня <b>${total}</b> событий, больше всего у <b>${topHost?topHost[0]:"—"}</b> (${topHost?topHost[1]:0}). Частая ошибка <b>ID ${topId?topId[0]:"—"}</b> ×${topId?topId[1]:0} — проверьте службу Agentless Management и DNS. Critical нет — стабильно.`;
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
buildSeg(); buildHosts(); apply();
if(!document.getElementById("exportBtn")){
  const controls=document.querySelector(".table-head .controls");
  if(controls){
    const btn=document.createElement("button"); btn.id="exportBtn"; btn.textContent="Экспорт CSV";
    btn.style.cssText="padding:7px 12px;border-radius:10px;border:1px solid var(--line);background:var(--card);font-weight:700;font-size:12px;cursor:pointer";
    btn.onclick=exportCsv; controls.appendChild(btn);
  }
}
</script>

<div id="msgModal" style="display:none;position:fixed;inset:0;z-index:50;align-items:center;justify-content:center;padding:20px">
  <div style="position:absolute;inset:0;background:rgba(0,0,0,.4);backdrop-filter:blur(4px)" onclick="closeModal()"></div>
  <div style="position:relative;background:var(--card);border:1px solid var(--line);border-radius:10px;max-width:720px;width:100%;max-height:80vh;overflow:auto;box-shadow:0 20px 40px -12px rgba(0,0,0,.25)">
    <div style="padding:16px 16px 12px;border-bottom:1px solid var(--line);display:flex;align-items:center;justify-content:space-between">
      <b style="font-size:13px">Сообщение</b>
      <button onclick="closeModal()" style="width:28px;height:28px;border-radius:10px;border:1px solid var(--line);background:var(--bg);cursor:pointer">✕</button>
    </div>
    <div id="modalText" style="padding:16px;white-space:pre-wrap;word-break:break-word;font-size:13px;line-height:1.5;color:var(--fg)"></div>
    <div style="padding:12px 16px;border-top:1px solid var(--line);display:flex;gap:8px;justify-content:flex-end">
      <button onclick="navigator.clipboard.writeText(document.getElementById('modalText').textContent)" style="padding:7px 12px;border-radius:10px;border:1px solid var(--line);background:var(--bg);font-size:12px;font-weight:700;cursor:pointer">Копировать</button>
      <button onclick="closeModal()" style="padding:7px 12px;border-radius:10px;background:var(--olive);color:#fff;font-weight:700;font-size:12px;border:1px solid var(--olive);cursor:pointer">Закрыть</button>
    </div>
  </div>
</div>
<script>
function openModal(text){ document.getElementById('modalText').textContent=text; document.getElementById('msgModal').style.display='flex'; }
function closeModal(){ document.getElementById('msgModal').style.display='none'; }
document.addEventListener('keydown', e=>{ if(e.key==='Escape') closeModal(); });
</script>

</body>
</html>
