import { describe, expect, it } from 'vitest';
import type { FilterGroup } from '@/shared/api/types';
import type { CostLine } from '@/shared/api/siteCosts';
import { filterState, isVisible } from '@/shared/siteFilter/rule';
import { REGISTRY, foldSites, foldedText, invoicesText, siteCostsLinks } from './siteCosts';

/** Отбор из ссылки — тем же путём, каким его прочтёт реестр: из фрагмента адреса. */
function filterOf(link: string): FilterGroup {
  const [path, hash] = link.split('#');
  expect(path).toBe(REGISTRY);
  return JSON.parse(new URLSearchParams(hash).get('filter')!) as FilterGroup;
}

const all = { months: ['09.2026', '10.2026'], site: null };
const site = { months: ['10.2026'], site: { id: '1', name: 'Комарова 36', invoices: 1, amount: 1, linked: true } };
const NOT_REJECTED = { type: 'condition', column: 'Состояние', op: 'neq', value: 'Отклонён' };

describe('ссылки отчёта «Затраты по стройке» в реестр', () => {
  it('итог всех строек — учётные месяцы периода и «не отклонён»', () => {
    expect(filterOf(siteCostsLinks.total(all))).toEqual({
      type: 'group', logic: 'and',
      children: [{ type: 'condition', column: 'УчётныйПериод', op: 'in', values: ['09.2026', '10.2026'] }, NOT_REJECTED],
    });
  });

  it('на экране стройки каждая ссылка несёт объект — под ним «Сумма» реестра становится долей', () => {
    const object = { type: 'condition', column: 'ОбъектыРазноски', op: 'eq', value: 'Комарова 36' };
    for (const link of [siteCostsLinks.total(site), siteCostsLinks.supplier(site, 'ЭТМ'), siteCostsLinks.unmatched(site),
      siteCostsLinks.payable(site)])
      expect(filterOf(link).children[0]).toEqual(object);
  });

  it('контрагент без названия — условие «пусто», а не название «поставщик не указан»', () => {
    expect(filterOf(siteCostsLinks.supplier(site, null)).children)
      .toContainEqual({ type: 'condition', column: 'Поставщик', op: 'is_empty' });
    expect(filterOf(siteCostsLinks.supplier(site, 'ЭТМ')).children)
      .toContainEqual({ type: 'condition', column: 'Поставщик', op: 'eq', value: 'ЭТМ' });
  });

  it('«к оплате» периода не называет: неоплаченный счёт не принадлежит ни одному', () => {
    const columns = filterOf(siteCostsLinks.payable(all)).children.map(c => 'column' in c && c.column);
    expect(columns).toEqual(['СостояниеОплаты', 'Состояние']);
  });

  it('отклонённых нет ни в одной ссылке', () => {
    for (const link of [siteCostsLinks.total(all), siteCostsLinks.object(all, 'Склад'), siteCostsLinks.unmatched(all),
      siteCostsLinks.payable(all), siteCostsLinks.supplier(site, null)])
      expect(filterOf(link).children).toContainEqual(NOT_REJECTED);
  });
});

describe('число счетов словами', () => {
  it.each([[1, '1 счёт'], [3, '3 счёта'], [5, '5 счетов'], [11, '11 счетов'], [21, '21 счёт'], [104, '104 счёта']])(
    '%i', (count, text) => expect(invoicesText(count)).toBe(text));
});

// Срез по разделам (G5b, issue #1198).
describe('ссылка раздела стройки', () => {
  it('несёт объект, раздел так, как его зовёт реестр, период и «не отклонён»', () => {
    expect(filterOf(siteCostsLinks.section(site, 'Комарова 36 / 4 эт.')).children).toEqual([
      { type: 'condition', column: 'ОбъектыРазноски', op: 'eq', value: 'Комарова 36' },
      { type: 'condition', column: 'УчётныйПериод', op: 'in', values: ['10.2026'] },
      { type: 'condition', column: 'РазделыРазноски', op: 'eq', value: 'Комарова 36 / 4 эт.' },
      NOT_REJECTED,
    ]);
  });
});

// Свёрнутая строка неизбранных строек (issue #1223): фильтр прячет строки, но чисел не меняет.
describe('стройки отчёта под фильтром «Избранные стройки»', () => {
  const line = (id: string, amount: number): CostLine => ({ id, name: id, invoices: 1, amount, linked: true });
  // Дроби — нарочно: 0,1 + 0,2 в плавающей точке не равно 0,3, а итог сервера — точное число.
  const sites = [line('a', 1000.1), line('b', 0.2), line('c', 0.1), line('gone', 50)];
  const articles = [line('склад', 7.7)];
  const unallocated = 3.3;
  // Итог отчёта — как его считает сервер: все строки вместе, в копейках.
  const total = 1061.4;
  const visible = (favorites: string[]) => {
    const state = filterState(favorites, true, new Set(['a', 'b', 'c']));
    return (id: string | null) => isVisible(id, state);
  };

  it('видимые строки и свёрнутая вместе с остальными сходятся с «Итого» — до копейки', () => {
    const { shown, folded } = foldSites(sites, visible(['a']));
    expect(shown.map(s => s.id)).toEqual(['a', 'gone']);
    expect(folded).toEqual({ count: 2, amount: 0.3 });
    const cents = [...shown, ...articles].reduce((sum, s) => sum + Math.round(s.amount * 100), 0)
      + Math.round(folded!.amount * 100) + Math.round(unallocated * 100);
    expect(cents).toBe(Math.round(total * 100));
  });

  it('стройки, которой нет в справочнике, не сворачивает: она видна, как в списках', () => {
    expect(foldSites(sites, visible(['a'])).shown.map(s => s.id)).toContain('gone');
  });

  it('фильтр не действует или все стройки избранные — сворачивать нечего', () => {
    expect(foldSites(sites, () => true).folded).toBeNull();
    expect(foldSites(sites, visible([])).folded).toBeNull();
    expect(foldSites([line('a', 1)], visible(['a'])).folded).toBeNull();
  });

  it.each([[1, 'Скрыто фильтром: 1 стройка'], [3, 'Скрыто фильтром: 3 стройки'], [11, 'Скрыто фильтром: 11 строек']])(
    'название свёрнутой строки — %i', (count, text) => expect(foldedText(count)).toBe(text));
});
