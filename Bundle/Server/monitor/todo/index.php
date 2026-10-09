<?php
/**
 * /monitor/todo/index.php — бэкенд для TODO/шня (бесплатно, без БД)
 * Отдельная папка monitor/todo/ — не смешивается с дашбордом.
 * GET  -> JSON массив задач
 * GET?since=2026-01-01T00:00:00 -> только новые
 * POST {"id":"...","text":"...","source":"voice","createdAt":"..."} -> добавление
 * DELETE?id=... -> удаление (или POST {"action":"delete","id":"..."} для хостингов без DELETE)
 *
 * Положи на pupseek.ru/monitor/todo/index.php, data/todo.json создастся сам.
 * URL для телефона/десктопа: https://pupseek.ru/monitor/todo/
 * CORS открыт. Токен опционально в ../config.php: $TODO_TOKEN = 'secret123';
 */
header('Access-Control-Allow-Origin: *');
header('Access-Control-Allow-Methods: GET, POST, DELETE, OPTIONS');
header('Access-Control-Allow-Headers: Authorization, Content-Type');
header('Content-Type: application/json; charset=utf-8');
if ($_SERVER['REQUEST_METHOD'] === 'OPTIONS') { http_response_code(204); exit; }

$parentConfig = __DIR__ . '/../config.php';
if (file_exists($parentConfig)) require $parentConfig;
$DATA = __DIR__ . '/data/todo.json';
if (!is_dir(__DIR__.'/data')) mkdir(__DIR__.'/data', 0755, true);
if (!file_exists($DATA)) file_put_contents($DATA, '[]');

$TODO_TOKEN = isset($TODO_TOKEN) ? $TODO_TOKEN : '';
if ($TODO_TOKEN !== '' && in_array($_SERVER['REQUEST_METHOD'], ['POST', 'DELETE'])) {
    $auth = $_SERVER['HTTP_AUTHORIZATION'] ?? '';
    if ($auth !== 'Bearer ' . $TODO_TOKEN) {
        http_response_code(401);
        echo json_encode(['error'=>'unauthorized'], JSON_UNESCAPED_UNICODE);
        exit;
    }
}

if ($_SERVER['REQUEST_METHOD'] === 'GET') {
    $raw = @file_get_contents($DATA);
    $list = json_decode($raw, true);
    if (!is_array($list)) $list = [];
    $since = $_GET['since'] ?? '';
    if ($since !== '') {
        $ts = strtotime($since);
        if ($ts !== false) {
            $list = array_values(array_filter($list, function($it) use ($ts) {
                $t = strtotime($it['createdAt'] ?? '1970-01-01');
                return $t !== false && $t > $ts;
            }));
        }
    }
    if (count($list) > 500) $list = array_slice($list, -500);
    echo json_encode($list, JSON_UNESCAPED_UNICODE|JSON_UNESCAPED_SLASHES);
    exit;
}

if ($_SERVER['REQUEST_METHOD'] === 'DELETE') {
    $id = $_GET['id'] ?? '';
    if ($id === '') { http_response_code(400); echo json_encode(['error'=>'id required'], JSON_UNESCAPED_UNICODE); exit; }
    $raw = @file_get_contents($DATA);
    $list = json_decode($raw, true);
    if (!is_array($list)) $list = [];
    $before = count($list);
    $list = array_values(array_filter($list, function($it) use ($id) { return ($it['id'] ?? '') !== $id; }));
    $deleted = $before - count($list);
    if ($deleted > 0) file_put_contents($DATA, json_encode($list, JSON_UNESCAPED_UNICODE|JSON_PRETTY_PRINT|JSON_UNESCAPED_SLASHES));
    echo json_encode(['ok'=>true,'deleted'=>$deleted], JSON_UNESCAPED_UNICODE);
    exit;
}

if ($_SERVER['REQUEST_METHOD'] === 'POST') {
    $body = file_get_contents('php://input');
    $in = json_decode($body, true);
    if (!is_array($in)) $in = [];
    // Fallback для хостингов без DELETE: POST {"action":"delete","id":"..."}
    if (($in['action'] ?? '') === 'delete' && isset($in['id'])) {
        $id = (string)$in['id'];
        $raw = @file_get_contents($DATA);
        $list = json_decode($raw, true);
        if (!is_array($list)) $list = [];
        $before = count($list);
        $list = array_values(array_filter($list, function($it) use ($id) { return ($it['id'] ?? '') !== $id; }));
        $deleted = $before - count($list);
        if ($deleted > 0) file_put_contents($DATA, json_encode($list, JSON_UNESCAPED_UNICODE|JSON_PRETTY_PRINT|JSON_UNESCAPED_SLASHES));
        echo json_encode(['ok'=>true,'deleted'=>$deleted], JSON_UNESCAPED_UNICODE);
        exit;
    }
    $items = isset($in['text']) ? [$in] : (array_values($in) === $in ? $in : [$in]);
    $raw = @file_get_contents($DATA);
    $list = json_decode($raw, true);
    if (!is_array($list)) $list = [];
    $ids = array_column($list, 'id');
    $added = 0;
    foreach ($items as $it) {
        $text = trim($it['text'] ?? '');
        if ($text === '') continue;
        $id = $it['id'] ?? bin2hex(random_bytes(8));
        if (in_array($id, $ids, true)) continue;
        $list[] = [
            'id' => $id,
            'text' => $text,
            'done' => (bool)($it['done'] ?? false),
            'status' => (int)($it['status'] ?? 0),
            'createdAt' => $it['createdAt'] ?? date('c'),
            'dueAt' => $it['dueAt'] ?? null,
            'source' => $it['source'] ?? 'voice',
        ];
        $ids[] = $id;
        $added++;
    }
    if ($added > 0) {
        usort($list, function($a,$b){ return strcmp($b['createdAt']??'', $a['createdAt']??''); });
        if (count($list) > 2000) $list = array_slice($list, 0, 2000);
        file_put_contents($DATA, json_encode($list, JSON_UNESCAPED_UNICODE|JSON_PRETTY_PRINT|JSON_UNESCAPED_SLASHES));
    }
    echo json_encode(['ok'=>true,'added'=>$added], JSON_UNESCAPED_UNICODE);
    exit;
}

http_response_code(405);
echo json_encode(['error'=>'method'], JSON_UNESCAPED_UNICODE);
