# AGENTS.md — инструкция для ИИ-агента, продолжающего работу над Zapret GUI

> Быстрый старт. Полный контекст, карта кода, факты о движке и список открытых вопросов —
> в **[docs/HANDOFF.md](docs/HANDOFF.md)**. Прочитайте оба файла до первого коммита.

---

## 1. Что это за проект

**Zapret GUI** — Windows-приложение (**C# .NET 8 + WPF**, MVVM) — графическая надстройка над
популярной сборкой [Flowseal/zapret-discord-youtube](https://github.com/Flowseal/zapret-discord-youtube)
(обход DPI для YouTube/Discord, утилита `winws.exe` из zapret от bol-van).

Ключевые принципы, которые нельзя нарушать:

1. **Приложение не содержит и не перепаковывает бинарники zapret.** Движок (`bin/winws.exe`,
   `WinDivert64.sys`, списки, `.bat` стратегий) скачивается с официального репозитория
   Flowseal по кнопке на странице «Обновления».
2. **Стратегии не хардкодятся.** GUI парсит те же `general*.bat`, что и оригинальная сборка
   (см. `Core/StrategyParser.cs`). Любая новая стратегия в релизах Flowseal должна появляться
   в интерфейсе автоматически, без изменений кода — это главное архитектурное решение.
3. **Обновление движка никогда не затирает пользовательские данные** (`*-user.txt`, режимы
   `ipset` и игрового фильтра, `ACTIVE_*.bin`, `.backup`-списки). См. `EngineService.CopyEngine`.

## 2. Язык и стиль

- **Весь UI-текст, комментарии в коде, README, описания коммитов — на русском.** Аудитория
  проекта — русскоязычные пользователи Windows.
- `ImplicitUsings` **выключены намеренно** (`UseWPF` + `UseWindowsForms` дают конфликты имён
  `MessageBox`/`Application`/`Timer`). Пишите явные `using` в каждом файле.
- Nullable включён: новые свойства/методы — с корректными `?`.
- MVVM: бизнес-логика только в `Core/` и `ViewModels/`, в code-behind страниц — лишь
  подключение DataContext, подписки на события, работа с визуальным деревом.
- Кисти в XAML — только через `{DynamicResource XxxBrush}`, иначе переключение темы сломается.
- Ключи ресурсов и `{Binding}` **не проверяются компилятором** — после правок XAML обязательно
  прогоните `python3 tools/check_bindings.py` (см. §4).

## 3. Сборка и проверка перед выдачей результата

```bash
# Сборка (работает и на Windows, и на Linux/macOS — включён EnableWindowsTargeting)
dotnet build src/ZapretGUI/ZapretGUI.csproj -c Release

# Проверка XAML: привязки, ключи стилей, обработчики Click
python3 tools/check_bindings.py

# Парсер стратегий на РЕАЛЬНЫХ .bat из релиза движка (без Windows)
#   распаковать релиз Flowseal в /tmp/engine и запустить:
dotnet run --project tools/StrategyParserHarness -- /tmp/engine

# Публикация портативного exe (как в GitHub Actions)
dotnet publish src/ZapretGUI/ZapretGUI.csproj -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o publish/portable
```

Сборка должна быть **без ошибок и предупреждений**. Запустить WPF-приложение на Linux нельзя —
Windows-специфичное поведение (запуск `winws.exe`, службы, трей, hosts) проверяется только на
Windows, поэтому в отчёте пользователю честно указывайте, что не проверялось в рантайме.

## 4. Что нельзя ломать (контракты)

| Контракт | Где | Почему критично |
|---|---|---|
| Формат разбора `.bat` (строка с `winws.exe`, продолжения `^`, `%BIN%`/`%LISTS%`/`%~dp0`, `%GameFilterTCP%`/`%GameFilterUDP%`, `^!` → `!`) | `Core/StrategyParser.cs` | иначе обход запустится с неверными параметрами |
| Кавычки при `sc create` (`binPath= "\"…\winws.exe\" --аргументы"`) | `Core/BypassController.InstallServiceAsync` | служба не создастся или не запустится |
| Семантика режимов ipset: пустой файл = `any`, `203.0.113.113/32` = `none`, иначе `loaded`; реальный список в `ipset-all.txt.backup` | `EngineService.GetIpsetMode/SetIpsetMode` | совместимость с `service.bat` |
| `utils\game_filter.enabled` = `all`/`tcp`/`udp`; при выключенном фильтре порт-заглушка `12`, при включённом `1024-65535` | `EngineService`, `BypassController.BuildArgs` | иначе игры/сервисы не фильтруются |
| Имя службы `zapret` и значение реестра `zapret-discord-youtube` = имя стратегии | `Core/WinServices.cs` | совместимость с оригинальным менеджером |
| Блок `hosts` между маркерами `# ==== Zapret GUI … begin/end ====` + резервная копия | `EngineService.ApplyHosts` | безопасное обновление hosts вместо ручного копирования |
| Сохранение пользовательских файлов при апдейте | `EngineService.CopyEngine`, `IsUserFile` | иначе пользователь потеряет свои списки |

## 5. Как добавить страницу или настройку

**Страница:**
1. `Views/XxxPage.xaml` + `.xaml.cs` с конструктором, принимающим ViewModel (`DataContext = vm`).
2. `ViewModels/XxxViewModel.cs` (наследник `ObservableObject`, команды — `RelayCommand`/`AsyncRelayCommand`).
3. В `MainViewModel`: создать VM, добавить `NavItem { Key, Title, Icon }` в `NavItems`
   (глифы — из Segoe Fluent Icons/MDL2 Assets), зарегистрировать страницу в словаре `_pages`
   в `Views/MainWindow.xaml.cs`.
4. Стили брать из `Themes/Controls.xaml` (готовы: `Card`, `Badge`, `Dot`, `PrimaryButton`,
   `SecondaryButton`, `GhostButton`, `DangerButton`, `SwitchCheckBox`, `AppComboBox`,
   `SearchBox`, `RowItemStyle`, `SegmentedControl`, `ThinProgress` и т. д.).

**Настройка:** новое свойство в `Core/Settings.cs` → `AppSettings` (значение по умолчанию),
UI-свойство в `SettingsViewModel` (с `SettingsStore.Save`), по возможности — применение «на лету».

## 6. Цикл работы с пользователем

Пользователь работает так: агент пишет код в своей песочнице → **архивирует исходники в zip** →
пользователь сам загружает их в GitHub-репозиторий → GitHub Actions (`.github/workflows/build.yml`)
собирает артефакты, а по тегу `v*` публикует релиз с `ZapretGUI-<версия>-win-x64-portable.exe`.

Из этого следует:
- **Не нужно** пытаться пушить в GitHub самому — отдавайте изменённые файлы архивом.
- Версию приложения поднимать в `src/ZapretGUI/ZapretGUI.csproj` (`<Version>`), при крупных
  изменениях — в README.
- Перед отдачей результата: сборка без предупреждений + `check_bindings.py` + (если правки
  касались парсера) прогон harness.

## 7. Бэклог (согласован с пользователем как ориентир, приоритеты уточнять у него)

- Автотест стратегий (порт `utils\test zapret.ps1` + `utils\targets.txt`) с оценкой по сайтам.
- Визуальный редактор аргументов `winws.exe` (конструктор/тюнинг стратегий).
- Профили под провайдеров/сети, расписание, автопереключение при падении доступности.
- Настройка DoH (DNS-over-HTTPS) из приложения, проверка DNS-подмены.
- Редактор пользовательских списков внутри GUI (сейчас — открытие в Блокноте).
- Самообновление GUI (поля `AutoCheckGuiUpdates`, `GuiRepo` в настройках уже есть — не задействованы).
- Локализация EN, акцентный цвет интерфейса.

Полный список рисков, фактов о движке и открытых вопросов — в **[docs/HANDOFF.md](docs/HANDOFF.md)**.
