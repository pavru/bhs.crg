import { describe, expect, it } from 'vitest';
import { GROUP_ALL, GROUP_FAVORITES, GROUP_NOT_FAVORITE, GROUP_OTHERS, revealText, siteChoices } from './siteChoices';

const SITES = [
  { id: 'a', name: 'ЖК «Север»' },
  { id: 'b', name: 'Школа №7' },
  { id: 'c', name: 'ТЦ «Меридиан»' },
  { id: 'd', name: 'Склад-2' },
];
const ON = { active: true, favorites: new Set(['a', 'b']) };
const OFF = { active: false, favorites: new Set(['a', 'b']) };

const shape = (r: ReturnType<typeof siteChoices>) => ({
  groups: r.groups.map(g => [g.label, g.sites.map(s => s.id)]),
  hidden: r.hidden,
});

describe('варианты выбора стройки под фильтром избранного (#1219)', () => {
  it('фильтр не действует — одна группа «Стройки», выхода нет', () => {
    expect(shape(siteChoices(SITES, null, OFF, false))).toEqual({ groups: [[GROUP_ALL, ['a', 'b', 'c', 'd']]], hidden: 0 });
  });

  it('действует — только избранные и выход с числом скрытых', () => {
    expect(shape(siteChoices(SITES, null, ON, false))).toEqual({ groups: [[GROUP_FAVORITES, ['a', 'b']]], hidden: 2 });
    expect(revealText(2)).toBe('Показать все стройки (ещё 2)…');
  });

  it('стоящая неизбранная стройка — своей группой и не в числе скрытых', () => {
    expect(shape(siteChoices(SITES, 'c', ON, false)))
      .toEqual({ groups: [[GROUP_FAVORITES, ['a', 'b']], [GROUP_NOT_FAVORITE, ['c']]], hidden: 1 });
  });

  it('стоящая стройка, которой нет в справочнике, — не «не в избранном»: потерю подписывает носитель', () => {
    expect(shape(siteChoices(SITES, 'gone', ON, false))).toEqual({ groups: [[GROUP_FAVORITES, ['a', 'b']]], hidden: 2 });
  });

  it('раскрыли — избранные и остальные, выхода больше нет', () => {
    expect(shape(siteChoices(SITES, 'c', ON, true)))
      .toEqual({ groups: [[GROUP_FAVORITES, ['a', 'b']], [GROUP_OTHERS, ['c', 'd']]], hidden: 0 });
  });

  it('поиск идёт по всем стройкам, найденное вне избранного — отдельной группой', () => {
    expect(shape(siteChoices(SITES, null, ON, false, 'С')))
      .toEqual({ groups: [[GROUP_FAVORITES, ['a']], [GROUP_OTHERS, ['d']]], hidden: 0 });
  });

  it('все неизбранные — это стоящая: выхода нет, прятать нечего', () => {
    const two = SITES.slice(0, 3);
    expect(shape(siteChoices(two, 'c', ON, false)).hidden).toBe(0);
  });
});
