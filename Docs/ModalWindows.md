# Эталон модальных окон: FTP / Экспорт книги / Пароли

> Актуально на релиз 3.9.1. Рецепт зафиксирован, чтобы не переделывать:
> любое новое окно FTP-стиля собирается по разделу 1, грабли из раздела 4 не повторять.

## 0. Файлы и точки входа

| Окно | Файл | Вызов |
|---|---|---|
| FTP выгрузка (Даш W и Z) | `Forms/FtpSettingsForm.cs` — `Show(owner): bool` | `DashboardForm.cs:124`, `ZabbixAgentDashboardForm.cs:72` (кнопка FTP) |
| Экспорт книги | `Forms/ExportBookDialog.cs` — `Show(owner, groups): (Group, Columns)?` | `RdpManagerForm.cs:1631` (`ExportAddressBook`) |
| Пароли (KeePass) | `Forms/KeePassVaultForm.cs` — синглтон `Instance`, большое sizable-окно + вложенный `EntryEditor` | меню трея «Пароли…» |
| Общее | `Helpers/UiModal.cs` (Hero/Card/Field/FieldLabel/ApplyDpiScale), `Controls/UiToggle.cs` (свитч на Panel) | — |

## 1. Рецепт окна (каркас — одинаковый везде)

- `Form`: `FixedDialog` (модалки) / `Sizable` (Пароли), `CenterParent`, `BackColor = UiTheme.Background`, `KeyPreview` + Esc → закрыть.
- Три слоя, **порядок добавления строго**: `content` (`Dock.Fill`) → `hero` (`Dock.Top`, 80) → `footer` (`Dock.Bottom`, 52). Иначе Fill забирает весь клиент и лежит поверх шапки/футера.
- `Hero(title, subtitle, iconKey, onClose = null)`: градиент, чип иконки, заголовок + подзаголовок, опциональный ✕. Если у окна есть штатный крестик — `onClose` не передаём (два крестика — баг).
- `Card(x, y, w, h, title, iconKey)`: скруглённая панель, тёмный заголовок (2× темнее Accent), приглушённая иконка. **Виджеты — ДЕТИ карточки**, координаты относительные. `RoundCorners(card, 10)` — режет Region по скруглению.
- Footer 52: кнопки по фактическим размерам **в `Shown`** (якоря, выставленные до layout, запоминают мусор и увозят кнопки за экран — факт по дампу: 903/1011/1096 при ширине 668). Скрытые `AcceptButton`/`CancelButton` (Label не `IButtonControl`); у accept-хука **НЕ ставить `DialogResult`** — иначе Enter закроет окно без валидации.
- `ApplyDpiScale` + ресайз `ClientSize` (приложение `SystemAware`: шрифты растут с DPI, координаты — нет).
- В `Shown`: финальные позиции + `BeginInvoke(Refresh)` против призраков первой отрисовки.
- `ShowDialog(owner)`, результат наружу (`bool` / tuple / `null` = отмена).

## 2. Виджеты (что чем рисуем)

- `Field(editor, w)`: белая скруглённая обёртка, hover/focus-акцент. **Редактор обязан быть ребёнком wrap** (`Controls[0]`). Фон обёртки = `Card` + `RoundCorners(wrap, 7)`, иначе белые углы.
- `ComboBox`/`NumericUpDown`/`DateTimePicker` **игнорируют заданный Height** → центрировать вручную (`CenterEditor`); у DateTimePicker шрифт выставлять вручную (в `Field` нет case).
- `UiToggle` — свитч **на Panel, НЕ на CheckBox** (натив + UserPaint + прозрачность = чёрный фон и мусор). Зелёный `#10B981` во ВКЛ, текст с `EndEllipsis`, клик/пробел переключают, фокус-рамки нет.
- Кнопки: `UiButtons.MakeCompactFilled` — только статичный текст (рисует closure-текст, поздний `.Text` не отобразится!). Динамический текст («...») — только локальная фабрика, рисующая `lbl.Text` (см. `FtpButton` в FTP).
- `EscNumericUpDown` / `EscDateTimePicker` — Esc закрывает изнутри нативных контролов.
- `CheckedListBox`: белый, без своей рамки, если нужен скруглённый Region; отступы содержимого — только геометрией (вложением в панель с полями), owner-draw не использовать без крайней нужды.

## 3. Что где лежит (по окнам)

**FTP** (684×880): карточки Подключение | KeePass / Расписание / Действия. `UpdateVis` прячет строки (SFTP/auth/ключ, интервал/время/дни). Порт 21/22 переключается с протоколом. KeePass: unlock → выбор записи → обзор ключа. «Тест» (`TestConnectionAsync`), «Выгрузить сейчас» (`UploadWithReportAsync` → `ShowReport`: та же мини-модалка hero+card+footer). Сохранение → `BuildSettings → FtpUploadStore.Save → Restart`.

**Экспорт** (560×596): hero без крестика; карточка Группа (комбо), карточка Колонки (белый список в белой панели с полями 10px, рамка дорисована вручную по скруглению — штатная срезается Region); `ActiveControl = null` в `Shown` (иначе синяя подсветка комбо при открытии); `Enter` идёт через валидацию «хотя бы одна колонка».

**Пароли** (880×520, sizable, синглтон): hero без крестика; контент = `SplitContainer` (карточки «Группы»/«Записи», отступы 5px снаружи и между — асимметричный Padding + `SplitterWidth = 5`, фон панелей = фон страницы); дерево/таблица — дети карточек через явный `FitCardChild` на `Resize` + полный `Invalidate` на каждый шаг (против застойных штрихов) и на `ColumnWidthChanged`; футер (слева действия, справа блокировка/закрытие, позиции в `Resize`); статус 24px над футером; `EntryEditor` — мини-модалка 500×530 (hero+card+footer). Вся логика без изменений: unlock-flow, CRUD, дублирование «(копия)», копирование пароля, ПКМ-меню, widths/split/window persistence.

## 4. Грабли (выстрадано — не повторять)

1. Fill добавляется первым (content → hero → footer), иначе перекроет всё.
2. Низкий индекс в `Controls` = задний план; непрозрачная карточка поверх сиблингов их скрывает — только дети.
3. Якоря `Right` до финального layout = мусорные дистанции → всё позиционировать в `Shown`/`Resize` по факту.
4. `wrap.Controls.Add(editor)` обязателен, иначе `Controls[0]` = исключение.
5. Высота комбо-редакторов — только через `PreferredHeight` + центрирование.
6. Белые углы: `BackColor = Card` + `Region` (`RoundCorners`); рамку поверх Region — рисовать вручную.
7. Никаких нативных CheckBox с кастомной отрисовкой — только `UiToggle` на Panel.
8. Accept-хуку нельзя ставить `DialogResult` (обход валидации по Enter).
9. `MakeCompactFilled` не умеет менять текст после создания.
10. Приложение `SystemAware` — любое окно с фиксированными координатами прогонять через `ApplyDpiScale`.
11. Сборка только при закрытом exe; `Select-Object -First` обрывает publish (exe протухает!) — полный редирект вывода; свежесть проверять по `LastWriteTime` exe > исходников + хешам при синке в `prod`.
12. При живых ресайзах (окно/сплиттер/колонки) — полный `Invalidate` карточек и грида на каждый шаг (`Resize`, `ColumnWidthChanged`), иначе остаются застойные штрихи.
