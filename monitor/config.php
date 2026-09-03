<?php
/**
 * /monitor/ config.php
 * Настройки дашборда событий.
 *
 * Хэш пароля: сгенерируй командой  /monitor/hash.php  на сервере,
 * затем вставь результат в $PASSWORD_HASH ниже. Меняй пароль — меняй хэш.
 */
$PASSWORD_HASH = '';
$SESSION_NAME   = 'monitor_user';
$SESSION_TTL    = 12 * 3600;   // время жизни сессии, секунд (12 часов)
$JSON_PATH      = __DIR__ . '/data/dashboard_events.json'; // путь к данным

/** Проверка введённого пароля */
function verify_password_hash($hash, $password) {
    if ($hash === '') return false;
    if (function_exists('password_verify')) {
        return password_verify($password, $hash);
    }
    // Резерв для очень старых PHP без password_verify
    return hash_equals($hash, crypt($password, $hash));
}
