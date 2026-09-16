using System;
using System.Drawing;
using System.Windows;
using System.Windows.Forms;

namespace ZapretGui.Core
{
    /// <summary>Иконка в системном трее (свернуть окно, быстро запустить/остановить обход).</summary>
    public sealed class TrayIcon : IDisposable
    {
        private readonly NotifyIcon _icon;
        private readonly ToolStripMenuItem _toggleItem;
        private readonly ToolStripMenuItem _openItem;

        public event Action? OpenRequested;
        public event Action? ToggleBypassRequested;
        public event Action? ExitRequested;

        public TrayIcon()
        {
            var menu = new ContextMenuStrip();
            _openItem = new ToolStripMenuItem("Открыть Zapret GUI", null, (_, __) => OpenRequested?.Invoke());
            _toggleItem = new ToolStripMenuItem("Запустить обход", null, (_, __) => ToggleBypassRequested?.Invoke());
            var exitItem = new ToolStripMenuItem("Выход", null, (_, __) => ExitRequested?.Invoke());

            menu.Items.Add(_openItem);
            menu.Items.Add(_toggleItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(exitItem);

            _icon = new NotifyIcon
            {
                Icon = LoadIcon(),
                Text = "Zapret GUI",
                Visible = true,
                ContextMenuStrip = menu
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

        public void UpdateState(bool running, string strategyName, string stateText)
        {
            try
            {
                _toggleItem.Text = running ? "Остановить обход" : "Запустить обход";
                var text = "Zapret GUI — " + stateText;
                if (!string.IsNullOrEmpty(strategyName) && running) text += " (" + strategyName + ")";
                if (text.Length > 120) text = text.Substring(0, 120);
                _icon.Text = text;
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
            }
            catch { }
        }
    }
}
