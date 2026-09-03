<?php
/**
 * /monitor/index.php — вход по паролю (сессия PHP).
 */
require __DIR__ . '/config.php';

session_name($SESSION_NAME);
session_start();

$err = '';

// Уже вошли? Перенаправляем на дашборд.
if (isset($_SESSION['auth']) && $_SESSION['auth'] === true) {
    header('Location: dashboard.php');
    exit;
}

if ($_SERVER['REQUEST_METHOD'] === 'POST') {
    $pass = isset($_POST['pass']) ? (string)$_POST['pass'] : '';
    if ($PASSWORD_HASH === '') {
        $err = 'Пароль ещё не задан. Откройте hash.php на сервере и настройте config.php.';
    } elseif (verify_password_hash($PASSWORD_HASH, $pass)) {
        session_regenerate_id(true);
        $_SESSION['auth'] = true;
        $_SESSION['ts'] = time();
        header('Location: dashboard.php');
        exit;
    } else {
        $err = 'Неверный пароль.';
    }
}
?>
<!DOCTYPE html>
<html lang="ru">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta name="robots" content="noindex, nofollow">
<title>Вход — Дашборд событий</title>
<style>
  * { box-sizing: border-box; }
  body { margin:0; font-family:'Segoe UI',Tahoma,sans-serif; background:#E8E4D2;
         display:flex; align-items:center; justify-content:center; min-height:100vh; }
  .card { background:#F2EFE0; border:1px solid #E2DDC9; border-radius:10px;
          padding:32px 34px; width:100%; max-width:340px; box-shadow:0 8px 30px rgba(0,0,0,.15); }
  .card h1 { font-size:18px; color:#2B2A20; margin:0 0 4px; }
  .card .sub { font-size:12.5px; color:#7E7A5E; margin-bottom:22px; }
  label { font-size:12.5px; color:#2B2A20; }
  input[type=password] { width:100%; padding:11px 12px; font-size:15px; margin:8px 0 16px;
        border:1px solid #D4CFBB; border-radius:7px; outline:none; background:#fff; }
  input[type=password]:focus { border-color:#764E4C; }
  button { width:100%; padding:12px; font-size:15px; font-weight:600; color:#fff;
        background:#764E4C; border:none; border-radius:7px; cursor:pointer; }
  button:hover { filter:brightness(.93); }
  .err { color:#A85A4A; font-size:13px; margin-top:14px; min-height:16px; }
</style>
</head>
<body>
  <form class="card" method="post">
    <h1>Дашборд событий</h1>
    <div class="sub">Введите пароль для просмотра</div>
    <label for="pass">Пароль</label>
    <input type="password" id="pass" name="pass" autofocus autocomplete="current-password">
    <button type="submit">Войти</button>
    <div class="err"><?php echo htmlspecialchars($err); ?></div>
  </form>
</body>
</html>
