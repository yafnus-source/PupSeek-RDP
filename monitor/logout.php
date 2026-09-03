<?php
/**
 * /monitor/logout.php — выход из сессии.
 */
require __DIR__ . '/config.php';
session_name($SESSION_NAME);
session_start();
$_SESSION = array();
if (ini_get('session.use_cookies')) {
    $p = session_get_cookie_params();
    setcookie(session_name(), '', time() - 42000, $p['path'], $p['domain'], $p['secure'], $p['httponly']);
}
session_destroy();
header('Location: index.php');
exit;
