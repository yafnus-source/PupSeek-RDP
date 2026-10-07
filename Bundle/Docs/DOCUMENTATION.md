# TODO.шная — документация

Приложение-диктофон на Android + вкладка TODO.шная в PupSeek RDP.
Надиктовал на телефон → текст появился в списке дел на ПК. Всё бесплатно,
без подписок и облаков, распознавание — офлайн на телефоне.

Сборка комплекта: 10.09.2026. APK v0.4.0 (versionCode 2).

---

## 1. Как это работает (сквозной сценарий)

```
┌──────────────┐  1. ЗАПИСЬ (AudioRecord   ┌──────────────┐  3. POST /todo   ┌──────────────────┐  4. опрос GET /todo
│   ТЕЛЕФОН    │ ──► PCM 16kHz WAV, волна,  │  pupseek.ru  │ ───────────────► │  PupSeek RDP (ПК)│ ──► каждые 15с ──►
│ TODO.шная    │  2. ГОТОВО → Vosk офлайн   │ monitor/todo │  data/todo.json  │ вкладка TODO.шная│     Merge в todo.json
└──────────────┘     распознавание (RU)     └──────────────┘                  └──────────────────┘
```

1. Открыл приложение — запись стартует сразу (разрешение на микрофон).
2. Во время записи: таймер, живая звуковая волна, кнопки `STOP` (пауза) и `ЗАПИСЬ`/`ГОТОВО` (одна кнопка-перевёртыш: жмёшь `ЗАПИСЬ` — идёт запись, на её месте появляется `ГОТОВО`).
3. Нажал `ГОТОВО` — запись останавливается, Vosk распознаёт речь в текст (офлайн, модель в APK), текст уходит POST-запросом на сервер, локальный wav-файл удаляется. **Записи на телефоне не хранятся.** Кнопка снова становится `ЗАПИСЬ`.
4. Десктоп опрашивает сервер каждые 15 секунд, новые задачи падают в таблицу вкладки + всплывает balloon. Удаление задачи в программе удаляет её и на сервере (и наоборот не воскресает — tombstones).
5. Привязка телефона к ПК — сканированием QR из диалога «Подключить телефон» (URL + опциональный токен).

Тишина / «Ничего не расслышал» — задача не отправляется, приложение ждёт нажатия `ЗАПИСЬ`.

---

## 2. Телефон (Android)

Исходники: `C:\temp\ToDo.шная\android` (Kotlin, Jetpack Compose Material3,
`ru.pupseek.todo`, minSdk 26, target/compileSdk 34).

Главный файл: `app/src/main/java/ru/pupseek/todo/MainActivity.kt`.

### 2.1. Экран и кнопки

- Тёмный фон `#092742`. Шапка `#1E3A5F` с логотипом, названием и точкой
  привязки (зелёная — сервер настроен, серая — нет).
- Статус-карта (белая): при записи — пульсирующий красный круг, таймер
  `мм:сс`, звуковая волна из 28 столбиков (сэмплирование пика PCM каждые
  100мс); в покое — текущий статус и подсказка.
- Большие кнопки 64dp: `STOP` (серая, пауза) + `ЗАПИСЬ`↔`ГОТОВО` (перевёртыш).
  Отдельной кнопки `ГОТОВО` нет.
- `Сканировать QR` (54dp) — привязка. Внизу пометка про нехранение записей.

### 2.2. Запись звука

`AudioRecord` (не MediaRecorder — Vosk ест только сырой PCM):
16kHz, mono, 16-bit → WAV-файл в `cacheDir/todo_*.wav` (заголовок дописывается
при стопе). Поток пишет чанками 4КБ и считает пик громкости для волны.
`ЗАПИСЬ` в покое начинает новую запись (старый кэш удаляется),
`STOP` — пауза. `onDestroy` всё останавливает и чистит кэш.

Разрешения (`AndroidManifest.xml`): `RECORD_AUDIO` (запрос при старте),
`CAMERA` (запрос при нажатии «Сканировать QR»), `INTERNET`.

### 2.3. Распознавание (Vosk, офлайн, 0₽)

- Библиотека `com.alphacephei:vosk-android:0.3.75` (Maven Central).
- Модель `vosk-model-small-ru-0.22` (45МБ) лежит в `assets/model-ru/`
  (+ обязательный файл `uuid` с версией — без него распаковщик 0.3.75
  падает; это было найдено декомпиляцией `StorageService`).
- При первом `ГОТОВО`: `StorageService.sync()` копирует модель в хранилище
  (~минута, статус «Готовлю модель»), дальше мгновенно.
- `Recognizer(model, 16000)` + `acceptWaveForm()` по чанкам → `finalResult`
  → поле `text`. Пустой текст = «Ничего не расслышал», задача не уходит.
- APK из-за модели весит ~108МБ. Актуальность small-модели проверена по
  alphacephei.com/vosk/models: новее 0.22 для русского нет (следующая —
  сразу 1.5–1.8ГБ серверные, на телефон не встанут).

### 2.4. Отправка и привязка

- `POST {ApiUrl}` JSON: `id` (uuid), `text`, `source:"voice"`,
  `createdAt` (ISO). Заголовок `Authorization: Bearer <token>` — только если
  токен задан. По умолчанию `ApiUrl=https://pupseek.ru/monitor/todo/`,
  токен пустой.
- QR содержит `todo://pair?url=...&token=...` (сканер: zxing-android-embedded).
  Данные кладутся в `SharedPreferences "todo"` (`apiUrl`, `apiToken`).

### 2.5. Сборка APK

Нужны JDK 17 + Android SDK (platforms;android-34, build-tools 34.0.0) +
Gradle 8.7 (AGP 8.4 требует минимум Gradle 8.6). Готовое окружение на этой
машине: `C:\temp\jdk17\jdk17`, `C:\Android\Sdk`, `C:\temp\gradle8\gradle-8.7`.
Проект копируется в ASCII-путь `C:\temp\TodoAndroid\android` (AGP на Windows
падает на кириллице в пути `ToDo.шная`), сборка:
`gradle.bat -p C:\temp\TodoAndroid\android assembleDebug`.
Готовый APK: `android/app/build/outputs/apk/debug/app-debug.apk`.
Важные мелочи, на которых спотыкались: `local.properties` без trailing-пробелов,
лицензии SDK файлом без пробелов, `compileOptions`/`jvmTarget` = Java 17,
`acceptWaveForm` (не `acceptWav`) в vosk 0.3.75, иконки — пакет
`material-icons-extended` (в core их нет).

---

## 3. Сервер (pupseek.ru, PHP, без БД — 0₽)

Файлы для заливки — папка `server/` этого комплекта → `pupseek.ru/monitor/todo/`:
`index.php`, `.htaccess`, `data/` (права на запись, `todo.json` создастся сам).

### 3.1. API

- `GET /monitor/todo/` → JSON-массив задач. `GET ?since=<ISO>` → только новее.
- `POST /monitor/todo/` `{"id","text","source","createdAt",...}` → добавление
  (дедуп по id, сортировка по createdAt desc, лимит 2000).
- `DELETE /monitor/todo/?id=...` → удаление. Fallback для хостингов без
  DELETE: `POST {"action":"delete","id":"..."}`.
- Токен: если в `../config.php` задан `$TODO_TOKEN`, POST/DELETE требуют
  `Authorization: Bearer`. Сейчас пустой — без авторизации.
- CORS открыт (`*`), листинг каталога закрыт. Хранится всё в
  `data/todo.json` (лимит выдачи 500).

### 3.2. Важно про URL

Опрашивать/слать надо URL **папки со слешем**: `https://pupseek.ru/monitor/todo/`.
Без слеша сервер отдаёт `301 → http://...` (downgrade!), десктопный код
слеш добавляет сам (`TodoSyncService.BuildUrl`), но лучше не полагаться.

---

## 4. Десктоп (вкладка TODO.шная в PupSeek RDP)

Код: `C:\temp\Clo\cpro` (там параллельно работает другой ИИ — править
аккуратно; снапшот для отката см. `desktop-NOTE.txt`).

- `Forms/TodoForm.cs` — вкладка: таблица (зебра, шрифт 11/11.5, строки 48px),
  фильтры Все/Активные/Выполненные/Отложенные/Сегодня, поиск как в Книге
  (с `✕`), ПКМ по строке (выполнено/отложить/дата/редактировать/удалить),
  ПКМ по пустому месту — «Добавить запись». Ширина и порядок колонок тянутся
  и сохраняются (`column_widths.json` + `column_order.json`, механизм
  `GridColumnOrder` как в остальных вкладках). Header окна `#100E0C`,
  шапка таблицы `#626F5E` (в тон Дашборду).
- `Services/TodoStore.cs` — `todo.json` + настройки; `Merge` (дедуп по Id,
  пропуск tombstones); `Delete` (tombstone + удаление на сервере).
- `Services/TodoSyncService.cs` — опрос каждые 15с: таймаут 30с, 3 попытки
  с паузой 2с (сеть до pupseek.ru flaky: бывают DNS-провалы и висяки),
  `poll added N items` в лог, balloon. `DeleteRemoteAsync` — DELETE с
  POST-fallback.
- `Models/TodoItem.cs` — `Id/Text/Done/Status/CreatedAt/DueAt/Source/Notified`
  + повторы (`Repeat/RepeatDays/RepeatDayOfMonth`, второй ИИ) + настройки
  (`ApiUrl/ApiToken/pollIntervalSeconds/useFileWatcher/deletedIds`).
- `Helpers/TodoPairDialog.cs` + `TodoQrDialog.cs` — диалог «Подключить телефон»
  с живым QR (`todo://pair?...`, QRCoder), кнопки как везде в программе.
- Кнопка `TODO.шная` в сайдбаре (цвет Книги `#296AA0`), пункт в трей-меню,
  сервис стартует из `ShadowRdpAppContext`.

Данные: `%PROGRAMDATA%\PupSeekRDP\todo.json`,
`todo_settings.json` (`deletedIds` — метки удалённых, до 2000),
`debug.log` (искать `Todo`, `poll added`).

---

## 5. Форматы данных

Задача (везде одинаковая):
`{"id":"guid","text":"...","done":false,"status":0,"createdAt":"ISO",
"dueAt":null,"source":"voice|manual"}` (`status`: 0 Active, 1 Done, 2 Postponed).

QR: `todo://pair?url=<urlencoded>&token=<urlencoded>` (токен опционален).

---

## 6. Диагностика (что уже болело и чем лечилось)

| Симптом | Причина | Лечение |
|---|---|---|
| `Todo poll failed: Этот хост неизвестен` / таймауты 8с, сотни штук | DNS/route до pupseek.ru периодически падает с этого ПК | Таймаут 30с + 3 попытки, варн только после 3-й |
| Задачи не приходят, ошибок нет | Старый exe / приложение закрыто / опрос в молчаливый 404 | Пересобрать, запустить, смотреть `poll added` в логе |
| `Ошибк
...[truncated 2085 chars]