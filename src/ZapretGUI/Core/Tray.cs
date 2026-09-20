using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace ZapretGui.Core
{
    /// <summary>
    /// Расширенное управление иконкой в системном трее:
    /// Быстрые действия, выбор стратегий, переключение DNS, игровой режим и мини-виджет.
    /// </summary>
    public sealed class TrayIcon : IDisposable
    {
        private readonly NotifyIcon _icon;
        private readonly ContextMenuStrip _menu;
        private readonly ToolStripMenuItem _headerItem;
        private readonly ToolStripMenuItem _toggleBypassItem;
        private readonly ToolStripMenuItem _gameModeItem;
        private readonly ToolStripMenuItem _strategiesSubMenu;
        private readonly ToolStripMenuItem _dnsSubMenu;
        private readonly ToolStripMenuItem _miniOverlayItem;
        private readonly ToolStripMenuItem _openItem;
        private readonly ToolStripMenuItem _logsItem;

        public event Action? OpenRequested;
        public event Action? ToggleBypassRequested;
        public event Action? ToggleGameModeRequested;
        public event Action? ToggleMiniOverlayRequested;
        public event Action<string>? SelectStrategyRequested;
        public event Action<DnsProfile>? SelectDnsRequested;
        public event Action? OpenLogsRequested;
        public event Action? ExitRequested;

        public TrayIcon()
        {
            _menu = new ContextMenuStrip();

            _headerItem = new ToolStripMenuItem("Zapret GUI — Остановлен")
            {
                Enabled = false,
                Font = new Font(Control.DefaultFont, FontStyle.Bold)
            };

            _openItem = new ToolStripMenuItem("Открыть Zapret GUI", null, (_, __) => OpenRequested?.Invoke());
            _toggleBypassItem = new ToolStripMenuItem("Запустить обход", null, (_, __) => ToggleBypassRequested?.Invoke());
            _gameModeItem = new ToolStripMenuItem("🎮 Игровой режим: Выкл", null, (_, __) => ToggleGameModeRequested?.Invoke());
            _strategiesSubMenu = new ToolStripMenuItem("Выбор стратегии обхода");
            _dnsSubMenu = new ToolStripMenuItem("Безопасный DNS (DoH)");
            _miniOverlayItem = new ToolStripMenuItem("Компактный мини-виджет (HUD)", null, (_, __) => ToggleMiniOverlayRequested?.Invoke());
            _logsItem = new ToolStripMenuItem("Журнал логов", null, (_, __) => OpenLogsRequested?.Invoke());
            var exitItem = new ToolStripMenuItem("Выход", null, (_, __) => ExitRequested?.Invoke());

            _menu.Items.Add(_headerItem);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(_openItem);
            _menu.Items.Add(_toggleBypassItem);
            _menu.Items.Add(_gameModeItem);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(_strategiesSubMenu);
            _menu.Items.Add(_dnsSubMenu);
            _menu.Items.Add(_miniOverlayItem);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(_logsItem);
            _menu.Items.Add(exitItem);

            PopulateDnsMenu();

            _icon = new NotifyIcon
            {
                Icon = LoadIcon(),
                Text = "Zapret GUI",
                Visible = true,
                ContextMenuStrip = _menu
            };
            _icon.DoubleClick += (_, __) => OpenRequested?.Invoke();
        }

        private static Icon LoadIcon()
        {
            try
            {
                var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(exe))
                {
                    var icon = Icon.ExtractAssociatedIcon(exe);
                    if (icon != null) return icon;
                }
            }
            catch { }
            return SystemIcons.Shield;
        }

        public void UpdateState(bool running, string strategyName, string stateText, string? pingText = null, string? activeGame = null, bool gameMode = false)
        {
            try
            {
                _toggleBypassItem.Text = running ? "Остановить обход" : "Запустить обход";
                _gameModeItem.Text = gameMode ? "🎮 Игровой режим: Включён" : "🎮 Игровой режим: Выключен";
                _gameModeItem.Checked = gameMode;

                var header = running ? "Zapret: Активен" : "Zapret: Остановлен";
                if (!string.IsNullOrEmpty(strategyName) && running) header += $" ({strategyName})";
                if (!string.IsNullOrEmpty(pingText)) header += $" · {pingText}";
                if (!string.IsNullOrEmpty(activeGame)) header += $" · 🎮 {activeGame}";

                _headerItem.Text = header;

                var tooltip = "Zapret GUI — " + stateText;
                if (!string.IsNullOrEmpty(strategyName) && running) tooltip += " (" + strategyName + ")";
                if (!string.IsNullOrEmpty(pingText)) tooltip += $" [{pingText}]";
                if (tooltip.Length > 63) tooltip = tooltip.Substring(0, 63);
                _icon.Text = tooltip;
            }
            catch { }
        }

        public void PopulateStrategiesMenu(IEnumerable<StrategyInfo> strategies, string selectedStrategy)
        {
            try
            {
                _strategiesSubMenu.DropDownItems.Clear();
                foreach (var strat in strategies)
                {
                    var item = new ToolStripMenuItem(strat.Name, null, (_, __) => SelectStrategyRequested?.Invoke(strat.Name))
                    {
                        Checked = string.Equals(strat.Name, selectedStrategy, StringComparison.OrdinalIgnoreCase)
                    };
                    _strategiesSubMenu.DropDownItems.Add(item);
                }
            }
            catch { }
        }

        private void PopulateDnsMenu()
        {
            try
            {
                _dnsSubMenu.DropDownItems.Clear();
                foreach (var profile in DnsManagementService.PredefinedProfiles)
                {
                    var item = new ToolStripMenuItem(profile.DisplayTitle, null, (_, __) => SelectDnsRequested?.Invoke(profile));
                    _dnsSubMenu.DropDownItems.Add(item);
                }
            }
            catch { }
        }

        public void ShowBalloon(string title, string text)
        {
            try
            {
                _icon.BalloonTipTitle = title;
                _icon.BalloonTipText = text;
                _icon.BalloonTipIcon = ToolTipIcon.Info;
                _icon.ShowBalloonTip(4000);
            }
            catch { }
        }

        public void Dispose()
        {
            try
            {
                _icon.Visible = false;
                _icon.Dispose();
                _menu.Dispose();
            }
            catch { }
        }
    }
}
