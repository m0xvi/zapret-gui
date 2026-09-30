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
  3. {StaticResource X} / {DynamicResource X} — объявлен ли ключ в Themes/, App.xaml или локальных
     ресурсах страниц (Views/*.xaml).
  4. Click="Handler" — есть ли метод Handler в соответствующем code-behind.
  5. Стиль карточек (v1.25.0): плотность и вертикальный отступ — только из токенов
     (CardPadding/CardPaddingCompact/CardPaddingList/CardGap), литеральные цвета — только в Themes/,
     кегль текста — только из текстовых стилей, у карточки есть заголовок, у иконочной кнопки —
     подпись для экранного диктора. Ошибки стиля валят проверку, предупреждения печатаются списком.

Запуск из корня репозитория:  python3 tools/check_bindings.py
Код возврата: 0 — проблем нет (могут быть предупреждения), 1 — найдены ошибки.
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
    "BypassCenterPage": ["BypassCenterViewModel"],
    "AutomationPage": ["SettingsViewModel"],
    "StrategiesPage": ["StrategiesViewModel"],
    "UpdatesPage": ["UpdatesViewModel"],
    "DiagnosticsPage": ["DiagnosticsViewModel"],
    "DpiPage": ["DiagnosticsViewModel"],
    "DeepCheckPage": ["DeepCheckViewModel"],
    "FirstLaunchPage": ["FirstLaunchViewModel"],
    "UserListsPage": ["UserListsViewModel"],
    "ProfilesPage": ["ProfilesViewModel"],
    "LogsPage": ["LogsViewModel"],
    "HelpPage": ["HelpViewModel"],
    "SearchOverlay": ["SearchViewModel"],
    "SettingsPage": ["SettingsViewModel"],
    "AboutPage": ["MainViewModel"],
    "MainWindow": ["MainViewModel"],
}

# Типы элементов, на которые указывают {Binding} внутри DataTemplate
ITEM_TYPES = {
    "UpdatesPage": ["EngineConsistencyItem", "EngineBackupInfo"],
    "HomePage": ["ConnectionCheck", "MonitorTarget"],
    "BypassCenterPage": ["DnsStrategyMatrixEntry"],
    "StrategiesPage": ["StrategyInfo", "StrategyCandidate", "StrategyCandidateEvaluation", "SavedStrategyCandidate", "StrategyEvaluationHistoryRecord", "StrategySwitchRecord", "MonitorTarget", "AutoTunerStepResult", "SniTestResult", "SniCandidate"],
    "ProfilesPage": ["UserProfile", "BackupArchiveInfo"],
    "DiagnosticsPage": ["DiagnosticItem", "DpiTargetResult", "DpiProbeResult", "MonitorTarget", "ResourceProbeResult", "DeepCheckFinding", "DeepCheckMetric", "DeepCheckRecommendation", "DiscordVoiceServerCheck"],
    "UserListsPage": ["GameFilterProfile", "DnsProfile", "DnsHijackEntry", "DnsHijackReport", "UserListOption"],
    "DpiPage": ["DpiTargetResult", "DpiProbeResult"],
    "DeepCheckPage": ["DeepCheckFinding", "DeepCheckMetric", "DeepCheckRecommendation", "EngineConsistencyCheck"],
    "LogsPage": ["LogEntry"],
    "SettingsPage": ["MetricHostOption"],
    "MainWindow": ["NavItem"],
    "SearchOverlay": ["SearchResultItem"],
}

# Типы, для которых второй сегмент не проверяем
SKIP_SECOND = {
    "string", "bool", "int", "double", "ICommand", "AppSettings", "void", "",
    "ObservableCollection<string>", "ObservableCollection<MonitorTarget>", "ObservableCollection<ConnectionCheck>", "ObservableCollection<DnsStrategyMatrixEntry>", "ObservableCollection<StrategyInfo>", "ObservableCollection<EngineBackupInfo>", "ObservableCollection<LogEntry>", "ObservableCollection<GameFilterProfile>", "ObservableCollection<DnsProfile>", "ObservableCollection<UserProfile>", "ObservableCollection<BackupArchiveInfo>", "List<string>", "ICollectionView",
}


# --- Стиль карточек (v1.25.0, docs/UI_CARD_AUDIT.md) -------------------------------------
CARD_PADDING_TOKENS = {"{DynamicResource CardPadding}", "{DynamicResource CardPaddingCompact}",
                       "{DynamicResource CardPaddingList}", "0"}
CARD_HEADING_TOKENS = ("SectionText", "TitleText", "SubtitleText")
CARD_CONTAINER_TAGS = ("ListBox", "ItemsControl", "ScrollViewer", "TextBox", "TreeView")
# У этих экранов карточка-панель по смыслу без заголовка (журнал, оверлеи, sticky-панель)
CARD_HEADING_EXEMPT = {"LogsPage.xaml", "SearchOverlay.xaml"}
COLOR_ATTRS = ("Background", "Foreground", "BorderBrush", "Fill", "Color", "Stroke")
COLOR_RE = re.compile(r'\b(' + '|'.join(COLOR_ATTRS) + r')="(#[0-9A-Fa-f]{3,8})"')
GLYPH_RE = re.compile('&#x[0-9A-Fa-f]{4};|[\U0001F300-\U0001FAFF\u2600-\u27BF\u2B00-\u2BFF]')
HEADING_RE = re.compile(r'StaticResource (' + '|'.join(CARD_HEADING_TOKENS) + r')\}')
BOLD_RE = re.compile(r'FontWeight="(SemiBold|Bold)"')


def iter_tags(text, name):
    """Все теги <name ...> с корректной обработкой кавычек: (start, end, tagtext)."""
    out = []
    for match in re.finditer(r'<' + name + r'(?=[\s/>])', text):
        start = match.start()
        index = start + len(name) + 1
        quote = None
        while index < len(text):
            ch = text[index]
            if quote:
                if ch == quote:
                    quote = None
            elif ch in '"\'':
                quote = ch
            elif ch == '>':
                break
            index += 1
        out.append((start, index + 1, text[start:index + 1]))
    return out


def get_attr(tag, attr):
    match = re.search(r'\s' + attr + r'="([^"]*)"', tag)
    return match.group(1) if match else None


def style_of(tag):
    match = re.search(r'Style="\{StaticResource (\w+)\}"', tag)
    return match.group(1) if match else None


def element_bodies(text, name):
    """Диапазоны тел всех элементов <name ...>…</name> в порядке появления."""
    pattern = re.compile(r'<' + name + r'(?=[\s/>])')
    close_re = re.compile(r'</' + name + r'\s*>')
    bodies = []
    for match in pattern.finditer(text):
        start = match.start()
        if '>' not in text[start:]:
            continue
        depth = 0
        index = start
        while index < len(text):
            nxt_open = pattern.search(text, index)
            nxt_close = close_re.search(text, index)
            if not nxt_close:
                break
            if nxt_open and nxt_open.start() < nxt_close.start():
                tag_end = text.find('>', nxt_open.start())
                if not text[nxt_open.start():tag_end + 1].rstrip().endswith('/>'):
                    depth += 1
                index = tag_end + 1
                continue
            depth -= 1
            if depth <= 0:
                bodies.append((start, nxt_close.end()))
                break
            index = nxt_close.end()
    return bodies


def border_body(text, start, tag_end):
    """Тело конкретного <Border> от открывающего тега до парного закрывающего."""
    for body_start, body_end in element_bodies(text, 'Border'):
        if body_start == start:
            return text[body_start:body_end]
    return text[start:tag_end]


def first_textblock(body):
    """Первый содержательный TextBlock карточки: иконки и глифы пропускаем."""
    for start, tag_end, tag in iter_tags(body, 'TextBlock'):
        if 'IconFont' in tag:
            continue
        text_value = get_attr(tag, 'Text') or ''
        if GLYPH_RE.search(text_value) and len(text_value.strip()) <= 3:
            continue
        return tag
    return None


EXPANDER_BODIES = {}


def check_card_style(path, text, problems, warnings):
    """Токены плотности/отступа, литеральные цвета, заголовок карточки и кегль текста."""
    EXPANDER_BODIES[path] = element_bodies(text, 'Expander')
    rel = os.path.relpath(path, ROOT)
    for match in COLOR_RE.finditer(text):
        warnings.append(f'{rel}: литеральный цвет {match.group(1)}="{match.group(2)}" — '
                        f'кисти живут только в Themes/ (v1.26.0 переведёт остатки на ключи)')

    for start, end, tag in iter_tags(text, 'Border'):
        if style_of(tag) != 'Card':
            continue
        padding = get_attr(tag, 'Padding')
        if padding is not None and padding not in CARD_PADDING_TOKENS:
            problems.append(f'{rel}: карточка с Padding="{padding}" — используйте CardPadding, '
                            f'CardPaddingCompact или CardPaddingList (docs/UI_CARD_AUDIT.md §7)')
        margin = get_attr(tag, 'Margin')
        if margin and re.fullmatch(r'0,\d+,0,0', margin):
            problems.append(f'{rel}: карточка с литеральным отступом Margin="{margin}" — '
                            f'используйте {{DynamicResource CardGap}}')
        block = border_body(text, start, end)
        if os.path.basename(path) in CARD_HEADING_EXEMPT:
            continue
        line = text[:start].count(chr(10)) + 1
        # заголовок задан стилем или первой жирной строкой карточки
        first = first_textblock(block)
        if first and (HEADING_RE.search(first) or BOLD_RE.search(first)):
            continue
        # контейнер списка (журнал, результаты проверки) или карточка внутри Expander
        # (заголовок даёт шапка Expander) — заголовок не нужен
        if any(f'<{container}' in block for container in CARD_CONTAINER_TAGS):
            continue
        if any(exp_start <= start < exp_end for exp_start, exp_end in EXPANDER_BODIES.get(path, ())):
            continue
        warnings.append(f"{rel}: карточка без заголовка ({', '.join(CARD_HEADING_TOKENS)}) — строка {line}")

    for start, end, tag in iter_tags(text, 'TextBlock'):
        size = get_attr(tag, 'FontSize')
        if not size or style_of(tag) or 'FontFamily=' in tag:
            continue
        if GLYPH_RE.search(get_attr(tag, 'Text') or ''):
            continue
        warnings.append(f'{rel}: TextBlock с FontSize="{size}" без стиля — '
                        f'строка {text[:start].count(chr(10)) + 1}')

    for start, end, tag in iter_tags(text, 'Button'):
        content = get_attr(tag, 'Content') or ''
        if not content or len(content) > 3:
            continue
        if 'AutomationProperties.Name' in tag or 'ToolTip' in tag:
            continue
        warnings.append(f'{rel}: иконочная кнопка без AutomationProperties.Name/ToolTip — '
                        f'строка {text[:start].count(chr(10)) + 1}')


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
    """Ключи ресурсов: темы и App.xaml + локальные ресурсы страниц (Window.Resources и т.п.).

    Локальный ключ виден только внутри своей страницы, поэтому проверка «ключ объявлен»
    остаётся приблизительной — она ловит опечатки, а не области видимости.
    """
    keys = set()
    for path in glob.glob(os.path.join(SRC, "Themes", "*.xaml")) + \
                glob.glob(os.path.join(SRC, "Views", "*.xaml")) + \
                [os.path.join(SRC, "App.xaml")]:
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

    warnings = []
    for path in xaml_files:
        check_card_style(path, read(path), problems, warnings)

    print(f"Проверено XAML-файлов: {len(xaml_files)}; ключей ресурсов: {len(keys)}")
    if warnings:
        print(f"\nПРЕДУПРЕЖДЕНИЯ СТИЛЯ ({len(warnings)}) — не валят проверку, но их стоит закрыть:")
        for warning in warnings:
            print("  - " + warning)
    if problems:
        print("\nНАЙДЕНЫ ПРОБЛЕМЫ:")
        for problem in problems:
            print("  - " + problem)
        return 1

    print("Проблем не найдено: все привязки, ключи ресурсов и обработчики на месте.")
    if warnings:
        print("(предупреждения стиля выше не блокируют проверку)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
