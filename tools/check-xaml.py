#!/usr/bin/env python3
"""Static checks for the WPF layer, which nothing else can catch before a user does.

Two whole classes of defect in WPF are invisible to the compiler:

  * A {StaticResource Key} that is not defined throws when the page is first shown.
  * A {Binding Path} to a property that does not exist fails silently — no exception,
    no build error, just a control that never shows anything.

Both are unreachable from the unit tests, because the tests deliberately do not depend
on Windows. This script closes that gap by resolving the XAML against the C# by hand.
Run it from the repository root; CI runs it on every push.
"""
import glob
import re
import sys
import xml.etree.ElementTree as ET

KEY = '{http://schemas.microsoft.com/winfx/2006/xaml}Key'

RESOURCE_DICTIONARIES = [
    'src/SoulRemote/Resources/Palette.xaml',
    'src/SoulRemote/Resources/Typography.xaml',
    'src/SoulRemote/Resources/Controls.xaml',
]

# Each view's DataContext is set by the implicit DataTemplates in MainWindow.xaml.
VIEW_CONTEXT = {
    'src/SoulRemote/MainWindow.xaml': 'ShellViewModel',
    'src/SoulRemote/Views/DashboardView.xaml': 'DashboardViewModel',
    'src/SoulRemote/Views/ConnectView.xaml': 'ConnectViewModel',
    'src/SoulRemote/Views/SettingsView.xaml': 'SettingsViewModel',
    'src/SoulRemote/Views/LogView.xaml': 'LogViewModel',
    'src/SoulRemote/Views/UpdateCard.xaml': 'UpdateViewModel',
}

# Inside a DataTemplate the DataContext is the item, not the page's view model.
ITEM_TYPES = ['ConnectionStep', 'PairedChatViewModel', 'LogEntry']

# Types that do not live in a file of their own.
DECLARED_IN = {
    'ConnectionStep': 'src/SoulRemote.Core/Services/ConnectionOrchestrator.cs',
    'LogEntry': 'src/SoulRemote.Core/Services/LogService.cs',
    'RelayLine': 'src/SoulRemote/Controls/RelayLine.xaml.cs',
}

SEARCH_ROOTS = ['src/SoulRemote', 'src/SoulRemote.Core']

# A binding that names its own source is not resolved against the DataContext.
QUALIFIED = re.compile(r'\{Binding[^}]*(?:RelativeSource=|ElementName=|Source=)')


def keys_defined_in(path):
    try:
        root = ET.parse(path).getroot()
    except ET.ParseError as error:
        sys.exit(f"XML parse error in {path}: {error}")
    return {el.attrib[KEY] for el in root.iter() if KEY in el.attrib}


def source_of(type_name):
    if type_name in DECLARED_IN:
        return DECLARED_IN[type_name]
    for root in SEARCH_ROOTS:
        for path in glob.glob(f'{root}/**/{type_name}.cs', recursive=True):
            return path
    sys.exit(f"could not find the source for {type_name}")


def properties_of(type_name):
    source = open(source_of(type_name), encoding='utf-8').read()
    marker = f'class {type_name}'
    if marker in source:
        start = source.index(marker)
        end = source.find('\npublic ', start + 1)
        source = source[start:end if end != -1 else len(source)]
    names = set(re.findall(r'public\s+[\w<>,\?\[\]\. ]+\s+([A-Za-z_]\w*)\s*(?:=>|\{)', source))
    names |= set(re.findall(r'public\s+[\w<>,\?\[\]\. ]+\s+([A-Za-z_]\w*)\s*\{\s*get', source))
    return names


def check_resources():
    defined = set()
    for path in RESOURCE_DICTIONARIES:
        defined |= keys_defined_in(path)

    problems = []
    for path in sorted(glob.glob('src/SoulRemote/**/*.xaml', recursive=True)):
        available = defined | keys_defined_in(path)
        text = open(path, encoding='utf-8').read()
        for reference in sorted(set(re.findall(r'\{StaticResource\s+([\w.]+)\}', text))):
            if reference not in available:
                problems.append(f"{path}: {{StaticResource {reference}}} is not defined")
    return defined, problems


def check_bindings():
    item_properties = set()
    for type_name in ITEM_TYPES:
        item_properties |= properties_of(type_name)

    problems = []
    for path, view_model in VIEW_CONTEXT.items():
        allowed = properties_of(view_model) | item_properties
        text = open(path, encoding='utf-8').read()
        for match in re.finditer(r'\{Binding\s+(?:Path=)?([A-Za-z_]\w*)', text):
            fragment = text[match.start():text.find('}', match.start()) + 1]
            if QUALIFIED.match(fragment):
                continue
            name = match.group(1)
            if name not in allowed:
                line = text[:match.start()].count('\n') + 1
                problems.append(f"{path}:{line}: {{Binding {name}}} is on neither "
                                f"{view_model} nor any DataTemplate item type")

    relay_properties = properties_of('RelayLine')
    relay = open('src/SoulRemote/Controls/RelayLine.xaml', encoding='utf-8').read()
    for match in re.finditer(r'\{Binding\s+([A-Za-z_]\w*),\s*ElementName=Root', relay):
        if match.group(1) not in relay_properties:
            problems.append(f"src/SoulRemote/Controls/RelayLine.xaml: "
                            f"{{Binding {match.group(1)}}} is not a property of RelayLine")
    return problems


def main():
    defined, resource_problems = check_resources()
    binding_problems = check_bindings()

    print(f"{len(defined)} resource keys defined across {len(RESOURCE_DICTIONARIES)} dictionaries")
    print(f"{len(VIEW_CONTEXT)} views checked, plus RelayLine")

    problems = resource_problems + binding_problems
    if problems:
        print("\nProblems:")
        for problem in problems:
            print("  " + problem)
        return 1

    print("every StaticResource and every binding path resolves")
    return 0


if __name__ == '__main__':
    sys.exit(main())
