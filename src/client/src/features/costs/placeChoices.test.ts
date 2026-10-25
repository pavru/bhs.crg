import { describe, expect, it } from 'vitest';
import { siteChoices } from '@/shared/siteFilter/siteChoices';
import type { HeaderObject } from './matrix';
import { FOUND_SECTIONS, WHOLE_SITE, fieldText, firstStep, searchStep, sectionStep, statusOf, type PlaceList } from './placeChoices';
import { LOADING_PLACE, OUTSIDE_SITES, type Place, type Places } from './places';
import { LOST } from './lostReferences';

const K36 = { id: 'k', name: 'Комарова 36', sections: [{ id: 's5', name: '5 эт.' }, { id: 's10', name: '10 эт.' }, { id: 's4', name: '4 эт.' }] };
const LENINA = { id: 'l', name: 'Ленина 5', sections: [] };
const SCHOOL = { id: 'w', name: 'Школа №7', sections: [{ id: 'w1', name: 'Спортзал' }] };
const PLACES: Places = {
  sites: [K36, LENINA, SCHOOL],
  articles: [{ id: 'a', name: 'Склад', archived: false }, { id: 'x', name: 'Офис', archived: true }],
};

const at = (construction: string | null, section: string | null = null, article: string | null = null): Place =>
  ({ construction, section, article });
const labels = (list: PlaceList) => list.groups.map(g => [g.label, g.rows.map(r => r.label)]);
const off = { active: false, favorites: new Set<string>() };
const favorites = { active: true, favorites: new Set(['k']) };

describe('шаг 1: стройки и статьи', () => {
  it('без фильтра — «Стройки», затем «Вне строек»; у стройки с разделами — их число', () => {
    const list = firstStep(PLACES, siteChoices(PLACES.sites!, null, off, false), null);
    expect(labels(list)).toEqual([['Стройки', ['Комарова 36', 'Ленина 5', 'Школа №7']], [OUTSIDE_SITES, ['Склад']]]);
    expect(list.groups[0].rows.map(r => r.sections)).toEqual([3, 0, 1]);
    expect(list.reveal).toBeNull();
  });

  it('у статьи «›» нет: разделов у неё не бывает', () => {
    const list = firstStep(PLACES, siteChoices(PLACES.sites!, null, off, false), null);
    expect(list.groups[1].rows.every(r => r.sections === 0)).toBe(true);
  });

  it('под фильтром — избранные, стоящая неизбранная в «Не в избранном», выход последним', () => {
    const list = firstStep(PLACES, siteChoices(PLACES.sites!, 'l', favorites, false), at('l'));
    expect(labels(list)).toEqual([
      ['Избранные стройки', ['Комарова 36']], ['Не в избранном', ['Ленина 5']], [OUTSIDE_SITES, ['Склад']],
    ]);
    expect(list.reveal).toBe('Показать все стройки (ещё 1)…');
  });

  it('стоящая архивная статья остаётся пунктом, другая архивная — нет', () => {
    const list = firstStep(PLACES, siteChoices(PLACES.sites!, null, off, false), at(null, null, 'x'));
    expect(list.groups.at(-1)!.rows.map(r => r.label)).toEqual(['Склад', 'Офис — в архиве']);
  });

  it('стоящая удалённая стройка — пунктом с честной подписью; справочник не пришёл — «…»', () => {
    const lost = firstStep(PLACES, siteChoices(PLACES.sites!, 'gone', off, false), at('gone'));
    expect(lost.lost?.label).toBe(LOST.construction);
    const loading = firstStep({ sites: undefined, articles: undefined }, siteChoices([], 'k', off, false), at('k', 's4'));
    expect(loading.lost?.label).toBe(LOADING_PLACE);
  });
});

describe('шаг 2: разделы стройки', () => {
  it('«вся стройка» первым, разделы — в порядке справочника, а не по алфавиту', () => {
    const list = sectionStep(K36, PLACES, null);
    expect(labels(list)).toEqual([['Комарова 36', [WHOLE_SITE, '5 эт.', '10 эт.', '4 эт.']]]);
    expect(list.groups[0].rows[0].place).toEqual(at('k'));
    expect(list.groups[0].rows[3].place).toEqual(at('k', 's4'));
  });

  it('стоящий раздел, которого у стройки нет, — «раздел удалён»', () => {
    const list = sectionStep(K36, PLACES, at('k', 'старый'));
    expect(list.groups[0].rows[1]).toMatchObject({ label: LOST.section, place: at('k', 'старый') });
  });

  it('раздел другой стройки — так и назван, а не «удалён»', () => {
    const list = sectionStep(K36, PLACES, at('k', 'w1'));
    expect(list.groups[0].rows[1].label).toBe(LOST.foreignSection);
  });
});

describe('поиск по обеим частям', () => {
  it('«4 эт» находит «Комарова 36 / 4 эт.»', () => {
    const list = searchStep('4 эт', PLACES, null);
    expect(labels(list)).toEqual([[FOUND_SECTIONS, ['Комарова 36 / 4 эт.']]]);
    expect(list.groups[0].rows[0].place).toEqual(at('k', 's4'));
  });

  it('слова — в любой части: «ком 10» находит раздел, «ком» — и стройку, и её разделы', () => {
    expect(labels(searchStep('ком 10', PLACES, null))).toEqual([[FOUND_SECTIONS, ['Комарова 36 / 10 эт.']]]);
    expect(labels(searchStep('ком', PLACES, null))[0]).toEqual(['Стройки', ['Комарова 36']]);
  });

  it('ищет и статьи, но не архивные', () => {
    expect(labels(searchStep('ск', PLACES, null))).toEqual([[OUTSIDE_SITES, ['Склад']]]);
    expect(searchStep('офис', PLACES, null).groups).toEqual([]);
  });
});

describe('что показывает поле', () => {
  const one = (place: Place, section: string | null | 'many'): HeaderObject =>
    ({ kind: 'one', place, section, sections: section === 'many' ? 2 : section === null ? 0 : 1, bare: section === null, complete: true });

  it('стройка, стройка с разделом, несколько разделов, несколько объектов', () => {
    expect(fieldText({ kind: 'none' }, PLACES)).toEqual({ text: '— выберите —', muted: true });
    expect(fieldText(one(at('k'), null), PLACES)).toEqual({ text: 'Комарова 36', muted: false });
    expect(fieldText(one(at('k'), 's4'), PLACES)).toEqual({ text: 'Комарова 36 / 4 эт.', muted: false });
    expect(fieldText(one(at('k'), 'many'), PLACES)).toEqual({ text: 'Комарова 36 · несколько разделов', muted: true });
    expect(fieldText({ kind: 'many', count: 2 }, PLACES)).toEqual({ text: '— несколько объектов —', muted: true });
  });

  it('удалённый раздел — «раздел удалён»; справочник не пришёл — «…», а не «удалён»', () => {
    expect(fieldText(one(at('k'), 'старый'), PLACES).text).toBe(`Комарова 36 / ${LOST.section}`);
    expect(fieldText(one(at('k'), 's4'), { sites: undefined, articles: undefined }).text).toBe(LOADING_PLACE);
  });
});

describe('состояние разноски словами', () => {
  const many = (sections: number, bare: boolean, complete = true): HeaderObject =>
    ({ kind: 'one', place: at('k'), section: 'many', sections, bare, complete });

  it('несколько разделов — спокойно, числом разделов; часть без раздела названа отдельно', () => {
    expect(statusOf(many(3, false))).toEqual({ status: 'разнесён на 3 раздела', calm: true });
    expect(statusOf(many(1, true))).toEqual({ status: 'разнесён на 1 раздел и без раздела', calm: true });
  });

  it('не полностью и несколько объектов — тоном предупреждения', () => {
    expect(statusOf(many(2, false, false))).toEqual({ status: 'разнесено не полностью', calm: false });
    expect(statusOf({ kind: 'many', count: 2 })).toEqual({ status: 'разнесён на 2 объекта', calm: false });
    expect(statusOf({ kind: 'none' })).toEqual({ status: 'не разнесён', calm: false });
  });
});
