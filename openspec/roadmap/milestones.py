"""Вехи PR: состав и зависимости заданы явно, состояние — только из OpenSpec."""
import re

PR = re.compile(r'\bPR\s+(\d+)\b', re.I)


def select(tasks, selectors):
    selected = []
    for selector in selectors:
        found = [t for t in tasks if t['n'] == selector or
                 (not selector or selector.endswith('.')) and t['n'].startswith(selector)]
        if not found:
            raise ValueError(f'Не найдены задачи по условию {selector!r}')
        selected.extend(t for t in found if t not in selected)
    return selected


def attach_completions(changes, configs, tasks_by_change, resolve):
    graph = {}
    for name, config in configs.items():
        if 'completedBy' not in config:
            continue
        completion = config['completedBy']
        source_name = resolve(completion['change'])
        source = changes[source_name]
        own_tasks = {t['n']: t for t in tasks_by_change[name]}
        source_tasks_by_number = {t['n']: t for t in tasks_by_change[source_name]}
        for number, numbers in completion['tasks'].items():
            if not numbers:
                raise ValueError(f'{name}:{number}: нужны задачи, закрывающие остаток')
            try:
                task = own_tasks[number]
                source_tasks = [source_tasks_by_number[n] for n in numbers]
            except KeyError as error:
                raise ValueError(f'{name} → {source_name}: не найдена задача {error}') from error
            graph[(name, number)] = {(source_name, n) for n in numbers}
            task['completedBy'] = {
                'change': source_name, 'label': completion['label'],
                'tasks': numbers,
                'done': source['archived'] and all(t['done'] for t in source_tasks),
            }
    return graph


def task_done(task):
    return task['done'] or task.get('completedBy', {}).get('done', False)


def attach_milestones(changes, definitions):
    def resolve(short):
        if short in changes:
            return short
        matches = [name for name in changes if name.startswith(short + '-')]
        if len(matches) != 1:
            raise ValueError(f'Неоднозначный или неизвестный change: {short}')
        return matches[0]

    configs = {resolve(short): value for short, value in definitions.items()}
    tasks_by_change = {name: [t for s in change['sections'] for t in s['items']]
                       for name, change in changes.items()}
    graph = attach_completions(changes, configs, tasks_by_change, resolve)
    for name, change in changes.items():
        if name.endswith('-00-epic'):
            continue
        tasks = tasks_by_change[name]
        config = configs.get(name, {})
        parts = {}
        for section in change['sections']:
            match = PR.search(section['title'])
            if not match or not section['items'] or re.search('Верификация|в каждом PR', section['title'], re.I):
                continue
            number = match[1]
            part = parts.setdefault(number, {'title': section['title'], 'tasks': []})
            part['tasks'].extend(item['n'] for item in section['items'])
        if not parts:
            body = [item['n'] for section in change['sections']
                    if not re.search(r'Верификация|^0\.', section['title'], re.I)
                    for item in section['items']]
            number = str(config.get('fallbackPart', 1))
            parts[number] = {'title': change['title'], 'tasks': body or [t['n'] for t in tasks]}
        assigned = set()
        change['milestones'] = []
        for number, inferred in sorted(parts.items(), key=lambda p: int(p[0])):
            explicit = config.get('parts', {}).get(number, {})
            selected = select(tasks, explicit.get('tasks', inferred['tasks']))
            ids = [t['n'] for t in selected]
            if assigned.intersection(ids):
                raise ValueError(f'{name}: задача назначена нескольким PR')
            assigned.update(ids)
            title = explicit.get('title', inferred['title'])
            title = re.sub(r'^\d+\.\s*', '', title)
            title = re.sub(r'^PR\s+\d+\s*:\s*', '', title, flags=re.I)
            title = re.sub(r'\s*\(PR\s+\d+\)', '', title, flags=re.I)
            change['milestones'].append({
                'id': f'{name}#{number}', 'number': int(number), 'title': title,
                'tasks': ids, 'conditions': [], 'documented': name in configs,
                'note': explicit.get('note', ''),
            })
        change['commonTasks'] = [t['n'] for t in tasks if t['n'] not in assigned]

    def condition(raw, own):
        raw = {'ref': raw} if isinstance(raw, str) else dict(raw)
        ref, _, number = raw.get('ref', own).partition('#')
        target_name = resolve(ref)
        target = changes[target_name]
        target_tasks = tasks_by_change[target_name]
        target_id = target_name + '#' + number if number else None
        if number:
            part = next((m for m in target['milestones'] if m['id'] == target_id), None)
            if part is None:
                raise ValueError(f'Неизвестная веха {target_id}')
            picked = select(target_tasks, part['tasks'])
            done = all(task_done(t) for t in picked)
            label = f'{target_name} / PR {number}: задачи выполнены'
        elif 'tasks' in raw:
            picked = select(target_tasks, raw['tasks'])
            done = all(task_done(t) for t in picked)
            label = f'{target_name}: задачи ' + ', '.join(t['n'] for t in picked)
        else:
            picked = []
            done = target['archived']
            label = f'{target_name}: change архивирован'
        return {
            'change': target_name, 'target': target_id, 'tasks': [t['n'] for t in picked],
            'label': raw.get('label', label), 'done': done, 'optional': raw.get('optional', False),
            'for': raw.get('for'), 'source': target['path'] + '/tasks.md',
        }

    for name, change in changes.items():
        if 'milestones' not in change:
            continue
        config = configs.get(name, {})
        tasks = tasks_by_change[name]
        for part in change['milestones']:
            settings = config.get('parts', {}).get(str(part['number']), {})
            conditions = [condition(raw, name) for raw in config.get('after', []) + settings.get('after', [])]
            part_tasks = select(tasks, part['tasks'])
            for c in conditions:
                if c['target'] == part['id']:
                    raise ValueError(f'Веха зависит от самой себя: {part["id"]}')
                c['applies'] = [t['n'] for t in select(part_tasks, c['for'])] if c['for'] else part['tasks']
            part['conditions'] = conditions
            pending = [t['n'] for t in part_tasks if not task_done(t)]
            blocked = {n for c in conditions if not c['done'] and not c['optional'] for n in c['applies']}
            blocked.update(t['n'] for t in part_tasks if 'completedBy' in t)
            part['startable'] = [n for n in pending if n not in blocked]
            if not part_tasks:
                part['state'] = 'unknown'
            elif not pending:
                part['state'] = 'done'
            elif not part['documented']:
                part['state'] = 'unknown'
                part['startable'] = []
            elif not part['startable']:
                part['state'] = 'waiting'
            elif blocked.intersection(pending):
                part['state'] = 'partial'
            else:
                part['state'] = 'ready'

    # Проверка графа именно задач: зависимость на часть PR не блокирует весь PR.
    for name, change in changes.items():
        for part in change.get('milestones', []):
            for c in part['conditions']:
                if c['optional']:
                    continue
                needs = c['tasks'] or [t['n'] for t in tasks_by_change[c['change']]]
                for n in c['applies']:
                    graph.setdefault((name, n), set()).update(
                        (c['change'], other) for other in needs
                        if (name, n) != (c['change'], other))
    visiting, visited = set(), set()

    def walk(node):
        if node in visiting:
            raise ValueError(f'Циклическое условие старта: {node}')
        if node in visited:
            return
        visiting.add(node)
        for dependency in graph.get(node, []):
            walk(dependency)
        visiting.remove(node)
        visited.add(node)

    for node in graph:
        walk(node)
