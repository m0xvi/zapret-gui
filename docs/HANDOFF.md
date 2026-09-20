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

### Мониторинг ресурсов (MonitoringPage)
- Встроенные YouTube/Discord/GitHub и пользовательские URL сохраняются в `AppSettings.MonitorTargets`.
- Отдельные DNS-, TCP- и HTTPS-проверки выполняются без системного прокси; отдельный режим TCP-задержки используется для игровых серверов.
- Сравнение «без обхода / с текущей стратегией» классифицирует результат: ресурс доступен,
  обход помогает (вероятная DPI-блокировка), стратегия мешает или проблема внешняя; показывается уровень уверенности, без выдуманного имени провайдера.
- После двух последовательных подтверждений сбоя можно автоматически проверить стратегии и переключиться на лучшую; между попытками действует cooldown 10 минут.
- Для игры до трёх альтернатив проверяются непосредственно на выбранном игровом адресе, а не на общем наборе сайтов; домены добавляются в `list-general-user.txt` только кнопкой пользователя.
- Фоновая проверка включается в настройках, уведомления можно отключить, приложение уже умеет запускаться при входе и оставаться в трее.

### Стратегии (StrategiesPage)
- Список автоматически строится из `*.bat` в корне папки движка (кроме `service*.bat`),
  естественная сортировка (`general`, `general (ALT)`, `general (ALT2)`, … `general (ALT10)`).
- Поиск, фильтр по категориям (`FAKE TLS AUTO` / `ALT` / `SIMPLE FAKE` / `БАЗОВАЯ` / `EXP`),
  чип «только рекомендуемые»; рекомендуемая = `general (FAKE TLS AUTO)`.
- Автогенерируемое человекочитаемое описание: `fake`, `multisplit`, `multidisorder`,
  `фейк TLS с SNI …`, `фейк QUIC (UDP 443)`, `повторы N`, `fooling badseq`, `все протоколы (игры)`,
  `ip-id=zero`, `Discord (голос/медиа)`.
- Кнопка проверки у каждой стратегии и последовательная проверка всех стратегий: временный запуск
  `winws.exe`, параллельная TCP+HTTP-проверка YouTube/Discord/GitHub, восстановление прежнего обхода,
  оценка результата и предложение лучшей стратегии.
- При первом запуске после установки движка автоматически запускаются диагностика и автоподбор;
  обе функции отключаются в настройках и выполняются только один раз.
- Панель деталей: полный список аргументов `winws.exe`, «Запустить», «Проверить», «Установить в службу»,
  «Сделать основной», «Открыть .bat», «Скопировать команду».

### Обновления (UpdatesPage)
- Проверка релизов: GitHub API `releases?per_page=20` (фильтр draft, флаг prerelease),
  резервный путь — `.service/version.txt` (как в `service.bat`), при недоступности API.
- Скачивание zip-ассета с прогрессом → распаковка (zip и tar.gz) → **слияние с сохранением
  пользовательских файлов** → маркер версии `.gui-engine-version` → восстановление режимов
  `ipset`/игрового фильтра → автоперезапуск обхода, если он работал.
- Обновление `lists\ipset-all.txt` (с учётом текущего режима: при `none`/`any` пишется в `.backup`).
- Проверка и **безопасное применение `hosts`** (маркеры блока + бэкап в `%APPDATA%\ZapretGUI\backups`).

### Диагностика (DiagnosticsPage) — 14 проверок + DPI-проверка
Результаты обычной диагностики и DPI сохраняются в `%APPDATA%\ZapretGUI\diagnostics-history.json`
и загружаются при следующем запуске. Кнопка «Проверить DPI» повторяет логику набора
`DPI checkers` из исходного `utils\test zapret.ps1`: независимые DNS-, TCP- и HTTPS-пробы,
буфер 64 КБ, HTTP/1.1, TLS 1.2 и TLS 1.3, до 12 узлов, без системного прокси. Для одного
узла дополнительно выполняется сравнение прямого соединения и текущей стратегии обхода
с уровнем и уверенностью результата; тайм-аут HTTPS помечается как возможный паттерн DPI
16–20 КБ, но не используется для определения точного провайдера. Загруженные после старта
результаты помечаются как устаревшие, DPI-проверку можно отменить с восстановлением обхода.
Обновление движка проверяет размер и SHA-256 архива из GitHub API, а при ошибке копирования
восстанавливает изменённые файлы из временной резервной копии.

Права администратора; файлы `bin` (`winws.exe`, `WinDivert64.sys`, `WinDivert.dll`); кириллица/
спецсимволы в пути; OneDrive в пути; служба `BFE`; TCP timestamps; системный прокси (реестр);
активные VPN-адаптеры (PowerShell `Get-NetAdapter`); конфликтующие службы (AdguardSvc, Killer,
Intel Connectivity, Check Point, SmartByte); другие обходы (`goodbyedpi`, `dpitunnel`, `tg-ws-proxy`,
`byedpi` + чужие `winws.exe` по пути процесса); остаточные службы `WinDivert`/`WinDivert14`;
состояние службы `zapret`; наличие строк GitHub в `hosts`; размер кэша Discord.
Исправления в один клик: запустить BFE с включением автозапуска, включить timestamps с повторной
проверкой фактического состояния, удалить все службы, очистить кэш Discord, сбросить сеть
(`netsh winsock reset`, `netsh int ip reset all`, `netsh winhttp reset proxy`, `ipconfig /flushdns` —
с предупреждением о перезагрузке). Ответы `sc.exe` и `netsh` показываются при отказе Windows, без ложного сообщения об успехе.

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
Автотест стратегий по полному `targets.txt`; визуальный редактор аргументов `winws`;
профили под провайдеров; настройка DoH; редактор списков внутри GUI;
безопасное самообновление GUI уже реализовано; остаются локализация EN.

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
│  ├─ StrategyTesting.cs     результаты проверки стратегий и рейтинга автоподбора
│  ├─ ResourceMonitoring.cs  цели, пробы, классификация блокировок, списки
│  ├─ WinServices.cs         обёртка sc.exe, реестр, RemoveEverything, BFE/timestamps
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
│  ├─ MonitoringViewModel.cs ресурсы, фоновые проверки, анализ причин и автоподбор
│  ├─ LogsViewModel.cs       фильтры уровней, AutoScroll + событие ScrollToEndRequested
│  └─ SettingsViewModel.cs   автосохранение настроек, автозапуск, пути, валидация движка
├─ Views/                MainWindow (сайдбар, заголовок, ContentControl PageHost, трей) + 8 страниц
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
15. **Безопасное обновление движка (v1.0.3, исправление BSOD).** Перезапись
    `bin\WinDivert64.sys` «на лету», пока драйвер в памяти ядра, роняла Windows в BSOD.
    Поэтому перед `CopyEngine` обязательно: `BypassController.PrepareForEngineUpdateAsync`
    (стоп обхода → kill `winws.exe` → `WinServices.StopForEngineUpdateAsync` для
    `zapret`/`WinDivert`/`WinDivert14` → `WaitForDriverUnloadAsync` с паузой 2 с).
    `EngineService.DownloadAndInstallAsync` дублирует защиту внутри
    (`PrepareFilesForUpdateAsync`), а `CopyEngine` пропускает заблокированные файлы
    драйвера с предупреждением (`EngineUpdateResult.Warnings`) вместо падения.
    Не удаляйте эти вызовы и не меняйте порядок «стоп → пауза → копирование».
16. **Окна ошибок гасятся.** `App.OnDispatcherUnhandledException` показывает диалог
    не чаще раза в 10 секунд и только для нового текста ошибки; повторы пишутся
    в `AppLog` как предупреждения. `TaskScheduler.UnobservedTaskException` — только
    в журнал. Не возвращайте безусловный `MessageBox` на каждую ошибку.

---

## 9. Открытые вопросы к пользователю и бэклог

**Ждём от пользователя:**
1. Правки по макету `docs/mockup.html` (он ревьюит интерфейс по нему: «в разделе X убери/добавь…»).
2. Что за «парочку функций» он хотел добавить изначально — **обязательно уточнить**,
   это может изменить приоритеты бэклога.
3. Самообновление GUI реализовано через официальный репозиторий `m0xvi/zapret-gui`:
   GitHub digest SHA-256, staged portable EXE, отдельный elevated-процесс после выхода,
   резервная копия и восстановление после прерванной замены.
4. Нужна ли локализация EN и настройка акцентного цвета.

**Бэклог (согласованный ориентир):**
- Расширить проверку стратегий дополнительными целями из `utils\test zapret.ps1` и `utils\targets.txt`.
- Визуальный редактор аргументов `winws` (конструктор/тюнинг: `--dpi-desync*`, `--filter-*`,
  фейки из `bin`, с подсказками и валидацией), сохранение как новый `.bat`.
- Профили под провайдеров/сети (например, «домашний Wi-Fi», «мобильный») + автопереключение
  при падении доступности.
- Расписание (обход по таймеру/дням).
- Настройка DoH из приложения, проверка DNS-подмены.
- Полноценный редактор пользовательских списков внутри GUI (сейчас — Блокнот).
- Локализация EN, акцентный цвет.

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
2. Спроектирован и реализован проект: WPF UI (8 страниц, тёмная/светлая тема), ядро (парсер,
   движок/обновления, службы, диагностика, конфиг, журнал, трей), CI на GitHub Actions, README, LICENSE, макет.
3. Проверки: Release-сборка без предупреждений; `dotnet publish` self-contained (≈139 МБ);
   скриптовая проверка всех привязок/ресурсов XAML; **прогон парсера на реальных .bat 1.10.2**
   → найден и исправлен баг с кавычкой (§8.1) и поправлена естественная сортировка
   (`general` теперь первый, а не последний); повторный прогон — 22 стратегии, 0 проблем.
4. Исходники переданы пользователю архивом, он загрузил их в свой GitHub-репозиторий.
   Со следующего этапа работа идёт **напрямую через git**: агент клонирует репозиторий,
   коммитит и пушит сам (см. §1 «Воркфлоу»), архивы больше не используются.
5. **Сессия v1.0.1:** исправлена привязка `ProgressBar.Value` в `UpdatesPage.xaml`
   (`Mode=OneWay`, публичный сеттер `Progress`), версия поднята до 1.0.1, релиз `v1.0.1` собран.
6. **Сессия v1.0.3 (исправление BSOD при обновлении):** обновление движка «на лету»
   роняло Windows в синий экран, т. к. `WinDivert64.sys` перезаписывался при загруженном
   драйвере. Реализовано безопасное обновление: `PrepareForEngineUpdateAsync` в
   `BypassController` (стоп обхода → kill `winws.exe` → стоп служб `zapret`/`WinDivert`/
   `WinDivert14` → ожидание выгрузки драйвера), `StopForEngineUpdateAsync` +
   `WaitForDriverUnloadAsync` + `IsFileLocked` в `WinServices`, защита в
   `EngineService.DownloadAndInstallAsync`/`CopyEngine` (пропуск заблокированных файлов
   драйвера с `Warnings` вместо падения). Добавлена автоустановка движка при первом
   запуске (`UpdatesViewModel.EnsureEngineInstalledAsync`, вызывается из `App.OnStartup`).
   Окна ошибок гасятся: `OnDispatcherUnhandledException` показывает диалог не чаще
   раза в 10 секунд, повторы — только в `AppLog`; добавлен обработчик
   `TaskScheduler.UnobservedTaskException`. Версия поднята до 1.0.3.
7. **Сессия v1.1.0:** исправлен краш «Стратегий» (`'System.Windows.Style' is not a valid
   value for property 'Template'` — `ComboBoxToggleTemplate` это Style, а подставлялся
   в `Template`; заменено на `Style=`). Добавлен детект старого запрета (`Core/LegacyZapret.cs`
   + `Views/LegacyZapretDialog`): служба zapret из чужой папки (`sc qc`) и чужие winws.exe;
   диалог с тремя действиями + «больше не спрашивать» (`LegacyZapretDismissed`), хук при
   старте (`MainWindow`) и перед запуском (`HomeViewModel.ResolveLegacyAsync`), баннер
   конфликта на главной. Диагностика: у 12 из 14 пунктов кнопки автоисправления
   (`DiagnosticItem.FixId/FixLabel`, `FixItemCommand`, исполнение в `DiagnosticsService`).
   На главной — круглая кнопка питания (`PowerButton`, `ToggleBypassCommand`).
   Журнал: категории записей (`LogEntry.Category`, `AppLog.Svc*`), фильтр источника
   «Приложение / Обход и служба», живой вывод winws.exe (`Shell.StartWithCapture`).
   Подсказки-тултипы добавлены по всем страницам. `check_bindings.py` больше не
   проверяет привязки с RelativeSource/ElementName (ложные срабатывания у FixItemCommand).
8. **Исправления после тестирования v1.1.0:** автофикс BFE теперь проверяет ответы `sc.exe`,
   а числовые коды состояния службы разбираются на русской и английской Windows. Исправление
   TCP timestamps повторно читает `netsh` и сообщает реальный отказ Windows вместо ложного
   успеха. В «Стратегиях» добавлена временная проверка каждой/всех стратегий на YouTube,
   Discord и GitHub с восстановлением прежнего состояния. Первый запуск после установки
   движка может автоматически выполнить диагностику и предложить лучшую стратегию; обе
   функции настраиваются и выполняются один раз.
9. **Сессия v1.2.0:** добавлена страница «Мониторинг» с фоновыми проверками ресурсов, пользовательскими
   URL, TCP-задержкой игровых серверов, сравнением ресурса без обхода и с текущей стратегией,
   классификацией вероятной DPI-блокировки/внешней проблемы/неподходящей стратегии. Добавлены
   безопасное добавление домена в `list-general-user.txt`, уведомления через трей и опциональный
   автоматический подбор стратегии после подтверждённого сбоя. Точное имя провайдера приложение
   не обещает: без внешнего API надёжно определяется тип проблемы и уровень блокировки, а не ISP.
10. **Доработка v1.2.0:** в мониторинг добавлены независимые DNS/TCP/HTTPS-пробы, отображение
    уверенности результата, счётчик двух последовательных сбоев перед автопереключением и
    cooldown 10 минут. Для игровых ресурсов до трёх альтернативных стратегий проверяются
    непосредственно на выбранном игровом TCP-адресе; добавлена отдельная настройка уведомлений.

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

---

## 12. Актуальная передача контекста: сессия 2026-09-18

Этот раздел добавлен для передачи работы следующему агенту. Он относится к последней сессии
по пользовательскому запросу о determinate progress, глубокой DPI-проверке, анализе deep-check
и смене логики рекомендуемой стратегии. Если старые разделы этого файла противоречат этому
разделу в части deep-check/DPI/progress, приоритет имеет этот раздел.

### 12.1. Git-состояние и границы работы

Работа выполнялась в репозитории:

```text
/home/user/zapret-gui
```

Ветка сессии фиксирована:

```text
arena/01a0aa59-zapret-gui
```

Нельзя переключаться на другую ветку, создавать другую ветку или пушить изменения в `main`.
Изменения этой сессии были отправлены в:

```text
origin/arena/01a0aa59-zapret-gui
```

На начало подготовки этой передачи рабочее дерево было чистым, а последний кодовый коммит был:

```text
90a5694 Дать полной DPI-матрице безопасное время
```

В этой передаче добавляется только документация. После её коммита текущий `HEAD` изменится на
новый коммит с `HANDOFF.md`; это не означает изменения рабочей логики приложения.

Пользователь сообщил, что позднее отдельно попросит смержить работу в `main`. На момент этой
передачи merge в `main` не выполнялся. До отдельного явного запроса пользователя не делать
merge и не пушить в `main`.

### 12.2. Запрос пользователя

Пользователь попросил:

- везде, где это возможно, заменить заглушки загрузки и неопределённую анимацию ожидания на
  аккуратный determinate progress bar;
- показывать реальный процент выполнения, текущий этап и, когда возможно, счётчик `N из M`;
- разобрать результаты deep-check и diagnostics из двух JSON-файлов;
- исправить функцию глубокой проверки по результатам анализа;
- сделать DPI-проверку более глубокой и близкой к Flowseal/zapret;
- не выбирать универсальную/provider-specific стратегию, поскольку она не работает в сети
  пользователя;
- после успешного завершения проверки показывать результат зелёным верхним статусным блоком
  в соответствующем разделе;
- сохранить безопасное подтверждение, восстановление обхода, экспорт неполного отчёта и не
  выводить ISP по провайдеру endpoint-а.

Пользовательские файлы, упомянутые в запросе:

```text
zapret-gui-deep-check-20260918-143634.json
zapret-gui-diagnostics-20260918-144014.json
```

### 12.3. Что реализовано в коде

#### Determinate progress

В местах, где число работ известно, добавлены determinate progress-состояния с процентом,
текущим этапом и/или счётчиком `N из M`. Это относится к глубокой проверке, diagnostics, DPI,
а также к соответствующим сценариям Home и First Launch.

Если число работ неизвестно, UI не должен притворяться, что показывает точный процент: вместо
этого показывается честный этап/текстовое состояние. Нельзя возвращать бесконечный spinner там,
где уже известен объём работ.

Основные затронутые места:

```text
src/ZapretGUI/ViewModels/DeepCheckViewModel.cs
src/ZapretGUI/ViewModels/DiagnosticsViewModel.cs
src/ZapretGUI/Views/DeepCheckPage.xaml
src/ZapretGUI/Views/DpiPage.xaml
src/ZapretGUI/Views/DiagnosticsPage.xaml
```

При дальнейшем UI-аудите искать также `IsIndeterminate`, spinner, loader и animation в остальных
XAML/ViewModel-файлах и заменять только те состояния, для которых можно честно вычислить ход.

#### Верхние статусные блоки

После успешной проверки вверху страницы соответствующего раздела отображается зелёный статусный
блок. Для предупреждений используется жёлтая семантика, для ошибок — красная. Статус должен
оставаться видимым после завершения, чтобы пользователь сразу видел итог, а не искал его среди
списка проб.

#### Глубокая проверка и strategy-DPI matrix

Полная deep-check теперь выполняет DPI-suite:

1. без стратегии;
2. под каждой стратегией, которая реально запустилась;
3. если есть `IsSuitable`, под пригодными стартовавшими стратегиями;
4. если пригодных стратегий нет, под всем набором стартовавших стратегий.

Ограничение на первые три стратегии (`Take(3)`) удалено. Полный общий timeout увеличен до 30
минут, потому что матрица «стратегия × DPI-suite» может выполняться десятки минут. Пользовательская
отмена остаётся отдельным механизмом и не подменяется сетевым timeout.

Временный запуск должен включать:

- временную стратегию;
- `ipset=any`;
- восстановление исходного состояния обхода;
- очистку временных параметров в `finally` даже при timeout, ошибке или отмене.

Основные файлы:

```text
src/ZapretGUI/Core/BypassController.cs
src/ZapretGUI/Core/DiagnosticsHistory.cs
src/ZapretGUI/Core/StrategyTesting.cs
src/ZapretGUI/ViewModels/DeepCheckViewModel.cs
src/ZapretGUI/ViewModels/DiagnosticsViewModel.cs
```

#### DPI-suite

Используется suite из 34 endpoint-ов и несколько уровней сетевых проб:

- DNS;
- TCP;
- HTTP/1.1;
- TLS 1.2;
- TLS 1.3;
- общий payload;
- параллельное выполнение;
- retry;
- контрольный endpoint;
- критерий возможного freeze.

Оригинальная логика Flowseal была изучена по:

```text
https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/main/utils/test%20zapret.ps1
```

Также использовался suite:

```text
https://hyperion-cs.github.io/dpi-checkers/ru/tcp-16-20/suite.v2.json
```

Провайдер и страна endpoint-а описывают тестовую инфраструктуру, но не ISP пользователя и не
могут использоваться как доказательство ISP.

Текущая реализация близка к Flowseal, но не является буквальным переносом всех его curl-метрик:
проект использует .NET `HttpClient`, поэтому на Windows отдельно нужно проверить connect timeout,
TLS timeout semantics, upload/download/exit metrics, поведение зависшего запроса и соответствие
критериям freeze.

#### Timeout, ошибки и отмена

Обязательные правила:

- timeout отдельной сетевой пробы не является нажатием пользователем кнопки «Отмена»;
- ошибка одного endpoint-а не останавливает всю глубокую проверку;
- timeout endpoint-а записывается как результат конкретной пробы с диагностическим статусом;
- после него обработка переходит к следующему endpoint-у;
- только отмена общего `CancellationToken` останавливает deep-check полностью;
- timeout после неуспешного TCP не должен автоматически считаться возможным DPI freeze;
- timeout HTTPS после успешного TCP может сохранять диагностический признак возможной DPI-блокировки.

#### DPI-score и рекомендация

Текущая оценка кандидата строится на фактическом результате, приблизительно по формуле:

```text
successful HTTPS * 10 - failed HTTPS * 20 - possible freeze * 100
```

Рекомендация должна учитывать, в порядке приоритета:

- фактический DPI-score;
- успешность контрольных ресурсов;
- стабильность повторов;
- воспроизводимость;
- TCP/DNS/HTTPS;
- задержку;
- отсутствие freeze;
- историю результатов.

Нельзя делать решающим фактором:

- провайдера endpoint-а;
- страну endpoint-а;
- предполагаемый ISP пользователя;
- название `.bat`;
- статический универсальный список рекомендаций.

Рекомендуемая стратегия должна подтверждаться повторяемым результатом именно в сети пользователя.

#### Smoke-совместимость

В `StrategyTesting.cs` требование минимум трёх успешных ресурсов применяется только к реальному
расширенному набору. Старые двухресурсные smoke-тесты остаются валидными. Это исправило регрессию
CoreLogicHarness, который создаёт результаты только для YouTube и Discord.

### 12.4. Коммиты этой работы

Основные коммиты реализации:

```text
8bde523 — детерминированный DPI progress
3585893 — smoke-совместимость и удаление patch-файла
fbc0a39 — DPI под всеми рабочими стратегиями
90a5694 — безопасное время для полной DPI-матрицы
```

Случайный файл, не входивший в рабочий baseline, был удалён:

```text
01a0aa2d-339a-7dc0-b7ef-c8a356a6f492.patch
```

### 12.5. Проверки и CI

Успешно выполнялись:

```bash
python3 tools/check_bindings.py
git diff --check
```

`check_bindings.py` подтвердил 16 XAML-файлов и 90 ключей ресурсов.

Последний CI run:

```text
35361227916
https://github.com/m0xvi/zapret-gui/actions/runs/35361227916
```

Он успешно прошёл Release build, static analysis, Windows harness, controller smoke,
CoreLogicHarness, публикацию обоих вариантов и артефакты.

В GitHub Actions остаётся предупреждение о deprecated Node.js 20 в используемых actions; оно не
повлияло на успешность run.

Локальная сборка в предыдущей среде была невозможна, потому что .NET SDK отсутствовал:

```text
dotnet: command not found
```

### 12.6. Что не удалось завершить и что проверить следующим агентом

Файлы JSON из пользовательского сообщения в предыдущей sandbox-среде отсутствовали:

```text
/home/user/uploads/zapret-gui-deep-check-20260918-143634.json
/home/user/uploads/zapret-gui-diagnostics-20260918-144014.json
```

Поэтому фактический численный анализ именно пользовательских результатов не выполнялся. Следующий
агент должен сначала проверить, появились ли файлы в новой среде или доступны ли они под другим
путём. Нельзя утверждать, что конкретные значения из JSON уже проанализированы, если файлы реально
не прочитаны.

При наличии JSON проверить:

- какие стратегии реально дали лучший результат;
- совпадает ли recommendation с повторяемо работающей стратегией;
- какие endpoint-ы систематически дают timeout/error;
- сколько было успешных DNS/TCP/HTTP/TLS проб;
- есть ли различие между прямым запуском и запуском под стратегией;
- не трактуются ли timeout как cancellation;
- корректно ли сохраняются частичные результаты;
- можно ли экспортировать полный, частичный и ошибочный отчёт;
- совпадают ли отображаемые проценты, счётчики и итоговые статусы с JSON;
- не используется ли metadata endpoint provider как вывод об ISP.

Дополнительно на Windows нужно проверить:

- реальный запуск `winws.exe`;
- временный запуск каждой стратегии;
- восстановление bypass после успеха, timeout, ошибки и отмены;
- отсутствие зависших процессов и временных файлов;
- корректность TLS 1.2/TLS 1.3;
- различие DNS/TCP/TLS/HTTP timeout;
- соответствие длительной DPI-проверки логике Flowseal;
- визуальный вид progress bar и зелёных итоговых карточек;
- работу экспорта после частичного результата и ошибки.

### 12.7. Правила, которые нельзя нарушить

- Работать только в `arena/01a0aa59-zapret-gui`.
- Не пушить в `main`, пока пользователь явно не попросит merge.
- Не удалять SafeMode и MessageBox-подтверждения.
- Не применять автоматически случайно сгенерированные параметры к рабочему обходу.
- Всегда восстанавливать обход после временной проверки.
- Всегда очищать временное состояние в `finally`.
- Не останавливать полную матрицу из-за одного endpoint timeout/error.
- Не смешивать системный timeout с пользовательской отменой.
- Сохранять экспорт неполного, частичного и ошибочного отчёта.
- Не выводить ISP пользователя по provider/стране тестового endpoint-а.
- Не показывать точный процент, если его невозможно вычислить.
- Не возвращать ограничение DPI только на три стратегии.
- После правок XAML запускать `python3 tools/check_bindings.py`.
- Перед коммитом запускать `git diff --check`.
- Не добавлять в репозиторий бинарники zapret и крупные временные артефакты.
- Тексты UI, комментарии и сообщения проекта писать на русском.
- Для кистей XAML использовать `DynamicResource`.

### 12.8. Готовый промпт для следующего агента

```text
Ты продолжаешь работу над репозиторием /home/user/zapret-gui.

Работай строго в ветке arena/01a0aa59-zapret-gui. Не переключайся на другую ветку, не создавай
новую ветку и не пушь в main. Пользователь позднее отдельно попросит merge в main; до этого
merge не выполняй.

Сначала прочитай:
- AGENTS.md
- docs/HANDOFF.md, особенно раздел 12 «Актуальная передача контекста: сессия 2026-09-18»

Текущий основной кодовый коммит до добавления этой документации:
90a5694 Дать полной DPI-матрице безопасное время

Основная уже выполненная работа:

1. Добавлен determinate progress там, где можно честно вычислить процент:
   - текущий этап;
   - процент;
   - N из M.
   Если точный процент неизвестен, показывай этап/счётчик, а не искусственный процент.

2. В DeepCheckPage, DpiPage и DiagnosticsPage есть верхние статусные блоки:
   зелёный для успешной проверки, жёлтый для предупреждения, красный для ошибки.

3. DPI-suite расширен до 34 endpoint-ов и включает DNS, TCP, HTTP/1.1, TLS 1.2, TLS 1.3,
   общий payload, параллельность, retry и freeze-критерий.

4. Полная strategy-DPI matrix запускается под всеми реально стартовавшими стратегиями.
   Если есть IsSuitable — используются пригодные стартовавшие стратегии; если их нет — весь
   набор стартовавших. Ограничение Take(3) удалено.

5. Для временной проверки используются ipset=any, восстановление обхода и cleanup в finally.
   Общий timeout увеличен до 30 минут.

6. Timeout одного endpoint-а не равен пользовательской отмене. Он должен быть записан как
   результат конкретной пробы, после чего проверка переходит к следующему endpoint-у. Только
   общий CancellationToken пользователя должен останавливать deep-check.

7. Экспорт должен работать для полного, частичного, отменённого и ошибочного отчёта.

8. Recommendation должна опираться на фактический результат сети: DPI-score, успешность
   контрольных ресурсов, повторяемость, стабильность, TCP/DNS/HTTPS, задержку и отсутствие
   freeze. Нельзя делать решающим фактором provider endpoint-а, название .bat или универсальный
   список ISP-рекомендаций.

9. В StrategyTesting.cs сохранена совместимость: минимум три успешных ресурса требуется только
   для расширенного набора; двухресурсные smoke-объекты остаются валидными.

Сначала проверь:

- git status --short --branch
- git log --oneline -5
- наличие файлов:
  /home/user/uploads/zapret-gui-deep-check-20260918-143634.json
  /home/user/uploads/zapret-gui-diagnostics-20260918-144014.json
- оставшиеся IsIndeterminate/spinner/loading/animation в XAML и ViewModels.

В предыдущей среде JSON отсутствовали, поэтому не утверждай, что их численный анализ уже
выполнен. Если они доступны сейчас — прочитай и отдельно опиши реальные найденные проблемы.

Проверь особенно:
- false positive recommendation;
- систематические timeout endpoint-ов;
- различие прямой проверки и проверки под стратегией;
- корректность статусов Timeout/Failed/Cancelled/Completed;
- экспорт неполного отчёта;
- совпадение progress с реальным числом работ;
- восстановление обхода после исключений.

Сохраняй SafeMode, MessageBox-подтверждения, безопасное применение стратегий, cleanup в finally
и восстановление обхода. Не запускай случайные параметры на рабочем обходе без подтверждения.
Не трактуй сетевой timeout как отмену пользователем.

После изменений выполни:

- git diff --check
- python3 tools/check_bindings.py
- доступные harness/тесты
- GitHub Actions после push

Локальный .NET SDK ранее отсутствовал (`dotnet: command not found`), поэтому не имитируй локальную
сборку, если SDK снова недоступен. В отчёте честно укажи, что проверено локально, что проверено
CI и что требует Windows runtime.

В конце сообщи:
- реальные данные из JSON, если они доступны;
- найденные проблемы;
- изменённые файлы;
- выполненные проверки;
- CI run и его статус;
- что осталось проверить на Windows;
- что merge в main не выполнялся без отдельного запроса пользователя.

---

### Итерация 2026-09-18 (Determinate Progress, Empirical Scoring & UI Polish)

1. **Замена спиннеров и плейсхолдеров на честные детерминированные индикаторы:**
   - `MonitoringPage.xaml` / `MonitoringViewModel.cs`: Добавлен детерминированный прогресс-бар `ProgressValue` / `ProgressMaximum` с расчётом `ProgressPercentText` и текстовым статусом по проверяемым ресурсам (`1 из N: Name…`).
   - `StrategiesPage.xaml` / `StrategiesViewModel.cs`: Заменены спиннеры `BusySpinner` в блоках генерации и проверки кандидатов на структурированные индикаторы и детерминированный прогресс-бар `CandidateEvaluationProgressValue` / `CandidateEvaluationProgressMaximum` с процентами.
   - `BypassController.cs` / `StrategiesViewModel.cs`: В `TestStrategyAsync` добавлен `IProgress<string>? progress`, транслирующий детальный прогресс подключения по 8 контрольным ресурсам из `ConnectionTester.RunAsync`.
   - `UpdatesPage.xaml` / `UpdatesViewModel.cs`: В панели загрузки обновления движка спиннер заменён на статусную строку с процентом `ProgressPercentText` и `ThinProgress`.

2. **Эмпирическая приоритизация рекомендаций:**
   - В `StrategyCandidateEvaluation` подтверждена формула `Score = SuccessfulRepeats * 10000 + PassedChecks * 100 + (RepeatCount == 0 ? 0 : SuccessfulRepeats * 100 / RepeatCount) + LatencyScore`, где стабильность и эмпирические результаты проверок строго превалируют над эвристиками.
   - Добавлен 33-й тест в `tools/CoreLogicHarness/Program.cs`: `Score кандидата приоритизирует эмпирические результаты проверок`.

3. **Исправление ошибки двухсторонней привязки ProgressBar (v1.2.2):**
   - В WPF `ProgressBar.Value` (наследуемый от `RangeBase.ValueProperty`) имеет `BindsTwoWayByDefault = true`.
   - На всех страницах (`HomePage.xaml`, `DeepCheckPage.xaml`, `DiagnosticsPage.xaml`, `DpiPage.xaml`, `FirstLaunchPage.xaml`, `MonitoringPage.xaml`, `StrategiesPage.xaml`, `UpdatesPage.xaml`) ко всем привязкам `ProgressBar.Value`, `Maximum`, `IsIndeterminate` явно добавлен `Mode=OneWay`.
   - Во всех ViewModels (`HomeViewModel`, `DeepCheckViewModel`, `DiagnosticsViewModel`, `FirstLaunchViewModel`, `MonitoringViewModel`, `StrategiesViewModel`, `UpdatesViewModel`) сеттеры прогресс-свойств сделаны открытыми (`public set => Set(ref ...)`), исключая исключения `InvalidOperationException` при запуске.
   - В `tools/check_bindings.py` добавлен статический валидатор, требующий `Mode=OneWay` для всех привязок `ProgressBar`.
   - Версия приложения обновлена до `1.2.2` в `ZapretGUI.csproj` и динамически выведена в `MainWindow.xaml`.

4. **Верификация:**
   - `python3 tools/check_bindings.py`: Проверено 16 XAML-файлов и 90 ключей ресурсов — 0 ошибок.
   - `git diff --check`: 0 предупреждений по форматированию и пробелам.

---

### Итерация 2026-09-19 (v1.2.3 · Navigation Architecture, Domain/Game Lists UX & Unified Diagnostics)

1. **Рефакторинг навигации и бокового меню:**
   - Из постоянного бокового меню (`MainViewModel.NavItems`) удалён пункт «Первый запуск» (`first-run`). Мастер первого запуска отображается только при первом открытии приложения либо запускается по кнопке «Запустить мастер первого запуска заново» в настройках.
   - Маршрутизация навигации: ссылки на старые диагностические ключи (`monitoring`, `dpi`, `deep-check`, `results`) прозрачно перенаправляются в объединённый раздел `DiagnosticsPage` с переключением на соответствующую подвкладку.

2. **Чёткое разделение разделов приложения:**
   - **Обновления (`UpdatesPage`):** Все инструменты обновления: движок zapret, списки ipset (любой/загруженный), файл hosts (GitHub/Discord) и самообновление GUI.
   - **Списки (`UserListsPage`):** Управление пользовательскими и встроенными списками доменов (`list-general-user`, `list-discord-user`, `list-youtube-user`, `list-exclude-user`, `ipset-exclude-user`), режим фильтрации ipset (`Loaded`/`Any`/`None`) и режим игрового фильтра (`Disabled`/`TcpAndUdp`/`TcpOnly`/`UdpOnly`) с кнопкой перезапуска обхода в один клик.
   - **Настройки (`SettingsPage`):** Исключительно параметры приложения (автозапуск, автообход, трей, подтверждения, темы, масштабирование, сброс кэша, сброс настроек и повторный запуск мастера настройки).
   - **Проверка и диагностика (`DiagnosticsPage`):** Объединённый диагностический центр с 5 подвкладками:
     1. `⚡ Экспресс (Мониторинг)`: фоновая проверка ключевых сервисов (YouTube, Discord) и выявление проблем.
     2. `🌐 Проверка DPI (34 узла)`: все 34 узла DNS/TCP/HTTP/TLS Flowseal-проверки.
     3. `🔬 Deep Check (Матрица)`: подбор параметров стратегии по многодоменной матрице.
     4. `🛠 Аудит системы`: права Windows, службы, WinDivert, сеть, исправления в один клик.
     5. `📊 Сводные результаты`: единый обзор статусов, история проверок и экспорт JSON/ZIP.

3. **История тестирования стратегий (`StrategyEvaluationHistory`):**
   - Добавлена очистка истории (`ClearHistoryCommand`, `StrategyEvaluationHistoryStore.Clear()`) с атомарной записью через временный файл.
   - Потокобезопасное добавление записей в UI-коллекцию через `Application.Current.Dispatcher`.
   - Вывод количества записей и карточек истории на странице стратегий.

4. **Прогресс-бары с процентами:**
   - Проверены и снабжены процентными индикаторами все прогресс-бары приложения (Express, DPI, Deep Check, Audit, Strategies, Updates, FirstLaunch, Home).
   - Все привязки `ProgressBar` используют `Mode=OneWay` и открытые геттеры/сеттеры во ViewModels.

5. **Версионирование и валидация:**
   - Версия приложения обновлена до `1.2.3` в `ZapretGUI.csproj`.
   - `python3 tools/check_bindings.py` проверил 16 XAML-файлов и 90 ресурсов — 0 ошибок.
   - `git diff --check` — 0 ошибок форматирования.

---

### Итерация 2026-09-19 (v1.2.3 Update · High-Density Strategies View, Header Badges & Segmented Controls Fix)

1. **Исправление сжатых кнопок переключателей (`SegmentedControl`):**
   - В `SegmentItemStyle` (`Themes/Controls.xaml`) отключен перенос слов (`TextWrapping="NoWrap"`), добавлен `TextTrimming="CharacterEllipsis"` и скорректирован паддинг (`12,7`), исключая перенос букв на новые строки (как на скриншоте «Выкл ючен», «Тольк о TCP»).
   - В `HomePage.xaml` убраны жёсткие ограничения ширины (`Width="220" MaxWidth="330"`) для игрового фильтра и ipset, заменены на `MinWidth="380"` / `MinWidth="340"` с `HorizontalAlignment="Right"`, благодаря чему на экранах любой ширины и в полноэкранном режиме кнопки отображаются просторно и пропорционально.
   - В `SettingsPage.xaml` для переключателя темы задан `MinWidth="280"` с выравниванием по правому краю.

2. **Компактное высокоплотное отображение стратегий (`StrategiesPage`):**
   - Переработана разметка страницы стратегий на современный **двухколоночный Master-Detail layout**:
     - **Левая колонка:** компактный список всех доступных стратегий со стилем `CompactRowItemStyle` (высота строк ~38-42px). Теперь на одном экране одновременно помещаются 12-16 стратегий без необходимости прокрутки через огромные карточки. Каждая строка содержит статус проверки, имя стратегии, бейдж категории, отметку «рекомендуется» и быстрые кнопки «Применить» и «Тест».
     - **Правая колонка:** детальная панель выбранной стратегии — запуск/применение, проверка, установка в службу, копирование аргументов winws.exe, открытие .bat, нормализованные признаки, результаты тестов и сворачиваемая история проверок с кнопкой очистки.

3. **Отображение активной стратегии и мониторинга узлов в верхней панели:**
   - В верхний заголовок окна (`MainWindow.xaml`) рядом с индикатором статуса обхода добавлены интерактивные бейджи:
     - **Активная стратегия:** отображает имя текущей запущенной стратегии (или выбранной стратегии по умолчанию со статусом) и по клику переходит в раздел «Стратегии».
     - **Краткий мониторинг узлов:** статус доступности контрольных ресурсов (`5/5 OK` / `Предупреждение` / `Ошибка`) с подробным всплывающим тултипом по каждому ресурсу и переходом в «Мониторинг» по клику.
   - Добавлены свойства `ActiveStrategySummaryText`, `ActiveStrategyKey`, `ActiveStrategyTooltipText`, `MonitoringSummaryText`, `MonitoringSummaryKey`, `MonitoringSummaryTooltip` и команды `NavigateStrategiesCommand`, `NavigateMonitoringCommand` в `MainViewModel`.

---

### Итерация 2026-09-20 (v1.2.4 Polish · Background Checks, Unbiased Recommendations, Engine Detection & Clean Icons)

1. **Мастер первого запуска (`FirstLaunchPage` / `FirstLaunchViewModel`):**
   - Селектор стратегии на 4-м шаге переведён на фирменный `Style="{StaticResource AppComboBox}"`.
   - При смене шага мастера вызывается `RefreshStrategyList()`, который перечитывает актуальные `.bat` файлы и активирует кнопку «Проверить все стратегии».

2. **Обновления (`UpdatesPage` / `UpdatesViewModel`):**
   - Добавлены зелёные галочки (`&#xE73E;`) при актуальности файлов `ipset-all.txt` и `hosts`.
   - Кнопки `[Проверить]` и `[Применить]` объединены в единое смарт-действие **«Обновить hosts»** (`UpdateHostsCommand`), проверяющее файл и применяющее актуальные записи.
   - Устранены наезжающие и слипающиеся отступы между карточками «Списки и hosts», «Что нового» и «О программе».

3. **История проверок и автоконструктор (`StrategyEvaluationHistory` / `StrategiesViewModel`):**
   - Добавлено свойство `DisplayName` и информативный `ResultSummaryText` с выводом числа пройденных проверок, стабильности и времени отклика.
   - Поля истории проверок отображаются с полными данными без пустых строк.

4. **Фоновая работа и статусная строка проверок в шапке окна (`MainWindow.xaml` / `MainViewModel.cs`):**
   - Проверки (DPI-тест 34 узлов, Deep Check матрица, проверка стратегий, аудит системы, мониторинг) выполняются в фоновых задачах и не прерываются при скрытии окна или переходе между вкладками.
   - В верхнем статусном баре добавлен индикатор активной фоновой операции (`IsAnyCheckRunning`, `ActiveCheckStatusText`, спиннер и кнопка быстрого перехода).

5. **Устранение предвзятых рекомендаций (`StrategyParser.cs` / `StrategyStore.cs`):**
   - Полностью убрана жёсткая привязка `IsRecommended` к стратегии `general (FAKE TLS AUTO)`.
   - Статус рекомендации присваивается исключительно по результатам реальных эмпирических проверок (`TestResult.IsSuitable == true`).

6. **Определение версии движка и окно «О программе» (`EngineService.cs` / `AboutPage.xaml`):**
   - В `EngineService.ReadVersion` добавлен опрос маркеров `.gui-engine-version`, `version.txt`, `service.bat`, `blockcheck.sh`, FileVersion `winws.exe` и проверка `IsEngineReady`, исключая ложный статус «не установлен».
   - В `AboutPage.xaml` версия Zapret GUI привязана к динамическому `{Binding AppVersion}` (вместо статического 1.0.0).

7. **Иконки действий в списках (`UserListsPage`, `MonitoringPage`, `DiagnosticsPage`):**
   - Текстовые кнопки «Изменить» и «Удалить» заменены на компактные аккуратные иконки карандаша (`&#xE70F;`) и корзины (`&#xE74D;`) (`SmallIconButton`).
   - Скруглены углы выделения элементов списков (`CompactRowItemStyle`).

8. **Сохранение результатов проверок, исправление высоты истории и Deep Check (v1.2.4 Update):**
   - В `StrategyStore` добавлен кэш `_cachedTestResults`: результаты тестов (`Passed`, `Warning`, `Failed`, бейджи и признаки) сохраняются при переключении/применении стратегий и не сбрасываются в «не проверено».
   - Устранён баг сжатия истории проверок в 1 пиксель (заменён `Style="{StaticResource Divider}"` на `InnerCard` и `BorderThickness`), история отображается разборчивыми информативными карточками.
   - Если hosts не проверялся, статус выводится красным (`Danger`), а зелёная галочка появляется только после фактической успешной проверки.
   - В «Проверке соединения» на главной текстовые кнопки «Удалить» заменены на аккуратные правые иконки карандаша и корзины.
   - Прогресс-бар `ThinProgress` переведён на точный расчёт `MultiBinding` (Value/Maximum/Minimum) без хаотичных колебаний.
   - В Deep Check исправлено применение стратегии (`ApplyRecommendationAsync`), добавлена кнопка **«Добавить стратегию в список»** с пользовательским именем.
   - На главной объединены кнопки службы: при установленной службе доступны аккуратные действия «Переустановить службу» и «Удалить службу».
   - На странице «Обновления» убран блок «Что нового» и параметр pre-release версий.

9. **Автозаполнение списков доменов и полный диагностический слепок (v1.4.0 Update):**
   - Добавлен модуль `DefaultDomainLists.cs` с эталонными списками доменов YouTube (включая `googlevideo.com`, `i.ytimg.com`, `ytimg.com`, `yt3.ggpht.com`), Discord (включая `cdn.discordapp.com`, `gateway.discord.gg`, `discord.gg`, `discord.media`) и общих заблокированных сервисов.
   - `DomainListUpdater.EnsureSeeded` и `StrategyParser.EnsureUserLists` гарантируют автоматическое первичное наполнение (auto-seeding) списков при их отсутствии или нулевом размере, предотвращая пропуск CDN/видео/голоса мимо фильтрации winws.
   - Реализована функция фонового автообновления списков с официального репозитория `Flowseal/zapret-discord-youtube` при сохранении пользовательских исключений.
   - В `SettingsViewModel` процедура `RunFullDiagnosticsAndExportAsync` проводит аудит состояния служб, активирует TCP Timestamps, актуализирует списки, прогоняет матрицу стратегий и сохраняет полный отчёт в буфер обмена.

10. **Адаптивность к Flowseal 1.10.3+ и исправление потокобезопасности UI (v1.4.1 Update):**
   - Устранено исключение `NotSupportedException: CollectionView does not support changes from a thread different from the Dispatcher thread` при фоновых проверках: все методы обновления `ObservableCollection` и вызовы `Refresh()` в `StrategyStore` и `StrategiesViewModel` теперь гарантированно выполняются на UI Dispatcher.
   - Устранён ложный статус «Стратегии не найдены» в верхней панели состояния: `strategyCount` теперь не обнуляется при фоновом тесте.
   - Добавлена полная поддержка Flowseal 1.10.3+: новый список `list-google.txt`, адаптивный парсер корневых каталогов `ResolveContentRoot`, чтение версий из `.service/version.txt` и `docs/version.txt`, поддержка стратегии `ALT13` и кастомных диапазонов GameFilter.

11. **Устранение бесконечного «Подключения к RTC» и диагностика Discord Voice (v1.4.3 Update):**
   - Добавлен специализированный зонд `DiscordVoiceRtcProber` (RFC 5389 STUN Binding / UDP WebRTC), выполняющий прямое тестирование голосовых шлюзов Discord (Роттердам, Франкфурт, Стокгольм, Мадрид) с замером задержки и потерь пакетов.
   - Добавлен `FakeBinManager` для каталогизации и управления бинарными фейковыми нагрузками (`bin/*.bin`) для голосового трафика Discord и GameFilter UDP.
   - В `DiagnosticsPage` добавлена вкладка **«🎙️ Discord Voice (RTC)»** с живым мониторингом голосовых серверов, кнопкой проверки и смарт-действием **«Применить фикс для Discord Voice»**.
   - Улучшена функция `ClearDiscordCache`: теперь очищает кэш всех редакций Discord (Stable, Canary, PTB, Dev), удаляет GPU/Dawn/Blob кэши и сбрасывает DNS-кэш Windows.

12. **Очистка кэша Discord и сетевого стека в 1 клик (v1.4.4 Update · Issues #10114, PR #16169, #15962):**
   - Создан комплексный модуль `DiscordNetworkCleaner`, выполняющий остановку заблокированных процессов Discord, сканирование и удаление кэшей всех редакций (Stable, Canary, PTB, Dev: `Cache`, `GPUCache`, `DawnCache`, `Code Cache`, `Session Storage`, `IndexedDB`, `Network`, WebRTC логи) с расчётом освобождённого места в МБ.
   - Автоматический сброс сетевого стека Windows: сброс кэша DNS (`ipconfig /flushdns`), очистка ARP-таблицы (`arp -d *`), NetBIOS (`nbtstat -R`), системного прокси WinHTTP и активация TCP Timestamps / Auto-Tuning.
   - В `DiagnosticsPage` добавлена карточка «⚡ Очистка кэша Discord и сетевого стека в 1 клик» с опцией автоматического перезапуска Discord, глубокого сброса сетевого стека и подробным отчётом об освобождённом месте.
   - На главной странице (`HomePage`) добавлен быстрый переход к фиксу Discord Voice RTC.


```
