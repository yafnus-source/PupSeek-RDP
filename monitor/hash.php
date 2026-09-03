<?php
/**
 * /monitor/hash.php — одноразовый помощник.
 * Открой в браузере, введи пароль, получи $PASSWORD_HASH для config.php.
 * ПОСЛЕ использования удали этот файл с сервера.
 */
require __DIR__ . '/config.php';
if (!function_exists('password_hash')) {
    exit('Требуется PHP >= 5.5 (password_hash). Свяжитесь с хостингом.');
}
$out = '';
if ($_SERVER['REQUEST_METHOD'] === 'POST') {
    $pass = isset($_POST['pass']) ? (string)$_POST['pass'] : '';
    if ($pass !== '' && strlen($pass) >= 6) {
        $out = password_hash($pass, PASSWORD_BCRYPT);
    } else {
        $out = 'Ошибка: пароль должен быть не короче 6 символов.';
    }
}
?>
<!DOCTYPE html>
<html lang="ru"><head><meta charset="utf-8"><title>Генератор хэша пароля</title></head>
<body style="font-family:sans-serif;background:#E8E4D2;padding:40px;">
<div style="background:#fff;max-width:480px;margin:0 auto;padding:24px;border-radius:8px;">
<h2 style="margin-top:0;">Генератор хэша пароля для /monitor/</h2>
<form method="post">
  <label>Пароль для доступа к дашборду:</label><br>
  <input type="password" name="pass" minlength="6" style="width:100%;padding:8px;margin:8px 0;">
  <br>
  <button style="padding:8px 18px;">Сгенерировать хэш</button>
</form>
<?php if ($out !== ''): ?>
<div style="margin-top:16px;padding:12px;background:#f0f0f0;border-radius:6px;">
  <div>Вставь это в <b>config.php</b> как <code>$PASSWORD_HASH</code>:</div>
  <code style="word-break:break-all;display:block;margin-top:8px;"><?php echo htmlspecialchars($out); ?></code>
</div>
<?php endif; ?>
<p style="color:#888;font-size:13px;">После генерации УДАЛИ этот файл: hash.php — он больше не нужен.</p>
</div>
</body></html>
