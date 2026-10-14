import { describe, it, expect } from 'vitest';
import type { AccessInfo } from '@/shared/api/access';
import { workNav } from './navConfig';
import {
  ALL, DEFAULT_KEY, LAST_KEY, OWN_MAX, compositionLabel, coreSectionsWith, newWorkspaceId, outsideItem, ownProblem,
  ownToWorkspace, sectionInWorkspace,
  parseOwn, resolveWorkspace, splitNav, startChoice, switcherShown, systemWorkspaces, type OwnWorkspace,
} from './workspaces';

const ID = { code: 'id', title: 'Исполнительная документация', sections: ['constructions', 'catalog', 'datasets', 'reconciliations'] };
const COSTS = { code: 'costs', title: 'Счета и накладные', sections: ['constructions', 'catalog', 'periods'] };

function access(...open: { code: string; title: string; sections: string[] }[]): AccessInfo {
  // Модули экземпляра: оба, доступность — по списку открытых.
  return {
    permissions: [],
    modules: [ID, COSTS].map(m => ({ ...m, available: open.some(o => o.code === m.code) })),
  };
}

const BOTH = access(ID, COSTS);
const SUPPLY: OwnWorkspace = { id: 'w1', name: 'Снабжение', modules: ['costs'] };
const MIXED: OwnWorkspace = { id: 'w2', name: 'Стройка', modules: ['costs', 'id'] };

/** Рабочие пространства (ТЗ AUTH-16.1, AUTH-16.3, AUTH-16.5; issue #1001). */
describe('рабочие пространства', () => {
  it('переключатель есть только при двух и более доступных модулях', () => {
    expect(switcherShown(BOTH)).toBe(true);
    // Один доступный — обычное положение дел: сужать нечего, переключателя нет.
    expect(switcherShown(access(COSTS))).toBe(false);
  });

  it('системные пространства вычисляются из доступа: «все» и по одному на модуль', () => {
    expect(systemWorkspaces(BOTH).map(w => w.key)).toEqual([ALL, 'module:id', 'module:costs']);
    expect(systemWorkspaces(access(ID)).map(w => w.key)).toEqual([ALL, 'module:id']);
  });

  it('пространство «Счета» показывает счета и разделы ядра, которые объявил модуль, — и только их', () => {
    const { workspace } = resolveWorkspace(BOTH, [], 'module:costs');
    const { inside, outside } = splitNav(workNav, workspace, BOTH);

    const shown = inside.map(i => i.label);
    expect(shown).toContain('Счета');
    expect(shown).toContain('Стройки');
    expect(shown).toContain('Учётный период');
    expect(shown).not.toContain('Документы качества');
    // Не потеряно — вне пространства, строкой «Ещё N разделов».
    expect(outside.map(i => i.label)).toEqual(expect.arrayContaining(['Документы качества', 'Наборы данных', 'Сверка']));
    expect(inside.length + outside.length).toBe(workNav.length);
  });

  it('«Все доступные модули» не сужает ничего', () => {
    const { workspace } = resolveWorkspace(BOTH, [], ALL);
    expect(splitNav(workNav, workspace, BOTH).outside).toHaveLength(0);
  });

  /**
   * Пункт ядра, не назвавший раздела, был бы виден везде, а пункт модуля без модуля — тоже: сужать
   * было бы нечем. Рабочее меню обязано называть одно из двух.
   */
  it('каждый рабочий пункт называет модуль или раздел ядра', () => {
    expect(workNav.filter(i => !i.module && !i.section).map(i => i.label)).toEqual([]);
  });

  it('состав называет модули словами — в порядке экземпляра — и недоступный вслух', () => {
    expect(compositionLabel(ownToWorkspace(MIXED), BOTH))
      .toBe('2 модуля: Исполнительная документация, Счета и накладные');
    expect(compositionLabel(ownToWorkspace(MIXED), access(ID, { code: 'x', title: 'x', sections: [] })))
      .toBe('2 модуля: Исполнительная документация; 1 недоступен');
  });

  it('недоступный модуль своего пространства из меню уходит, а пространство живёт доступными', () => {
    const narrowed = access(ID, COSTS);
    narrowed.modules = narrowed.modules.map(m => (m.code === 'id' ? { ...m, available: false } : m));
    // Доступен один модуль — переключателя нет, сужать нечего: выбор ждёт, а не переписывается.
    expect(resolveWorkspace(narrowed, [MIXED], 'own:w2')).toEqual({ workspace: expect.objectContaining({ key: ALL }), notice: null });
  });

  it('пространство без доступных модулей откатывается на «все» с разовым сообщением, называющим модуль', () => {
    const thirdGone: OwnWorkspace = { id: 'w3', name: 'Планирование', modules: ['plan'] };
    const r = resolveWorkspace(BOTH, [thirdGone], 'own:w3');

    expect(r.workspace.key).toBe(ALL);
    expect(r.notice).toBe('Пространство «Планирование» осталось без доступных модулей: нет доступа к «plan». Открыты все разделы.');
  });

  it('системное пространство модуля, ставшего недоступным, — тоже откат с названием', () => {
    const noCosts = access(ID, { code: 'other', title: 'Другое', sections: [] });
    noCosts.modules.push({ code: 'other', title: 'Другое', available: true });
    const r = resolveWorkspace(noCosts, [], 'module:costs');

    expect(r.workspace.key).toBe(ALL);
    expect(r.notice).toContain('«Счета и накладные»');
  });

  it('удалённое своё пространство молча открывает «все»: человек сделал это сам', () => {
    expect(resolveWorkspace(BOTH, [], 'own:gone')).toEqual({ workspace: expect.objectContaining({ key: ALL }), notice: null });
  });

  it('начало работы: вкладка, затем умолчание из профиля, затем последнее выбранное', () => {
    const settings = { [DEFAULT_KEY]: 'own:w1', [LAST_KEY]: 'module:id' };
    expect(startChoice(settings, 'module:costs')).toBe('module:costs');
    expect(startChoice(settings, null)).toBe('own:w1');
    expect(startChoice({ [LAST_KEY]: 'module:id' }, null)).toBe('module:id');
    // Первый вход — «Все доступные модули», мастера нет.
    expect(startChoice({}, null)).toBe(ALL);
  });

  it('плашка «вне пространства» — у раздела вне его, по самому точному адресу', () => {
    const { workspace } = resolveWorkspace(BOTH, [], 'module:id');

    expect(outsideItem('/tables/costs.invoices/registry', workNav, workspace, BOTH)?.label).toBe('Реестр счетов');
    expect(outsideItem('/invoices', workNav, workspace, BOTH)?.label).toBe('Счета');
    expect(outsideItem('/document-sets/123/sections/4', workNav, workspace, BOTH)).toBeNull();
    expect(outsideItem('/profile', workNav, workspace, BOTH)).toBeNull();
  });

  /**
   * Комплект живёт под адресом «Строек», но это модуль ИД (ревью PR #1330): по одному префиксу пункта
   * меню он считался бы «Стройками» и в пространстве без ИД открывался бы без плашки.
   */
  it('комплект под адресом строек — страница модуля ИД, а не раздела ядра', () => {
    const costs = resolveWorkspace(BOTH, [], 'module:costs').workspace;
    const id = resolveWorkspace(BOTH, [], 'module:id').workspace;

    expect(outsideItem('/document-sets/c1/sets/s1', workNav, costs, BOTH)?.label).toBe('Комплект документов');
    expect(outsideItem('/document-sets/c1/sets/s1/datasets', workNav, costs, BOTH)?.label).toBe('Комплект документов');
    expect(outsideItem('/document-sets/c1/sets/s1', workNav, id, BOTH)).toBeNull();
    // Сама стройка и её раздел — ядро, их объявил модуль счетов.
    expect(outsideItem('/document-sets/c1/sections/r1', workNav, costs, BOTH)).toBeNull();
  });

  /** Панель «Наборы данных» стройки — раздел ядра `datasets`, которого счета не объявили (ревью PR #1330). */
  it('панель наборов данных стройки вне пространства счетов — с плашкой', () => {
    const costs = resolveWorkspace(BOTH, [], 'module:costs').workspace;
    expect(outsideItem('/document-sets/c1/datasets', workNav, costs, BOTH)?.label).toBe('Наборы данных стройки');
    expect(outsideItem('/document-sets/c1/sections/r1/datasets', workNav, costs, BOTH)?.label).toBe('Наборы данных стройки');
    expect(sectionInWorkspace('datasets', costs, BOTH)).toBe(false);
    expect(sectionInWorkspace('constructions', costs, BOTH)).toBe(true);
    expect(outsideItem('/document-sets/c1/datasets', workNav, resolveWorkspace(BOTH, [], 'module:id').workspace, BOTH)).toBeNull();
  });

  it('«Вместе с ними» называет разделы ядра отмеченных модулей, видные человеку', () => {
    expect(coreSectionsWith(['costs'], workNav, BOTH)).toEqual(['Стройки', 'Общие данные', 'Учётный период']);
    expect(coreSectionsWith([], workNav, BOTH)).toEqual([]);
    // Раздел, закрытый правом, не обещается: в меню его не будет.
    expect(coreSectionsWith(['costs'], workNav.filter(i => i.label !== 'Учётный период'), BOTH))
      .toEqual(['Стройки', 'Общие данные']);
  });

  it('сохранение заперто с причиной: имя, повтор имени, модули', () => {
    expect(ownProblem('  ', ['costs'], [], null)).toBe('Назовите пространство');
    expect(ownProblem('снабжение ', ['costs'], [SUPPLY], null)).toBe('Пространство с таким именем уже есть');
    // Своё имя при правке — не повтор.
    expect(ownProblem('Снабжение', ['costs'], [SUPPLY], 'w1')).toBeNull();
    expect(ownProblem('Склад', [], [SUPPLY], null)).toBe('Отметьте хотя бы один модуль');
    expect(ownProblem('все доступные модули', ['costs'], [], null)).toBe('Так называется системное пространство');
    // И название модуля — у него своё системное пространство (ревью PR #1330).
    const reserved = systemWorkspaces(BOTH).map(w => w.name);
    expect(ownProblem('Счета и накладные ', ['costs', 'id'], [], null, reserved)).toBe('Так называется системное пространство');
  });

  /** Пределы сервера — причиной под кнопкой, а не общим отказом 400 после нажатия (ревью PR #1330). */
  it('пределы сервера запирают сохранение с причиной', () => {
    const many: OwnWorkspace[] = Array.from({ length: OWN_MAX }, (_, i) => ({ id: `w${i}`, name: `П${i}`, modules: ['costs'] }));
    expect(ownProblem('Ещё одно', ['costs'], many, null)).toBe(`Своих пространств уже ${OWN_MAX} — удалите ненужное`);
    // Правка существующего при полном списке — можно.
    expect(ownProblem('П0 новое', ['costs'], many, 'w0')).toBeNull();

    const codes = Array.from({ length: 20 }, (_, i) => `module_${i}`.padEnd(12, 'x'));
    const fat: OwnWorkspace[] = Array.from({ length: 45 }, (_, i) =>
      ({ id: `w${i}`.padEnd(40, '0'), name: `Пространство ${i}`, modules: codes }));
    expect(ownProblem('Последнее', ['costs'], fat, null)).toBe('Пространства не помещаются в настройку — удалите ненужное');
  });

  it('идентификатор своего пространства проходит форму сервера', () => {
    const a = newWorkspaceId();
    expect(a).toMatch(/^[A-Za-z0-9-]{1,40}$/);
    expect(newWorkspaceId()).not.toBe(a);
  });

  it('негодная настройка списка — пустой список, а не падение шапки', () => {
    expect(parseOwn(undefined)).toEqual([]);
    expect(parseOwn('не json')).toEqual([]);
    expect(parseOwn('{"id":"w1"}')).toEqual([]);
    expect(parseOwn(JSON.stringify([SUPPLY, { id: 1 }]))).toEqual([SUPPLY]);
  });
});
