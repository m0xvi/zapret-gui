using System.Windows.Input;
using ZapretGui.Core;

namespace ZapretGui.ViewModels
{
    /// <summary>
    /// «Помощь» — утилита в подвале меню (этап 6, docs/IA_REDESIGN.md §3.6).
    ///
    /// Правило раздела: **Помощь объясняет, Проверки делают**. Здесь нет ни настроек, ни состояния,
    /// ни второго экземпляра команд: каждая кнопка — это переход в нужный подраздел или вызов уже
    /// существующей команды (например, «Выключить обход» — та же команда, что на «Главной»).
    /// </summary>
    public sealed class HelpViewModel : ObservableObject
    {
        private readonly MainViewModel _main;

        public HelpViewModel(MainViewModel main)
        {
            _main = main;
            _main.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.ExpertMode)) Raise(nameof(ExpertMode));
            };

            CheckYoutubeCommand = new RelayCommand(() => OpenChecksTab(0));
            OpenHardSitesCommand = new RelayCommand(() => OpenBypassTab(4));
            CheckVoiceCommand = new RelayCommand(() => OpenChecksTab(1));
            OpenChecksCommand = new RelayCommand(() => _main.Navigate("diagnostics"));
            ResetNetworkCommand = new RelayCommand(OpenSystemSubTab);
            OpenGameFilterCommand = new RelayCommand(() => OpenBypassTab(4));
            OpenProfilesCommand = new RelayCommand(() => _main.Navigate("profiles"));
            OpenLogsCommand = new RelayCommand(() => _main.Navigate("logs"));
        }

        /// <summary>Режим интерфейса: «Полный сброс сети» ведёт в экспертный подраздел «Проверок» (§7).</summary>
        public bool ExpertMode => _main.ExpertMode;

        // ------------------------------------------------------------------ Сценарий 1: YouTube
        public ICommand CheckYoutubeCommand { get; }
        public ICommand OpenHardSitesCommand { get; }

        // ------------------------------------------------------------------ Сценарий 2: голос в Discord
        public ICommand CheckVoiceCommand { get; }

        // ------------------------------------------------------------------ Сценарий 3: интернет после установки
        /// <summary>Та же команда, что на «Главной» — Help не дублирует логику обхода.</summary>
        public ICommand ToggleBypassCommand => _main.Home.ToggleBypassCommand;

        public ICommand ResetNetworkCommand { get; }

        // ------------------------------------------------------------------ Сценарий 4: игры
        public ICommand ToggleGameModeCommand => _main.Home.ToggleGameModeCommand;
        public ICommand OpenGameFilterCommand { get; }

        // ------------------------------------------------------------------ Сценарий 5: вернуть как было
        public ICommand OpenProfilesCommand { get; }

        // ------------------------------------------------------------------ Отчёт
        /// <summary>Сборка ZIP-архива диагностики (журнал + результаты проверок + версии) — тот же
        /// экспорт, что в «Проверках → История и отчёты».</summary>
        public ICommand SendReportCommand => _main.Diagnostics.ExportArchiveCommand;

        /// <summary>Тот же отчёт одним JSON-файлом.</summary>
        public ICommand SaveReportJsonCommand => _main.Diagnostics.ExportReportCommand;

        public ICommand OpenLogsCommand { get; }

        public ICommand OpenChecksCommand { get; }

        private void OpenChecksTab(int tab)
        {
            _main.Diagnostics.SelectedSubTab = tab;
            _main.Navigate("diagnostics");
        }

        private void OpenSystemSubTab()
        {
            // В «Простом» режиме подраздела «Система» нет — «Проверки» откроются на быстрой проверке.
            _main.Diagnostics.OpenSystemSubTab();
            _main.Navigate("diagnostics");
        }

        private void OpenBypassTab(int tab)
        {
            _main.BypassCenter.SelectedSubTab = tab;
            _main.Navigate("bypass-center");
        }
    }
}
