# Инвентаризация функций Zapret GUI — 2026-10-24

> **Цель:** показать сколько функций в приложении, где они сейчас разбросаны и как их удобнее сгруппировать.
> Версия на момент анализа: `v1.34.17` (`449a123`). Навигация: 8 кликабельных разделов + 4 заголовка-группы, внутри — ещё 22 под-вкладки. `ICommand` в ViewModels: **279** объявлений (~90 уникальных пользовательских действий). `Core/*.cs`: **52** сервиса.

---

## 1. Текущая навигация (как есть сейчас)

### Левая панель `MainWindow.xaml` (NavList)

| # | Key | Название | Иконка | Кратко | Внутри |
|---|-----|----------|--------|--------|--------|
| — | `group-main` | **ОСНОВНОЕ** | — | заголовок | — |
| 1 | `home` | **Обзор** |  | Дашборд | карточка статуса (кольцо, State/Strategy/Uptime/PID/Service), фильтры `GameFilter 4 режима` + `ipset 3 режима`, проверка соединений (YouTube/Discord/GitHub), быстрый выбор стратегии |
| 2 | `bypass-center` | **Центр обхода** |  | «всё в одном» | DNS-профили + ApplyDns, CheckHijack, Hosts/Ipset/Lists статус + Update, матрица `стратегия×DNS` (Run/Cancel/ApplyBest/ClearHistory), переключатели `DisableQuicFake / YoutubeSNI / PreferIPv4 / UseDoH`, Seamless toggle + TestNow |
| 3 | `strategies` | **Стратегии** |  | Каталог | 5 под-вкладок: `Каталог` (поиск, категории FAKE/ALT/SIMPLE, тест одной/всех, детали args), `Конструктор параметров` (VisualStrategyBuilder, превью args, тест/сохр./применить), `Умный автоподбор` (SmartAutoTuner), `Пул TLS SNI` (SniFakePool, тест пула, авто-выбор), `Контрольные адреса` (targets.txt) |
| 4 | `game-mode` | **Игровой режим** |  | Игры | Toggle GameMode (TCP-only), профиль портов, чекбоксы игр (`GameBypassOptions`), Detection (вкл/авто), GamingNetworkOptimizer (применить/откатить твики), фиксы `Phasmophobia / PUBG / выбранные`, статус сети |
| — | `group-checks` | **ПРОВЕРКИ** | — | заголовок | — |
| 5 | `diagnostics` | **Проверка** |  | 6 под-вкладок | `⚡ Экспресс` (мониторинг ресурсов Targets/Results), `🌐 DPI 34 узла` (DPI-проверка 34 узла, сравнение control vs bypass), `🔬 Deep Check` (Findings/Metrics/Recommendations/GeneratedCandidate), `🛠 Аудит` (14 проверок), `📊 Результаты` (история + графики), `🎙 Voice RTC` (Discord voice probe, оптимизация, чистка кэша, глубокий сброс сети) |
| — | `group-data` | **СПИСКИ И ФИЛЬТРЫ** | — | заголовок | — |
| 6 | `user-lists` | **Списки** |  | 3 под-вкладки | `Редактор списков` (CRUD записей, поиск, sort/dedup, duplicate/invalid, import/export, open in notepad), `Безопасный DNS (DoH)` — дублирует BypassCenter DNS, `Режимы фильтрации` (game filter profiles + ipset) |
| 7 | `profiles` | **Профили** |  | 3 под-вкладки | `Профили конфигураций` (create/duplicate/delete/export/import/apply + привязка к сети), `Резервные копии ZIP` (CreateFullBackup/Restore/Delete), `Портативный режим & Перенос` |
| — | `group-system` | **СИСТЕМА** | — | заголовок | — |
| 8 | `settings` | **Настройки** |  | 8 под-вкладок | `⚙ Общие` (путь движка, AutoStartBypass, StopOnExit, SafeMode, FirstLaunch wizard), `🛡 Обход` (Seamless, Watchdog, Schedule days+time), `🌐 Сеть` (DNS, hosts/ipset, ToolbarMetrics, RealTimePing, Provider context, интервалы), `🎨 Интерфейс` (тема, zoom, сводка, hotkeys, mini overlay, tray), `🎮 Игры` (detection, autoGameMode), `🔄 Обновления` (встроен `UpdatesPage` целиком), `📄 Журнал` (LogsPage — AppLog), `ℹ О программе` (AboutPage) |

**Дополнительные скрытые/встроенные страницы:** `UpdatesPage` (отдельно доступна через 🔄 в title bar), `MonitoringPage` (фактически внутри Diagnostics→Экспресс, но есть отдельный VM), `DpiPage` (дубль вкладки DPI), `HomePage`, `LogsPage`, `AboutPage`.

**Титульная панель (шапка 46px):** чип статуса обхода → чип активной стратегии → чип мониторинга → живой RTT ping → бейдж игры → бейдж активной проверки (спиннер + %) → чип готовности (Readiness) | иконки: HUD, тема, папка движка, сводка, обновления (красный бейдж 8px), обновить.

**Правая панель «Сводка» (340px, сворачивается → 42px):** дублирует Обзор — карточка ЗАПРЕТ (статус+стратегия+версия), СЕТЬ (узлы+RTT), ИГРЫ (toggle + профиль), БЫСТРО (обновить/папка/диагностика). Управляется `IsSummarySheetExpanded`.

**Оверлеи:** `GuiUpdateOverlay` (затемнение на загрузке GUI), `GlobalOverlay` (полноэкранный прогресс для длительных операций — DPI/DeepCheck/тест стратегий/матрица, с кнопкой «В фон» + минимизированный индикатор справа внизу).

**Трей + TaskbarMetricsWindow + MiniOverlayWindow (HUD):** иконка в трее (показать/скрыть/логи/выход), плавающее окно метрик над панелью задач (как в MSI Afterburner), компактный HUD 380×104.

---

## 2. Полный каталог функций по доменам (что умеет приложение)

### A. Управление обходом — 11 действий
Запустить / Остановить / Переключить · Установить в службу / Удалить / Переустановить / Переключить автозапуск (ToggleService) / Reapply · Статусы (State, Uptime, PID, ServiceState, EngineVersion/Path) · Фоновые службы: `WatchdogService`, `SeamlessFailoverService` (бесшовное переключение при падении), `BypassScheduleService` (расписание по дням/времени), `BypassController`, `StrategyApplicationService`, `WinServices`.

### B. Стратегии — ~28 действий
Парсинг `general*.bat` (кроме `service*.bat`, естественная сортировка ALT/ALT2…), человекочитаемое описание (fake/multisplit/multidisorder/фейк TLS/QUIC/повторы/badseq/ip-id), поиск + фильтр категории + «только рекомендуемые» · Детали: список args `winws.exe`, копировать команду, открыть .bat · Применение: Запустить / Проверить / Установить в службу / Сделать основной / Откатить предыдущую · Массово: Проверить одну / Проверить все (параллельный TCP+HTTP, overlay с %, отмена, восстановление) · Конструктор (VisualStrategyBuilder — имя/валидация, превью args, тест/сохр./применить, копировать) · Автотюнер (SmartStrategyAutoTuner — старт/отмена/применить победителя) · Генерация кандидатов (StrategyConstruction + CandidateStore + EvaluationHistory) · SNI-пул (SniFakePoolManager — тест пула, авто-выбор лучшего, ротация) · Контрольные адреса (TargetsTxtLoader — targets.txt) · Матрица (StrategyCandidateStore + MatrixHistory) · Экспорт отчёта кандидатов.

### C. Сеть, DNS, списки — ~24 действия
**Списки доменов** (UserLists VM): `general`, `general-user`, `built-in` (read-only) + кастомные; добавление (одиночно/массово BulkAdd, quick add домен), редактирование/удаление, поиск/фильтр, `Sort/Dedup`, `DuplicateCount/InvalidCount`, `Import/Export`, `OpenInNotepad`, `Save/Cancel/Reload`, `CopyFingerprintHint`.  
**DNS:** профили DoH (Cloudflare/Google/Quad9/AdGuard/DHCP-авто), выбор + `ApplyDns`, `CheckHijack`, `GetCurrentDnsSummary`, `DnsManagementService`, `ResetNetwork` (winsock/int ip/winhttp/flushdns).  
**Hosts / ipset:** `UpdateHosts`/`CheckHosts`/`ApplyHosts`, `UpdateIpset`/`EnsureLists`, `DomainListUpdater`, `EngineFileUpdater`, `BackupRestoreService` (бэкапы в `%APPDATA%\ZapretGUI\backups`).  
**Мониторинг сети:** `MonitorTargets` (YouTube/Discord/GitHub + кастомные URL/PING), `CheckAll/Diagnose/OpenUnifiedCheck/AddToBypassList`, `ResourceMonitoring`, `RealTimePingService` (живой RTT, интервал 3–120с), `ToolbarMetrics` (выбор хостов, интервал 15–300с, окно над таскбаром), `ProviderTelemetryExporter`, `NetworkDetector`.

### D. Диагностика и проверки — ~26 действий
**Экспресс/Мониторинг:** Targets/Results, `CheckAll`, `Diagnose` (сравнение без/с обходом), AutoRecoverStrategy (2 сбоя → автоподбор).  
**DPI 34 узла:** независимые DNS/TCP/HTTPS пробы, буфер 64КБ, HTTP/1.1 + TLS1.2/1.3, progress + отмена + сравнение control vs bypass.  
**Deep Check:** `Run/Cancel/ApplyRecommendation/SaveGeneratedCandidate/Export`, Findings/Metrics/Recommendations, `GeneratedCandidate` (args/features).  
**Аудит 14 пунктов:** админ-права, bin (`winws.exe`/`WinDivert64.sys`/`.dll`), кириллица/спецсимволы в пути, OneDrive, служба BFE, TCP timestamps, системный прокси, VPN-адаптеры, конфликтующие службы (Adguard/Killer/Intel/Checkpoint/SmartByte), чужие `winws.exe`/`goodbyedpi`, остаточные WinDivert, служба `zapret`, hosts GitHub, кэш Discord · Фиксы: `StartBFE`, `EnableTimestamps`, `RemoveServices`, `ClearDiscordCache`, `ResetNetwork`, `RecoverBypass`, `FixTimestamps`, `OpenHosts/SystemNetwork/EngineFolder`.  
**Voice RTC:** `RunVoiceRtcAudit`, `OptimizeDiscordVoice`, `CleanDiscordAndNetwork`, `CleanAndRestartDiscord`, `DeepNetworkReset`, `RefreshDiscordCacheStatus`.  
**История/экспорт:** `DiagnosticsHistory`, `DiagnosticsExportReport` (md/json/zip), `RecoveryJournal`, графики Metrics, бейдж «устарело».

### E. Игры — ~15 действий
`GameModeActive` toggle (UDP пропускается), `GameFilterPortConfig` (профили портов), чекбоксы `GameBypassOption` (Phasmophobia, PUBG, Discord и др.), `ApplyProfile`, `FixPhasmophobia/FixPubg/FixSelectedGames`, `GameDetectionService` (вотч процесса, `AutoGameModeOnLaunch`), `GamingNetworkOptimizer` (ApplyGamingTweaks/Revert), `DiscordNetworkCleaner`/`DiscordVoiceRtcProber`, подсказки Photon SNI.

### F. Профили и автоматика — ~16 действий
Профили: `CreateFromCurrent/Duplicate/Delete/Export/Import/Apply` + `ProfileManager` · Бэкапы ZIP: `CreateFullBackup/Restore/Delete/RefreshHistory` · Портативный режим (`AppPaths.IsPortableMode`) · Автопереключение: `NetworkDetector` fingerprint, `BindToCurrentNetwork/Unbind`, `AutoSwitchOnNetworkChange/OnFailure`, `RefreshNetwork/CopyFingerprint`, сервисы `ProfileAutoSwitchService` + `Watchdog` + `SeamlessFailover`.

### G. Обновления — 13 действий
`GuiUpdateService` (CheckGuiUpdate/UpdateGui, прогресс %, SHA-256, бэкап exe в `%APPDATA%\gui-backup`, overlay + retry/manual download/dismiss), `EngineFileUpdater` (Check/UpdateEngine, zip/tar.gz, слияние с сохранением user-листов, маркер `.gui-engine-version`, восстановление ipset/game filter, перезапуск обхода), `UpdateIpset/CheckHosts`, `Consistency` check (`EngineConsistency`), `RollbackEngine`, `CopyNetworkReport`, `ReleasesText`, `NetworkReport`.

### H. Приложение и система — ~14 действий
Тема (`ThemeService` System/Dark/Light), масштаб `InterfaceZoom 80–140%`, `ToggleSummarySheet`, `GlobalHotkeyService` (ToggleBypass/ToggleGameMode/ToggleOverlay), `MiniOverlay/HUD` + `TaskbarMetricsWindow`, `Tray` (7 пунктов), `AppLog`/`LogsPage`, `AboutPage`, `OpenRepo/OpenGuiRepo/OpenReleasePage`, `BrowseEnginePath/DetectEngine/ValidateEngine/OpenEngineFolder/OpenSettingsFile/OpenLogsFolder/OpenBackupFolder/ResetSettings/RerunWizard`, `BypassSchedule`, `Seamless/Watchdog` уведомления.

**Итого видимых пользователю действий:** **~90 уникальных команд** + ~40 статусных индикаторов + 22 под-вкладки.

---

## 3. Где сейчас боль (почему неудобно)

| Проблема | Пример | Последствие |
|----------|--------|-------------|
| **Дублирование DNS** | DNS в `Центр обхода` + `Списки → DoH` + `Настройки → Сеть` + `Проверка → hijack` | Непонятно куда нажать, настройки рассинхронизируются |
| **Дублирование списков** | `Списки` (редактор) vs `Центр обхода` (Hosts/Ipset статус) vs `Обновления` (обновить ipset/hosts) vs `Настройки → Сеть` | 4 места для одного файла |
| **Центр обхода = клон Стратегий** | Матрица `стратегия×DNS` + выбор DNS + переключатели QUIC/SNI — всё это уже есть в `Стратегиях` и `Настройках` | Две страницы делают одно и то же, путает |
| **Проверка перегружена** | 6 под-вкладок в одном разделе + отдельный `DpiPage` + `DeepCheckPage` + `MonitoringPage` как отдельные файлы | Вкладки мелкие, история смешана, «Экспресс» на самом деле мониторинг |
| **Настройки — 8 вкладок** | Внутри `Настройки` лежит целый `UpdatesPage` (дубль шапки до v1.34.16), `Журнал` и `О программе` — это отдельные страницы | Скролл на 2 экрана, теряется иерархия |
| **Игры в 4 местах** | `Игровой режим` (основная) + `Настройки → Игры` (галочки) + `Обзор` (бейдж) + `Сводка справа` (карточка) + `Центр обхода` (DisableQuicFake) | Фикс Phasmophobia ищут по всему приложению |
| **Сводка справа дублирует Обзор** | Левая навигация + центр + правая сводка показывают один и тот же статус | Узкий центр (пустые поля по бокам до v1.34.17), 3 места для одного тумблера |
| **Автоматика размазана** | `Watchdog`/`Seamless`/`Schedule`/`AutoSwitch` — часть в `MainViewModel`, часть в `Настройки → Обход`, часть в `Профили` | Не видно единой картины «что само переключается» |

**Навигационный вес:** 8 разделов в левом меню + 22 под-вкладки + шапка 6 чипов + правая сводка 4 карточки = **~40 точек входа** для 90 действий. Пользователь кликает 3–4 раза чтобы найти нужный тумблер.

---

## 4. Предлагаемая реструктуризация

### Принципы
- **Одна функция — одно место** (убираем дубли DNS/hosts/игр).
- **По задаче пользователя**, а не по типу файла: «запустить обход» / «проверить что не работает» / «добавить сайт» / «починить игру» / «сохранить/перенести».
- **Глубина ≤2 кликов** до любого действия. Вложенные табы — только где без них нельзя (Стратегии 5 → 3, Проверка 6 → 5).
- **Сводка справа — сворачиваемая по умолчанию**, не дублирует центр. Title bar — только индикаторы, без дублей кнопок.

### Вариант A — «Мягкий рефактор» (сохраняет привычную структуру, убирает дубли)

Оставляем 8 разделов слева, но чистим начинку:

- **Обзор** — только дашборд: Статус/Стратегия/Uptime/PID/Service + 2 тумблера (GameFilter/ipset) + проверка соединений. Без выбора стратегии.
- **Обход** (переименовать `Центр обхода → Обход`) — сливаем с `Стратегиями`: слева каталог + конструктор + автотюнер/SNI, справа — запуск/служба/расписание/Seamless. Убираем DNS/hosts отсюда.
- **Проверка** — без изменений, но `Мониторинг` полностью переезжает сюда как `⚡ Ресурсы` (вместо отдельного VM), `DpiPage` удаляется (дубль вкладки).
- **Списки и сеть** (переименовать `Списки → Списки и сеть`) — 2 вкладки вместо 3: `Домены` (редактор) + `Сеть` (DNS DoH + Hosts/ipset + Targets). DNS из Центра и Настроек удаляется.
- **Игровой режим** — забирает всё игровое: `Настройки → Игры` переезжает сюда, `DisableQuicFake`/`YoutubeSNI` из Центра тоже сюда.
- **Профили** — без изменений, но `Seamless/Watchdog/Schedule/AutoSwitch` сводятся в один блок «Автоматика» внутри Профилей (вместо разброса по Настройкам).
- **Настройки** — 8 → 4 вкладки: `Общие` (путь движка, автозапуск, SafeMode, wizard), `Интерфейс` (тема/zoom/HUD/трей/горячие клавиши/toolbar metrics), `Журнал`, `О программе`. `Обход/Сеть/Игры/Обновления` удаляются (разъехались по своим разделам). `🔄 Обновления` остаётся доступна только через 🔄 в шапке + внутри `Система → Обновления` как ссылка (не дубль страницы).

Плюс: почти не ломает привычки, минус: разделов остаётся 8.

### Вариант B — «По задачам» (рекомендуемый, 6 разделов)

Сжимаем 8 → 6, каждый — одна пользовательская цель:

| Новый раздел | Что внутри | Откуда переехало |
|--------------|------------|------------------|
| **1. Обзор** | Только сводный дашборд + быстрые действия (Запустить/Остановить/Служба) + Readiness + версия движка. Без настроек. | `HomePage` (очищенный) |
| **2. Обход** | Всё что запускает: выбор стратегии (каталог + конструктор), запуск/служба, фильтры game/ipset, тонкие (QUIC/SNI/IPv4/DoH), матрица `стратегия×DNS` (если оставляем), расписание, Watchdog/Seamless/автоподбор. 3 под-вкладки: `Каталог / Конструктор / Автоматика`. | `Стратегии` (5→3) + `Центр обхода` + `Настройки → Обход` |
| **3. Проверка** | Все диагностики в одном месте: `Ресурсы ⚡` (мониторинг), `DPI 34`, `Deep Check`, `Аудит 14`, `Результаты/История`, `Voice RTC`. Единая история + экспорт md/json/zip + кнопка «В фон». | `Diagnostics` (6 вкладок) + `MonitoringPage` + `DeepCheckPage` + `DpiPage` |
| **4. Списки и сеть** | `Домены` (редактор) + `Сеть` (DNS DoH + hosts/ipset + targets.txt). Один экран для всех файлов `lists/`. | `UserLists` (3) + DNS из Центра/Настроек + `DomainListUpdater` |
| **5. Игры** | `Режим` (TCP-only toggle + detection) + `Библиотека` (чекбоксы игр/порты/профили) + `Фиксы` (Phasmophobia/PUBG/Discord/Photon + GamingNetworkOptimizer). Одна страница, 3 карточки. | `GameModePage` + `Настройки → Игры` + `DisableQuicFake/YoutubeSNI` + часть `Home` |
| **6. Система** | `Профили` (конфиги + бэкапы ZIP + портативный) + `Автопереключение` (сеть/fingerprint) + `Обновления` (GUI/движок/ipset/hosts + NetworkReport) + `Настройки` (общие/интерфейс) + `Журнал` + `О программе`. 4 под-вкладки: `Профили / Автоматика / Обновления / Настройки`. | `Profiles` + `Settings` (урезанные) + `UpdatesPage` (встраивается только здесь, не дубль) + `Logs/About` |

Левая панель: 6 пунктов вместо 8, без заголовков-групп. Шапка: 4 чипа (статус/стратегия/сеть/игра) + 🔄 обновлений. Правая «Сводка»: по умолчанию свернута (42px, только цветные точки), разворачивается по клику — не конкурирует с центром.

Плюс: минус 2 раздела, минус ~10 дублей, глубина 2 клика, логика «куда нажать» очевидна.

### Вариант C — «Трёхзонный» (если хочется сохранить текущую ширину)

Оставить 7 разделов, но жёстко закрепить зоны: левая навигация 210px (фикс), центр 900–1100px (растягивается, как в v1.34.17), правая сводка — только оверлей (поверх, не отнимает ширину). Внутри каждого раздела — плоские Elevated карточки (minimal стиль) без двойных AccentSoft.

---

## 5. Что предлагается удалить/объединить (конкретно)

- Удалить `BypassCenterPage` как отдельную страницу или слить в `StrategiesPage` (матрица + DNS → внутрь Стратегий).
- Удалить `DpiPage.xaml` (дубль вкладки DPI внутри Diagnostics).
- Слить `MonitoringViewModel` → `DiagnosticsViewModel` (Monitoring уже вызывается из Diagnostics→Экспресс).
- DNS: оставить один источник — `Списки и сеть → Сеть`, удалить из Центра и Настроек.
- Hosts/ipset: оставить в `Списки и сеть → Сеть` + `Система → Обновления`, удалить из Центра.
- `Настройки → 8` → 4 (удалить `Обход/Сеть/Игры/Обновления` как дубли).
- `UpdatesPage` — только один экземпляр: доступен через 🔄 в шапке и как вкладка `Система → Обновления` (ShowHeader=false, как в v1.34.15–17), не как дубль в левом меню.
- Игровые переключатели `DisableQuicFake/YoutubeSNI/PreferIPv4/UseDoH` — из Центра/Настроек → в `Игры` или `Обход → Автоматика`.

---

## 6. Следующий шаг

Выбери вариант (A мягкий / B по задачам / C трёхзонный) или предложи свой — соберу точный план файлов (какие VM/Views сливать, какие табы удалять) и сделаю реализацию с миграцией настроек.
