using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using ZapretGui.Core;

namespace ZapretGui.ViewModels
{
    /// <summary>Строка выдачи поиска: пункт индекса плюс пользовательская метка «избранное».</summary>
    public sealed class SearchResultItem : ObservableObject
    {
        private readonly SearchViewModel _owner;
        private bool _isFavorite;

        public SearchResultItem(SearchEntry entry, SearchViewModel owner, bool isFavorite)
        {
            Entry = entry;
            _owner = owner;
            _isFavorite = isFavorite;
            ToggleFavoriteCommand = new RelayCommand(() => _owner.ToggleFavorite(this));
        }

        public SearchEntry Entry { get; }
        public string Title => Entry.Title;
        public string Meta => Entry.Meta;
        public string Icon => Entry.Icon;
        public string KindText => Entry.KindText;

        /// <summary>Подпись под названием; у экспертных подразделов — с пометкой режима.</summary>
        public string MetaText => Entry.ExpertOnly ? Entry.Meta + " · только в режиме «Эксперт»" : Entry.Meta;

        public bool IsFavorite
        {
            get => _isFavorite;
            set
            {
                if (!Set(ref _isFavorite, value)) return;
                Raise(nameof(StarGlyph));
                Raise(nameof(FavoriteHint));
            }
        }

        /// <summary>Глифы шрифта иконок: E735 — залитая звезда, E734 — контур (★/☆ в этом шрифте нет).</summary>
        public string StarGlyph => _isFavorite ? "\uE735" : "\uE734";
        public string FavoriteHint => _isFavorite ? "Убрать из избранного" : "В избранное";

        public ICommand ToggleFavoriteCommand { get; }
    }

    /// <summary>
    /// Поиск по приложению, `Ctrl+K` (этап 7, docs/IA_REDESIGN.md §10.4).
    ///
    /// Страховка навигации: единый вход к разделам, подразделам, настройкам, командам и сценариям
    /// «Помощи». Индекс требует и новых, и старых слов («способ обхода» и «стратегия», `watchdog`,
    /// `бесшовное`, `матрица`, `SNI`, `DPI`, `Voice RTC`, `Deep Check`, `аудит`, `бэкап`, `SSID`) —
    /// опытный пользователь вводит привычный термин и попадает в новое место.
    ///
    /// Поиск ничего не выполняет сам: каждая строка — переход (`Navigate`) или уже существующая
    /// команда приложения. Внутри поиска живут «Недавние» (5 последних мест) и «Избранное».
    /// </summary>
    public sealed class SearchViewModel : ObservableObject
    {
        private const int RecentLimit = 5;

        private readonly MainViewModel _main;
        private readonly List<SearchEntry> _entries = new();
        private readonly Dictionary<string, Action> _actions = new();
        private readonly Dictionary<string, SearchResultItem> _items = new();

        private bool _isOpen;
        private string _query = "";
        private SearchResultItem? _selectedResult;

        public SearchViewModel(MainViewModel main)
        {
            _main = main;

            Results = new ObservableCollection<SearchResultItem>();
            Recent = new ObservableCollection<SearchResultItem>();
            Favorites = new ObservableCollection<SearchResultItem>();

            OpenCommand = new RelayCommand(Open);
            CloseCommand = new RelayCommand(Close);
            ClearQueryCommand = new RelayCommand(() => Query = "");
            ClearRecentCommand = new RelayCommand(ClearRecent);
            ExecuteSelectedCommand = new RelayCommand(() => Execute(SelectedResult));

            BuildCatalog();
        }

        // ------------------------------------------------------------------ Состояние

        /// <summary>Открыт ли оверлей поиска (видимость задаёт MainWindow).</summary>
        public bool IsOpen
        {
            get => _isOpen;
            private set => Set(ref _isOpen, value);
        }

        public string Query
        {
            get => _query;
            set
            {
                if (!Set(ref _query, value ?? "")) return;
                UpdateResults();
            }
        }

        public SearchResultItem? SelectedResult
        {
            get => _selectedResult;
            set => Set(ref _selectedResult, value);
        }

        public ObservableCollection<SearchResultItem> Results { get; }
        public ObservableCollection<SearchResultItem> Recent { get; }
        public ObservableCollection<SearchResultItem> Favorites { get; }

        public bool HasQuery => !string.IsNullOrWhiteSpace(_query);
        public bool QueryPlaceholderVisible => !HasQuery;
        public bool StartPanelVisible => !HasQuery;
        public bool ResultsVisible => HasQuery && Results.Count > 0;
        public bool NothingFoundVisible => HasQuery && Results.Count == 0;
        public bool HasFavorites => Favorites.Count > 0;
        public bool HasRecent => Recent.Count > 0;

        public string NothingFoundText => $"Ничего не найдено по запросу «{_query.Trim()}». Попробуйте «журнал», «стратегия», «watchdog» или «отчёт».";

        public string FooterHint => "↑ ↓ — выбор · Enter — открыть · Esc — закрыть";
        public string IndexHint => "Поиск идёт по разделам, настройкам, командам и сценариям «Помощи», включая прежние термины.";
        public string QueryHint => "Раздел, настройка или команда: «журнал», «стратегия», «watchdog», «SSID»…";

        // ------------------------------------------------------------------ Команды

        public ICommand OpenCommand { get; }
        public ICommand CloseCommand { get; }
        public ICommand ClearQueryCommand { get; }
        public ICommand ClearRecentCommand { get; }
        public ICommand ExecuteSelectedCommand { get; }

        // ------------------------------------------------------------------ Поведение

        public void Open()
        {
            Query = "";
            RefreshStartLists();
            IsOpen = true;
        }

        public void Close()
        {
            IsOpen = false;
            Query = "";
            SelectedResult = null;
        }

        /// <summary>Перемещение по выдаче стрелками (↑ — -1, ↓ — +1).</summary>
        public void MoveSelection(int delta)
        {
            if (Results.Count == 0) return;

            var index = SelectedResult == null ? -1 : Results.IndexOf(SelectedResult);
            var next = index < 0 ? (delta > 0 ? 0 : Results.Count - 1) : index + delta;
            if (next < 0) next = Results.Count - 1;
            if (next >= Results.Count) next = 0;
            SelectedResult = Results[next];
        }

        public void Execute(SearchResultItem? item)
        {
            if (item == null) return;

            var entry = item.Entry;
            _actions.TryGetValue(entry.Id, out var action);
            // Выполняем после текущего события: список мог быть источником клика, а его состав
            // меняется при закрытии оверлея и записи в «Недавние».
            Defer(() =>
            {
                if (entry.Kind != SearchKind.Command) RecordRecent(entry.Id);
                action?.Invoke();
                Close();
            });
        }

        public void ToggleFavorite(SearchResultItem item)
        {
            var favorite = !item.IsFavorite;
            item.IsFavorite = favorite;

            var ids = _main.Settings.SearchFavoriteIds;
            ids.RemoveAll(id => id == item.Entry.Id);
            if (favorite) ids.Insert(0, item.Entry.Id);
            SettingsStore.Save(_main.Settings);

            Defer(RefreshStartLists);
        }

        /// <summary>Отложить изменение коллекций на конец текущего события (клик по строке/звёздочке).</summary>
        private static void Defer(Action action)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null) action();
            else dispatcher.BeginInvoke(action);
        }

        // ------------------------------------------------------------------ Индекс

        private void UpdateResults()
        {
            Results.Clear();
            if (HasQuery)
            {
                foreach (var entry in SearchCatalog.Search(_entries, _query))
                    Results.Add(ItemFor(entry));
            }

            SelectedResult = Results.FirstOrDefault();
            RaiseResultState();
        }

        private void RaiseResultState()
        {
            Raise(nameof(HasQuery));
            Raise(nameof(QueryPlaceholderVisible));
            Raise(nameof(StartPanelVisible));
            Raise(nameof(ResultsVisible));
            Raise(nameof(NothingFoundVisible));
            Raise(nameof(NothingFoundText));
        }

        private SearchResultItem ItemFor(SearchEntry entry)
        {
            if (_items.TryGetValue(entry.Id, out var cached))
            {
                cached.IsFavorite = IsFavorite(entry.Id);
                return cached;
            }

            var item = new SearchResultItem(entry, this, IsFavorite(entry.Id));
            _items[entry.Id] = item;
            return item;
        }

        private bool IsFavorite(string id) => _main.Settings.SearchFavoriteIds.Contains(id);

        private void RefreshStartLists()
        {
            Favorites.Clear();
            foreach (var entry in _entries.Where(e => IsFavorite(e.Id)))
                Favorites.Add(ItemFor(entry));

            Recent.Clear();
            foreach (var id in _main.Settings.SearchRecentIds)
            {
                var entry = _entries.FirstOrDefault(e => e.Id == id);
                if (entry != null) Recent.Add(ItemFor(entry));
            }

            Raise(nameof(HasFavorites));
            Raise(nameof(HasRecent));
            RaiseResultState();
        }

        private void RecordRecent(string id)
        {
            if (id.Length == 0) return;

            var ids = _main.Settings.SearchRecentIds;
            ids.RemoveAll(item => item == id);
            ids.Insert(0, id);
            while (ids.Count > RecentLimit) ids.RemoveAt(ids.Count - 1);
            SettingsStore.Save(_main.Settings);

            RefreshStartLists();
        }

        private void ClearRecent()
        {
            _main.Settings.SearchRecentIds.Clear();
            SettingsStore.Save(_main.Settings);
            RefreshStartLists();
        }

        // ------------------------------------------------------------------ Наполнение индекса

        private void Add(string id, string title, string path, SearchKind kind, string icon, Action action,
                         string aliases = "", bool expertOnly = false)
        {
            var entry = new SearchEntry
            {
                Id = id,
                Title = title,
                Path = path,
                Kind = kind,
                Icon = icon,
                ExpertOnly = expertOnly,
                Aliases = aliases.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            };

            _entries.Add(entry);
            _actions[id] = action;
        }

        private void BuildCatalog()
        {
            // Иконки разделов — те же, что в меню (MainWindow), чтобы место узнавалось сразу.
            const string iconHome = "\uE80F";
            const string iconBypass = "\uE8D2";
            const string iconChecks = "\uE90F";
            const string iconAuto = "\uE945";
            const string iconSettings = "\uE713";
            const string iconHelp = "\uE897";
            const string iconCommand = "\uE768";

            // --- Разделы верхнего уровня и «Помощь» ---
            Add("nav.home", "Главная", "", SearchKind.Section, iconHome,
                () => _main.Navigate("home"),
                "статус|обзор|включить обход|кнопка питания|состояние");
            Add("nav.bypass", "Обход", "", SearchKind.Section, iconBypass,
                () => _main.Navigate("bypass-center"),
                "bypass|способ обхода|стратегия|dns|списки|сложные сайты");
            Add("nav.checks", "Проверки", "", SearchKind.Section, iconChecks,
                () => _main.Navigate("diagnostics"),
                "проверка|диагностика|тест|проверить|мониторинг|журнал");
            Add("nav.automation", "Автоматизация", "", SearchKind.Section, iconAuto,
                () => _main.Navigate("automation"),
                "автоматика|автозапуск|watchdog|автопилот|расписание");
            Add("nav.settings", "Настройки", "", SearchKind.Section, iconSettings,
                () => _main.Navigate("settings"),
                "настройка|параметры|конфиг|конфигурация|тема");
            Add("nav.help", "Помощь", "Подвал меню", SearchKind.Section, iconHelp,
                () => _main.Navigate("help"),
                "не работает|что делать|подсказка|инструкция|faq|сценарии");

            // --- Обход ---
            const string pathBypass = "Обход";
            Add("bypass.strategy", "Способ обхода (стратегия)", pathBypass, SearchKind.Subsection, iconBypass,
                () => OpenBypassTab(0),
                "стратегия|способ обхода|winws|alt11|general|аргументы|sni пул|кандидаты|история оценок");
            Add("bypass.network", "Под мою сеть", pathBypass, SearchKind.Subsection, iconBypass,
                () => _main.Navigate("network"),
                "провайдер|asn|моя сеть|ростелеком|таймстемпы|timestamps|ipv6|ipv4|doh|перехват|сценарий сети",
                expertOnly: true);
            Add("bypass.workbench", "Рабочий стол настройщика", pathBypass, SearchKind.Subsection, iconBypass,
                () => _main.Navigate("configuration"),
                "конфигурация|всё сразу|что применено|аргументы|sni|ipset|порты|dns|откат|история снимков|рабочий стол|настройщик",
                expertOnly: true);
            Add("bypass.pick", "Подбор способа обхода", pathBypass, SearchKind.Subsection, iconBypass,
                () => OpenBypassTab(1),
                "подбор|подобрать|автоподбор|матрица|перебор с разными dns|92 теста|кандидаты");
            Add("bypass.dns", "DNS", pathBypass, SearchKind.Subsection, iconBypass,
                () => OpenBypassTab(2),
                "днс|doh|dns-over-https|безопасный dns|подмена dns|резолвер");
            Add("bypass.lists", "Списки и hosts", pathBypass, SearchKind.Subsection, iconBypass,
                () => OpenBypassTab(3),
                "hosts|ipset|мои сайты|домены|исключения|блокнот|списки");
            Add("bypass.hard", "Сложные сайты", pathBypass, SearchKind.Subsection, iconBypass,
                () => OpenBypassTab(4),
                "youtube|ютуб|discord|quic|quic fake|sni|подмена sni|ipv4|doh|сложные сайты");
            Add("bypass.advanced", "Дополнительно (режим службы)", pathBypass, SearchKind.Subsection, iconBypass,
                () => OpenBypassTab(5),
                "служба|service|режим службы|консоль winws|установка службы|удалить службу|процесс",
                expertOnly: true);

            // --- Проверки ---
            const string pathChecks = "Проверки";
            Add("diag.quick", "Быстрая проверка", pathChecks, SearchKind.Subsection, iconChecks,
                () => OpenChecksTab(0),
                "быстрая|мониторинг|проверить всё|сайты|youtube|соединение|rtt|пинг");
            Add("diag.dpi", "Сайты и звонки (DPI)", pathChecks, SearchKind.Subsection, iconChecks,
                () => OpenChecksTab(1),
                "dpi|deep packet inspection|voice rtc|discord|голос|звонки|34 узла|подмена tls");
            Add("diag.system", "Система (аудит)", pathChecks, SearchKind.Subsection, iconChecks,
                () => OpenChecksTab(2),
                "аудит|система|сброс сети|winsock|dns кэш|шлюз|proxy|драйвер windivert|службы",
                expertOnly: true);
            Add("diag.deep", "Глубокая проверка", pathChecks, SearchKind.Subsection, iconChecks,
                () => OpenChecksTab(3),
                "deep check|глубокая|глубокий анализ|находки|метрики",
                expertOnly: true);
            Add("diag.results", "История и отчёты", pathChecks, SearchKind.Subsection, iconChecks,
                () => OpenChecksTab(4),
                "отчёты|бэкап|бэкапы|история|экспорт|zip|архив отчёта|результаты",
                expertOnly: true);
            Add("diag.logs", "Журнал", pathChecks, SearchKind.Subsection, iconChecks,
                () => OpenChecksTab(5),
                "журнал|логи|лог|log|отладка|debug|ошибки|только важное");

            // --- Автоматизация ---
            const string pathAuto = "Автоматизация";
            Add("auto.launch", "Запуск и автозапуск", pathAuto, SearchKind.Subsection, iconAuto,
                () => OpenAutomationTab(0),
                "автозапуск|при входе в windows|старт приложения|трей|свёрнутый запуск");
            Add("auto.recovery", "Восстановление (присмотр)", pathAuto, SearchKind.Subsection, iconAuto,
                () => OpenAutomationTab(1),
                "watchdog|присмотр|бесшовное переключение|seamless|failover|автоперезапуск|переключаться при ухудшении|кулдаун");
            Add("auto.schedule", "Расписание", pathAuto, SearchKind.Subsection, iconAuto,
                () => OpenAutomationTab(2),
                "расписание|по времени|schedule|интервал|дни недели");
            Add("auto.nets", "Профили по сетям", pathAuto, SearchKind.Subsection, iconAuto,
                () => OpenAutomationTab(3),
                "ssid|сети|профиль по сети|wi-fi|wifi|таблица сетей",
                expertOnly: true);

            // --- Настройки ---
            const string pathSettings = "Настройки";
            Add("set.general", "Общие: движок, папки, автозапуск", pathSettings, SearchKind.Setting, iconSettings,
                () => OpenSettingsTab(0),
                "движок|папка движка|автозапуск|движок и папки|первый старт|режим эксперт");
            Add("set.bypass", "Обход: поведение и сторож", pathSettings, SearchKind.Setting, iconSettings,
                () => OpenSettingsTab(1),
                "безопасный режим|safe mode|автозапуск обхода|сторож|автоподбор|остановка при выходе");
            Add("set.network", "Сеть: провайдер и телеметрия", pathSettings, SearchKind.Setting, iconSettings,
                () => OpenSettingsTab(2),
                "провайдер|asn|телеметрия|фоновый мониторинг|провайдер по ip");
            Add("set.appearance", "Интерфейс: тема, трей, клавиши", pathSettings, SearchKind.Setting, iconSettings,
                () => OpenSettingsTab(3),
                "тема|масштаб|zoom|трей|горячие клавиши|hud|мини-виджет|метрики панели задач|эксперт");
            Add("set.gaming", "Игры: детектор и твики", pathSettings, SearchKind.Setting, iconSettings,
                () => OpenSettingsTab(4),
                "игровой режим|детектор игр|твики|game mode|udp|задержки");
            Add("set.updates", "Обновления движка", pathSettings, SearchKind.Setting, iconSettings,
                () => OpenSettingsTab(5),
                "обновление движка|winws обновление|pre-release|бета|проверить обновления");
            Add("set.profiles", "Профили и копии", pathSettings, SearchKind.Setting, iconSettings,
                () => _main.Navigate("profiles"),
                "профили|бэкап|архив|резервная копия|восстановить|экспорт|импорт|портативный режим");
            Add("set.about", "О программе", pathSettings, SearchKind.Setting, iconSettings,
                () => _main.Navigate("about"),
                "версия|о приложении|лицензия|обновление приложения|github");

            // --- Сценарии «Помощи» (тексты живут на странице «Помощь») ---
            Add("help.youtube", "Не открывается YouTube", "Помощь", SearchKind.Scenario, iconHelp,
                () => _main.Navigate("help"),
                "ютуб не работает|youtube|видео не грузится|медленно youtube");
            Add("help.discord", "Пропал голос в Discord", "Помощь", SearchKind.Scenario, iconHelp,
                () => _main.Navigate("help"),
                "discord|голос пропал|voice|rtc|микрофон|звонок");
            Add("help.internet", "Пропал интернет после установки", "Помощь", SearchKind.Scenario, iconHelp,
                () => _main.Navigate("help"),
                "нет интернета|интернет пропал|сломался интернет|сброс сети|откат");
            Add("help.games", "Лагают игры", "Помощь", SearchKind.Scenario, iconHelp,
                () => _main.Navigate("help"),
                "лаги|игры|пинг|фризы|лаг|fps");
            Add("help.revert", "Хочу вернуть как было", "Помощь", SearchKind.Scenario, iconHelp,
                () => _main.Navigate("help"),
                "вернуть как было|восстановить|удалить|откат|всё сбросить");

            // --- Команды: выполняются сразу, без перехода ---
            Add("cmd.bypass.toggle", "Включить или выключить обход", "", SearchKind.Command, iconCommand,
                () => Run(_main.Home.ToggleBypassCommand),
                "включить обход|выключить обход|старт|стоп|bypass|питание|запустить|остановить");
            Add("cmd.gamemode.toggle", "Игровой режим", "", SearchKind.Command, iconCommand,
                () => Run(_main.Home.ToggleGameModeCommand),
                "game mode|игровой|пропуск udp|лаг|игры");
            Add("cmd.expert.toggle", "Режим «Эксперт» или «Простой»", "", SearchKind.Command, iconCommand,
                () => Run(_main.ToggleExpertModeCommand),
                "эксперт|простой режим|технические настройки|advanced|скрытые функции");
            Add("cmd.report.zip", "Собрать отчёт (ZIP)", "", SearchKind.Command, iconCommand,
                () => Run(_main.Diagnostics.ExportArchiveCommand),
                "отчёт|report|архив|диагностика|поддержка|отправить отчёт");
            Add("cmd.updates.check", "Проверить обновления движка", "", SearchKind.Command, iconCommand,
                () => Run(_main.Home.CheckUpdatesCommand),
                "обновление|проверить обновления|движок|update");
            Add("cmd.theme.toggle", "Переключить тему", "", SearchKind.Command, iconCommand,
                () => Run(_main.ToggleThemeCommand),
                "тема|тёмная|светлая|оформление|ночной режим");
            Add("cmd.engine.folder", "Открыть папку движка", "", SearchKind.Command, iconCommand,
                () => Run(_main.OpenEngineFolderCommand),
                "папка движка|открыть папку|файлы|explorer");
            Add("cmd.admin.restart", "Перезапустить от администратора", "", SearchKind.Command, iconCommand,
                () => Run(_main.RestartAsAdminCommand),
                "администратор|права|uac|перезапуск");
            Add("cmd.hud.toggle", "Компактный мини-виджет (HUD)", "", SearchKind.Command, iconCommand,
                () => Run(_main.Home.OpenOverlayCommand),
                "hud|мини-виджет|оверлей|компактный режим");
        }

        private static void Run(ICommand command)
        {
            if (command.CanExecute(null)) command.Execute(null);
        }

        private void OpenBypassTab(int tab)
        {
            _main.BypassCenter.SelectedSubTab = tab;
            _main.Navigate("bypass-center");
        }

        private void OpenChecksTab(int tab)
        {
            _main.Diagnostics.SelectedSubTab = tab;
            _main.Navigate("diagnostics");
        }

        private void OpenAutomationTab(int tab)
        {
            _main.SettingsPage.VisibleAutomationTab = tab;
            _main.Navigate("automation");
        }

        private void OpenSettingsTab(int tab)
        {
            _main.SettingsPage.SelectedTabIndex = tab;
            _main.Navigate("settings");
        }
    }
}
