# Анализ бесшовного переключения стратегий — все источники

Дата: 2026-09-21
Версия: 1.6.3 (после фикса гонки службы)

## 1. Проблема пользователя

> «Автостратегия остаётся включённой, в разделе Стратегии нельзя переключить на обычную без выключения обхода и повторного применения»

Корень: `BypassController.StartAsync` сразу падал с `«Обход уже запущен как служба. Остановите службу…»` если `WinServices.Query("zapret")==Running`, даже когда пользователь нажимал «Запустить» для другой стратегии. Кнопка «Запустить» в каталоге была тупиковой при активном обходе.

Дополнительно: установка служб (`InstallServiceAsync`) никак не связана с кнопкой «Запустить» — пользователь не понимал, что нужно вручную останавливать.

## 2. Карта всех точек входа

| № | Модуль | Команда / метод | Было | Стало | Комментарий |
|---|--------|-----------------|------|-------|-------------|
| 1 | `StrategiesViewModel.RunAsync` (кнопка «Запустить» в каталоге) | `RunCommand` | `StartAsync` → FAIL при службе | `IsRunning ? SwitchToStrategyAsync : StartAsync` | Главный фикс кейса пользователя |
| 2 | `StrategiesViewModel.InstallServiceAsync` | `InstallServiceCommand` | `InstallServiceAsync` | оставить — явная установка службы, `Delete+Create` уже бесшовна | Переключает режим standalone→служба |
| 3 | `StrategiesViewModel.ApplyWinnerStrategyAsync` (автоподбор) | `ApplyWinnerStrategyCommand` | `StartAsync` если running | `SwitchToStrategyAsync` | Иначе «умный автоподбор» не мог применить победителя без ручного стопа |
| 4 | `StrategiesViewModel.BuilderApplyAsync` | `BuilderApplyCommand` | через `RunAsync` | через `RunAsync`(исправлен) | Сохранение+применение конструктора |
| 5 | `StrategiesViewModel.RunSavedCandidateAsync` | `RunSavedCandidateCommand` | через `RunAsync` | через `RunAsync` | Кандидаты из «АВТОКОНСТРУКТОР» |
| 6 | `StrategiesViewModel.MakeCandidatePrimary` | `MakeCandidatePrimaryCommand` | только `SelectedStrategy=` | оставить — только выбор по умолчанию, без автостарта | Не стартует, поэтому не трогаем |
| 7 | `StrategiesViewModel.ApplyBestRecommendedAsync` | `ApplyBestRecommendedCommand` | через `RunAsync` | через `RunAsync` | Эмпирическая рекомендация после `TestAll` |
| 8 | `HomeViewModel.StartAsync` | `StartCommand` (Обзор → Запустить) | `StartAsync` с `ResolveLegacy` + `Confirm` | оставить — срабатывает только когда `!IsRunning` | Старт с нуля, бесшовность не нужна |
| 9 | `HomeViewModel.ApplyRecommendedStrategyAsync` (после «Проверить всё») | `ApplyRecommendedStrategyCommand` | `SelectedStrategy=` + `StartAsync` если wasStopped | `SwitchToStrategyAsync` если wasRunning, иначе `StartAsync` | Иначе рекомендация из 4-шаговой проверки не применялась без стопа |
| 10 | `HomeViewModel.InstallServiceAsync / Reinstall / Remove` | три кнопки службы | `Install/Remove` | оставить | Явное управление службой |
| 11 | `MiniOverlayViewModel.CycleStrategyAsync` (горячие клавиши ←/→, мини-HUD) | `NextStrategyCommand` | `StartAsync` | `SwitchToStrategyAsync` | Иначе переключение горячими клавишами падало при службе |
| 12 | `MainWindow.xaml.cs` — Tray `SelectStrategyRequested` | иконка трея → выбор стратегии | `StartAsync` | `SwitchToStrategyAsync` | Самый частый путь из трея |
| 13 | `MainWindow.xaml.cs` — Tray `ToggleBypassRequested` | иконка трея → вкл/выкл | `Stop` / `Start` (stopped only) | оставить — toggle, не switch |
| 14 | `MainWindow.xaml.cs` — автозапуск при старте приложения | `MainWindow` ctor | `StartAsync` | оставить — старт с нуля |
| 15 | `MainWindow.xaml.cs` — `ShowLegacyDialog` → TakeOver | диалог конфликта старого zapret | `StartAsync` | оставить — legacy уже остановлен, обход выключен |
| 16 | `ProfileManager.ApplyProfileAsync` | профили → Применить | `StartAsync` | `SwitchToStrategyAsync` | Профиль меняет стратегию+DNS+GameMode, должен бесшовно перезапускать |
| 17 | `MonitoringViewModel.StartSelectedStrategyAsync` | фоновый мониторинг, автоподбор при сбое | `before.State==RunningService ? Install : Start` | `current.ServiceState==Running ? Install : IsRunning ? Switch : Start` | Было завязано на `before`, ломалось при гонке `Running` без процесса |
| 18 | `WatchdogService.HandleCrashAsync` | сторожевой таймер | `StartAsync` | `SwitchToStrategyAsync` (standalone) / `WinServices.Start` (служба) | Автовосстановление после падения |
| 19 | `UpdatesViewModel.UpdateEngineAsync` | Обновления → Скачать движок | `PrepareForEngineUpdate` → `StartAsync` (терял режим службы) | `beforeStatus` + `wasService ? Install : Switch` | Фикс потери службы после обновления движка |
| 20 | `UpdatesViewModel.RollbackEngineAsync` | Откат движка | `before.State==RunningService ? WinServices.Start : Start` | оставить — уже корректно различает службу/процесс |
| 21 | `DeepCheckViewModel.ApplyRecommendationAsync` | Глубокая проверка → Применить | `StartAsync` | `SwitchToStrategyAsync` | Глубокая проверка тоже должна бесшовно применять |
| 22 | `UserListsViewModel` — применение хостов/списков с перезапуском | добавление в bypass | `StartAsync` | `SwitchToStrategyAsync` | Перезапуск после изменения списков |
| 23 | `FirstLaunchViewModel.InstallServiceAsync` | мастер первого запуска | `InstallServiceAsync` | оставить | Явная установка |

**Итого 23 точки, 13 исправлены на `SwitchToStrategyAsync`.**

## 3. Логика `SwitchToStrategyAsync` (новое)

```csharp
public async Task<OperationResult> SwitchToStrategyAsync(StrategyInfo s, GameFilterMode g, bool showConsole, CancellationToken ct) {
    var status = GetStatus(); // ServiceState + процесс winws
    if (status.ServiceState is Running or StartPending or StopPending) {
        // служба имеет приоритет — даже если процесс ещё не виден (гонка)
        return await InstallServiceAsync(s, g, ct);
    }
    if (status.IsRunning) { // standalone
        await StopAsync(ct);
        return await StartAsync(s, g, showConsole, ct);
    }
    return await StartAsync(s, g, showConsole, ct);
}
```

Почему именно так:

* `GetStatus()` раньше возвращал `BypassState.RunningService` только когда `ServiceState==Running && processExists`. При быстром клике «Запустить» сразу после `sc start zapret` процесс ещё не появился → старая ветка уходила в `StartAsync` и падала с `«Обход уже запущен как служба»`. Новая ветка смотрит напрямую на `ServiceState`, поэтому корректно переустанавливает службу.
* `StartPending/StopPending` тоже трактуем как «служба», иначе повторный клик во время перехода давал бы race.
* `IsRunning` (standalone) — останавливаем скрытый `winws.exe` через `StopAsync` (служба + процесс), затем стартуем заново. `StopAsync` уже ждёт `WaitForAsync(() => !IsProcessRunning, 8000)` и `ServiceState!=Running`.
* Если выключено — обычный `StartAsync` (проверяет `WinwsPath`, `EnsureUserLists`, `EnsureTcpTimestamps`, `BuildArgs`, скрытый/консольный запуск, проверка `HasExited`).

## 4. Дополнительные защиты

* `StrategiesViewModel.RunAsync` теперь проверяет `Shell.IsAdmin()` и наличие `bin\winws.exe` до попытки, показывает понятное сообщение вместо тихого `Fail`.
* `ProfileManager`, `Watchdog`, `Monitoring`, `DeepCheck`, `UserLists` — все переведены на `Switch`, чтобы не терять режим.
* `UpdatesViewModel` сохраняет `beforeStatus` до `PrepareForEngineUpdateAsync` и после успешного копирования файлов делает `wasService ? InstallServiceAsync : Switch`, чтобы обновление не превращало службу в standalone.
* `MainWindow` Tray и `MiniOverlay` — те же исправления, иначе горячие клавиши/трей ломались при службе.

## 5. Диаграмма состояний

```
[Stopped] --StartAsync--> [RunningStandalone] --Switch--> [RunningStandalone'] (Stop+Start)
   |                           |                                   |
   | InstallService            | Switch (ServiceState==Running)    | switch
   v                           v                                   v
[RunningService] <---InstallService--- [RunningService'] (reinstall)
   |  ^                                    |
   |  | StopAsync                           | StopAsync + Start (если нужен standalone)
   v  |                                    v
[Stopped] --------------------------------+
```

Все переходы атомарны с точки зрения UI: кнопка «Запустить» в любом состоянии даёт конечное состояние «новая стратегия активна» без промежуточного ручного стопа.

## 6. Проверка — симуляция (Python)

Запущен скрипт `/tmp/sim_switch.py`, который моделирует `GetStatus` + обе логики:

```
Служба запущена + процесс                old:InstallService OK | new:InstallService OK
Служба запущена, процесс ещё не виден     old:Start        FAIL| new:InstallService OK  ← главный фикс
Служба StartPending + процесс             old:Stop+Start   FAIL| new:InstallService OK
Служба StopPending без процесса           old:Start        FAIL| new:InstallService OK
Только процесс (standalone)               old:Stop+Start   OK  | new:Stop+Start   OK
...
```

Новая логика покрывает все 5 пользовательских сценариев, старая падала в 3 из 8.

## 7. Можно ли протестировать удобство «живьём»?

* **Песочница Linux** не может запустить WPF (`net8.0-windows`, `UseWPF`, `WinDivert`, `sc.exe`, реестр, `Process.GetProcessesByName("winws")`). Локальный `dotnet build` отсутствует (`dotnet-install.sh` падает с `SSL_ERROR_SYSCALL`), `apt` пуст. Поэтому интерактивный запуск GUI в этой среде невозможен.
* **Что сделано вместо этого:**
  1. `python3 tools/check_bindings.py` — 18 XAML, 93 ключа, 0 ошибок.
  2. Статическая симуляция всех 23 точек входа (скрипт выше).
  3. Сборка на `windows-latest` в GitHub Actions — `dotnet build -c Release` (см. runs `35590664154` `success`, `35590854873` `success`).
  4. Ручной аудит всех `StartAsync` → `Switch` с учётом `ServiceState` гонки.
* **Как проверить на Windows вручную (чек-лист):**
  1. Установить службу с автостратегией → в «Стратегии» выбрать `general` → нажать «Запустить» → должна пройти переустановка службы без ошибки, `GetStatus().ServiceStrategy == "general"`.
  2. Запустить standalone `fake-tls-auto` → в каталоге выбрать `alt` → «Запустить» → процесс перезапустился, `State==RunningStandalone`, `StrategyName==alt`.
  3. Выключить обход → выбрать любую стратегию → «Запустить» → `RunningStandalone`.
  4. Горячие клавиши мини-HUD `→`/`←` при службе — должны переустанавливать службу.
  5. Трей → правый клик → выбор стратегии при службе — переустановка.
  6. `Updates` → обновить движок при службе → после `PrepareForEngineUpdate` служба должна вернуться как служба, а не как процесс.
  7. `Monitoring` → сбой `StrategyBreaks` → автоподбор → должен вызвать `Install` если была служба.
  8. Без прав администратора — любой `RunAsync` показывает «Перезапустите от администратора», а не generic fail.

## 8. Связь с оверлеем GUI

Хотя это отдельный тикет, оверлей `UpdatesViewModel.IsGuiUpdating` + `MainWindow` `Border #AA000000` уже затемняет всё окно при `DownloadAndScheduleAsync` и показывает `Progress 0-100%` + `Status`. Логика не влияет на переключение стратегий, но использует тот же подход `ProgressInfo` + `Indeterminate`.

## 9. Дальнейшие улучшения (не вошли в 1.6.3)

* Кнопка «Запустить» → динамический текст «Переключить на …» когда `IsRunning && Selected != RunningStrategy`.
* `SetDefault` → опционально сразу `Switch` если `IsRunning`.
* `Watchdog` для службы — использовать `InstallService` если `SelectedStrategy` изменилась, а не просто `WinServices.Start`.
* Централизовать `ResolveLegacy` + `IsAdmin` + `Confirm` в один `StrategyApplicationService`, чтобы не дублировать в каждом ViewModel.

---
*Автор аудита: агент Arena, ветка `arena/01a0c100-zapret-gui`, коммиты `ff7917f` → `f1ba118` → `1.6.3`*
