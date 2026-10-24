import { describe, expect, it } from 'vitest';
import type { CostsArticle } from '@/shared/api/articles';
import type { CostsConstruction } from '@/shared/api/invoices';
import type { FilterCondition } from '@/shared/api/types';
import { listMatches } from '@/shared/filter/conditionMatch';
import { conditionNotice, missActions } from '@/shared/filter/conditionNotice';
import type { ColumnKnown } from '@/shared/filter/rowFilterModel';
import { matchOptions } from '@/shared/ui/suggestMatch';
import { GROUP_OUTSIDE, objectKnown, sectionKnown, TWIN_NOTE } from './registryKnown';
import cases from './placeLabelCases.json';

/** Перечни «Объекта» и «Раздела» реестра счетов для подсказки в отборе (COST-20.1). */

const site = (id: string, name: string, sections: string[] = []): CostsConstruction =>
  ({ id, name, sections: sections.map((s, i) => ({ id: `${id}-${i}`, name: s })) });
const article = (name: string, archived = false): CostsArticle => ({ id: `a-${name}`, name, archived });

const SITES = [
  site('k', 'Комарова 36', ['кровля', '4 эт.']),
  site('l', 'Ленина 5', ['4 эт.']),
  site('ka', 'Комарова 36А', ['4 эт.']),
  site('lo', 'Лесная 1'),
];
const ARTICLES = [article('Склад', true), article('Офис'), article('Ленточный транспортёр')];

const shown = (known: ColumnKnown, q: string) =>
  matchOptions(known.items, q).map(i => [i.label ?? i.value, i.group ?? null]);

describe('перечень «Объекта»', () => {
  it('избранные первыми под включённым фильтром, остальные — ниже своей группой', () => {
    expect(shown(objectKnown(SITES, ARTICLES, new Set(['l'])), 'л')).toEqual([
      ['Ленина 5', 'Избранные стройки'],
      ['Лесная 1', 'Остальные стройки'],
      ['Ленточный транспортёр', GROUP_OUTSIDE],
    ]);
  });

  it('фильтр выключен — стройки по алфавиту без подписей «Избранные» / «Остальные»', () => {
    const known = objectKnown(SITES, ARTICLES, null);
    expect(known.items.slice(0, 4).map(i => [i.value, i.group])).toEqual([
      ['Комарова 36', undefined], ['Комарова 36А', undefined], ['Ленина 5', undefined], ['Лесная 1', undefined],
    ]);
  });

  it('«Вне строек»: действующие статьи, затем архивные со словом «в архиве»; выбор подставляет название', () => {
    const known = objectKnown(SITES, ARTICLES, null);
    expect(known.items.filter(i => i.group === GROUP_OUTSIDE).map(i => i.label ?? i.value))
      .toEqual(['Ленточный транспортёр', 'Офис', 'Склад — в архиве']);
    expect(matchOptions(known.items, 'скл')).toEqual([{ value: 'Склад', label: 'Склад — в архиве', group: GROUP_OUTSIDE }]);
  });

  it('«объект удалён» — последним, и такое значение не помечается', () => {
    const known = objectKnown(SITES, ARTICLES, null);
    expect(known.items.at(-1)).toEqual({ value: cases.words.lostObject });
    expect(matchOptions(known.items, 'объ').at(-1)?.value).toBe('объект удалён');
    const cond: FilterCondition = { type: 'condition', column: 'Объект', op: 'eq', value: 'объект удалён' };
    expect(conditionNotice(cond, { kind: 'list', known })).toBeNull();
  });

  it('стройка и статья с одним названием — один пункт стройки с припиской', () => {
    const known = objectKnown([site('s', 'Склад')], [article('Склад')], null);
    expect(known.items.filter(i => i.value === 'Склад')).toEqual([{ value: 'Склад', group: undefined, note: TWIN_NOTE }]);
  });

  it('фильтр перечень не урезает', () => {
    expect(objectKnown(SITES, ARTICLES, new Set(['l'])).items).toHaveLength(SITES.length + ARTICLES.length + 1);
  });

  it('статьи не пришли — подсказка предлагает стройки, перечень неполон', () => {
    const known = objectKnown(SITES, undefined, null);
    expect(known.complete).toBe(false);
    expect(matchOptions(known.items, 'ком').map(i => i.value)).toEqual(['Комарова 36', 'Комарова 36А']);
    const cond: FilterCondition = { type: 'condition', column: 'Объект', op: 'eq', value: 'Склад' };
    expect(conditionNotice(cond, { kind: 'list', known })).toBeNull();
  });

  it('стройки ещё не пришли — перечень пуст и неполон', () => {
    expect(objectKnown(undefined, ARTICLES, null).complete).toBe(false);
  });
});

describe('перечень «Раздела»', () => {
  it('подписи — по общему набору случаев с сервером', () => {
    const known = sectionKnown([site('x', cases.sections[0].site, [cases.sections[0].section])], null);
    expect(known.items.map(i => i.value)).toEqual([
      cases.sections[0].label, `${cases.sections[0].site} / ${cases.words.noSection}`, cases.words.lostSection,
    ]);
  });

  it('«4 эт» — разделы всех строек, где он есть, избранные стройки выше', () => {
    expect(shown(sectionKnown(SITES, new Set(['l'])), '4 эт')).toEqual([
      ['Ленина 5 / 4 эт.', 'Избранные стройки'],
      ['Комарова 36 / 4 эт.', 'Остальные стройки'],
      ['Комарова 36А / 4 эт.', 'Остальные стройки'],
    ]);
  });

  it('разделы стройки по алфавиту, «без раздела» — последним среди них', () => {
    const values = sectionKnown(SITES, null).items.map(i => i.value);
    expect(values.slice(0, 3)).toEqual(['Комарова 36 / 4 эт.', 'Комарова 36 / кровля', 'Комарова 36 / без раздела']);
    expect(values.at(-1)).toBe('раздел удалён');
  });

  it('статей в перечне нет', () => {
    expect(sectionKnown([site('s', 'Склад')], null).items.map(i => i.value)).toEqual(['Склад / без раздела', 'раздел удалён']);
  });
});

describe('голое название стройки в «Разделе»', () => {
  const known = sectionKnown(SITES, null);
  const cond: FilterCondition = { type: 'condition', column: 'Раздел', op: 'eq', value: 'Комарова 36' };
  const notice = conditionNotice(cond, { kind: 'list', known });
  const miss = notice?.kind === 'values' ? notice.misses[0] : undefined;
  const actions = miss ? missActions(cond, miss) : [];

  it('пояснение и два действия, без одной замены', () => {
    expect(miss?.explain?.text).toBe('это стройка, а не раздел — разделы пишутся «Комарова 36 / …»');
    expect(miss?.replace).toBeUndefined();
    expect(actions.map(a => a.label)).toEqual(['Все разделы стройки', 'Без раздела']);
  });

  it('«Все разделы стройки» — «начинается с Комарова 36 /» и не ловит «Комарова 36А / …»', () => {
    const all = actions[0].next;
    expect([all.op, all.value]).toEqual(['starts_with', 'Комарова 36 /']);
    expect(listMatches(all.op, [all.value!], ['Комарова 36 / 4 эт.'])).toBe(true);
    expect(listMatches(all.op, [all.value!], ['Комарова 36А / 4 эт.'])).toBe(false);
    expect(conditionNotice(all, { kind: 'list', known })).toBeNull();
  });

  it('«Без раздела» — «равно Комарова 36 / без раздела»', () => {
    const none = actions[1].next;
    expect([none.op, none.value]).toEqual(['eq', 'Комарова 36 / без раздела']);
    expect(conditionNotice(none, { kind: 'list', known })).toBeNull();
  });
});
