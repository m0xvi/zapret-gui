using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Win32;
using ZapretGui.Core;

namespace ZapretGui.ViewModels
{
    /// <summary>
    /// Полная проверка конкретной сети: готовность Windows, движок, прямое соединение,
    /// DPI-признаки и реальные результаты стратегий. Системные исправления здесь не
    /// выполняются автоматически — автоматически выбирается только подтверждённая стратегия.
    /// </summary>
    public sealed class DeepCheckViewModel : ObservableObject
    {
        private readonly MainViewModel _main;
        private bool _isRunning;
        private double _progressValue;
        private bool _progressIndeterminate;
        private string _progressPercentText = "0%";
        private string _progressText = "";
        private string _summary = "Глубокая проверка ещё не запускалась";
        private string _summaryKey = "Muted";
        private string _lastCheckText = "Результаты ещё не сохранялись";
        private string _message = "";
        private string _customHost = "";
        private string _providerContext = "";
        private string _providerLimitations = "";
        private string _networkObservation = "";
        private string _automaticActionText = "Автоматические действия ещё не выполнялись";
        private string _savedReportPath = "";
        private DeepCheckReport? _report;
        private StrategyInfo? _recommendedStrategy;
        private StrategyCandidate? _generatedCandidate;
        private string _candidateGenerationText = "";
        private CancellationTokenSource? _cts;

        private sealed class StrategyDpiEvaluation
        {
            public StrategyInfo Strategy { get; init; } = new();
            public DpiCheckSnapshot Snapshot { get; init; } = new();
            public int Score { get; init; }
            public int FreezeCount { get; init; }
            public int FailedHttpsCount { get; init; }
            public int SuccessfulHttpsCount { get; init; }
        }

        public DeepCheckViewModel(MainViewModel main)
        {
            _main = main;
            RunCommand = new AsyncRelayCommand(RunAsync, () => !IsRunning);
            CancelCommand = new RelayCommand(Cancel, () => IsRunning);
            ApplyRecommendationCommand = new AsyncRelayCommand(ApplyRecommendationAsync,
                () => !IsRunning && _recommendedStrategy != null &&
                      _recommendedStrategy.TestResult?.IsSuitable == true && !Settings.SafeMode);
            ExportCommand = new RelayCommand(ExportReport, () => HasReport);
            SaveGeneratedCandidateCommand = new RelayCommand(SaveGeneratedCandidate,
                _ => !IsRunning && GeneratedCandidate != null);
        }

        public AppSettings Settings => _main.Settings;
        public ObservableCollection<DeepCheckFinding> Findings { get; } = new();
        public ObservableCollection<DeepCheckMetric> Metrics { get; } = new();
        public ObservableCollection<DeepCheckRecommendation> Recommendations { get; } = new();

        public bool IsRunning
        {
            get => _isRunning;
            private set
            {
                if (!Set(ref _isRunning, value)) return;
                Raise(nameof(ProgressVisible));
                (RunCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (CancelCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (ApplyRecommendationCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (SaveGeneratedCandidateCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }

        public bool ProgressVisible => IsRunning;

        public double ProgressValue
        {
            get => _progressValue;
            private set => Set(ref _progressValue, Math.Clamp(value, 0, 100));
        }

        public bool ProgressIndeterminate
        {
            get => _progressIndeterminate;
            private set => Set(ref _progressIndeterminate, value);
        }

        public string ProgressPercentText
        {
            get => _progressPercentText;
            private set => Set(ref _progressPercentText, value);
        }

        public string ProgressText
        {
            get => _progressText;
            private set => Set(ref _progressText, value);
        }

        public string Summary
        {
            get => _summary;
            private set => Set(ref _summary, value);
        }

        public string SummaryKey
        {
            get => _summaryKey;
            private set => Set(ref _summaryKey, value);
        }

        public string LastCheckText
        {
            get => _lastCheckText;
            private set => Set(ref _lastCheckText, value);
        }

        public string Message
        {
            get => _message;
            private set
            {
                if (Set(ref _message, value)) Raise(nameof(MessageVisible));
            }
        }

        public bool MessageVisible => !string.IsNullOrWhiteSpace(Message);
        public bool HasReport => _report != null;
        public string SavedReportText => string.IsNullOrWhiteSpace(_savedReportPath)
            ? "Промежуточный JSON ещё не записан"
            : "Автосохранённый JSON: " + _savedReportPath;
        public bool HasFindings => Findings.Count > 0;
        public bool HasMetrics => Metrics.Count > 0;
        public bool HasRecommendations => Recommendations.Count > 0;
        public bool HasGeneratedCandidate => GeneratedCandidate != null;
        public bool RecommendationCanBeApplied => _recommendedStrategy != null &&
            _recommendedStrategy.TestResult?.IsSuitable == true && !Settings.SafeMode;

        public string CustomHost
        {
            get => _customHost;
            set => Set(ref _customHost, value ?? "");
        }

        public string ProviderContext
        {
            get => _providerContext;
            private set => Set(ref _providerContext, value);
        }

        public string ProviderLimitations
        {
            get => _providerLimitations;
            private set => Set(ref _providerLimitations, value);
        }

        public string NetworkObservation
        {
            get => _networkObservation;
            private set => Set(ref _networkObservation, value);
        }

        public string AutomaticActionText
        {
            get => _automaticActionText;
            private set => Set(ref _automaticActionText, value);
        }

        public StrategyCandidate? GeneratedCandidate
        {
            get => _generatedCandidate;
            private set
            {
                if (!Set(ref _generatedCandidate, value)) return;
                Raise(nameof(HasGeneratedCandidate));
                Raise(nameof(GeneratedCandidateName));
                Raise(nameof(GeneratedCandidateSummary));
                Raise(nameof(GeneratedCandidateArgs));
                Raise(nameof(GeneratedCandidateFeatures));
                Raise(nameof(CandidateGenerationText));
                (SaveGeneratedCandidateCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }

        public string GeneratedCandidateName => GeneratedCandidate?.Name ?? "";
        public string GeneratedCandidateSummary => GeneratedCandidate?.Summary ?? "";
        public string GeneratedCandidateArgs => GeneratedCandidate?.ArgsText ?? "";
        public string GeneratedCandidateFeatures => GeneratedCandidate?.Features.Summary ?? "";
        public string CandidateGenerationText
        {
            get => _candidateGenerationText;
            private set => Set(ref _candidateGenerationText, value);
        }

        public string RecommendedStrategyText => _recommendedStrategy == null
            ? "Рабочая стратегия не подтверждена"
            : $"Рекомендована: «{_recommendedStrategy.Name}»";

        public string RecommendedStrategyArgs => _recommendedStrategy?.ShortArgs ?? "";

        public ICommand RunCommand { get; }
        public ICommand CancelCommand { get; }
        public ICommand ApplyRecommendationCommand { get; }
        public ICommand SaveGeneratedCandidateCommand { get; }
        public ICommand ExportCommand { get; }

        private void SetProgress(double value, string text)
        {
            ProgressValue = value;
            ProgressPercentText = $"{ProgressValue:0}%";
            ProgressText = text + "…";
        }

        private void UpdateProgress(string text)
        {
            var dpi = System.Text.RegularExpressions.Regex.Match(
                text, @"DPI_PROGRESS:(\d+)/(\d+)\s*(?:—\s*)?(.*)");
            if (dpi.Success)
            {
                var current = int.Parse(dpi.Groups[1].Value);
                var total = Math.Max(1, int.Parse(dpi.Groups[2].Value));
                SetProgress(65 + 25d * current / total,
                    dpi.Groups[3].Value.Length > 0 ? dpi.Groups[3].Value : "Проверяю DPI");
                return;
            }

            var diagnostics = System.Text.RegularExpressions.Regex.Match(
                text, @"DIAGNOSTICS_PROGRESS:(\d+)/(\d+)\s*(?:—\s*)?(.*)");
            if (diagnostics.Success)
            {
                var current = int.Parse(diagnostics.Groups[1].Value);
                var total = Math.Max(1, int.Parse(diagnostics.Groups[2].Value));
                SetProgress(10 + 18d * current / total,
                    diagnostics.Groups[3].Value.Length > 0 ? diagnostics.Groups[3].Value : "Проверяю системные условия");
                return;
            }

            var connection = System.Text.RegularExpressions.Regex.Match(
                text, @"CONNECTION_PROGRESS:(\d+)/(\d+)\s*(?:—\s*)?(.*)");
            if (connection.Success)
            {
                var current = int.Parse(connection.Groups[1].Value);
                var total = Math.Max(1, int.Parse(connection.Groups[2].Value));
                SetProgress(28 + 2d * current / total,
                    connection.Groups[3].Value.Length > 0 ? connection.Groups[3].Value : "Проверяю соединение");
                return;
            }

            var strategy = System.Text.RegularExpressions.Regex.Match(
                text, @"Проверяю стратегию (\d+) из (\d+):\s*(.*)");
            if (strategy.Success)
            {
                var current = int.Parse(strategy.Groups[1].Value);
                var total = Math.Max(1, int.Parse(strategy.Groups[2].Value));
                SetProgress(30 + 30d * current / total, strategy.Groups[3].Value);
                return;
            }

            var candidate = System.Text.RegularExpressions.Regex.Match(
                text, @"Проверяю новую комбинацию (\d+) из (\d+), повтор (\d+) из (\d+)");
            if (candidate.Success)
            {
                var index = int.Parse(candidate.Groups[1].Value);
                var total = Math.Max(1, int.Parse(candidate.Groups[2].Value));
                var repeat = int.Parse(candidate.Groups[3].Value);
                var repeats = Math.Max(1, int.Parse(candidate.Groups[4].Value));
                var done = (index - 1) * repeats + repeat;
                SetProgress(90 + 9d * done / Math.Max(1, total * repeats), text);
                return;
            }

            ProgressText = text + "…";
        }

        private async Task RunAsync()
        {
            if (IsRunning) return;
            if (_main.Diagnostics.IsRunning || _main.Diagnostics.IsDpiRunning ||
                _main.StrategiesPage.IsTestingAll || _main.StrategiesPage.IsBusy)
            {
                Message = "Сначала дождитесь завершения уже запущенной диагностики или проверки стратегии.";
                SummaryKey = "Warning";
                return;
            }
            var answer = System.Windows.MessageBox.Show(
                "Глубокая проверка последовательно проверит Windows, движок, DNS, TCP, HTTPS, DPI-suite под всеми запустившимися стратегиями, а также до трёх новых комбинаций автоконструктора. Временные стратегии запускаются только для теста, текущий обход после каждой пробы восстанавливается. Системные настройки автоматически изменяться не будут; рекомендация не становится активной без отдельного подтверждения. Продолжить?",
                "Подтверждение глубокой проверки", System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning);
            if (answer != System.Windows.MessageBoxResult.Yes) return;

            IsRunning = true;
            Findings.Clear();
            Metrics.Clear();
            Recommendations.Clear();
            _report = null;
            _savedReportPath = "";
            Raise(nameof(HasReport));
            Raise(nameof(SavedReportText));
            _recommendedStrategy = null;
            GeneratedCandidate = null;
            CandidateGenerationText = "";
            Raise(nameof(HasFindings));
            Raise(nameof(HasMetrics));
            Raise(nameof(HasRecommendations));
            Raise(nameof(RecommendedStrategyText));
            Raise(nameof(RecommendedStrategyArgs));
            Raise(nameof(RecommendationCanBeApplied));
            (ApplyRecommendationCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            Message = "";
            Summary = "Идёт глубокая проверка…";
            SummaryKey = "Warning";
            ProgressValue = 0;
            ProgressPercentText = "0%";
            ProgressIndeterminate = false;
            AutomaticActionText = "Системные исправления не выполняются автоматически.";
            _cts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
            var ct = _cts.Token;

            try
            {
                var progress = new Progress<string>(UpdateProgress);
                ProviderContext = (Settings.ProviderContext ?? new ProviderContext()).DisplayText;
                ProviderLimitations = BuildProviderLimitations(Settings.ProviderContext);
                AddProviderFinding(Settings.ProviderContext);

                SetProgress(0, "Проверяю готовность приложения и комплектность движка");
                AddReadinessFindings();
                var consistency = await Task.Run(
                    () => EngineConsistencyChecker.Check(Settings.EnginePath), ct).ConfigureAwait(true);
                foreach (var check in consistency.Checks)
                {
                    AddFinding("Движок", check.Name, check.Details,
                        check.IsOk ? "" : "Обновите или проверьте официальный комплект движка на странице «Обновления». ",
                        check.IsOk ? "Success" : "Warning");
                }
                SetProgress(10, "Проверяю системные условия и конфликты");
                var diagnostics = await DiagnosticsService.RunAsync(Settings, progress, ct).ConfigureAwait(true);
                foreach (var item in diagnostics)
                {
                    AddFinding("Windows", item.Title, item.Details, item.FixHint, item.SeverityKey);
                }

                var service = await Task.Run(ServiceHealthCache.Capture, ct).ConfigureAwait(true);
                AddFinding("Службы", "BFE", service.BfeText,
                    service.BfeNeedsRecovery ? "BFE нужна драйверу WinDivert; исправление доступно на странице «Диагностика»." : "",
                    service.Bfe == ServiceState.Running ? "Success" : "Warning");
                AddFinding("Службы", "WinDivert", service.WinDivertText + "; WinDivert14: " + service.WinDivert14Text,
                    service.HasWinDivertLeftovers ? "Остаточные службы удаляйте только отдельной подтверждённой операцией." : "",
                    service.HasWinDivertLeftovers ? "Warning" : "Success");

                SetProgress(28, "Снимаю базовые сетевые метрики без обхода");
                var directChecks = await ConnectionTester.RunAsync(ct, progress).ConfigureAwait(true);
                StrategyTestBatchResult? strategyBatch = null;
                AddConnectionMetrics(directChecks);
                var directPassed = directChecks.Count(check => check.Ok);
                AddFinding("Сеть", "Прямое соединение",
                    $"Доступно {directPassed} из {directChecks.Count} контрольных ресурсов.",
                    directPassed == directChecks.Count ? "" : "Недоступность может быть вызвана DNS, провайдером, сервером или локальной сетью; одной этой пробы недостаточно для вывода о DPI.",
                    directPassed == directChecks.Count ? "Success" : "Warning");

                SetProgress(30, "Последовательно проверяю все стратегии");
                strategyBatch = await _main.StrategiesPage.TestAllAsync(progress).ConfigureAwait(true);
                if (strategyBatch == null || strategyBatch.Results.Count == 0)
                {
                    AddFinding("Стратегии", "Проверка стратегий", "Стратегии не найдены или движок не готов.",
                        "Укажите папку движка и обновите список стратегий.", "Danger");
                }
                else
                {
                    var suitable = strategyBatch.Results.Count(result => result.IsSuitable);
                    var best = strategyBatch.Best;
                    Metrics.Add(new DeepCheckMetric
                    {
                        Title = "Стратегии",
                        Value = $"{suitable} из {strategyBatch.Results.Count} подходят",
                        Details = best == null ? "нет запускаемых результатов" : $"лучший результат: {best.Strategy.Name}, {best.PassedCount}/{best.Checks.Count}",
                        StatusKey = suitable > 0 ? "Success" : "Warning"
                    });
                    if (best?.IsSuitable == true)
                    {
                        AutomaticActionText = $"Первичный лидер «{best.Strategy.Name}» прошёл {best.PassedCount} из {best.Checks.Count}. Итоговая рекомендация будет рассчитана после DPI, сравнения direct/bypass и автопроверки новых вариантов; активная стратегия не изменена.";
                    }
                    else
                    {
                        AddFinding("Стратегии", "Подходящая стратегия", "Полный успешный результат не подтверждён.",
                            "Не включайте случайную стратегию. Проверьте ограничения сети и повторите тест позже.", "Warning");
                    }
                    foreach (var result in strategyBatch.Results.Where(result => !result.IsSuitable).Take(5))
                    {
                        AddFinding("Стратегии", result.Strategy.Name,
                            result.Started ? result.ChecksDetailsText : result.ErrorMessage,
                            result.FailureReasonsText, "Warning");
                    }

                    var parameterNames = _main.Strategies.Items
                        .SelectMany(strategy => StrategyFeatureAnalyzer.Analyze(strategy).ParameterNames)
                        .Where(name => !string.IsNullOrWhiteSpace(name))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    Metrics.Add(new DeepCheckMetric
                    {
                        Title = "Покрытие параметров",
                        Value = $"{parameterNames.Length} уникальных параметров winws.exe",
                        Details = parameterNames.Length == 0 ? "Параметры не распознаны." : string.Join(", ", parameterNames),
                        StatusKey = parameterNames.Length > 0 ? "Info" : "Warning"
                    });
                }
                SetProgress(60, "Сравниваю прямой доступ с текущей стратегией");

                ResourceDiagnosisResult? comparison = null;
                if (EngineService.IsEngineReady(Settings.EnginePath) && _main.Strategies.Items.Count > 0)
                {
                    SetProgress(60, "Сравниваю прямой доступ с текущей стратегией");
                    var target = MonitorTarget.CreateBuiltIn("YouTube", "https://www.youtube.com/generate_204");
                    comparison = await _main.Bypass.DiagnoseResourceAsync(target, ct).ConfigureAwait(true);
                    AddComparisonMetrics(comparison);
                    AddFinding("Сеть и обход", "Сравнение без обхода и с обходом", comparison.Summary,
                        comparison.Kind == ResourceDiagnosisKind.BypassHelps
                            ? "Вероятна блокировка DPI. Используйте подтверждённую стратегию с fake TLS/split и проверяйте её повторно."
                            : comparison.Kind == ResourceDiagnosisKind.StrategyBreaks
                                ? "Текущая стратегия мешает доступу или повышает нагрузку; выберите другую после проверки."
                                : "Сравнение не даёт доказательств ограничения именно провайдером.",
                        comparison.Kind == ResourceDiagnosisKind.Available ? "Success" : "Warning");
                }

                SetProgress(65, "Проверяю DNS, TLS и возможные признаки DPI");
                var dpi = await _main.Bypass.RunDpiCheckAsync(
                    string.IsNullOrWhiteSpace(CustomHost) ? null : CustomHost.Trim(), progress, ct,
                    maxTargets: 34).ConfigureAwait(true);
                var observation = NetworkObservationSnapshot.From(
                    dpi.Results, comparison, dpi.CreatedAt, dpi.ControlResult);
                NetworkObservation = observation.Summary + $" · средняя задержка проб: {observation.AverageLatencyMs} мс";
                AddDpiMetrics(dpi, observation);
                AddFinding("DPI", "Глубокая проверка DPI", dpi.ErrorMessage.Length > 0 ? dpi.ErrorMessage : dpi.Summary,
                    BuildDpiRecommendation(observation), dpi.ErrorMessage.Length > 0 ? "Danger" : observation.Summary.StartsWith("Нормализованные", StringComparison.Ordinal) ? "Success" : "Warning");
                SetProgress(70, "Проверяю DPI-suite под лидирующими стратегиями");
                var strategyDpiEvaluations = await RunStrategyDpiMatrixAsync(strategyBatch, ct)
                    .ConfigureAwait(true);
                SetProgress(88, "Собираю и проверяю варианты стратегии под эту сеть");

                await BuildProviderCandidateAsync(observation, ct).ConfigureAwait(true);
                SetNetworkAwareRecommendation(strategyBatch, observation, strategyDpiEvaluations);
                BuildRecommendations(diagnostics, observation, comparison);
                var errors = Findings.Count(item => item.StatusKey == "Danger");
                var warnings = Findings.Count(item => item.StatusKey == "Warning");
                Summary = errors > 0
                    ? $"Глубокая проверка завершена: критичных признаков {errors}, предупреждений {warnings}"
                    : warnings > 0
                        ? $"Глубокая проверка завершена: критичных признаков нет, предупреждений {warnings}"
                        : "Глубокая проверка завершена: критичных признаков не найдено";
                SummaryKey = errors > 0 ? "Danger" : warnings > 0 ? "Warning" : "Success";
                SetProgress(100, "Глубокая проверка завершена");
                LastCheckText = "Актуально · проверено: " + DateTime.Now.ToString("dd.MM.yyyy HH:mm");
                _report = BuildAndPersistReport();
                Raise(nameof(HasReport));
                (ExportCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
            catch (OperationCanceledException)
            {
                Summary = "Глубокая проверка отменена; промежуточные данные сохранены на экране";
                SummaryKey = "Info";
                LastCheckText = "Незавершено · данные собраны: " + DateTime.Now.ToString("dd.MM.yyyy HH:mm");
                Message = "Проверка отменена без системных изменений. Промежуточный отчёт автоматически записан в каталог истории; его также можно экспортировать.";
                _report = BuildAndPersistReport();
                Raise(nameof(HasReport));
                (ExportCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
            catch (Exception ex)
            {
                Summary = "Глубокая проверка завершилась с ошибкой";
                SummaryKey = "Danger";
                Message = ex.Message;
                _report = BuildAndPersistReport();
                Raise(nameof(HasReport));
                (ExportCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
            finally
            {
                _cts?.Dispose();
                _cts = null;
                ProgressText = "";
                IsRunning = false;
            }
        }

        private void AddReadinessFindings()
        {
            var ready = ReadinessEvaluator.Evaluate(Settings, _main.IsAdmin,
                EngineService.IsEngineReady(Settings.EnginePath), _main.Strategies.Items.Count);
            AddFinding("Готовность", ready.Status, ready.Details, "", ready.Key);
            AddFinding("Готовность", "Режим безопасности", Settings.SafeMode ? "Включён" : "Выключен",
                Settings.SafeMode ? "Автоматический запуск обхода запрещён безопасным режимом." : "Системные исправления всё равно требуют отдельного подтверждения.",
                Settings.SafeMode ? "Warning" : "Success");
            Metrics.Add(new DeepCheckMetric
            {
                Title = "Права",
                Value = _main.IsAdmin ? "администратор" : "обычный пользователь",
                Details = _main.IsAdmin ? "Системные операции доступны после подтверждения." : "Запуск движка и службы может быть недоступен.",
                StatusKey = _main.IsAdmin ? "Success" : "Warning"
            });
            var bypass = _main.Bypass.GetStatus();
            Metrics.Add(new DeepCheckMetric
            {
                Title = "Текущее состояние обхода",
                Value = bypass.StateText,
                Details = bypass.IsRunning
                    ? $"Стратегия: {bypass.StrategyName} · PID: {bypass.Pid?.ToString() ?? "не определён"} · время работы: {bypass.UptimeText}"
                    : "Активный процесс winws.exe не обнаружен",
                StatusKey = bypass.IsRunning ? "Info" : "Warning"
            });
            Metrics.Add(new DeepCheckMetric
            {
                Title = "Движок",
                Value = _main.EngineVersionText,
                Details = $"Путь: {Settings.EnginePath} · стратегий в памяти: {_main.Strategies.Items.Count}",
                StatusKey = EngineService.IsEngineReady(Settings.EnginePath) ? "Success" : "Danger"
            });
        }

        private void AddProviderFinding(ProviderContext? context)
        {
            var known = context?.IsKnown == true;
            AddFinding("Провайдер", known ? "Контекст провайдера" : "Провайдер не подтверждён",
                ProviderContext,
                known ? ProviderLimitations : "Укажите имя/ASN или выполните внешнее определение в настройках. Endpoint из DPI-suite провайдером пользователя не считается.",
                known ? "Info" : "Warning");
        }

        private void AddConnectionMetrics(IReadOnlyList<ConnectionCheck> checks)
        {
            foreach (var check in checks)
            {
                Metrics.Add(new DeepCheckMetric
                {
                    Title = check.Title,
                    Value = check.Ok ? $"доступен · {check.Milliseconds} мс" : "недоступен",
                    Details = check.Details,
                    StatusKey = check.Ok ? "Success" : "Warning"
                });
            }

            var times = checks.Where(check => check.Ok).Select(check => check.Milliseconds).ToArray();
            Metrics.Add(new DeepCheckMetric
            {
                Title = "Средняя задержка HTTPS",
                Value = times.Length == 0 ? "нет измерений" : $"{times.Average():0} мс",
                Details = times.Length == 0 ? "Контрольные узлы не ответили." : $"Минимум {times.Min()} мс · максимум {times.Max()} мс",
                StatusKey = times.Length == 0 ? "Warning" : "Info"
            });
        }

        private void AddComparisonMetrics(ResourceDiagnosisResult comparison)
        {
            Metrics.Add(new DeepCheckMetric
            {
                Title = "Без обхода",
                Value = comparison.Direct.Ok ? $"доступен · {comparison.Direct.Milliseconds} мс" : "недоступен",
                Details = comparison.Direct.Details + " · " + comparison.Direct.DnsDetails,
                StatusKey = comparison.Direct.Ok ? "Success" : "Warning"
            });
            Metrics.Add(new DeepCheckMetric
            {
                Title = "С обходом",
                Value = comparison.WithBypass.Ok ? $"доступен · {comparison.WithBypass.Milliseconds} мс" : "недоступен",
                Details = comparison.WithBypass.Details + " · " + comparison.WithBypass.DnsDetails,
                StatusKey = comparison.WithBypass.Ok ? "Success" : "Warning"
            });
        }

        private void AddDpiMetrics(DpiCheckSnapshot dpi, NetworkObservationSnapshot observation)
        {
            var probes = dpi.Results.SelectMany(result => result.Probes).ToList();
            var suspicious = probes.Count(probe => probe.PossibleDpiFreeze || probe.PossibleDnsSpoof);
            Metrics.Add(new DeepCheckMetric
            {
                Title = "DPI-узлы",
                Value = $"{dpi.TargetsTested} из {dpi.TargetsTotal} проверено",
                Details = dpi.SuiteSource + $" · подозрительных проб: {suspicious}",
                StatusKey = suspicious > 0 ? "Warning" : "Success"
            });
            Metrics.Add(new DeepCheckMetric
            {
                Title = "Признаки сети",
                Value = observation.Summary,
                Details = $"Средняя задержка всех проб: {observation.AverageLatencyMs} мс",
                StatusKey = observation.Summary.StartsWith("Нормализованные", StringComparison.Ordinal) ? "Success" : "Warning"
            });
            var providers = dpi.Results.Select(result => result.Provider)
                .Where(provider => !string.IsNullOrWhiteSpace(provider))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8);
            Metrics.Add(new DeepCheckMetric
            {
                Title = "Провайдеры endpoint-ов",
                Value = providers.Any() ? string.Join(", ", providers) : "нет данных",
                Details = "Это операторы тестовых узлов, а не определение провайдера пользователя.",
                StatusKey = "Info"
            });
            foreach (var result in dpi.Results)
            {
                var targetProbes = result.Probes.Where(probe => probe.Milliseconds > 0).ToArray();
                var targetSuspicious = result.Probes.Count(probe => probe.PossibleDpiFreeze || probe.PossibleDnsSpoof);
                Metrics.Add(new DeepCheckMetric
                {
                    Title = "DPI · " + result.DisplayName,
                    Value = $"{result.Probes.Count - targetSuspicious}/{result.Probes.Count} обычных проб",
                    Details = string.Join(" · ", result.Probes.Select(probe =>
                        $"{probe.TestName}: {probe.Status} · {probe.Milliseconds} мс")),
                    StatusKey = targetSuspicious > 0 ? "Warning" : targetProbes.Length > 0 ? "Success" : "Info"
                });
            }
        }

        private void SetNetworkAwareRecommendation(StrategyTestBatchResult? batch,
            NetworkObservationSnapshot observation,
            IReadOnlyDictionary<string, StrategyDpiEvaluation>? dpiEvaluations = null)
        {
            var suitable = batch?.Results
                .Where(result => result.IsSuitable)
                .ToList() ?? new List<StrategyTestResult>();
            if (suitable.Count == 0)
            {
                _recommendedStrategy = null;
                Raise(nameof(RecommendedStrategyText));
                Raise(nameof(RecommendedStrategyArgs));
                Raise(nameof(RecommendationCanBeApplied));
                (ApplyRecommendationCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                return;
            }

            var provider = Settings.ProviderContext ?? new ProviderContext();
            var history = StrategyEvaluationHistoryStore.Load();
            var hasDpiSignal = observation.HasDpiFreeze || observation.HasTlsError ||
                               observation.HasTcpTimeout || observation.BypassImprovesResult;
            var ranked = suitable.Select(result =>
            {
                var features = StrategyFeatureAnalyzer.Analyze(result.Strategy);
                var networkFit = 0;
                if (hasDpiSignal && features.UsesFakeTls) networkFit += 4;
                if ((observation.HasDpiFreeze || observation.HasTcpTimeout || observation.BypassImprovesResult) &&
                    features.SplitParameterCount > 0) networkFit += 3;
                if (observation.HasTlsError && features.UsesFakeTls) networkFit += 2;
                if (observation.HasDpiFreeze && features.UsesMultisplit) networkFit++;

                var providerScore = provider.IsKnown
                    ? history.Where(record => record.MatchesProvider(provider) &&
                                               record.CandidateName.Equals(result.Strategy.Name, StringComparison.OrdinalIgnoreCase))
                        .Select(record => record.Score)
                        .DefaultIfEmpty(0)
                        .Max()
                    : 0;
                var dpiFit = dpiEvaluations != null && dpiEvaluations.TryGetValue(
                    result.Strategy.Name, out var dpiResult) ? dpiResult.Score : 0;
                return new { result, networkFit, providerScore, dpiFit, features };
            })
            .OrderByDescending(item => item.dpiFit)
            .ThenByDescending(item => item.networkFit)
            .ThenByDescending(item => item.providerScore)
            .ThenByDescending(item => item.result.PassedCount)
            .ThenBy(item => item.result.Checks.Where(check => check.Ok).Sum(check => check.Milliseconds))
            .First();

            _recommendedStrategy = ranked.result.Strategy;
            Raise(nameof(RecommendedStrategyText));
            Raise(nameof(RecommendedStrategyArgs));
            Raise(nameof(RecommendationCanBeApplied));
            (ApplyRecommendationCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            AutomaticActionText = $"Рекомендация «{ranked.result.Strategy.Name}» рассчитана по результатам контрольных ресурсов, признакам DPI" +
                (provider.IsKnown ? " и подтверждённой истории для указанного провайдера." : ". Провайдер endpoint-ов не использовался как ISP.") +
                " Активная стратегия не изменена.";
            Recommendations.Add(new DeepCheckRecommendation
            {
                Title = "Сетевая рекомендация готова",
                Details = $"«{ranked.result.Strategy.Name}» прошла {ranked.result.PassedCount} из {ranked.result.Checks.Count} контрольных проверок; DPI-fit: {ranked.dpiFit}, сетевое соответствие: {ranked.networkFit}, история провайдера: {(ranked.providerScore > 0 ? "есть" : "нет") }.",
                ActionText = "Применяйте только после отдельного подтверждения и повторной проверки на своей сети.",
                StatusKey = "Success",
                IsAutomatic = false
            });
            Raise(nameof(HasRecommendations));
        }

        private async Task<IReadOnlyDictionary<string, StrategyDpiEvaluation>> RunStrategyDpiMatrixAsync(
            StrategyTestBatchResult? batch, CancellationToken ct)
        {
            var strategies = batch?.Results
                .Where(result => result.Started && result.IsSuitable)
                .OrderByDescending(result => result.PassedCount)
                .ThenBy(result => result.Checks.Where(check => check.Ok).Sum(check => check.Milliseconds))
                .ToList() ?? new List<StrategyTestResult>();
            if (strategies.Count == 0)
            {
                strategies = batch?.Results
                    .Where(result => result.Started)
                    .OrderByDescending(result => result.PassedCount)
                    .ToList() ?? new List<StrategyTestResult>();
            }

            var evaluations = new Dictionary<string, StrategyDpiEvaluation>(StringComparer.OrdinalIgnoreCase);
            if (strategies.Count == 0) return evaluations;

            for (var index = 0; index < strategies.Count; index++)
            {
                ct.ThrowIfCancellationRequested();
                var strategy = strategies[index].Strategy;
                SetProgress(70 + 18d * index / strategies.Count,
                    $"Проверяю DPI-suite под стратегией {index + 1} из {strategies.Count}: {strategy.Name}");
                var strategyProgress = new Progress<string>(text =>
                {
                    var progressMatch = System.Text.RegularExpressions.Regex.Match(
                        text, @"DPI_PROGRESS:(\d+)/(\d+)\s*(?:—\s*)?(.*)");
                    if (progressMatch.Success)
                    {
                        var current = int.Parse(progressMatch.Groups[1].Value);
                        var total = Math.Max(1, int.Parse(progressMatch.Groups[2].Value));
                        SetProgress(70 + 18d * (index + current / (double)total) / strategies.Count,
                            progressMatch.Groups[3].Value.Length > 0 ? progressMatch.Groups[3].Value : "Проверяю DPI-suite");
                    }
                    else
                    {
                        ProgressText = text + "…";
                    }
                });
                try
                {
                    var snapshot = await _main.Bypass.RunDpiCheckAsync(
                        null, strategyProgress, ct, maxTargets: 34, temporaryStrategy: strategy)
                        .ConfigureAwait(true);
                    var allResults = snapshot.Results.Concat(snapshot.ControlResult == null
                        ? Array.Empty<DpiTargetResult>() : new[] { snapshot.ControlResult });
                    var https = allResults.SelectMany(result => result.Probes)
                        .Where(probe => probe.ProbeKind == "HTTPS")
                        .ToList();
                    var freezes = https.Count(probe => probe.PossibleDpiFreeze);
                    var failed = https.Count(probe => probe.Status != "ОТВЕТ");
                    var successful = https.Count - failed;
                    evaluations[strategy.Name] = new StrategyDpiEvaluation
                    {
                        Strategy = strategy,
                        Snapshot = snapshot,
                        FreezeCount = freezes,
                        FailedHttpsCount = failed,
                        SuccessfulHttpsCount = successful,
                        Score = successful * 10 - failed * 20 - freezes * 100
                    };
                    Metrics.Add(new DeepCheckMetric
                    {
                        Title = "DPI · стратегия · " + strategy.Name,
                        Value = $"HTTPS: {successful}/{https.Count} ответов",
                        Details = $"Freeze: {freezes}; ошибок/тайм-аутов: {failed}; score: {evaluations[strategy.Name].Score}",
                        StatusKey = freezes == 0 && failed == 0 ? "Success" : "Warning"
                    });
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Metrics.Add(new DeepCheckMetric
                    {
                        Title = "DPI · стратегия · " + strategy.Name,
                        Value = "не запустилась",
                        Details = ex.Message,
                        StatusKey = "Warning"
                    });
                }
                SetProgress(70 + 18d * (index + 1) / strategies.Count,
                    $"DPI-suite под стратегией {strategy.Name} завершён");
            }
            return evaluations;
        }

        private async Task BuildProviderCandidateAsync(NetworkObservationSnapshot observation,
            CancellationToken ct)
        {
            if (_main.Strategies.Items.Count == 0)
            {
                CandidateGenerationText = "Нет исходных стратегий для автосборки.";
                return;
            }

            ProgressText = "Собираю и проверяю варианты стратегии под наблюдаемую сеть…";
            var generation = await Task.Run(() => StrategyCandidateGenerator.Generate(
                _main.Strategies.Items,
                Settings.ProviderContext ?? new ProviderContext(),
                EngineService.GetGameFilterMode(Settings.EnginePath),
                new StrategyCandidateGenerationOptions
                {
                    MaxCandidates = 16,
                    MaxVariantsPerSource = 4
                },
                ct,
                StrategyEvaluationHistoryStore.Load(),
                observation), ct).ConfigureAwait(true);

            var firstMutation = generation.Candidates.FirstOrDefault(item =>
                !item.MutationDescription.Equals("Исходная стратегия без изменений", StringComparison.OrdinalIgnoreCase));
            GeneratedCandidate = firstMutation ?? generation.Candidates.FirstOrDefault();
            if (generation.Candidates.Count == 0)
            {
                CandidateGenerationText = "Автоконструктор не получил подходящего набора вариантов.";
            }
            else
            {
                CandidateGenerationText = $"Собрано вариантов: {generation.Candidates.Count}; начинаю проверку до 3 новых комбинаций двумя повторами… {generation.ProviderHeuristicText}";
                var evaluations = await EvaluateGeneratedCandidatesAsync(generation.Candidates, ct)
                    .ConfigureAwait(true);
                var best = evaluations
                    .Where(evaluation => evaluation.RepeatCount >= 2 && evaluation.IsStable &&
                                          evaluation.Repeats.All(result => result.IsSuitable))
                    .OrderByDescending(evaluation => evaluation.Score)
                    .ThenBy(evaluation => evaluation.AverageElapsedMilliseconds)
                    .FirstOrDefault();
                if (best != null) GeneratedCandidate = best.Candidate;

                var stable = evaluations.Count(evaluation => evaluation.RepeatCount >= 2 && evaluation.IsStable);
                var suitable = evaluations.Count(evaluation => evaluation.Repeats.Any(result => result.IsSuitable));
                CandidateGenerationText = $"Собрано вариантов: {generation.Candidates.Count}; " +
                    $"новых комбинаций проверено: {evaluations.Count}; стабильных: {stable}; " +
                    $"с успешным контрольным результатом: {suitable}. {generation.ProviderHeuristicText}" +
                    (best == null
                        ? " Подтверждённый новый кандидат не найден — запуск не предлагается."
                        : $" Лучший подтверждённый вариант: {best.Candidate.Name}, score {best.Score}; запуск требует отдельного подтверждения.");
            }

            Metrics.Add(new DeepCheckMetric
            {
                Title = "Автоконструктор",
                Value = GeneratedCandidate == null
                    ? "вариант не собран"
                    : CandidateGenerationText.Contains("Лучший подтверждённый", StringComparison.Ordinal)
                        ? "новая комбинация проверена"
                        : "черновик собран",
                Details = CandidateGenerationText,
                StatusKey = GeneratedCandidate == null
                    ? "Warning"
                    : CandidateGenerationText.Contains("Лучший подтверждённый", StringComparison.Ordinal)
                        ? "Success"
                        : "Info"
            });
            if (GeneratedCandidate != null)
            {
                Recommendations.Add(new DeepCheckRecommendation
                {
                    Title = "Стратегия под текущий профиль сети",
                    Details = $"Собран кандидат «{GeneratedCandidate.Name}» на основе «{GeneratedCandidate.SourceStrategy}». Учтены провайдерский контекст, история результатов и локальные признаки сети.",
                    ActionText = CandidateGenerationText.Contains("Лучший подтверждённый", StringComparison.Ordinal)
                        ? "Комбинация проверена повторами на контрольных ресурсах; сохранение и запуск требуют отдельного подтверждения."
                        : "Новая комбинация не подтверждена: не сохраняйте и не запускайте её без отдельной проверки.",
                    StatusKey = CandidateGenerationText.Contains("Лучший подтверждённый", StringComparison.Ordinal) ? "Success" : "Info",
                    IsAutomatic = false
                });
                Raise(nameof(HasRecommendations));
            }
        }

        private async Task<List<StrategyCandidateEvaluation>> EvaluateGeneratedCandidatesAsync(
            IReadOnlyList<StrategyCandidate> candidates, CancellationToken ct)
        {
            const int maxCandidatesToTest = 3;
            const int repeats = 2;
            var evaluations = candidates
                .Where(candidate => !candidate.MutationDescription.Equals(
                    "Исходная стратегия без изменений", StringComparison.OrdinalIgnoreCase))
                .Take(maxCandidatesToTest)
                .Select(candidate => new StrategyCandidateEvaluation(candidate))
                .ToList();

            if (evaluations.Count == 0) return evaluations;
            var history = new List<StrategyEvaluationHistoryRecord>();
            try
            {
                for (var index = 0; index < evaluations.Count; index++)
                {
                    var evaluation = evaluations[index];
                    for (var repeat = 1; repeat <= repeats; repeat++)
                    {
                        ct.ThrowIfCancellationRequested();
                        ProgressText = $"Проверяю новую комбинацию {index + 1} из {evaluations.Count}, повтор {repeat} из {repeats}…";
                        evaluation.MarkTesting(repeat, repeats);
                        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        attemptCts.CancelAfter(TimeSpan.FromSeconds(45));
                        var result = await _main.Bypass.TestStrategyAsync(
                            ToStrategyInfo(evaluation.Candidate), attemptCts.Token).ConfigureAwait(true);
                        ct.ThrowIfCancellationRequested();
                        evaluation.AddResult(result, repeat, repeats);
                        if (attemptCts.IsCancellationRequested && !ct.IsCancellationRequested) break;
                    }
                    evaluation.Complete();
                    history.Add(StrategyEvaluationHistoryRecord.FromEvaluation(evaluation));
                }
                return evaluations;
            }
            finally
            {
                foreach (var evaluation in evaluations.Where(item => item.IsTesting)) evaluation.Complete();
                foreach (var record in history) StrategyEvaluationHistoryStore.TryAppend(record);
            }
        }

        private static StrategyInfo ToStrategyInfo(StrategyCandidate candidate)
            => new()
            {
                Name = candidate.Name,
                Args = candidate.Args.ToList(),
                Description = candidate.MutationDescription
            };

        private void SaveGeneratedCandidate(object? parameter)
        {
            var candidate = GeneratedCandidate;
            if (candidate == null) return;
            var answer = System.Windows.MessageBox.Show(
                "Сохранить собранный кандидат отдельно от оригинальных .bat-файлов? Он не будет запускаться автоматически даже после успешной проверки.",
                "Сохранение провайдерского кандидата", System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question);
            if (answer != System.Windows.MessageBoxResult.Yes) return;

            if (!StrategyCandidateStore.TrySave(candidate, out var saved))
            {
                Message = "Не удалось сохранить провайдерский кандидат";
                return;
            }

            _main.StrategiesPage.Refresh();
            Message = $"Кандидат «{saved.DisplayName}» сохранён. Теперь его нужно отдельно проверить на странице «Стратегии».";
        }

        private void BuildRecommendations(IReadOnlyList<DiagnosticItem> diagnostics,
            NetworkObservationSnapshot observation, ResourceDiagnosisResult? comparison)
        {
            foreach (var item in diagnostics.Where(item => item.Status != DiagStatus.Ok &&
                                                             !string.IsNullOrWhiteSpace(item.FixHint)).Take(6))
            {
                Recommendations.Add(new DeepCheckRecommendation
                {
                    Title = item.Title,
                    Details = item.FixHint,
                    ActionText = item.HasAutoFix ? "Доступно отдельное исправление на странице «Диагностика»" : "Только после ручной проверки",
                    StatusKey = item.SeverityKey,
                    IsAutomatic = false
                });
            }

            if (observation.HasDnsError)
                Recommendations.Add(new DeepCheckRecommendation
                {
                    Title = "Проверить DNS",
                    Details = "Обнаружены ошибки DNS. Сравните результат с защищённым DNS в браузере или настройках Windows.",
                    ActionText = "Автоматическое изменение DNS не выполняется",
                    StatusKey = "Warning"
                });
            if (observation.HasDpiFreeze || observation.BypassImprovesResult)
                Recommendations.Add(new DeepCheckRecommendation
                {
                    Title = "Вероятен DPI-паттерн",
                    Details = "Порядок проверки стоит строить вокруг стратегий с fake TLS и split; это рекомендация по признакам, а не доказательство причины.",
                    ActionText = _recommendedStrategy == null ? "Проверить стратегии вручную" : "Подтверждённая стратегия выбрана автоматически",
                    StatusKey = "Warning"
                });
            if (comparison?.Kind == ResourceDiagnosisKind.StrategyBreaks)
                Recommendations.Add(new DeepCheckRecommendation
                {
                    Title = "Текущая стратегия ухудшает результат",
                    Details = "Сравнение показало, что обход не помогает или повышает задержку.",
                    ActionText = "Примените другую стратегию после подтверждённого теста",
                    StatusKey = "Danger"
                });

            Recommendations.Add(new DeepCheckRecommendation
            {
                Title = "Ограничения провайдера",
                Details = ProviderLimitations,
                ActionText = "Точный лимит скорости, тариф и правила фильтрации по этим пробам определить нельзя",
                StatusKey = "Info"
            });
            Raise(nameof(HasRecommendations));
        }

        private string BuildDpiRecommendation(NetworkObservationSnapshot observation)
        {
            if (observation.HasDpiFreeze || observation.BypassImprovesResult)
                return "Есть признаки, при которых обычно помогают fake TLS/split. Выбор делайте по подтверждённым результатам, а не по имени endpoint-провайдера.";
            if (observation.HasDnsError)
                return "Сначала проверьте DNS; DPI-стратегия не исправляет ошибку разрешения имени сама по себе.";
            return "Подозрительных признаков не найдено на проверенных узлах; это не гарантирует доступность каждого сайта.";
        }

        private static string BuildProviderLimitations(ProviderContext? context)
        {
            var identity = context?.IsKnown == true ? context.DisplayText : "Провайдер не указан";
            return identity + ". Локальная проверка измеряет только доступность, DNS, TCP, TLS, HTTP и задержку. Она не видит тарифный лимит, реальную полосу, правила DPI или форму блокировки всего провайдера. Названия операторов из внешнего DPI-suite относятся к тестовым endpoint-ам.";
        }

        private void AddFinding(string category, string title, string details, string recommendation, string statusKey)
        {
            Findings.Add(new DeepCheckFinding
            {
                Category = category,
                Title = title,
                Details = details ?? "",
                Recommendation = recommendation ?? "",
                StatusKey = statusKey
            });
            Raise(nameof(HasFindings));
        }

        private DeepCheckReport BuildReport()
            => new()
            {
                CreatedAt = DateTime.Now,
                Summary = Summary,
                SummaryKey = SummaryKey,
                ProviderContext = ProviderContext,
                ProviderLimitations = ProviderLimitations,
                SelectedStrategy = Settings.SelectedStrategy,
                RecommendedStrategy = _recommendedStrategy?.Name ?? "",
                GeneratedCandidate = GeneratedCandidate?.Name ?? "",
                GeneratedCandidateArgs = GeneratedCandidate?.ArgsText ?? "",
                GeneratedCandidateMutation = GeneratedCandidate?.MutationDescription ?? "",
                GeneratedCandidateValidation = CandidateGenerationText,
                NetworkObservation = NetworkObservation,
                Findings = Findings.ToList(),
                Metrics = Metrics.ToList(),
                Recommendations = Recommendations.ToList()
            };

        private DeepCheckReport BuildAndPersistReport()
        {
            var report = BuildReport();
            try
            {
                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Converters = { new JsonStringEnumConverter() }
                };
                var json = JsonSerializer.Serialize(report, options);
                var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
                var reportPath = Path.Combine(AppPaths.DeepCheckReportsDir,
                    "deep-check-" + stamp + ".json");
                var latestPath = Path.Combine(AppPaths.DeepCheckReportsDir,
                    "deep-check-latest.json");
                var temporary = reportPath + ".tmp";
                File.WriteAllText(temporary, json);
                File.Move(temporary, reportPath, true);
                File.Copy(reportPath, latestPath, true);
                _savedReportPath = reportPath;
                Raise(nameof(SavedReportText));
            }
            catch (Exception ex)
            {
                AppLog.Warn("Не удалось автоматически сохранить отчёт глубокой проверки: " + ex.Message);
            }
            return report;
        }

        private async Task ApplyRecommendationAsync()
        {
            var strategy = _recommendedStrategy;
            if (strategy == null || strategy.TestResult?.IsSuitable != true) return;
            if (Settings.SafeMode)
            {
                Message = "Безопасный режим запрещает автоматический запуск обхода. Отключите его или примените стратегию вручную после подтверждения.";
                return;
            }

            var answer = System.Windows.MessageBox.Show(
                $"Запустить обход со стратегией «{strategy.Name}»? Текущий процесс будет остановлен и запущен с новыми параметрами.",
                "Применение рекомендации", System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question);
            if (answer != System.Windows.MessageBoxResult.Yes) return;

            Message = "Применяю подтверждённую рекомендацию…";
            await _main.StrategiesPage.ApplyStrategyAsync(strategy).ConfigureAwait(true);
            Message = "Рекомендация применена. Проверьте состояние на странице «Обзор».";
        }

        private void Cancel()
        {
            if (IsRunning)
            {
                ProgressText = "Отменяю глубокую проверку и восстанавливаю состояние…";
                _cts?.Cancel();
            }
        }

        private void ExportReport()
        {
            if (_report == null) return;
            try
            {
                var dialog = new SaveFileDialog
                {
                    Title = "Экспорт глубокой проверки",
                    Filter = "JSON-отчёт (*.json)|*.json",
                    FileName = "zapret-gui-deep-check-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json",
                    AddExtension = true,
                    DefaultExt = ".json"
                };
                if (dialog.ShowDialog() != true) return;
                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Converters = { new JsonStringEnumConverter() }
                };
                File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(_report, options));
                Message = "Глубокий отчёт сохранён: " + dialog.FileName;
            }
            catch (Exception ex)
            {
                Message = "Не удалось сохранить отчёт: " + ex.Message;
            }
        }
    }
}
