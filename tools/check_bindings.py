#!/usr/bin/env python3
"""
check_bindings.py — статическая проверка XAML проекта Zapret GUI.

Компилятор XAML НЕ проверяет корректность {Binding} и ключей ресурсов: ошибки видны
только в рантайме (пустые элементы, серые рамки), поэтому этот скрипт обязателен
после любых правок в Views/*.xaml или Themes/*.xaml.

Что проверяется:
  1. {Binding Path} — существует ли свойство в соответствующей ViewModel
     (с учётом свойств элементов DataTemplate: StrategyInfo, ConnectionCheck,
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
    "LogsPage": ["LogsViewModel"],
    "SettingsPage": ["SettingsViewModel"],
    "AboutPage": ["MainViewModel"],
    "MainWindow": ["MainViewModel"],
}

# Типы элементов, на которые указывают {Binding} внутри DataTemplate
ITEM_TYPES = {
    "HomePage": ["ConnectionCheck"],
    "StrategiesPage": ["StrategyInfo"],
    "DiagnosticsPage": ["DiagnosticItem"],
    "LogsPage": ["LogEntry"],
    "MainWindow": ["NavItem"],
}

# Типы, для которых второй сегмент не проверяем
SKIP_SECOND = {
    "string", "bool", "int", "double", "ICommand", "AppSettings", "void", "",
    "ObservableCollection<string>", "List<string>", "ICollectionView",
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

            member = re.search(
                r"public\s+(?:static\s+|virtual\s+|override\s+|readonly\s+|sealed\s+|event\s+)*"
                r"([\w\.<>?\[\],\s]+?)\s+(\w+)\s*(?:[\{=\(;]|$)", line)
            if member and current:
                name = member.group(2)
                if name in ("get", "set", "class"):
                    continue
                members.setdefault(current, set()).add(name)
                prop_types[(current, name)] = member.group(1).strip().replace("?", "")
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
            for match in re.finditer(r"\{Binding\s+([^},]+)", text):
                binding = match.group(1).strip()
                first = binding.split(".")[0].split(",")[0].strip()
                if not first or first in ("RelativeSource", "ElementName", "Source"):
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
                    owner = next((item for item in items if first in members.get(item, set())), None)

                if owner and owner not in SKIP_SECOND and second not in members.get(owner, set()):
                    problems.append(f"{os.path.relpath(path, ROOT)}: у типа {owner} нет свойства '{second}' (в '{binding}')")

        for match in re.finditer(r"\{(?:Static|Dynamic)Resource\s+([^}\s,]+)\s*\}", text):
            key = match.group(1)
            if key not in keys:
                problems.append(f"{os.path.relpath(path, ROOT)}: ключ ресурса '{key}' не объявлен в Themes/ или App.xaml")

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
