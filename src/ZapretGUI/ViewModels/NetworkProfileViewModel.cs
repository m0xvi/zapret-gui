using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Windows.Input;
using ZapretGui.Core;

namespace ZapretGui.ViewModels
{
    /// <summary>Строка шага «под мою сеть»: что проверяем и что вышло.</summary>
    public sealed class NetworkStep : ObservableObject
    {
        public int Number { get; init; }
        public string Title { get; init; } = "";
        public string WhatText { get; init; } = "";
        public string Section { get; init; } = "";
        public string NumberText => Number.ToString();

        private string _valueText = "не проверено";
        public string ValueText { get => _valueText; set => Set(ref _valueText, value); }

        private string _key = "Info";
        public string Key { get => _key; set => Set(ref _key, value); }
    }

    /// <summary>
    /// «Под мою сеть» (v1.30.0): один сценарий из шести шагов — провайдер и ASN, перехват (ipset),
    /// порты, TCP-таймстемпы, DNS/DoH, IPv4/IPv6. Каждый шаг проверяется отдельно и с результатом;
    /// «Проверить всё по шагам» прогоняет шесть проверок подряд.
    /// Правка значений живёт в одном месте — на рабочем столе настройщика (кнопка «Поправить»);
    /// здесь правится только то, чего там нет: провайдер/ASN и TCP-таймстемпы.
    /// </summary>
    public sealed class NetworkProfileViewModel : ObservableObject
    {
        private readonly MainViewModel _main;

        public NetworkProfileViewModel(MainViewModel main)
        {
            _main = main;

            DetectProviderCommand = new AsyncRelayCommand(DetectProviderAsync, () => !IsBusy);
            SaveProviderCommand = new RelayCommand(SaveProvider, () => !IsBusy);
            FixTimestampsCommand = new AsyncRelayCommand(FixTimestampsAsync, () => !IsBusy);
            CheckAllCommand = new AsyncRelayCommand(CheckAllAsync, () => !IsBusy);
            OpenStepCommand = new RelayCommand(parameter => OpenStep(parameter as string ?? ""));
            OpenWorkbenchCommand = new RelayCommand(() => _main.Navigate("configuration"));
            EnableExpertModeCommand = new RelayCommand(() => { if (_main.SimpleMode) _main.ToggleExpertMode(); });

            ProviderName = main.Settings.ProviderContext.Name;
            ProviderAsn = main.Settings.ProviderContext.Asn;

            Steps.Add(new NetworkStep { Number = 1, Title = "Провайдер и ASN", WhatText = "кто ваш провайдер — пригодится при подборе способа обхода", Section = "settings" });
            Steps.Add(new NetworkStep { Number = 2, Title = "Перехват (ipset)", WhatText = "что именно заворачивает драйвер: список сетей, только домены или все адреса", Section = "user-lists" });
            Steps.Add(new NetworkStep { Number = 3, Title = "Порты и игровой фильтр", WhatText = "какие порты обрабатываются, а какие исключены (пинг в играх)", Section = "bypass" });
            Steps.Add(new NetworkStep { Number = 4, Title = "TCP-таймстемпы", WhatText = "метки времени TCP: Windows иногда оставляет их выключенными", Section = "" });
            Steps.Add(new NetworkStep { Number = 5, Title = "DNS и DoH", WhatText = "ответы приходят от провайдера или шифрованными от Cloudflare", Section = "bypass" });
            Steps.Add(new NetworkStep { Number = 6, Title = "IPv4 и IPv6", WhatText = "какой стек используется для обхода", Section = "bypass" });

            ReloadLight();
        }

        // ------------------------------------------------------------------ Шаги

        public ObservableCollection<NetworkStep> Steps { get; } = new();

        // ------------------------------------------------------------------ Состояние

        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                if (!Set(ref _isBusy, value)) return;
                Raise(nameof(IsIdle));
                (DetectProviderCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (SaveProviderCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (FixTimestampsCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (CheckAllCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            }
        }

        public bool IsIdle => !IsBusy;

        private string _statusText = "";
        public string StatusText
        {
            get => _statusText;
            private set { if (Set(ref _statusText, value)) Raise(nameof(HasStatus)); }
        }

        public bool HasStatus => !string.IsNullOrWhiteSpace(StatusText);

        private string _statusKey = "Info";
        public string StatusKey { get => _statusKey; private set => Set(ref _statusKey, value); }

        private string _providerName = "";
        public string ProviderName { get => _providerName; set => Set(ref _providerName, value); }

        private string _providerAsn = "";
        public string ProviderAsn { get => _providerAsn; set => Set(ref _providerAsn, value); }

        private string _providerSourceText = "";
        public string ProviderSourceText { get => _providerSourceText; private set => Set(ref _providerSourceText, value); }

        private string _detectedText = "";
        public string DetectedText
        {
            get => _detectedText;
            private set { if (Set(ref _detectedText, value)) Raise(nameof(HasDetected)); }
        }

        public bool HasDetected => !string.IsNullOrWhiteSpace(DetectedText);

        public bool IsSimpleMode => _main.SimpleMode;
        public bool IsExpertMode => _main.ExpertMode;

        private string _networkName = "";
        public string NetworkName { get => _networkName; private set => Set(ref _networkName, value); }

        // ------------------------------------------------------------------ Команды

        public ICommand DetectProviderCommand { get; }
        public ICommand SaveProviderCommand { get; }
        public ICommand FixTimestampsCommand { get; }
        public ICommand CheckAllCommand { get; }
        public ICommand OpenStepCommand { get; }
        public ICommand OpenWorkbenchCommand { get; }
        public ICommand EnableExpertModeCommand { get; }

        // ------------------------------------------------------------------ Работа

        /// <summary>Быстрая часть: без запуска netsh — чтобы не тормозить старт приложения.</summary>
        private void ReloadLight()
        {
            ProviderName = _main.Settings.ProviderContext.Name;
            ProviderAsn = _main.Settings.ProviderContext.Asn;
            ProviderSourceText = _main.Settings.ProviderContext.DisplayText;
            RefreshNetworkName();
        }

        public void Reload()
        {
            var settings = _main.Settings;
            ProviderName = settings.ProviderContext.Name;
            ProviderAsn = settings.ProviderContext.Asn;
            ProviderSourceText = settings.ProviderContext.DisplayText;

            RefreshNetworkName();
            RefreshSteps(deep: false);
        }

        private void RefreshNetworkName()
        {
            try
            {
                var identity = NetworkDetector.GetCurrentIdentity();
                NetworkName = identity.IsValid ? identity.DisplayName : "сеть не определена";
            }
            catch { NetworkName = "сеть не определена"; }
        }

        /// <summary>Пересчитывает шесть строк. deep = true — с реальными проверками (DNS-подмена, timestamps).</summary>
        private void RefreshSteps(bool deep)
        {
            foreach (var step in Steps)
            {
                switch (step.Number)
                {
                    case 1:
                        var ctx = _main.Settings.ProviderContext;
                        step.ValueText = ctx.IsKnown ? ctx.DisplayText : "не указан — определите автоматически";
                        step.Key = ctx.IsKnown ? "Success" : "Info";
                        break;
                    case 2:
                        var ipset = EngineService.GetIpsetMode(_main.Settings.EnginePath);
                        var networks = CountIpsetNetworks();
                        step.ValueText = ipset switch
                        {
                            IpsetMode.None => "ipset none — перехват только по доменам из списков",
                            IpsetMode.Any => "ipset any — перехватываются все адреса (самый широкий режим)",
                            _ => $"ipset loaded — перехват по списку сетей ({networks} сетей)"
                        };
                        step.Key = ipset == IpsetMode.Any ? "Warning" : ipset == IpsetMode.None ? "Info" : "Success";
                        break;
                    case 3:
                        var profile = GameFilterPortConfig.PredefinedProfiles
                            .FirstOrDefault(p => p.Id == _main.Settings.GameFilterProfileId);
                        var mode = EngineService.GetGameFilterMode(_main.Settings.EnginePath);
                        step.ValueText = $"фильтр: {(profile?.Name ?? "не выбран")} · режим движка: {mode} · TCP {_main.Settings.CustomGameFilterTcpPorts}, UDP {_main.Settings.CustomGameFilterUdpPorts}";
                        step.Key = mode == GameFilterMode.Disabled ? "Info" : "Success";
                        break;
                    case 4:
                        var tcp = WinServices.GetTcpTimestampsState();
                        step.ValueText = !tcp.Known
                            ? "состояние не удалось прочитать: " + tcp.Details
                            : tcp.Enabled ? "включены — как нужно" : "выключены — включите кнопкой «Включить»";
                        step.Key = tcp.Enabled ? "Success" : tcp.Known ? "Warning" : "Info";
                        break;
                    case 5:
                        var hijack = _main.Settings.LastDnsHijackSummary;
                        var dnsNow = DnsManagementService.GetCurrentDnsSummary();
                        step.ValueText = string.IsNullOrWhiteSpace(hijack)
                            ? dnsNow + " · подмена ещё не проверялась"
                            : dnsNow + " · " + hijack;
                        step.Key = string.IsNullOrWhiteSpace(hijack) ? "Info"
                            : _main.Settings.LastDnsHijackCheckedAt.HasValue ? "Success" : "Info";
                        break;
                    case 6:
                        var hasIpv6 = HasRoutableIpv6(out var ipv6Address);
                        step.ValueText = _main.Settings.PreferIPv4ForBypass
                            ? "приоритет IPv4 включён · IPv6 в сети " + (hasIpv6 ? "есть (" + ipv6Address + ")" : "не обнаружен")
                            : "IPv4 и IPv6 равноправны · IPv6 в сети " + (hasIpv6 ? "есть (" + ipv6Address + ")" : "не обнаружен");
                        step.Key = "Info";
                        break;
                }
            }

            _ = deep;   // глубина проверки сейчас влияет только на текст выше; задел под будущие живые тесты
        }

        private int CountIpsetNetworks()
        {
            try
            {
                var all = System.IO.Path.Combine(_main.Settings.EnginePath, "lists", "ipset-all.txt");
                if (!System.IO.File.Exists(all)) return 0;
                return System.IO.File.ReadAllLines(all).Count(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith("#"));
            }
            catch { return 0; }
        }

        private static bool HasRoutableIpv6(out string address)
        {
            address = "";
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var addr in nic.GetIPProperties().UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily != AddressFamily.InterNetworkV6) continue;
                        if (addr.Address.IsIPv6LinkLocal || addr.Address.IsIPv6SiteLocal) continue;
                        address = addr.Address.ToString();
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private async Task DetectProviderAsync()
        {
            if (IsBusy) return;
            IsBusy = true;
            StatusText = "Определяю провайдера по внешнему IP…";
            StatusKey = "Info";
            try
            {
                var info = await ProviderDetectionService.DetectAsync();
                if (!info.Ok)
                {
                    DetectedText = "";
                    StatusText = "Не удалось определить автоматически — источники недоступны. Введите имя провайдера и ASN вручную.";
                    StatusKey = "Warning";
                }
                else
                {
                    ProviderName = info.Name;
                    ProviderAsn = info.Asn;
                    DetectedText = info.DisplayText + $"\nИсточник: {info.Source}";
                    StatusText = "Определено: " + info.DisplayText + ". Нажмите «Сохранить провайдера».";
                    StatusKey = "Success";
                }
                Raise(nameof(HasDetected));
            }
            catch (Exception ex)
            {
                StatusText = "Ошибка определения: " + ex.Message;
                StatusKey = "Danger";
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void SaveProvider()
        {
            var ctx = _main.Settings.ProviderContext;
            ctx.Name = ProviderName.Trim();
            ctx.Asn = ProviderAsn.Trim();
            ctx.Source = ProviderContextSource.UserInput;
            ctx.CheckedAt = DateTime.Now;
            ctx.Confidence = 100;
            SettingsStore.Save(_main.Settings);
            ProviderSourceText = ctx.DisplayText;
            StatusText = ctx.IsKnown ? "Провайдер сохранён: " + ctx.DisplayText : "Провайдер очищен.";
            StatusKey = "Success";
            RefreshSteps(deep: false);
        }

        private async Task FixTimestampsAsync()
        {
            if (IsBusy) return;
            IsBusy = true;
            StatusText = "Включаю TCP-таймстемпы…";
            StatusKey = "Info";
            try
            {
                var (ok, message) = await Task.Run(() => WinServices.EnsureTcpTimestamps());
                StatusText = message;
                StatusKey = ok ? "Success" : "Warning";
                RefreshSteps(deep: true);
            }
            catch (Exception ex)
            {
                StatusText = "Не удалось включить TCP-таймстемпы: " + ex.Message;
                StatusKey = "Danger";
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>Прогон всех шести проверок подряд (без изменений в системе).</summary>
        private async Task CheckAllAsync()
        {
            if (IsBusy) return;
            IsBusy = true;
            StatusText = "Проверяю сеть по шагам…";
            StatusKey = "Info";
            try
            {
                RefreshSteps(deep: false);

                var problems = new List<string>();
                foreach (var step in Steps)
                {
                    if (step.Key == "Warning" || step.Key == "Danger") problems.Add($"{step.Number}. {step.Title}: {step.ValueText}");
                }

                StatusText = problems.Count == 0
                    ? "Проверено: все шесть шагов в порядке — сеть настроена под обход."
                    : "Найдено, что поправить: " + string.Join(" · ", problems);
                StatusKey = problems.Count == 0 ? "Success" : "Warning";
                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                StatusText = "Ошибка проверки: " + ex.Message;
                StatusKey = "Danger";
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void OpenStep(string section)
        {
            switch (section)
            {
                case "user-lists":
                    _main.Navigate("user-lists");
                    break;
                case "bypass":
                    _main.Navigate("bypass-center");
                    break;
                case "settings":
                    _main.Navigate("settings");
                    break;
                default:
                    _main.Navigate("configuration");
                    break;
            }
        }
    }
}
