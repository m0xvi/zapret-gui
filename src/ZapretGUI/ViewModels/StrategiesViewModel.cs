using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Input;
using ZapretGui.Core;

namespace ZapretGui.ViewModels
{
    public sealed class StrategiesViewModel : ObservableObject
    {
        private readonly MainViewModel _main;
        private string _searchText = "";
        private int _categoryIndex;
        private bool _onlyRecommended;
        private StrategyInfo? _selected;
        private bool _isBusy;
        private string _message = "";

        public StrategiesViewModel(MainViewModel main)
        {
            _main = main;

            View = CollectionViewSource.GetDefaultView(Store.Items);
            View.Filter = FilterItem;

            Categories = new[] { "Все категории", "FAKE TLS AUTO", "ALT", "SIMPLE FAKE", "БАЗОВАЯ", "EXP" };

            RunCommand = new AsyncRelayCommand(RunAsync, () => Selected != null && !IsBusy);
            InstallServiceCommand = new AsyncRelayCommand(InstallServiceAsync, () => Selected != null && !IsBusy);
            OpenBatCommand = new RelayCommand(() => { if (Selected != null) Shell.OpenInNotepad(Selected.FullPath); });
            CopyArgsCommand = new RelayCommand(CopyArgs, () => Selected != null);
            SetDefaultCommand = new RelayCommand(SetDefault, () => Selected != null);
            RefreshCommand = new RelayCommand(Refresh);
            OpenFolderCommand = new RelayCommand(() => Shell.OpenFolder(Store.Folder));
            UseRecommendedCommand = new RelayCommand(UseRecommended);
        }

        public StrategyStore Store => _main.Strategies;
        public AppSettings Settings => _main.Settings;
        public BypassController Bypass => _main.Bypass;

        public ICollectionView View { get; }
        public string[] Categories { get; }

        public bool IsEngineReady => EngineService.IsEngineReady(Settings.EnginePath);
        public bool IsEngineMissing => !IsEngineReady;

        public string SearchText
        {
            get => _searchText;
            set
            {
                if (Set(ref _searchText, value)) View.Refresh();
            }
        }

        public int CategoryIndex
        {
            get => _categoryIndex;
            set
            {
                if (Set(ref _categoryIndex, value)) View.Refresh();
            }
        }

        public bool OnlyRecommended
        {
            get => _onlyRecommended;
            set
            {
                if (Set(ref _onlyRecommended, value)) View.Refresh();
            }
        }

        public StrategyInfo? Selected
        {
            get => _selected;
            set
            {
                if (!Set(ref _selected, value)) return;
                Raise(nameof(HasSelection));
                Raise(nameof(SelectedDescription));
                Raise(nameof(SelectedArgs));
                Raise(nameof(SelectedCategory));
                Raise(nameof(SelectedPath));
                Raise(nameof(IsSelectedDefault));
                (RunCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (InstallServiceCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (CopyArgsCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (SetDefaultCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }

        public bool HasSelection => Selected != null;
        public string SelectedDescription => Selected?.Description ?? "";
        public string SelectedArgs => Selected?.ShortArgs ?? "";
        public string SelectedCategory => Selected?.Category ?? "";
        public string SelectedPath => Selected?.FullPath ?? "";
        public bool IsSelectedDefault => Selected != null &&
            Selected.Name.Equals(Settings.SelectedStrategy, StringComparison.OrdinalIgnoreCase);

        public string CountText => $"{View.Cast<object>().Count()} из {Store.Items.Count} стратегий";

        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                if (Set(ref _isBusy, value))
                {
                    (RunCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (InstallServiceCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                }
            }
        }

        public string Message
        {
            get => _message;
            private set
            {
                if (Set(ref _message, value)) Raise(nameof(MessageVisible));
            }
        }

        public bool MessageVisible => !string.IsNullOrWhiteSpace(_message);

        public ICommand RunCommand { get; }
        public ICommand InstallServiceCommand { get; }
        public ICommand OpenBatCommand { get; }
        public ICommand CopyArgsCommand { get; }
        public ICommand SetDefaultCommand { get; }
        public ICommand RefreshCommand { get; }
        public ICommand OpenFolderCommand { get; }
        public ICommand UseRecommendedCommand { get; }

        // ------------------------------------------------------------------ логика

        private bool FilterItem(object item)
        {
            if (item is not StrategyInfo strategy) return false;

            if (OnlyRecommended && !strategy.IsRecommended) return false;

            if (CategoryIndex > 0 && !strategy.Category.Equals(Categories[CategoryIndex], StringComparison.OrdinalIgnoreCase))
                return false;

            if (!string.IsNullOrWhiteSpace(SearchText))
            {
                var query = SearchText.Trim();
                var haystack = strategy.Name + " " + strategy.Description + " " + strategy.Category;
                if (haystack.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) return false;
            }

            return true;
        }

        public void Refresh()
        {
            var keep = Selected?.Name;
            Store.Refresh();
            View.Refresh();

            Selected = Store.Find(keep) ?? Store.Recommended ?? Store.Items.FirstOrDefault();
            Raise(nameof(CountText));
            Raise(nameof(IsEngineReady));
            Raise(nameof(IsEngineMissing));
        }

        private void UseRecommended()
        {
            var recommended = Store.Recommended ?? Store.Items.FirstOrDefault();
            if (recommended == null) return;
            Selected = recommended;
            SetDefault();
        }

        private void SetDefault()
        {
            if (Selected == null) return;
            Settings.SelectedStrategy = Selected.Name;
            SettingsStore.Save(Settings);
            Message = $"«{Selected.Name}» назначена основной стратегией";
            _main.Home.ReloadFromEngine();
            Raise(nameof(IsSelectedDefault));
        }

        private void CopyArgs()
        {
            if (Selected == null) return;
            try
            {
                System.Windows.Clipboard.SetText(Selected.ShortArgs);
                Message = "Команда запуска скопирована в буфер обмена";
            }
            catch (Exception ex) { Message = "Не удалось скопировать: " + ex.Message; }
        }

        private async Task RunAsync()
        {
            if (Selected == null) return;
            if (!Shell.IsAdmin())
            {
                Message = "Нужны права администратора — перезапустите приложение от имени администратора";
                return;
            }

            IsBusy = true;
            Message = $"Запускаю «{Selected.Name}»…";
            try
            {
                var result = await Bypass.StartAsync(Selected, EngineService.GetGameFilterMode(Settings.EnginePath), Settings.ShowWinwsConsole);
                Message = result.Message;
                if (result.Ok)
                {
                    Settings.SelectedStrategy = Selected.Name;
                    SettingsStore.Save(Settings);
                    _main.Home.ReloadFromEngine();
                }
            }
            finally { IsBusy = false; }
        }

        private async Task InstallServiceAsync()
        {
            if (Selected == null) return;
            if (!Shell.IsAdmin())
            {
                Message = "Нужны права администратора";
                return;
            }

            IsBusy = true;
            Message = $"Устанавливаю службу со стратегией «{Selected.Name}»…";
            try
            {
                var result = await Bypass.InstallServiceAsync(Selected, EngineService.GetGameFilterMode(Settings.EnginePath));
                Message = result.Message;
                _main.Home.RefreshStatus();
            }
            finally { IsBusy = false; }
        }
    }
}
