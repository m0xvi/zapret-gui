# Структура приложения Zapret GUI — разделы, подразделы и их функции

> Версия документа: `v1.18.0` (ветка `arena/01a0f306-zapret-gui`, этапы 1–2 информационной архитектуры)
> Стек: `C# .NET 8 + WPF (MVVM)`, движок `Flowseal/zapret-discord-youtube` (`bin/winws.exe`, `WinDivert`), служба `zapret`
> Язык интерфейса: русский. Сборка: `windows-latest` GitHub Actions.

Документ — полный перечень видимых разделов/подразделов приложения и фоновых служб в форме `Название: функция`. Источник — фактические `View`/`ViewModel`/`Core` на ветке `arena/01a0c100-zapret-gui`.

> **См. также:** [`docs/IA_REDESIGN.md`](IA_REDESIGN.md) — проектное предложение по новой информационной архитектуре (5 разделов, режимы «Простой/Эксперт», таблица миграции) и кликабельный прототип [`docs/ia_prototype.html`](ia_prototype.html).

---

## 1. Навигация и каркас

*   **Боковое меню (MainWindow + MainViewModel.NavItems):** переключение страниц, бейджи `Watchdog/Seamless/Мониторинг`, индикатор `BypassState`. Группы `ОСНОВНОЕ / ПРОВЕРКИ / СПИСКИ И ФИЛЬТРЫ / СИСТЕМА`.
*   **Пункты меню (`v1.17.25`):** `Главная` (`home`) · `Обход` (`bypass-center`) · `Стратегии` (`strategies`) · `Проверки` (`diagnostics`) · `Журнал` (`logs`) · `Списки` (`user-lists`) · `Профили и копии` (`profiles`) · `Обновления` (`updates`) · `Настройки` (`settings`) · `О программе` (`about`). До `v1.17.25` пункты `Журнал`, `Обновления`, `О программе` были недостижимы из меню (открывались только ссылками со страниц), а `Мониторинг` (`monitoring`) — мёртвый ключ: `Navigate` перенаправляет его в подвкладку «Экспресс». План дальнейшей перестройки — `docs/IA_REDESIGN.md`.
*   **Шапка и трей:** `TrayIcon` — двойной клик возвращает окно, меню `Запустить/Остановить обход`, `Смена стратегии/DNS/Профиля`, `Игровой режим`, `Логи`, `Выход`. `Balloon` — мягкие уведомления `Watchdog`/`Seamless`/`Мониторинг` без модальных окон.
*   **Горячие клавиши (GlobalHotkeyService):** `Ctrl+Shift+Z` — переключить обход, `Ctrl+Shift+G` — игровой режим, `Ctrl+Shift+O` — мини-оверлей. Регистрируются на `MainWindow`.
*   **Мини-оверлей (MiniOverlayWindow / MiniOverlayViewModel):** компактное окно поверх игр — статус обхода, `Uptime`, `Ping`, быстрый `Start/Stop`.
*   **Метрики на панели задач (TaskbarMetricsWindow):** окно как в `MSI Afterburner` рядом с треем — живой `RTT` по `YouTube/Discord/GitHub`, перетаскивается, интервал `ToolbarMetricsIntervalSeconds`.
*   **Глобальный оверлей загрузки (GlobalOverlayViewModel):** модальный прогресс с отменой для длительных операций (проверка SNI, тест стратегий, обновление движка). `Esc` — закрыть/отменить.

---

## 2. ОСНОВНОЕ

### 2.1 Главная (HomePage / HomeViewModel) — главный экран
*   **Кнопка питания (PowerButton → ToggleBypassCommand):** бесшовно вкл/выкл обхода без вопроса (с `v1.17.24` без `MessageBox`), сохраняет режим `служба/процесс`.
*   **Быстрые переходы (`v1.18.0`):** `Проверить сайты` (`OpenChecksCommand` → «Проверки»), `Подобрать обход заново` (`OpenBypassCommand` → «Обход» с подвкладкой «Подбор»), `Открыть журнал` (`OpenLogsCommand` → «Проверки → Журнал»).
*   **Что убрано с главного экрана в `v1.18.0` (перенесено, не удалено):** тумблер службы Windows → «Обход → Дополнительно»; блок «Продвинутые настройки» (игровой фильтр, ipset, флаг `.bat`, проверка соединения) → «Списки», «Обход → Дополнительно» и «Настройки»; список адресов и результаты проверки соединения → «Проверки → Экспресс» (там тот же `MonitorTargetStore`). На главной остались кольцо здоровья соединения (`ConnectionHealth*`) и строка `ServiceText`.
*   **Статус обхода:** `StatusText` / `StatusKey` (`Обход запущен` / `Обход выключен` / `Ошибка`), `StrategyText`, `UptimeText`, `PidText`, `ServiceText` (`Служба запущена: ALT11` / `Служба установлена, но остановлена` / `Служба не установлена`), `BypassStateKey` для цвета.
*   **Здоровье соединения:** `ConnectionHealthVisible/Key/Text` — кольцо `ok/total OK · avg мс` по результатам `ConnectionTester`.
*   **Служба Windows:** тумблер `ServiceInstalled` → `ToggleServiceCommand` (без вопроса с `v1.17.24`), индикатор `IsServicePending`/`ServicePendingText` (`START_PENDING`/`STOP_PENDING`), кнопка `Обновить службу` (`ReinstallServiceCommand`).
*   **Баннер конфликта:** `LegacyWarningVisible/Text` — обнаружен старый `zapret` из другой папки, кнопка `ResolveLegacyCommand` с диалогом `LegacyZapretDialog` (варианты `TakeOver`/`ImportAndTakeOver`/`StopOnly`).
*   **Быстрая настройка — 1 клик (RunFullCheckCommand):** 4 шага 1–2 мин: `1) аудит системы (BFE/WinDivert)` → `2) проверка сайтов` → `3) тест 22 стратегий` → `4) рекомендация лучшей`. Прогресс `FullCheckProgress`, результат `FullCheckSummaryText/Key`, кнопка `Применить` рекомендованную `ApplyRecommendedStrategyCommand`.
*   **Игровой режим:** `GameModeActive`, `GameStatusBadgeText`, кнопка `ToggleGameMode`, блок оптимизации сети `GamingNetworkOptimizer` (`ApplyGamingTweaks/Revert`).

### 2.2 Обход (BypassCenterPage / BypassCenterViewModel) — 6 подвкладок с `v1.18.0`

*   **Подвкладки (`BypassTabs` / `SelectedSubTab`, `SegmentedControl` как в «Проверках»):** `🎯 Стратегия` · `🧠 Подбор` · `🌐 DNS` · `📋 Списки` · `🔥 Сложные сайты` · `🛠 Дополнительно`. Карточка «СТАТУС» и баннер сообщения — над вкладками, поэтому статус виден всегда. Логика, команды и все биндинги прежние: менялась только раскладка (было «всё в одном» одной простынёй).
*   `Дополнительно` — управление службой Windows, переехавшее с «Главной»: `Home.ServiceToggleStatusText`, `Home.ServiceText`, `Home.ServiceInstalled` + `Home.ToggleServiceCommand`, `Home.ReinstallServiceCommand`, `Home.IsServicePending`/`Home.ServicePendingText` (проксируются через `BypassCenterViewModel.Home`). Плюс кнопка перехода в «Проверки → Аудит системы» (`OpenSystemCheckCommand`) для опасных операций.
*   `Списки` — статусы `HostsStatusText`/`IpsetStatusText`, кнопки `Обновить hosts` / `Обновить ipset` / `Списки OK` и переход в полный редактор списков (`OpenListsCommand` → страница «Списки»).
*   `Сложные сайты` — карточка YouTube (QUIC-fake, SNI, IPv4, DoH) и карточка «Тяжёлые игры» со ссылкой на фильтр игр (сам фильтр живёт на странице «Списки» — без второго экземпляра настроек).

#### Прежнее содержимое (осталось внутри подвкладок)
*   **Заголовок:** описание + кнопки `Открыть папку движка` (`OpenEngineFolderCommand`) и `Обновить статусы` (`RefreshCommand`).
*   **Баннер сообщения:** `Message`/`MessageKey`/`MessageVisible` с крестиком `DismissMessageCommand` (без рефлексии).
*   **Статус:** `BypassStatusText`/`BypassStatusKey`, `EngineStatusText` (`Готов · vX.Y · стратегий N`), `CurrentDnsText` (`GetCurrentDnsSummary`), `ListsStatusText`, бейджи `HostsStatusText` / `IpsetStatusText`.
*   **Стратегии:** `ComboBox Strategies.Items → SelectedStrategy` + `ApplyStrategyCommand` (`SwitchToStrategyAsync`/`InstallServiceAsync` бесшовно, сохраняет `служба/процесс`), описание `SelectedStrategy.Description`, чекбокс `SeamlessEnabled` + `SeamlessStatus`/`SeamlessStatusKey` + `Проверить сейчас` (`TestSeamlessNowCommand`).
*   **DNS:** карточка `CurrentDnsText`, `ComboBox DnsProfiles → SelectedDnsProfile` (`PredefinedProfiles`: Cloudflare 1.1.1.1, Quad9 9.9.9.9, Google 8.8.8.8, DHCP), кнопки `Применить DNS` (`ApplyDnsProfileAsync` via `netsh`) и `Проверить подмену` (`CheckHijackAsync`).
*   **HOSTS & IPSET:** `HostsStatusText` (`ReadSystemHosts`), `IpsetStatusText` (`ipset-all.txt` `33048` сетей + `ipset-discord.txt`), кнопки `Обновить hosts` / `Обновить ipset` / `Списки OK` (`UpdateHosts/UpdateIpset/EnsureLists`), `ListsStatusText` (`general 68/+2 · youtube 38/+1 · discord 23/+0`).
*   **YouTube (Тяжёлый случай) — тонкая настройка:** вынесено из глубины настроек — `DisableQuicFake`, `YoutubeSniOverride` (список `YoutubeSniOptions`: `google.com`/`googlevideo.com`/`yt3.ggpht.com`...), `PreferIPv4`, `UseDohForBlocked` — прокси в `AppSettings` → `SettingsStore.Save`.
*   **Исчерпывающая матрица стратегия×DNS:** `RunMatrixCommand` — `23 стратегии × 4 DNS = 92 теста` (каждый — `TestStrategyAsync` на 16 эндпоинтах `Dns+Tcp+Http`), прогресс `MatrixProgress/Max/Text/Percent/Summary`, `CancelMatrixCommand`, карточка лучшего `MatrixBestText` + `ApplyBestCommand` (`ApplyDnsProfileAsync` + `SwitchTo/InstallService` + `hosts`), таблица `ListView MatrixResults` (`Strategy/Dns/Status/Summary/⭐ IsBest`), `SelectedMatrixEntry`. При прерывании восстанавливает исходный `DNS` и обход.

### 2.3 Стратегии (StrategiesPage / StrategiesViewModel)
*   **Каталог:** `StrategyStore.Items` — парсинг `general*.bat` (`StrategyParser`), имя/категория `ALT`/`GENERAL`/`EXP`, `ArgsPreview`, `IsRecommended`, `TestResult` (`Passed/Total/AvgMs`).
*   **Выбор и применение:** `Selected` → `RunAsync` (`StartAsync`/`SwitchTo`/`InstallService` через `StrategyApplicationService.ApplyAsync` бесшовно, без вопроса с `v1.17.24`), `SelectAsDefault` без вопроса.
*   **Тестирование:** `TestStrategyAsync` (одна стратегия, `ConnectionTester.RunAsync` + `Progress`), `TestAllAsync` (перебор 22-х с `CancellationToken`), `TestProgress`, `IsTestingAll`, кандидаты `StrategyCandidateStore`/`CandidatePreview` (`MakeCandidatePrimary` бесшовно).
*   **Пул SNI:** `TestSniPoolAsync` (`SniFakePoolManager`), статус `SniTestingStatusText`, результаты `SniTestResults`.
*   **История:** `SwitchHistory` (`StrategySwitchHistoryStore`), `EvaluationHistory` (`StrategyEvaluationHistoryStore`), `RecoveryJournalStore`.

---

## 3. ПРОВЕРКИ

### 3.1 Проверка (DiagnosticsPage / DiagnosticsViewModel — бывший Аудит)
*   **Экспресс-диагностика:** `RunAsync` — 14 пунктов: `BFE`, `WinDivert/WinDivert14`, админ-права, `TCP timestamps`, служба `zapret`, `ipset`/`lists`, `hosts`, `DNS`. Карточки `StatusKey` (`Success/Warning/Danger`), `FixHint`/`FixId`.
*   **Исправления:** `FixAsync` (`bfe`, `timestamps`, `zapretstuck`...), `RemoveServiceAsync`, `ChangeTcpTimestamps`, `DeepResetNetwork` — с подтверждениями `MessageBox` (только здесь).
*   **DPI-suite:** `RunDpiCheckAsync` (`temporaryStrategy`, `ipset any` на время, восстановление), прогресс `ProgressVisible/ProgressText`.
*   **Экспорт:** `ExportDiagnostics` — `DiagnosticsExportReport` (`DiagnosticsHistoryStore`, `StrategyEvaluationHistory`, `RecoveryJournal`) + `ProviderTelemetryExporter`.

### 3.2 Глубокая проверка (DeepCheckPage / DeepCheckViewModel)
*   **Цели:** `CustomHost` + `targets.txt` (`TargetsTxtLoader`: `Key = https://...` / `PING:1.1.1.1`), провайдер `ProviderContext`/`ProviderLimitations`.
*   **Запуск:** `RunDeepCheckAsync` — многоточечная проверка, `ProgressVisible/Text/Percent`, `IsRunning`, `Findings`/`Metrics`/`Recommendations`.
*   **Кандидат:** `GeneratedCandidateName/Summary/Features`, `HasGeneratedCandidate`, `SaveCandidate`, `ApplyCandidate`.

### 3.3 Мониторинг ресурсов (MonitoringPage / MonitoringViewModel)
*   **Цели:** `Targets` (`MonitorTargetStore.EnsureDefaults` + пользовательские), `Results` (`ResourceProbe.CheckAsync`), `SelectedTarget`, `LastCheckText`.
*   **Ручная проверка:** `CheckAllCommand` → `CheckAllAsync` с прогрессом `ProgressValue/Maximum/Text`, `Diagnosis` (`DiagnoseSelectedAsync`).
*   **Фон:** `ResourceMonitoringEnabled` (`DispatcherTimer` `GetInterval()` 5–120 мин), `AutoRecoverStrategy` (при `StrategyBreaks` → `DiagnoseAndRecoverAsync` → `ProfileAutoSwitch.TrySwitchOnFailure` → `TestAll` → `SelectAsDefault` + `StartSelectedStrategyAsync` без окон), `AutoSwitchToBestStrategy` (`TrySwitchToBestStrategyAsync`).
*   **Управление:** `AddResource`/`RemoveResource`/`AddToBypassList` через `InputDialog`.

---

## 4. СПИСКИ И ФИЛЬТРЫ

### 4.1 Списки (UserListsPage / UserListsViewModel)
*   **Списки:** `list-general.txt` (68), `list-youtube.txt` (38), `list-discord.txt` (23) + `list-*-user.txt` (пользовательские добавления) + `list-general-user` и т.д. Счётчики `CountLines`, двойной клик/Enter — правка.
*   **IPSet:** `ipset-all.txt` (33048), `ipset-discord.txt`, `ipset-user.txt`, режим `IpsetMode` (`Loaded/None/Any` через `EngineService.Get/SetIpsetMode`), `UpdateIpsetAsync` (скачивание), `SystemIpsText`/`DohIpsText`.
*   **Игровой фильтр:** `GameFilterMode` (`Disabled/TcpAndUdp/TcpOnly/UdpOnly`), `GameFilterProfileId` + кастом порты, чекбокс `UseGameFilterOnStart`.
*   **DNS внутри списков:** выбор профиля `SelectedDnsProfile`, отображение `PrimaryServer/SecondaryServer/DohUrl`.

### 4.2 Профили (ProfilesPage / ProfilesViewModel)
*   **Пресеты:** `ProfileManager` — сохранение `AppSettings` + `EnginePath` + `WatchdogEnabled` и т.д. в `UserProfile`. Список `Profiles`, применение `ApplyProfileAsync`, удаление `DeleteProfile` (с `MessageBox`).
*   **Бэкапы:** `BackupRestoreService` — полные архивы `BackupHistory`, `RestoreBackup`, `DeleteBackup`, `ClearSystem`.
*   **Сеть:** `NetworkDetector` → `NetworkIdentity` (`Fingerprint/Ssid/DisplayName`), `ProfileAutoSwitchService` (таймер 30с + `NetworkAddressChanged` с дебаунсом 3с, `Cooldown 60с`).

---

## 5. СИСТЕМА

### 5.1 Настройки (SettingsPage / SettingsViewModel)
*   **Общие:** тема `ThemeMode` (`System/Dark/Light`), масштаб `InterfaceZoomPercent` (80–140), `CloseToTray`, `StartMinimized`, `RunAtStartup` (планировщик/реестр), `ConfirmOnStop` (только для ручной остановки, по умолчанию выкл).
*   **Обход и движок:** `EnginePath`, `ShowWinwsConsole`, `AutoCheckEngineUpdates`/`IncludePrerelease`/`PreserveUserDataOnUpdate`, `SelectedFakeSni` + `AutoSniRotationEnabled` + `CustomSniList`. Флаг `BatAutoUpdate` (`utils\check_updates.enabled`) с `v1.18.0` настраивается здесь (переехал с «Главной»); `GameFilterOptions`/`IpsetOptions` живут на странице «Списки» и в «Обход → Сложные сайты».
*   **Автозапуск:** `AutoStartBypass`, `StartupDelaySeconds` (0–60), `StopBypassOnExit`, `SafeMode`.
*   **Watchdog:** `WatchdogEnabled` (по умолчанию вкл), `WatchdogIntervalSeconds` (5–120), `WatchdogAutoRestart`, `WatchdogNotifyUser` — `WatchdogService` (15с проверка `winws`/`zapret`, автоперезапуск + `BFE` + fallback на 5 альтернативных стратегий с `v1.17.24`).
*   **Бесшовное переключение:** `SeamlessFailoverEnabled` (вкл), `SeamlessCheckMinutes` (2–60), `SeamlessCooldownMinutes` (5–120), статус `SeamlessLastReason`/`SeamlessLastSwitchTime` — `SeamlessFailoverService` (5 мин порог 2, cooldown 10 мин, 8 кандидатов).
*   **Профили/сеть:** `AutoSwitchProfileOnNetworkChange`/`OnFailure`, `LastNetworkFingerprint`/`LastAutoSwitchedProfileId`.
*   **Расписание:** `ScheduleService` (`BypassScheduleService`) — время вкл/выкл.
*   **Уведомления и метрики:** `MonitorNotificationsEnabled`, `ResourceMonitoringEnabled`/`IntervalMinutes`, `RealTimePingEnabled`/`IntervalSeconds`, `ToolbarMetricsEnabled`/`IntervalSeconds`/`VisibleTargets`, позиция `TaskbarMetricsLeft/Top`.
*   **Строгие регионы (YouTube 0/13):** `DisableQuicFake`, `PreferIPv4ForBypass`, `UseDohForBlockedHosts`, `YoutubeSniOverride` + `YoutubeSniOptions`, `HostSpecificStrategies`.
*   **Горячие клавиши и игры:** `GameDetectionEnabled`, `AutoGameModeOnLaunch`, `Hotkey` настройки.
*   **Сброс:** `ResetSettings` → `MessageBox`.

### 5.2 Обновления (UpdatesPage / UpdatesViewModel)
*   **Движок:** текущая `EngineVersion` (`ReadVersion`), последняя `LatestVersionText` (`GetLatestVersionTextAsync`), `UpdateAvailable` (`CompareVersions`), кнопка `UpdateEngine` (`PrepareForEngineUpdateAsync` → остановка `zapret`+`WinDivert` → `WaitForDriverUnloadAsync` → `CopyEngine` → восстановление), `AutoCheckEngineUpdates`.
*   **GUI:** `GuiUpdateService` (`CheckGuiUpdates`, `DefaultRepository` `m0xvi/zapret-gui`), `AutoCheckGuiUpdates`, `InstallGuiUpdate` (замена `exe` через копию).

### 5.3 Журнал (LogsPage / LogsViewModel)
*   **Лог:** `AppLog` (категории `SvcInfo/SvcWarn/Error/Debug`), фильтр по уровню, очистка, автоскролл.

### 5.4 О программе (AboutPage)
*   **Инфо:** `AppVersion` (`InformationalVersion`), `EngineVersionText`, `EnginePathText`, кнопки `OpenEngineFolder`/`OpenLogs`/`CheckUpdates`, лицензия.

### 5.5 Первый запуск (FirstLaunchPage / FirstLaunchViewModel)
*   **Мастер:** `FirstLaunchWizardCompleted`/`StrategyTestsCompleted`/`FirstLaunchDiagnosticsCompleted`, выбор `EnginePath`, установка `BFE` (`MessageBox`), очистка `WinDivert` (`MessageBox`), `AutoTestStrategiesOnFirstLaunch` + `AutoDiagnoseOnFirstLaunch`, `SafeMode` переключатель.

---

## 6. ФОНОВЫЕ СЛУЖБЫ И ЯДРО

*   **BypassController:** `GetStatus` (`BypassState` `Stopped/RunningStandalone/RunningService` + `ServiceState` `Running/Stopped/NotInstalled` + `Pid/Uptime` + `ServiceStrategy`), `BuildArgs` (`BypassArgumentBuilder` + `GameFilter` + `SNI` + `QUIC` флаги), `StartAsync`/`StopAsync`/`SwitchToStrategyAsync`/`TestStrategyAsync`/`DiagnoseResourceAsync`/`TestStrategyOnResourceAsync`/`RunDpiCheckAsync`/`InstallServiceAsync`/`RemoveServiceAsync`/`PrepareForEngineUpdateAsync`. `RecoveryJournalStore.Append`.
*   **WinServices:** `Query`/`ParseServiceState` (`sc query` + числовой `STATE: 4` для русской Windows), `Create/Start/Stop/Delete`, `Get/SetInstalledStrategyName` (реестр `HKLM\System\...\Services\zapret`), `StopForEngineUpdateAsync`/`WaitForDriverUnloadAsync` (`WinDivert64.sys` 2с пауза), `RemoveEverything`, `GetTcpTimestampsState`/`EnsureTcpTimestamps` (`netsh`).
*   **DnsManagementService:** `PredefinedProfiles`, `GetCurrentDnsSummary`/`GetSystemDnsServers` (`GetActiveInterface` → `DnsAddresses`), `ApplyDnsProfileAsync` (`netsh`), `CheckHijackAsync` (`DnsHijackReport`).
*   **EngineService:** `IsEngineReady` (`bin/winws.exe`), `ReadVersion`, `GetGameFilterMode/SetGameFilterMode`, `GetIpsetMode/SetIpsetMode`, `GetBatAutoUpdateFlag/SetBatAutoUpdateFlag`, `CheckHostsAsync`/`ApplyHosts`/`ReadSystemHosts`, `GetIpsetMode`, `UpdateIpsetAsync`.
*   **ConnectionTester / ResourceProbe / MonitorTarget:** `RunAsync` по `ConnectionCheck`/`MonitorTarget` (Url/Host/IsGame/Enabled/LastStatus), `ResourceProbeResult` (`Ok/Milliseconds/StatusText/StatusKey/Details/DnsDetails`), `ResourceDiagnosisResult` (`Available/BypassHelps/StrategyBreaks/ProviderOrServerIssue`).
*   **WatchdogService (v1.17.24 усилен):** `Timer` 15с, `HandleCrashAsync` при `serviceInstalled && ServiceState!=Running` или `RunningStandalone` без `PID`, обеспечивает `BFE`, `TryStartServiceWithWaitAsync` 15с, при 3+ падениях — `TryFallbackStrategyAsync` 5 альтернатив бесшовно.
*   **SeamlessFailoverService (v1.17.21):** `Timer` 5 мин, порог 2, `Cooldown` 10 мин, `CheckAndFailoverAsync` — лёгкая проверка 5 целей → `DiagnoseResourceAsync` → поиск 8 кандидатов → `TestStrategyOnResourceAsync` → `DoSeamlessSwitchAsync` (`InstallService` если служба).
*   **ProfileAutoSwitchService:** `Timer` 30с + `NetworkAddressChanged`, `CheckAndSwitchAsync` по `Fingerprint/Ssid`, `TrySwitchOnFailureAsync`.
*   **BypassScheduleService / GameDetectionService / GlobalHotkeyService / RealTimePingService / ProviderTelemetryExporter / DiagnosticsService / LegacyZapret / Shell / SettingsStore / AppLog / Converters / TaskbarMetricsWindow** — вспомогательные.

---

## 7. Сборка и версия

*   **csproj:** `<Version>1.17.24</Version>` / `AssemblyVersion`/`FileVersion`/`InformationalVersion` `1.17.24.0`, `LangVersion latest`, `System.Text.Encoding.CodePages`.
*   **Workflow:** `.github/workflows/build.yml` (`windows-latest`, `dotnet 8.0.x`, `restore → build → WindowsIntegrationHarness → CoreLogicHarness → publish portable / framework-dependent → release` на `v*`).
*   **Проверка привязок:** `python3 tools/check_bindings.py` — 20 `XAML` / 93 ключа, `Run Text Mode=OneWay` для `read-only`.

