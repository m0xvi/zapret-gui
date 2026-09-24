#!/usr/bin/env python3
"""
check_bindings.py — статическая проверка XAML проекта Zapret GUI.

Компилятор XAML НЕ проверяет корректность {Binding} и ключей ресурсов: ошибки видны
только в рантайме (пустые элементы, серые рамки), поэтому этот скрипт обязателен
после любых правок в Views/*.xaml или Themes/*.xaml.

Что проверяется:
  1. {Binding Path} — существует ли свойство в соответствующей ViewModel
     (с учётом свойств элементов DataTemplate: StrategyInfo, StrategyCandidate,
      StrategyCandidateEvaluation, SavedStrategyCandidate, ConnectionCheck,
      DiagnosticItem, LogEntry, NavItem).
  2. Второй сегмент пути (Some.Property) — существует ли свойство у типа-владельца.
  3. {StaticResource X} / {DynamicResource X} — объявлен ли ключ в Themes/ или App.xaml.
  4. Click="Handler" — есть ли метод Handler в соответствующем code-behind.

Запуск из корня репозитория:  python3 tools/check_bindings.py
Код возврата: 0 — проблем нет, 1 — найдены проблемы.
"""
import glob
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "src", "ZapretGUI")

# Страница -> ViewModel (DataContext задаётся в code-behind конструктором)
PAGE_VM = {
    "HomePage": ["HomeViewModel"],
    "StrategiesPage": ["StrategiesViewModel"],
    "UpdatesPage": ["UpdatesViewModel"],
    "DiagnosticsPage": ["DiagnosticsViewModel"],
    "DpiPage": ["DiagnosticsViewModel"],
    "DeepCheckPage": ["DeepCheckViewModel"],
    "FirstLaunchPage": ["FirstLaunchViewModel"],
    "MonitoringPage": ["MonitoringViewModel"],
    "UserListsPage": ["UserListsViewModel"],
    "ProfilesPage": ["ProfilesViewModel"],
    "LogsPage": ["LogsViewModel"],
    "SettingsPage": ["SettingsViewModel"],
    "AboutPage": ["MainViewModel"],
    "MainWindow": ["MainViewModel"],
}

# Типы элементов, на которые указывают {Binding} внутри DataTemplate
ITEM_TYPES = {
    "UpdatesPage": ["EngineConsistencyItem", "EngineBackupInfo"],
    "HomePage": ["ConnectionCheck", "MonitorTarget"],
    "StrategiesPage": ["StrategyInfo", "StrategyCandidate", "StrategyCandidateEvaluation", "SavedStrategyCandidate", "StrategyEvaluationHistoryRecord", "StrategySwitchRecord", "MonitorTarget", "AutoTunerStepResult", "SniTestResult", "SniCandidate"],
    "ProfilesPage": ["UserProfile", "BackupArchiveInfo"],
    "DiagnosticsPage": ["DiagnosticItem", "DpiTargetResult", "DpiProbeResult", "MonitorTarget", "ResourceProbeResult", "DeepCheckFinding", "DeepCheckMetric", "DeepCheckRecommendation", "DiscordVoiceServerCheck"],
    "UserListsPage": ["GameFilterProfile", "DnsProfile", "DnsHijackEntry", "DnsHijackReport", "UserListOption"],
    "DpiPage": ["DpiTargetResult", "DpiProbeResult"],
    "DeepCheckPage": ["DeepCheckFinding", "DeepCheckMetric", "DeepCheckRecommendation", "EngineConsistencyCheck"],
    "MonitoringPage": ["MonitorTarget", "ResourceProbeResult"],
    "LogsPage": ["LogEntry"],
    "MainWindow": ["NavItem"],
}

# Типы, для которых второй сегмент не проверяем
SKIP_SECOND = {
    "string", "bool", "int", "double", "ICommand", "AppSettings", "void", "",
    "ObservableCollection<string>", "ObservableCollection<MonitorTarget>", "ObservableCollection<ConnectionCheck>", "ObservableCollection<EngineBackupInfo>", "ObservableCollection<LogEntry>", "ObservableCollection<GameFilterProfile>", "ObservableCollection<DnsProfile>", "ObservableCollection<UserProfile>", "ObservableCollection<BackupArchiveInfo>", "List<string>", "ICollectionView",
}


def read(path):
    with open(path, encoding="utf-8") as handle:
        return handle.read()


def collect_members():
    """Собирает публичные члены классов и типы свойств из Core/ и ViewModels/."""
    members, prop_types = {}, {}
    for path in glob.glob(os.path.join(SRC, "ViewModels", "*.cs")) + \
                glob.glob(os.path.join(SRC, "Core", "*.cs")):
        current = None
        for line in read(path).split("\n"):
            class_match = re.search(r"class\s+(\w+)", line)
            if class_match and ("public" in line or "internal" in line or "sealed" in line):
                current = class_match.group(1)

            # Поддержка CommunityToolkit [ObservableProperty] private TYPE _field -> public Property
            obs = re.search(r"\[ObservableProperty\].*private\s+[\w\.<>\?\[\],\s]+\s+_(\w+)\s*[=;]", line)
            if obs and current:
                raw = obs.group(1)
                # _isVisible -> IsVisible, _errorText -> ErrorText
                prop = raw[0].upper() + raw[1:] if raw else raw
                members.setdefault(current, set()).add(prop)
                # тип не критичен для проверки биндингов — оставим object
                prop_types[(current, prop)] = "object"
                # Также добавляем связанную команду для [RelayCommand] — будет обработано ниже
            member = re.search(
                r"public\s+(?:static\s+|virtual\s+|override\s+|readonly\s+|sealed\s+|event\s+)*"
                r"([\w\.<>?\[\],\s]+?)\s+(\w+)\s*(?:[\{=\(;]|$)", line)
            if member and current:
                name = member.group(2)
                if name in ("get", "set", "class"):
                    continue
                members.setdefault(current, set()).add(name)
                prop_types[(current, name)] = member.group(1).strip().replace("?", "")
            # CommunityToolkit [RelayCommand] private void Foo() -> public ICommand FooCommand (учёт раздельных строк)
            if "[RelayCommand" in line and current:
                # запоминаем что следующая void-метод — команда
                # ставим маркер в members через временный атрибут (используем глобальную переменную)
                # Проще: сразу ищем метод в этой же строке
                m2 = re.search(r"void\s+(\w+)\s*\(", line)
                if m2:
                    members.setdefault(current, set()).add(m2.group(1) + "Command")
                else:
                    # отмечаем ожидание команды на следующей строке
                    members.setdefault(current + "_pendingRelay", set()).add("1")
                continue
            if current and (current + "_pendingRelay") in members and "1" in members[current + "_pendingRelay"]:
                m2 = re.search(r"void\s+(\w+)\s*\(", line)
                if m2:
                    members.setdefault(current, set()).add(m2.group(1) + "Command")
                    members[current + "_pendingRelay"].discard("1")
                elif line.strip() and not line.strip().startswith("[") and not line.strip().startswith("//"):
                    # если не метод — сбрасываем ожидание
                    members[current + "_pendingRelay"].discard("1")
    return members, prop_types


def collect_resource_keys():
    keys = set()
    for path in glob.glob(os.path.join(SRC, "Themes", "*.xaml")) + [os.path.join(SRC, "App.xaml")]:
        keys.update(re.findall(r'x:Key="([^"]+)"', read(path)))
    return keys


def main():
    members, prop_types = collect_members()
    keys = collect_resource_keys()
    problems = []

    xaml_files = glob.glob(os.path.join(SRC, "Views", "**", "*.xaml"), recursive=True)

    for path in xaml_files:
        base = os.path.basename(path).replace(".xaml", "")
        text = read(path)
        vms = PAGE_VM.get(base, [])
        items = ITEM_TYPES.get(base, [])

        if vms:
            for match in re.finditer(r"\{Binding\s+([^}]+)\}", text):
                full = match.group(1)
                # Привязки через RelativeSource/ElementName/Source указывают не на ViewModel —
                # компилятор XAML их тоже не проверяет, пропускаем во избежание ложных срабатываний
                if "RelativeSource" in full or "ElementName" in full or "Source=" in full:
                    continue
                binding = full.split(",")[0].strip()
                first = binding.split(".")[0].split(",")[0].strip()
                if not first:
                    continue

                known = any(first in members.get(vm, set()) for vm in vms) or \
                        any(first in members.get(item, set()) for item in items)
                if not known:
                    problems.append(f"{os.path.relpath(path, ROOT)}: привязка '{binding}' не найдена в {', '.join(vms + items)}")
                    continue

                if "." not in binding:
                    continue

                second = binding.split(".")[1].split(",")[0].strip()
                owner = next((prop_types.get((vm, first)) for vm in vms
                              if first in members.get(vm, set())), None)
                if owner is None:
                    owner = next((prop_types.get((item, first)) for item in items
                                  if first in members.get(item, set())), None)

                if owner and owner not in SKIP_SECOND and second not in members.get(owner, set()):
                    problems.append(f"{os.path.relpath(path, ROOT)}: у типа {owner} нет свойства '{second}' (в '{binding}')")

        for match in re.finditer(r"\{(?:Static|Dynamic)Resource\s+([^}\s,]+)\s*\}", text):
            key = match.group(1)
            if key not in keys:
                problems.append(f"{os.path.relpath(path, ROOT)}: ключ ресурса '{key}' не объявлен в Themes/ или App.xaml")

        # Проверка ProgressBar: в WPF RangeBase.ValueProperty (ProgressBar.Value, Maximum, Minimum)
        # по умолчанию регистрируется с BindsTwoWayByDefault=true. Если не указан Mode=OneWay,
        # WPF выбросит InvalidOperationException для read-only свойств в рантайме.
        for pb_match in re.finditer(r"<ProgressBar\b([^>]+?)(?:/>|>)", text, re.DOTALL):
            pb_attrs = pb_match.group(1)
            for attr in ("Value", "Maximum", "Minimum"):
                attr_match = re.search(rf'\b{attr}\s*=\s*"\{{Binding\s+([^}}]+)\}}"', pb_attrs)
                if attr_match:
                    binding_expr = attr_match.group(1)
                    if "Mode=OneWay" not in binding_expr:
                        problems.append(
                            f"{os.path.relpath(path, ROOT)}: ProgressBar.{attr} по умолчанию привязывается TwoWay; добавьте Mode=OneWay в '{{Binding {binding_expr}}}'"
                        )

        code_behind = path + ".cs"
        if os.path.exists(code_behind):
            code_text = read(code_behind)
            for match in re.finditer(r'Click="(\w+)"', text):
                if f"void {match.group(1)}(" not in code_text:
                    problems.append(f"{os.path.relpath(code_behind, ROOT)}: нет обработчика '{match.group(1)}'")

    print(f"Проверено XAML-файлов: {len(xaml_files)}; ключей ресурсов: {len(keys)}")
    if problems:
        print("\nНАЙДЕНЫ ПРОБЛЕМЫ:")
        for problem in problems:
            print("  - " + problem)
        return 1

    print("Проблем не найдено: все привязки, ключи ресурсов и обработчики на месте.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
