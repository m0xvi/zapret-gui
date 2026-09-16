# HANDOFF.md — полный контекст проекта Zapret GUI для следующего агента

**English abstract:** Zapret GUI is a Windows GUI (C# .NET 8 + WPF, MVVM) for the
`Flowseal/zapret-discord-youtube` DPI-bypass bundle. It parses the bundle's own `general*.bat`
strategy files, launches `bin\winws.exe` with identical arguments, installs the `zapret`
Windows service, checks/downloads engine updates from the official GitHub repo (preserving user
lists), manages game/ipset filters, updates `ipset-all.txt` and `hosts`, and runs a 14-point
diagnostics. Code compiled clean (Release, .NET SDK 8.0.425) and the strategy parser was
validated against the real 1.10.2 release files, but **nothing was run on Windows** — runtime
verification is the first task for the next agent. UI language is Russian. See §5 for the
upstream engine facts and §8 for pitfalls.

Документ написан для агента, который продолжит работу **с нуля контекста**. Читайте по порядку;
если нужен только быстрый старт — смотрите `AGENTS.md`.

---

## 1. Продукт, пользователь и способ работы

**Продукт:** удобный GUI для сборки `Flowseal/zapret-discord-youtube` — заменяет собой
`general*.bat` и `service.bat`: запуск/остановка обхода, служба Windows, выбор стратегии,
фильтры, автообновление движка, диагностика.

**Пользователь:** русскоязычный, Windows 10/11 x64, за DPI-блокировками (Калининград, РФ).
Заказал «максимально удобный и user-friendly интерфейс» + автообновление из официального
репозитория, а также «парочку функций», которые пока не назвал. Первый макет он ревьюит и
дальше будет присылать правки по разделам («в “Стратегиях” убери X, добавь Y»).

**Воркфлоу (важно!):** агенту выданы права на запись в GitHub-репозиторий пользователя, и он
**работает напрямую через git — без архивов с исходниками.** Цикл:

```
git pull → правки → проверки (сборка + check_bindings.py + harness) → git commit/push origin main
   → GitHub Actions собирает приложение → дождаться зелёного workflow → отчёт пользователю
   → (при необходимости) git tag vX.Y.Z → публикация GitHub Release с exe
```

Токен доступа (fine-grained PAT, `Contents: Read and write` + `Actions: Read`) пользователь
выдаёт в чате; его нужно держать в переменной окружения, подставлять в URL remote'а
(`https://x-access-token:$GITHUB_TOKEN@github.com/<логин>/<репозиторий>.git`), **не коммитить**
и не писать в файлы репозитория. `.git/config` и credentials в песочнице агента между сессиями
не сохраняются — при новой сессии токен подставляется заново (или запрашивается у пользователя).
Пользователь формулирует правки словами (по разделам интерфейса) и проверяет результат в собранном
приложении; архивы и ручная загрузка файлов больше не используются — только git.

**CI:** `.github/workflows/build.yml` на `windows-latest`, .NET 8 SDK. На push/PR — сборка и
артефакты (`dist/ZapretGUI-<ver>-win-x64-portable.exe`, `…-net8.zip`), на тег `v*` —
публикация GitHub Release. Версия берётся из `<Version>` в csproj (или из тега).

---

## 2. Что уже реализовано (состояние на момент передачи)

### Обзор (HomePage)
- Карточка статуса: кольцо-индикатор, текст состояния (`Обход запущен (служба)` / `Обход выключен`),
  стратегия, аптайм, PID, состояние службы `zapret`; кнопки «Запустить», «Остановить»,
  «Установить в службу», «Удалить службы».
- Фильтры: игровой фильтр (4 режима, `SegmentedControl`), фильтр `ipset` (3 режима),
  переключатель легаси-флага `utils\check_updates.enabled`.
- Проверка соединения: TCP+HTTP к YouTube / Discord / GitHub (`Core/ConnectionTester.cs`).
- Правая колонка: версия движка, наличие обновления, количество стратегий, путь, подсказки.

### Стратегии (StrategiesPage)
- Список автоматически строится из `*.bat` в корне папки движка (кроме `service*.bat`),
  естественная сортировка (`general`, `general (ALT)`, `general (ALT2)`, … `general (ALT10)`).
- Поиск, фильтр по категориям (`FAKE TLS AUTO` / `ALT` / `SIMPLE FAKE` / `БАЗОВАЯ` / `EXP`),
  чип «только рекомендуемые»; рекомендуемая = `general (FAKE TLS AUTO)`.
- Автогенерируемое человекочитаемое описание: `fake`, `multisplit`, `multidisorder`,
  `фейк TLS с SNI …`, `фейк QUIC (UDP 443)`, `повторы N`, `fooling badseq`, `все протоколы (игры)`,
  `ip-id=zero`, `Discord (голос/медиа)`.
- Панель деталей: полный список аргументов `winws.exe`, «Запустить», «Установить в службу»,
  «Сделать основной», «Открыть .bat», «Скопировать команду».

### Обновления (UpdatesPage)
- Проверка релизов: GitHub API `releases?per_page=20` (фильтр draft, флаг prerelease),
  резервный путь — `.service/version.txt` (как в `service.bat`), при недоступности API.
- Скачивание zip-ассета с прогрессом → распаковка (zip и tar.gz) → **слияние с сохранением
  пользовательских файлов** → маркер версии `.gui-engine-version` → восстановление режимов
  `ipset`/игрового фильтра → автоперезапуск обхода, если он работал.
- Обновление `lists\ipset-all.txt` (с учётом текущего режима: при `none`/`any` пишется в `.backup`).
- Проверка и **безопасное применение `hosts`** (маркеры блока + бэкап в `%APPDATA%\ZapretGUI\backups`).

### Диагностика (DiagnosticsPage) — 14 проверок
Права администратора; файлы `bin` (`winws.exe`, `WinDivert64.sys`, `WinDivert.dll`); кириллица/
спецсимволы в пути; OneDrive в пути; служба `BFE`; TCP timestamps; системный прокси (реестр);
активные VPN-адаптеры (PowerShell `Get-NetAdapter`); конфликтующие службы (AdguardSvc, Killer,
Intel Connectivity, Check Point, SmartByte); другие обходы (`goodbyedpi`, `dpitunnel`, `tg-ws-proxy`,
`byedpi` + чужие `winws.exe` по пути процесса); остаточные службы `WinDivert`/`WinDivert14`;
состояние службы `zapret`; наличие строк GitHub в `hosts`; размер кэша Discord.
Исправления в один клик: удалить все службы, включить timestamps, очистить кэш Discord,
сбросить сеть (`netsh winsock reset`, `netsh int ip reset all`, `netsh winhttp reset proxy`,
`ipconfig /flushdns` — с предупреждением о перезагрузке).

### Прочее
- **Журнал**: кольцевой буфер 2000 записей + файл с ротацией 1 МБ, фильтры по уровням,
  автопрокрутка, копирование/сохранение в файл.
- **Настройки**: путь к движку (+ «Обзор…», «Найти автоматически», «Проверить»), тема,
  сворачивание/запуск в трей, поведение обхода (автозапуск обхода вместе с GUI, остановка при
  выходе, подтверждение остановки, показывать консоль `winws.exe`), обновления, автозапуск через
  планировщик задач (`schtasks /tn ZapretGUI /sc onlogon /rl highest /f`), быстрый доступ к
  пользовательским спискам/файлам. Все настройки сохраняются автоматически.
- **Трей** (`Core/Tray.cs`, WinForms `NotifyIcon`): открыть окно, «Запустить/Остановить обход»,
  выход, балунные подсказки; закрытие окна сворачивает в трей, если включено.
- **Темы**: тёмная/светлая/системная, переключение на лету; кастомный заголовок окна
  (`WindowChrome`), панель навигации, стили карточек/кнопок/свитчей/сегментов/скроллбаров.

### Осознанно НЕ реализовано (см. §9)
Автотест стратегий по `targets.txt`; визуальный редактор аргументов `winws`;
профили под провайдеров; расписание; настройка DoH; редактор списков внутри GUI;
самообновление GUI (поля настроек есть, логики нет); локализация EN.

---

## 3. Что проверено, а что нет (честная карта рисков)

**Проверено (фактически выполнено):**
- `dotnet build -c Release` — успешно, **0 ошибок / 0 предупреждений** (Linux, .NET SDK 8.0.425).
- `dotnet publish -r win-x64 --self-contained -p:PublishSingleFile=true` — успешно,
  один `ZapretGUI.exe` размером 145 192 642 байт (≈139 МБ).
- Все `{Binding}` в XAML и все ключи `{DynamicResource}`/`{StaticResource}` проверены
  скриптом против свойств ViewModels и словарей тем — расхождений нет.
- Обработчики `Click="…"` в XAML имеют соответствующие методы в code-behind.
- **Парсер стратегий проверен на реальных файлах релиза 1.10.2**: 22 стратегии, по 82–101
  аргументу, плейсхолдеры раскрыты, `^!` → `!`, подстановка игрового фильтра корректна,
  «проблемных» токенов нет. Именно на этом прогоне был найден и исправлен серьёзный баг
  (см. §8.1).

**НЕ проверено (нет Windows-среды) — зона первого этапа работы:**
- запуск `winws.exe` и реальный факт работы обхода;
- `sc create/start/delete` для службы `zapret`, запись значения в реестр;
- поведение трея, `NotifyIcon`, извлечение иконки, балунные подсказки;
- кастомный заголовок `WindowChrome` (перетаскивание, разворот, отступы при maximize,
  корректность «снапа» к краям);
- `SegmentedControl` (двусторонняя привязка `SelectedIndex` и тайминги, см. §8.3);
- запись `hosts`, копирование бэкапов, права доступа;
- реальный вывод диагностики на русской/английской локализации Windows;
- работа трея и приложения при выключенном окне (свёрнуто в трей) — там таймер статуса
  продолжает опрашивать `sc.exe` каждые 3 секунды (см. §8.8).

---

## 4. Карта кода

```
src/ZapretGUI/
├─ ZapretGUI.csproj      net8.0-windows, UseWPF+UseWindowsForms, ImplicitUsings=disable,
│                        Nullable=enable, EnableWindowsTargeting=true, NoWarn=WFAC010,
│                        ApplicationIcon=Assets/app.ico, PackageReference System.Text.Encoding.CodePages 8.0.0
├─ app.manifest          requireAdministrator, dpiAwareness=PerMonitorV2, longPathAware
├─ App.xaml / .cs        порядок словарей: [0] тема, [1] Controls; конвертеры в ресурсах;
│                        ShutdownMode=OnExplicitShutdown; мьютекс "ZapretGUI.SingleInstance";
│                        регистрация CodePagesEncodingProvider; тихая проверка обновлений через 2.5 с
├─ Core/                 ядро (без UI-зависимостей кроме Mvvm/утилит)
│  ├─ EngineService.cs       релизы, скачивание, распаковка, слияние, ipset, hosts, версия, кэш Discord
│  ├─ BypassController.cs    статус/запуск/остановка winws.exe, служба zapret, BuildArgs, режимы
│  ├─ StrategyParser.cs      разбор .bat, StrategyInfo, EnsureUserLists, NaturalStringComparer
│  ├─ WinServices.cs         обёртка sc.exe, реестр, RemoveEverything, EnsureTcpTimestamps
│  ├─ DiagnosticsService.cs  14 проверок + ResetNetwork
│  ├─ ConnectionTester.cs    TCP+HTTP проверки YouTube/Discord/GitHub
│  ├─ Settings.cs            AppSettings + SettingsStore (JSON) + ThemeMode/GameFilterMode/IpsetMode
│  ├─ AppPaths.cs            %APPDATA%\ZapretGUI, DefaultEngine=%LOCALAPPDATA%\ZapretGUI\engine, маркер версии
│  ├─ AppLog.cs              LogEntry, буфер 2000, файл с ротацией, событие EntryAdded
│  ├─ Shell.cs               Run (CP866!), StartDetached, OpenUrl/OpenFolder/OpenInNotepad,
│  │                         IsAdmin, RestartElevated, IsProcessRunning, KillProcess, WaitForAsync
│  ├─ ThemeService.cs        подмена MergedDictionaries[0], определение системной темы из реестра
│  ├─ Tray.cs                NotifyIcon + контекстное меню
│  ├─ Converters.cs          BoolToVis (параметр "invert"), InverseBool, StringToVis ("invert"),
│  │                         SeverityBrush, SoftSeverityBrush, CountToVis, BypassStateBrush
│  └─ Mvvm.cs                ObservableObject, RelayCommand, AsyncRelayCommand (маршалинг CanExecuteChanged)
├─ ViewModels/
│  ├─ MainViewModel.cs       владеет Settings/Bypass/StrategyStore и всеми под-VM; навигация;
│  │                         DispatcherTimer 3 с → Home.RefreshStatus() + Updates.RefreshBadge();
│  │                         Notify(name), ShutdownAsync(); ключи страниц: home/strategies/updates/
│  │                         diagnostics/logs/settings/about
│  ├─ StrategyStore.cs       общий список стратегий (ObservableCollection<StrategyInfo>), Refresh()
│  ├─ HomeViewModel.cs       статус, фильтры, старт/стоп/служба, тест соединения, баннер сообщений
│  ├─ StrategiesViewModel.cs ICollectionView-фильтрация (поиск/категория/только рекомендуемые)
│  ├─ UpdatesViewModel.cs    проверка/скачивание/установка, ipset, hosts, прогресс, LastKnownLatest
│  ├─ DiagnosticsViewModel.cs список проверок, прогресс, быстрые исправления
│  ├─ LogsViewModel.cs       фильтры уровней, AutoScroll + событие ScrollToEndRequested
│  └─ SettingsViewModel.cs   автосохранение настроек, автозапуск, пути, валидация движка
├─ Views/                MainWindow (сайдбар, заголовок, ContentControl PageHost, трей) + 7 страниц
│  └─ Controls/SegmentedControl.xaml(.cs)   ДП ItemsSource + SelectedIndex (TwoWay по умолчанию)
├─ Themes/               Dark.xaml, Light.xaml (кисти), Controls.xaml (стили компонентов)
└─ Assets/               app.ico (16…256), app.png
tools/                   check_bindings.py, StrategyParserHarness (см. §6)
docs/mockup.html         интерактивный макет интерфейса (тот же дизайн, что в приложении)
```

**Ключевые сигнатуры (чтобы не искать заново):**

```csharp
// StrategyParser
List<StrategyInfo> LoadAll(string engineRoot);            // без service*.bat, естественная сортировка
StrategyInfo? Parse(string batPath, string engineRoot);
List<string> Tokenize(string command);                    // кавычки + экранирование ^
void EnsureUserLists(string engineRoot);
// StrategyInfo: Name, FileName, FullPath, Args, Category, IsRecommended,
//               UsesFakeTls/UsesFakeQuic/UsesSplit/UsesGameFilter, Description, ShortArgs
// Args содержит плейсхолдеры "{GameFilterTCP}"/"{GameFilterUDP}" — подстановка при запуске.

// BypassController
BypassStatus GetStatus();                                  // state, strategy, pid, uptime, service
List<string> BuildArgs(StrategyInfo s, GameFilterMode g);  // 12 / 1024-65535
Task<OperationResult> StartAsync(StrategyInfo s, GameFilterMode g, bool showConsole, CancellationToken ct = default);
Task<OperationResult> StopAsync(CancellationToken ct = default);
Task<OperationResult> InstallServiceAsync(StrategyInfo s, GameFilterMode g, CancellationToken ct = default);
Task<OperationResult> RemoveServiceAsync();
bool IsServiceInstalled();

// EngineService
Task<ReleaseInfo?> GetLatestReleaseAsync(bool includePrerelease, CancellationToken ct = default);
Task<string?> GetLatestVersionTextAsync(CancellationToken ct = default);
int CompareVersions(string? a, string? b);                 // 1.10.2 > 1.9.9d
Task<EngineUpdateResult> DownloadAndInstallAsync(ReleaseInfo r, string engineRoot, AppSettings s,
                                                IProgress<ProgressInfo>? p = null, CancellationToken ct = default);
Task<bool> UpdateIpsetAsync(string engineRoot, IProgress<ProgressInfo>? p = null, CancellationToken ct = default);
Task<HostsCheckResult> CheckHostsAsync(CancellationToken ct = default);
(bool Ok, string Message) ApplyHosts(string downloadedFile);
IpsetMode GetIpsetMode(string engineRoot);  bool SetIpsetMode(string engineRoot, IpsetMode target);
GameFilterMode GetGameFilterMode(string engineRoot);  void SetGameFilterMode(string engineRoot, GameFilterMode mode);
bool GetBatAutoUpdateFlag(string engineRoot);  void SetBatAutoUpdateFlag(string engineRoot, bool enabled);
string ReadVersion(string engineRoot);  void WriteVersion(string engineRoot, string version);
```

**Хранение данных**

| Что | Где |
|---|---|
| Настройки | `%APPDATA%\ZapretGUI\settings.json` (см. `AppSettings`: ~20 полей) |
| Журнал | `%APPDATA%\ZapretGUI\logs\zapretgui.log` (+ `.old`, ротация 1 МБ) |
| Бэкапы (hosts и др.) | `%APPDATA%\ZapretGUI\backups\hosts-YYYYMMDD-HHmmss.bak` |
| Временные файлы | `%TEMP%\ZapretGUI\` |
| Движок по умолчанию | `%LOCALAPPDATA%\ZapretGUI\engine` (можно указать любую папку) |
| Маркер версии движка | `<engine>\.gui-engine-version` (fallback — парсинг `LOCAL_VERSION=` из `service.bat`) |

---

## 5. Факты о движке Flowseal/zapret-discord-youtube (проверено по релизу 1.10.2)

Всё ниже — результаты изучения репозитория/релиза; это «источник истины» для совместимости.

**Релизы.** `GET https://api.github.com/repos/Flowseal/zapret-discord-youtube/releases`.
На момент работы актуальный — **1.10.2** (публикация 2026-08-24). Ассеты релиза:
`zapret-discord-youtube-<версия>.zip`, `…tar.gz`, `…rar`. **RAR не поддерживается** (нет
бесплатного распаковщика в .NET) — в этом случае GUI показывает сообщение со ссылкой на релиз.
Zip-архив содержит **одну корневую папку** `zapret-discord-youtube-<версия>/` (её нужно
«развернуть» — реализовано в `ResolveContentRoot`). Внутри: `bin/`, `lists/`, `utils/`,
`.service/`, `README.md`, `LICENSE.txt`, **23 .bat** (22 стратегии + `service.bat`), при этом
`.service/` и `utils/test zapret.ps1` в архиве есть, а `version.txt` — только в репозитории.

**Быстрые ссылки (raw, main):**
`…/main/.service/version.txt` (текущая версия), `…/main/.service/ipset-service.txt` (список IP),
`…/main/.service/hosts` (строки GitHub-адресов).

**Раскладка движка:**
`bin\winws.exe`, `bin\WinDivert.dll`, `bin\WinDivert64.sys`, `bin\cygwin1.dll`, фейки
`bin\*.bin` (`ACTIVE_DISCORD_UDP.bin`, `ACTIVE_GAME_UDP.bin`, `quic_initial_*.bin`,
`tls_clienthello_*.bin`, `stun*.bin`);
`lists\list-general.txt`, `list-google.txt`, `list-exclude.txt`, `ipset-exclude.txt`,
`ipset-all.txt` (+`.backup`), а также создаваемые пользовательские `*-user.txt`;
`utils\game_filter.enabled`, `utils\check_updates.enabled` (наличие = включено),
`utils\targets.txt`, `utils\test zapret.ps1`.

**Строка запуска в .bat (контракт парсера):**

```bat
start "zapret: %~n0" /min "%BIN%winws.exe" --wf-tcp=80,443,…,%GameFilterTCP% ^
--filter-udp=443 --hostlist="%LISTS%list-general.txt" … --new ^
--filter-tcp=80,443 --dpi-desync=multisplit --dpi-desync-split-pos=1 …
```

- Переменные: `%BIN%` = `<engine>\bin\`, `%LISTS%` = `<engine>\lists\`, `%~dp0` = папка движка.
- `%GameFilterTCP%` / `%GameFilterUDP%` = `1024-65535` при включённом игровом фильтре и `12`
  при выключенном; `%GameFilter%` (если встречается) = оба протокола.
- Экранирование `^` перед `!`, `"`, `&`, `|`, `<`, `>`, `(`, `)` снимается; например
  `--dpi-desync-fake-tls=^!` превращается в `--dpi-desync-fake-tls=!`.
- **Критично:** в строке путь `"%BIN%winws.exe"` в кавычках — при выделении хвоста команды
  обязательно снять ведущую закрывающую кавычку (в текущем коде это сделано; при рефакторинге
  легко сломать, см. §8.1).

**Служба (аналог `Install Service`):**
`sc create zapret binPath= "\"<engine>\bin\winws.exe\" <аргументы>" DisplayName= "zapret" start= auto`,
`sc description zapret "Zapret DPI bypass software"`, `sc start zapret`, затем
`reg add "HKLM\System\CurrentControlSet\Services\zapret" /v zapret-discord-youtube /t REG_SZ /d "<имя стратегии>" /f`.
Удаление (`Remove Services`): `net stop`/`sc delete zapret`, `taskkill /IM winws.exe /F`,
плюс службы `WinDivert` и `WinDivert14`.
Перед запуском включаются TCP timestamps: `netsh interface tcp set global timestamps=enabled`.

**Режимы фильтра ipset** (из `service.bat` → `ipset_switch`): файл `lists\ipset-all.txt`
пустой → `any`; содержит `203.0.113.113/32` → `none`; иначе `loaded`. Реальный список хранится
в `ipset-all.txt.backup`; переключения перекладывают файлы (`loaded→none` — переименовать в
`.backup` и записать заглушку; `none→any` — обнулить; `any→loaded` — восстановить из `.backup`).

**Игровой фильтр**: файл `utils\game_filter.enabled` со словом `all` / `tcp` / `udp`;
отсутствие файла = выключен. После переключения требуется перезапуск стратегии.

**Файл hosts** из репозитория: адреса вида `146.75.22.132 raw.githubusercontent.com`
(+ IPv6 `2606:50c0:8000::154`) для githubusercontent-доменов; нужен для веб-версии Telegram и
голосового чата Discord. Оригинальный `service.bat` просит пользователя копировать вручную —
GUI делает это сам: бэкап + блок между маркерами `# ==== Zapret GUI (Flowseal/zapret-discord-youtube) begin/end ====`
+ удаление прежнего блока и дублей.

**Ограничения GitHub API:** 60 запросов/час на IP без токена. Поэтому есть резервный путь
(чтение `.service/version.txt`) и понятные сообщения об ошибках в UI.

---

## 6. Как воспроизвести проверки (инструменты в репозитории)

1. **Сборка:** `dotnet build src/ZapretGUI/ZapretGUI.csproj -c Release`.
   На Linux/macOS работает благодаря `EnableWindowsTargeting=true` (это только компиляция,
   запуск невозможен). Целевой SDK — 8.0.x.
2. **Проверка XAML:** `python3 tools/check_bindings.py` — ищет привязки, которых нет в
   ViewModels (с учётом типов элементов `DataTemplate`), отсутствующие ключи ресурсов и
   `Click`-обработчики без метода в code-behind. Запускать после любых правок XAML.
3. **Парсер стратегий (реальные файлы движка, без Windows):**
   ```bash
   # скачать и распаковать релиз движка, например 1.10.2, в /tmp/engine
   curl -L -o /tmp/engine.zip https://github.com/Flowseal/zapret-discord-youtube/releases/download/1.10.2/zapret-discord-youtube-1.10.2.zip
   unzip -q /tmp/engine.zip -d /tmp && mv /tmp/zapret-discord-youtube-1.10.2 /tmp/engine
   dotnet run --project tools/StrategyParserHarness -- /tmp/engine
   ```
   Harness компилирует **те же** `Core/StrategyParser.cs`, `AppLog.cs`, `AppPaths.cs` (через
   `<Compile Include>`), печатает список стратегий, их описания, число аргументов и найденные
   проблемы (нераскрытые плейсхолдеры, остатки `^`, кавычки). Ожидаемый результат на 1.10.2:
   **22 стратегии, 82–101 аргумент, 0 проблем**.
4. **Публикация:** см. §3 (команда `dotnet publish …` проверена — файл ≈139 МБ).

На Windows первый прогон: `ZapretGUI.exe` → UAC → «Обновления» → «Проверить обновления» →
«Скачать и установить» (движок появится в `%LOCALAPPDATA%\ZapretGUI\engine`) → «Обзор» →
«Запустить обход». Проверять по журналу (страница «Журнал») — там пишутся и аргументы запуска.

---

## 7. Соглашения по UI (чтобы правки выглядели «своими»)

- **Кисти:** `BgBrush`, `SidebarBrush`, `TitleBarBrush`, `CardBrush`, `CardHoverBrush`,
  `ElevatedBrush`, `BorderBrush`, `BorderStrongBrush`, `TextBrush`, `TextMutedBrush`,
  `TextDimBrush`, `TextInverseBrush`, `AccentBrush`/`AccentHoverBrush`/`AccentPressedBrush`/
  `AccentSoftBrush`/`OnAccentBrush`, `SuccessBrush`/`SuccessSoftBrush`, `WarningBrush`/`…Soft`,
  `DangerBrush`/`…Soft`, `InfoBrush`/`…Soft`, `MutedBrush`, `ScrollThumbBrush`/`…Hover`,
  `SelectionBrush`, `WindowControlHoverBrush`, `WindowCloseHoverBrush`, `ShadowBrush`;
  геометрия — `CardRadius` (14), `ControlRadius` (9); шрифты — `UiFont`, `UiFontDisplay`, `IconFont`.
- **Текстовые стили:** `DisplayText` (25), `TitleText` (17), `SectionText` (11, капс-заголовок
  секции), `BodyText` (13), `MutedText` (12), `DimText` (11.5), `MonoText`, `StatValueText`, `IconText`.
- **Компоненты:** `Card`, `InnerCard`, `Badge`, `Dot`, `PrimaryButton`, `SecondaryButton`,
  `GhostButton`, `DangerButton`, `LinkButton`, `IconButton`, `TitleBarButton(_Close)`,
  `ChipToggle`, `SwitchCheckBox`, `CheckBoxApp`, `InputBox`, `SearchBox`, `AppComboBox`,
  `PlainListBox`, `NavItemStyle`, `RowItemStyle`, `LogItemStyle`, `SegmentItemStyle`,
  `ThinProgress`, `Divider`.
- **Иконки** — глифы Segoe Fluent Icons / Segoe MDL2 Assets (`IconFont`), уже используемые:
  `E80F` обзор, `E71D` стратегии, `E895` обновления, `E90F` диагностика, `E7C3` журнал,
  `E713` настройки, `E946` информация/о программе, `E793` тема, `E72E`/`E721`, `E77B`/`E8BB`
  управление окном, `E894` закрыть/скрыть плашку, `E70D` стрелка ComboBox, `E73E` галочка,
  `E7BA` предупреждение. Если глифа нет в системе — отобразится квадрат, проверяйте визуально.
- **Макет страниц:** заголовок `DisplayText` + подзаголовок `MutedText` → баннер сообщения
  (`MessageKey` → `SoftSeverityBrush` фон) → контент в две колонки (основная `*` и боковая
  `330…420 px`); внутри — карточки `Card` с секциями `SectionText`.
- **Локализация текстов:** русский, без канцелярита; термины движка (`ipset`, `loaded/none/any`,
  `fake TLS`, `multisplit`) не переводить.
- Дизайн-прототип и «живой» макет — `docs/mockup.html`; **при изменении дизайна синхронизируйте
  макет**, пользователь сверяет правки по нему.

---

## 8. Подводные камни и известные риски (по убыванию важности)

1. **Найденный и исправленный баг парсера (не сломать снова).** В `.bat` путь к `winws.exe`
   стоит в кавычках, поэтому при отрезании части строки после `winws.exe` остаётся
   закрывающая `"`, которая «открывает» кавычку для токенизатора и склеивает **всю** команду
   в один аргумент (симптом: у стратегии «1 аргумент», но работающая видимость описания).
   Исправлено снятием ведущих `"`/`^` в `ExtractCommand`. Регрессионный тест — harness (§6.3).
2. **`sc.exe` и кавычки.** Аргументы передаются через `ProcessStartInfo.ArgumentList`
   (`binPath=`, значение отдельными токенами). Значение — одна строка с внутренними кавычками
   вокруг пути. Если будете менять — проверяйте на пути с пробелами/кириллицей.
3. **`SegmentedControl`.** `ListBox.SelectedIndex` игнорирует установку до появления элементов,
   поэтому в контроле есть `ApplyIndex()` на `Loaded`, `StatusChanged` и защита `_syncing`.
   При добавлении новых сегментов проверьте, что выделение восстанавливается после
   `ReloadFromEngine()` (страницы пересоздают списки опций при обновлении движка).
4. **Локализация вывода утилит.** `WinServices.Query` парсит `sc query` (ключевые слова
   `STATE`, коды вроде `1060`), диагностика — вывод `netsh`/PowerShell. Работает и на русской,
   и на английской Windows, но **если Microsoft изменит формат вывода — разбор сломается**.
   При доработке диагностики предпочитайте WMI/PowerShell с `ConvertTo-Json`, а не разбор текста.
5. **`Shell.Run` по умолчанию ограничен 30 с**, вывод читается в CP866
   (`Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)` вызывается в `App.OnStartup`
   и в статическом конструкторе `Shell`). Долгие операции (например, `netsh int ip reset`)
   передавайте с явным таймаутом.
6. **Обновление движка — только zip/tar.gz.** Для `.rar` показывается сообщение со ссылкой.
   Идея на будущее: искать 7-Zip/WinRAR в системе и распаковывать через них.
7. **GitHub API rate limit (60/ч).** Не опрашивайте API по таймеру. Сейчас проверка — по кнопке
   и один раз при запуске (с задержкой 2.5 с). Для «тихих» проверок используйте
   `.service/version.txt` (raw-ссылка, лимитов у GitHub RAW практически нет).
8. **Таймер статуса каждые 3 с** вызывает `sc query zapret` (запуск процесса). Это заметно в
   диспетчере задач. Кандидат на оптимизацию: кэшировать состояние, опрашивать по событию
   завершения процесса/службы (или `ServiceController`/WMI-подписку).
9. **`AppSettings` без `INotifyPropertyChanged`.** Часть настроек привязана в XAML напрямую
   (`Settings.IncludePrerelease` и др.) — двусторонняя запись работает, но программное изменение
   не обновит чекбокс. При доработке настроек либо переводите на свойства VM, либо добавляйте
   уведомления.
10. **Завершение приложения.** `ShutdownMode=OnExplicitShutdown` + `MainWindow.OnClosing`
    вызывает `ShutdownAsync().Wait(6000)` и затем `App.ShutdownApp()` (который ещё раз
    останавливает обход) — двойной вызов безвреден, но при рефакторинге следите, чтобы
    выход не блокировал UI-поток дольше 6 с.
11. **Трей.** Иконка берётся из exe (`Icon.ExtractAssociatedIcon`), иначе `SystemIcons.Shield`.
    `NotifyIcon` из WinForms требует `UseWindowsForms=true` (уже включено). Создание трея
    обёрнуто в try/catch — при сбое приложение продолжит работать.
12. **Права.** Манифест требует администратора всегда: часть операций (службы, WinDivert, hosts)
    без него невозможна. Если решите сделать «непривилегированный режим», проверяйте
    `Shell.IsAdmin()` и оставляйте баннер с кнопкой «Перезапустить от администратора»
    (`MainWindow` уже содержит такую нижнюю плашку).
13. **Парсер и новые форматы.** Если Flowseal добавит новые переменные в `.bat` или изменит
    структуру запуска, парсер вернёт токены с `%…%` — они попадут в аргументы как есть, и
    `winws.exe` упадёт с ошибкой. Harness печатает такие токены как «не раскрыт плейсхолдер» —
    запускайте его после выхода новых релизов движка.
14. **Проверка версий.** `CompareVersions` нормализует версии (`1.9.9d` → `000001.000009.000009.d`),
    так что буквенные суффиксы и двузначные номера сравниваются корректно. Не заменяйте на
    `Version.Parse` — он падает на `1.9.9d`.

---

## 9. Открытые вопросы к пользователю и бэклог

**Ждём от пользователя:**
1. Правки по макету `docs/mockup.html` (он ревьюит интерфейс по нему: «в разделе X убери/добавь…»).
2. Что за «парочку функций» он хотел добавить изначально — **обязательно уточнить**,
   это может изменить приоритеты бэклога.
3. Нужно ли самообновление GUI (`AutoCheckGuiUpdates`/`GuiRepo` в настройках уже есть, логики нет).
   Для этого понадобится его репозиторий и формат релизов (можно переиспользовать `EngineService`).
4. Нужна ли локализация EN и настройка акцентного цвета.

**Бэклог (согласованный ориентир):**
- Автотест стратегий: порт `utils\test zapret.ps1` + `utils\targets.txt` — последовательный запуск
  стратегий, проверка сайтов, вывод «какая стратегия работает у вас». Сильный UX-выигрыш,
  технически: запускать `winws.exe` по очереди, проверять доступность через `ConnectionTester`,
  собирать рейтинг, предлагать лучшую.
- Визуальный редактор аргументов `winws` (конструктор/тюнинг: `--dpi-desync*`, `--filter-*`,
  фейки из `bin`, с подсказками и валидацией), сохранение как новый `.bat`.
- Профили под провайдеров/сети (например, «домашний Wi-Fi», «мобильный») + автопереключение
  при падении доступности.
- Расписание (обход по таймеру/дням).
- Настройка DoH из приложения, проверка DNS-подмены.
- Полноценный редактор пользовательских списков внутри GUI (сейчас — Блокнот).
- Самообновление GUI, локализация, акцентный цвет.

---

## 10. Что не делать (анти-паттерны)

- Не хардкодить список стратегий и не копировать их аргументы в код — источник истины `.bat` движка.
- Не менять формат `ipset-all.txt`/флагов фильтров: пользователь может пользоваться и
  оригиналами (`general*.bat`, `service.bat`) параллельно с GUI.
- Не класть в репозиторий бинарники zapret (`winws.exe`, `WinDivert*`, `*.bin`, списки) —
  лицензия/антивирусы/размер; всё скачивается из официального репозитория.
- Не добавлять внешние NuGet-пакеты «для удобства» (кроме уже используемого
  `System.Text.Encoding.CodePages`): сборка должна быть воспроизводимой и лёгкой.
- Не делать вывод на английском без запроса пользователя и не переводить термины движка.
- Не забывать: XAML-привязки не проверяются компилятором — без `tools/check_bindings.py`
  ошибки вылезут только у пользователя в рантайме (в WPF это «пустые» элементы, а не исключение).

---

## 11. Журнал работы (что было сделано в этой сессии)

1. Изучен репозиторий Flowseal: релизы/ассеты, `service.bat` целиком (меню, `sc create`,
   режимы ipset/game filter, обновление, диагностика, hosts), структура `bin/`, `lists/`, `utils/`.
2. Спроектирован и реализован проект: WPF UI (7 страниц, тёмная/светлая тема), ядро (парсер,
   движок/обновления, службы, диагностика, конфиг, журнал, трей), CI на GitHub Actions, README, LICENSE, макет.
3. Проверки: Release-сборка без предупреждений; `dotnet publish` self-contained (≈139 МБ);
   скриптовая проверка всех привязок/ресурсов XAML; **прогон парсера на реальных .bat 1.10.2**
   → найден и исправлен баг с кавычкой (§8.1) и поправлена естественная сортировка
   (`general` теперь первый, а не последний); повторный прогон — 22 стратегии, 0 проблем.
4. Исходники переданы пользователю архивом, он загрузил их в свой GitHub-репозиторий.
   Со следующего этапа работа идёт **напрямую через git**: агент клонирует репозиторий,
   коммитит и пушит сам (см. §1 «Воркфлоу»), архивы больше не используются.

**Первые шаги следующего агента:**
1. Получить у пользователя адрес репозитория и токен доступа, склонировать проект,
   настроить `user.name`/`user.email` для коммитов.
2. Прогнать три проверки (команды в §6) на текущем состоянии кода и отчитаться о базе.
3. Уточнить у пользователя список правок и «те самые функции» из §9.
4. Внести правки, проверить, закоммитить и запушить в `main`; дождаться зелёного workflow
   GitHub Actions и дать пользователю ссылку на артефакт сборки.
5. Для раздачи готового exe — поднять `<Version>` в csproj, поставить тег `vX.Y.Z`, запушить тег
   и прислать ссылку на GitHub Release. Обновить README/HANDOFF/mockup при необходимости.
6. Попросить пользователя прогнать exe на Windows и прислать журнал со страницы «Журнал»,
   устранить рантайм-замечания из §3.
