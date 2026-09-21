using System;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;

namespace ZapretGui.Core
{
    /// <summary>
    /// Служба автопереключения профилей при смене сети и при диагностированном сбое стратегии.
    /// Работает без блокировки UI, с cooldown и проверкой SafeMode.
    /// </summary>
    public sealed class ProfileAutoSwitchService : IDisposable
    {
        private readonly AppSettings _settings;
        private readonly Func<BypassController> _bypassFactory;
        private readonly Func<StrategyStore> _strategiesFactory;
        private readonly System.Timers.Timer _pollTimer;
        private NetworkIdentity _lastIdentity;
        private DateTime _lastSwitchTime = DateTime.MinValue;
        private bool _isSwitching;
        private const int CooldownSeconds = 60;
        private const int PollSeconds = 30;

        public event Action<string>? StatusChanged;
        public event Action<UserProfile, NetworkIdentity>? ProfileSwitched;

        public NetworkIdentity CurrentIdentity { get; private set; }
        public string LastReason { get; private set; } = "";

        public ProfileAutoSwitchService(AppSettings settings, Func<BypassController> bypassFactory, Func<StrategyStore> strategiesFactory)
        {
            _settings = settings;
            _bypassFactory = bypassFactory;
            _strategiesFactory = strategiesFactory;
            CurrentIdentity = NetworkDetector.GetCurrentIdentity();
            _lastIdentity = CurrentIdentity;
            _pollTimer = new System.Timers.Timer(TimeSpan.FromSeconds(PollSeconds).TotalMilliseconds);
            _pollTimer.Elapsed += async (_, _) => await CheckAndSwitchAsync(trigger: "таймер");
            _pollTimer.AutoReset = true;

            try { NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged; } catch { }
        }

        public void Start()
        {
            if (_settings.SafeMode) return;
            _pollTimer.Start();
            CurrentIdentity = NetworkDetector.GetCurrentIdentity();
            _lastIdentity = CurrentIdentity;
            AppLog.Info($"[ProfileAutoSwitch] Служба запущена, текущая сеть: {CurrentIdentity.DisplayName}");
        }

        public void Stop()
        {
            _pollTimer.Stop();
        }

        public NetworkIdentity RefreshCurrentIdentity()
        {
            CurrentIdentity = NetworkDetector.GetCurrentIdentity();
            return CurrentIdentity;
        }

        private void OnNetworkAddressChanged(object? sender, EventArgs e)
        {
            // Дебаунс 3с, т.к. событие может пожаровать несколько раз
            Task.Delay(3000).ContinueWith(async _ => await CheckAndSwitchAsync(trigger: "смена сети"));
        }

        public async Task CheckAndSwitchAsync(string trigger = "проверка")
        {
            if (_isSwitching) return;
            if (_settings.SafeMode) return;
            if (!_settings.AutoSwitchProfileOnNetworkChange && !_settings.AutoSwitchProfileOnFailure) return;

            var identity = NetworkDetector.GetCurrentIdentity();
            CurrentIdentity = identity;
            if (!identity.IsValid)
            {
                LastReason = "Сеть не определена";
                return;
            }

            var fingerprintChanged = !string.Equals(identity.Fingerprint, _lastIdentity.Fingerprint, StringComparison.OrdinalIgnoreCase);
            var timeSinceLastSwitch = (DateTime.UtcNow - _lastSwitchTime).TotalSeconds;

            // Если автопереключение по сети выключено — только обновляем LastIdentity и выходим
            if (!_settings.AutoSwitchProfileOnNetworkChange)
            {
                _lastIdentity = identity;
                return;
            }

            if (!fingerprintChanged)
            {
                // Сеть не менялась — не переключаем
                return;
            }

            if (timeSinceLastSwitch < CooldownSeconds)
            {
                AppLog.Debug($"[ProfileAutoSwitch] Cooldown {CooldownSeconds}с не истёк ({timeSinceLastSwitch:0}с), пропуск");
                return;
            }

            // Ищем профиль, привязанный к этой сети
            var profiles = ProfileManager.LoadProfiles();
            var target = ProfileManager.FindProfileForNetwork(profiles, identity.Fingerprint);
            if (target == null)
            {
                // Пробуем найти по SSID подстроке (для случая смены шлюза при том же Wi-Fi)
                if (!string.IsNullOrWhiteSpace(identity.Ssid))
                {
                    target = profiles.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.NetworkFingerprint)
                        && p.NetworkFingerprint.Contains(identity.Ssid, StringComparison.OrdinalIgnoreCase));
                }
                if (target == null)
                {
                    _lastIdentity = identity;
                    LastReason = $"Для сети «{identity.DisplayName}» нет привязанного профиля";
                    StatusChanged?.Invoke(LastReason);
                    AppLog.Info($"[ProfileAutoSwitch] {LastReason} (trigger={trigger})");
                    return;
                }
            }

            // Защита от зацикливания: если уже применяли этот профиль для этой сети — не повторяем
            if (string.Equals(_settings.LastNetworkFingerprint, identity.Fingerprint, StringComparison.OrdinalIgnoreCase)
                && string.Equals(_settings.LastAutoSwitchedProfileId, target.Id, StringComparison.OrdinalIgnoreCase))
            {
                _lastIdentity = identity;
                LastReason = $"Профиль «{target.Name}» уже применён для этой сети";
                return;
            }

            _isSwitching = true;
            try
            {
                AppLog.Info($"[ProfileAutoSwitch] Обнаружена смена сети ({trigger}): {_lastIdentity.DisplayName} → {identity.DisplayName}. Переключаю на профиль «{target.Name}»");
                LastReason = $"Переключаю на «{target.Name}» для сети «{identity.DisplayName}»";
                StatusChanged?.Invoke(LastReason);

                var bypass = _bypassFactory();
                var store = _strategiesFactory();
                var (ok, msg) = await ProfileManager.ApplyProfileAsync(target, _settings, bypass, store).ConfigureAwait(false);

                _lastSwitchTime = DateTime.UtcNow;
                _lastIdentity = identity;
                _settings.LastNetworkFingerprint = identity.Fingerprint;
                _settings.LastAutoSwitchedProfileId = target.Id;
                _settings.LastAutoSwitchTime = DateTime.UtcNow;
                SettingsStore.Save(_settings);

                if (ok)
                {
                    LastReason = $"Автопрофиль «{target.Name}» применён ({identity.DisplayName})";
                    AppLog.Info($"[ProfileAutoSwitch] {LastReason}");
                    ProfileSwitched?.Invoke(target, identity);
                }
                else
                {
                    LastReason = $"Не удалось применить «{target.Name}»: {msg}";
                    AppLog.Warn($"[ProfileAutoSwitch] {LastReason}");
                }
                StatusChanged?.Invoke(LastReason);
            }
            catch (Exception ex)
            {
                AppLog.Error($"[ProfileAutoSwitch] Ошибка автопереключения: {ex.Message}");
                LastReason = "Ошибка автопереключения: " + ex.Message;
                StatusChanged?.Invoke(LastReason);
            }
            finally
            {
                _isSwitching = false;
            }
        }

        /// <summary>Попытка автопереключения при диагностированном сбое стратегии (из MonitoringViewModel).</summary>
        public async Task<bool> TrySwitchOnFailureAsync(MonitorTarget failedTarget, BypassStatus before)
        {
            if (!_settings.AutoSwitchProfileOnFailure || !_settings.AutoSwitchProfileOnNetworkChange) return false;
            if (_settings.SafeMode) return false;
            if ((DateTime.UtcNow - _lastSwitchTime).TotalSeconds < CooldownSeconds) return false;

            var identity = RefreshCurrentIdentity();
            var profiles = ProfileManager.LoadProfiles();
            // Ищем профили, привязанные к текущей сети, кроме текущего
            var candidates = profiles
                .Where(p => p.IsNetworkBound && p.NetworkFingerprint.Equals(identity.Fingerprint, StringComparison.OrdinalIgnoreCase))
                .Where(p => !p.StrategyName.Equals(before.StrategyName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (candidates.Count == 0)
            {
                // Fallback: любые профили с другой стратегией
                candidates = profiles.Where(p => !string.IsNullOrWhiteSpace(p.StrategyName)
                    && !p.StrategyName.Equals(before.StrategyName, StringComparison.OrdinalIgnoreCase)).Take(3).ToList();
            }
            if (candidates.Count == 0) return false;

            // Для теста: пробуем первый подходящий профиль, который делает ресурс доступным
            var bypass = _bypassFactory();
            foreach (var cand in candidates)
            {
                var strategy = _strategiesFactory().Find(cand.StrategyName);
                if (strategy == null) continue;
                var probe = await bypass.TestStrategyOnResourceAsync(strategy, failedTarget).ConfigureAwait(false);
                if (probe.Ok)
                {
                    _isSwitching = true;
                    try
                    {
                        AppLog.Info($"[ProfileAutoSwitch] Сбой стратегии, переключаю на профиль «{cand.Name}» ({cand.StrategyName}) — ресурс {failedTarget.Name} стал доступен");
                        var (ok, msg) = await ProfileManager.ApplyProfileAsync(cand, _settings, bypass, _strategiesFactory()).ConfigureAwait(false);
                        _lastSwitchTime = DateTime.UtcNow;
                        _settings.LastAutoSwitchTime = DateTime.UtcNow;
                        SettingsStore.Save(_settings);
                        LastReason = ok ? $"Профиль «{cand.Name}» восстановлен после сбоя" : $"Ошибка профиля «{cand.Name}»: {msg}";
                        StatusChanged?.Invoke(LastReason);
                        if (ok) ProfileSwitched?.Invoke(cand, identity);
                        return ok;
                    }
                    finally { _isSwitching = false; }
                }
            }
            return false;
        }

        public void Dispose()
        {
            try { NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged; } catch { }
            _pollTimer.Dispose();
        }
    }
}
