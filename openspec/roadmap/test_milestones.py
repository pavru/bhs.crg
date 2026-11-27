"""Проверки графа условий и сохранности задач и истории на реальной нарезке."""
import copy
import json
import unittest
from pathlib import Path

from build import CHANGE, MOBILE_CHANGE, change_dirs, read_tasks
from milestones import attach_milestones, select

HERE = Path(__file__).resolve().parent
WORK_25 = 'work-25-roles-e2e-violation'
WORK_25_1 = 'work-25-1-isolation-test-controls'


def load(pattern=CHANGE):
    result = {}
    for name, (path, archived) in change_dirs(pattern).items():
        title, sections = read_tasks(path / 'tasks.md', name)
        result[name] = {'title': title, 'sections': sections, 'archived': archived,
                        'path': path.relative_to(HERE.parents[1]).as_posix()}
    return result


def tasks(changes, name):
    return [t for s in changes[name]['sections'] for t in s['items']]


class RoadmapTests(unittest.TestCase):
    def test_mobile_release_gate_and_independent_first_milestones(self):
        mobile = load(MOBILE_CHANGE)
        definitions = json.loads((HERE / 'milestones-5.json').read_text(encoding='utf-8'))['changes']
        for change in mobile.values():
            change['archived'] = False
            for section in change['sections']:
                for task in section['items']:
                    task['done'] = False

        def states():
            attach_milestones(mobile, definitions)
            return {part['id']: part['state'] for change in mobile.values()
                    for part in change.get('milestones', [])}

        self.assertEqual(set(states().values()), {'waiting'})
        release_gate = next(t for t in tasks(mobile, 'mob-00-epic') if t['n'] == '0.1')
        release_gate['done'] = True
        self.assertEqual({key for key, state in states().items() if state == 'ready'},
                         {'mob-01-field-contract#1', 'mob-06-offline-entry#1'})
        self.assertEqual(states()['mob-06-offline-entry#2'], 'waiting')

    @classmethod
    def setUpClass(cls):
        cls.base = load()
        # A fixed workflow situation, independent of future progress in the actual project.
        for name in ('work-18-crew', 'work-19-period-closure', 'work-21-review-ui'):
            cls.base[name]['archived'] = False
            for task in tasks(cls.base, name):
                task['done'] = name == 'work-18-crew' and task['n'].startswith(('1.', '3.'))
        cls.definitions = json.loads((HERE / 'milestones.json').read_text(encoding='utf-8'))['changes']

    def build(self, changes=None, definitions=None):
        changes = copy.deepcopy(changes if changes is not None else self.base)
        attach_milestones(changes, definitions if definitions is not None else self.definitions)
        return changes

    def test_all_tasks_appear_once_and_every_active_change_is_documented(self):
        changes = self.build()
        for name, change in changes.items():
            if name.endswith('00-epic'):
                continue
            assigned = change['commonTasks'] + [n for p in change['milestones'] for n in p['tasks']]
            self.assertCountEqual(assigned, [t['n'] for t in tasks(changes, name)], name)
            self.assertEqual(len(assigned), len(set(assigned)), name)
            if not change['archived']:
                self.assertTrue(all(p['documented'] for p in change['milestones']), name)

    def test_crew_server_done_panel_waiting_without_waiting_for_archive(self):
        changes = self.build()
        server, panel = changes['work-18-crew']['milestones']
        self.assertEqual(server['state'], 'done')
        self.assertEqual(panel['state'], 'waiting')
        self.assertFalse(changes['work-18-crew']['archived'])
        server_condition = next(c for c in panel['conditions'] if c['target'] == server['id'])
        self.assertTrue(server_condition['done'])

    def test_panel_readiness_follows_live_checkboxes_not_saved_status(self):
        source = copy.deepcopy(self.base)
        for name in ('work-21-review-ui', 'work-18-crew'):
            for task in tasks(source, name):
                if name.startswith('work-21') or task['n'] == '2.1':
                    task['done'] = True
        panel = self.build(source)['work-18-crew']['milestones'][1]
        self.assertEqual(panel['state'], 'ready')
        self.assertEqual(panel['startable'], ['2.2', '2.3'])
        # A changed prerequisite blocks the panel again, with no edits to metadata.
        tasks(source, 'work-18-crew')[0]['done'] = False
        self.assertEqual(self.build(source)['work-18-crew']['milestones'][1]['state'], 'waiting')

    def test_partial_pr_does_not_block_server_on_future_client(self):
        parts = self.build()['work-19-period-closure']['milestones']
        self.assertEqual(parts[0]['state'], 'partial')
        self.assertIn('1.1', parts[0]['startable'])
        self.assertNotIn('1.4', parts[0]['startable'])
        self.assertNotIn('1.5', parts[0]['startable'])
        # PR 2 needs only the mechanism, not PR 1's future UI or version inventory test.
        source = copy.deepcopy(self.base)
        for t in tasks(source, 'work-19-period-closure'):
            if t['n'] in ('1.1', '1.2'):
                t['done'] = True
        self.assertEqual(self.build(source)['work-19-period-closure']['milestones'][1]['state'], 'ready')

    def test_preserves_removed_pr_number_and_combines_sections_of_one_pr(self):
        changes = self.build()
        self.assertEqual([p['number'] for p in changes['work-09-module-exchange']['milestones']], [2])
        first = changes['plan-05-grand-smeta-reader']['milestones'][0]
        self.assertIn('1.1', first['tasks'])
        self.assertIn('2.1', first['tasks'])
        self.assertNotIn('4.1', first['tasks'])

    def test_unknown_conditions_are_never_recommended(self):
        definitions = copy.deepcopy(self.definitions)
        del definitions['work-18']
        changes = self.build(definitions=definitions)
        self.assertEqual(changes['work-18-crew']['milestones'][1]['state'], 'unknown')
        self.assertEqual(changes['work-18-crew']['milestones'][1]['startable'], [])
        source = copy.deepcopy(self.base)
        source['work-99-empty'] = {'title': 'Ещё нет задач', 'sections': [], 'archived': False,
                                   'path': 'openspec/changes/work-99-empty'}
        self.assertEqual(self.build(source)['work-99-empty']['milestones'][0]['state'], 'unknown')

    def test_rejects_dangling_task_and_dependency_cycle(self):
        definitions = copy.deepcopy(self.definitions)
        definitions['work-18']['parts']['1']['after'] = [{'tasks': ['404']}]
        with self.assertRaisesRegex(ValueError, 'Не найдены задачи'):
            self.build(definitions=definitions)
        definitions = copy.deepcopy(self.definitions)
        definitions['work-18']['parts']['1']['after'] = ['work-18#2']
        with self.assertRaisesRegex(ValueError, 'Циклическое'):
            self.build(definitions=definitions)
        definitions['work-18']['parts']['1']['after'] = ['work-18#1']
        with self.assertRaisesRegex(ValueError, 'самой себя'):
            self.build(definitions=definitions)

    def test_selectors_do_not_confuse_1_1_with_1_10(self):
        picked = select([{'n': '1.1'}, {'n': '1.10'}, {'n': '1.1a'}], ['1.1'])
        self.assertEqual(picked, [{'n': '1.1'}])

    def test_changes_with_shared_short_prefix_require_their_full_names(self):
        changes = self.build()
        self.assertTrue(changes[WORK_25]['milestones'][0]['documented'])
        self.assertTrue(changes[WORK_25_1]['milestones'][0]['documented'])
        with self.assertRaisesRegex(ValueError, 'Неоднозначный.*work-25'):
            self.build(definitions={'work-25': {}})

    def test_current_completion_preserves_the_archive_snapshot(self):
        changes = self.build()
        for name, count in [('work-00-epic', 37), (WORK_25_1, 12)]:
            self.assertTrue(changes[name]['archived'], name)
            self.assertEqual([t['done'] for t in tasks(changes, name)], [True] * count, name)
        old_tasks = tasks(changes, WORK_25)
        self.assertTrue(changes[WORK_25]['archived'])
        self.assertEqual(len(old_tasks), 15)
        unchecked = {t['n']: t['completedBy'] for t in old_tasks if not t['done']}
        self.assertEqual({n: t['tasks'] for n, t in unchecked.items()}, {
            '1.1': ['1.1', '1.2'], '1.5': ['1.3', '1.4', '1.5'], '1.6': ['2.1', '2.2', '2.3'],
        })
        self.assertTrue(all(t['change'] == WORK_25_1 and t['done'] for t in unchecked.values()))
        self.assertEqual(changes[WORK_25]['milestones'][0]['state'], 'done')

    def test_completion_requires_source_archive_and_its_named_tasks(self):
        for archived, unchecked, expected in [(False, None, set()), (True, '1.4', {'1.1', '1.6'})]:
            with self.subTest(archived=archived, unchecked=unchecked):
                source = copy.deepcopy(self.base)
                source[WORK_25_1]['archived'] = archived
                for task in tasks(source, WORK_25_1):
                    task['done'] = task['n'] != unchecked
                changes = self.build(source)
                old_tasks = tasks(changes, WORK_25)
                self.assertEqual({t['n'] for t in old_tasks if t.get('completedBy', {}).get('done')}, expected)
                part = changes[WORK_25]['milestones'][0]
                self.assertEqual(part['state'], 'waiting')
                self.assertEqual(part['startable'], [])

    def test_rejects_dangling_or_cyclic_completion_links(self):
        for mapping in [{'404': ['1.1']}, {'1.1': ['404']}]:
            with self.subTest(mapping=mapping):
                definitions = copy.deepcopy(self.definitions)
                definitions[WORK_25]['completedBy']['tasks'] = mapping
                with self.assertRaisesRegex(ValueError, 'не найдена задача.*404'):
                    self.build(definitions=definitions)
        definitions = copy.deepcopy(self.definitions)
        definitions[WORK_25_1]['completedBy'] = {
            'change': WORK_25, 'label': 'Work-25', 'tasks': {'1.2': ['1.1']},
        }
        with self.assertRaisesRegex(ValueError, 'Циклическое'):
            self.build(definitions=definitions)

    def test_every_claude_date_keeps_its_original_task_key(self):
        marks = json.loads((HERE / 'claude-marks.json').read_text(encoding='utf-8'))['marks']
        keys = {name + ':' + t['n'] for name in self.base for t in tasks(self.base, name)}
        self.assertEqual(len(marks), 453)
        self.assertTrue(set(marks) <= keys)
        # History cannot override unchecked current tasks.
        key = next(key for key in marks if not key.startswith(('plan-00-epic:', 'work-00-epic:')))
        name, number = key.split(':')
        source = copy.deepcopy(self.base)
        next(t for t in tasks(source, name) if t['n'] == number)['done'] = False
        changed = self.build(source)
        self.assertFalse(next(t for t in tasks(changed, name) if t['n'] == number)['done'])


if __name__ == '__main__':
    unittest.main()
