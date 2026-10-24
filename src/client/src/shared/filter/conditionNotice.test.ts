import { describe, expect, it } from 'vitest';
import type { FilterCondition } from '@/shared/api/types';
import { conditionNotice, missActions, noticeText, type NoticeMiss } from './conditionNotice';
import type { ColumnKnown, FilterColumn } from './rowFilterModel';

/** Пометка «нет в справочнике» (UI-56): по оператору, когда молчит и что предлагает. */

const KNOWN: ColumnKnown = {
  complete: true,
  items: [{ value: 'Комарова 36' }, { value: 'Комарова 36А' }, { value: 'Берёзовая 4' }, { value: 'Склад' }],
};
const column = (known: ColumnKnown | null = KNOWN): FilterColumn => ({
  name: 'Объект', kind: 'list', known: known ?? undefined,
  operators: ['eq', 'neq', 'in', 'not_in', 'contains', 'not_contains', 'starts_with', 'ends_with', 'is_empty', 'is_not_empty'],
});
const one = (op: FilterCondition['op'], value: string): FilterCondition => ({ type: 'condition', column: 'Объект', op, value });
const list = (op: FilterCondition['op'], values: string[]): FilterCondition => ({ type: 'condition', column: 'Объект', op, values });
const missesOf = (cond: FilterCondition, col = column()): NoticeMiss[] => {
  const notice = conditionNotice(cond, col);
  return notice?.kind === 'values' ? notice.misses : [];
};
const missed = (cond: FilterCondition, col = column()) => missesOf(cond, col).map(m => m.value);

describe('conditionNotice: правило сверки', () => {
  it('регистр не важен', () => {
    expect(conditionNotice(one('eq', 'комарова 36'), column())).toBeNull();
  });

  it('ё вместо е — помечено', () => {
    expect(missed(one('eq', 'Березовая 4'))).toEqual(['Березовая 4']);
  });

  it('хвостовой пробел — помечено', () => {
    expect(missed(one('eq', 'Комарова 36 '))).toEqual(['Комарова 36 ']);
  });
});

describe('conditionNotice: по оператору', () => {
  it('из списка — помечено только значение вне перечня', () => {
    expect(missed(list('in', ['Комарова 36', 'Ленина 5']))).toEqual(['Ленина 5']);
  });

  it('не равно — тоже: условие ничего не отсекает', () => {
    expect(missed(one('neq', 'Ленина 5'))).toEqual(['Ленина 5']);
  });

  it('«содержит» без совпадений — помечено условие', () => {
    const notice = conditionNotice(one('contains', 'Комаров36'), column());
    expect(notice).toEqual({ kind: 'pattern' });
    expect(noticeText(notice!)).toBe('Ни одно название справочника не подходит');
  });

  it('«содержит», «начинается с», «заканчивается на» с совпадением — без пометки', () => {
    expect(conditionNotice(one('contains', 'марова'), column())).toBeNull();
    expect(conditionNotice(one('starts_with', 'Берёз'), column())).toBeNull();
    expect(conditionNotice(one('ends_with', '36А'), column())).toBeNull();
    expect(conditionNotice(one('not_contains', 'Ленина'), column())).toEqual({ kind: 'pattern' });
  });

  it('«пусто» и «не пусто» — без пометки', () => {
    expect(conditionNotice({ type: 'condition', column: 'Объект', op: 'is_empty' }, column())).toBeNull();
    expect(conditionNotice({ type: 'condition', column: 'Объект', op: 'is_not_empty' }, column())).toBeNull();
  });

  it('подсказка чипа называет значения', () => {
    expect(noticeText(conditionNotice(list('in', ['Ленина 5']), column())!)).toBe('«Ленина 5» — нет в справочнике');
  });
});

describe('conditionNotice: когда молчит', () => {
  it('перечень известен не целиком — «ещё не знаем» не «нет»', () => {
    expect(conditionNotice(one('eq', 'Ленина 5'), column({ ...KNOWN, complete: false }))).toBeNull();
  });

  it('у колонки нет перечня', () => {
    expect(conditionNotice(one('eq', 'Ленина 5'), column(null))).toBeNull();
  });

  it('у условия есть проблема — сначала она', () => {
    expect(conditionNotice(list('in', []), column())).toBeNull();
    expect(conditionNotice(one('gt', 'Ленина 5'), column())).toBeNull();
  });

  it('пустое значение', () => {
    expect(conditionNotice(one('eq', ''), column())).toBeNull();
  });
});

describe('замена на похожее', () => {
  it('ё и е — «Заменить на «Берёзовая 4»», значение меняется, пометка уходит', () => {
    const cond = one('eq', 'Березовая 4');
    const [miss] = missesOf(cond);
    expect(miss.replace).toBe('Берёзовая 4');
    const [action] = missActions(cond, miss);
    expect(action.label).toBe('Заменить на «Берёзовая 4»');
    expect(conditionNotice(action.next, column())).toBeNull();
  });

  it('пробелы вокруг «/»', () => {
    const col = column({ complete: true, items: [{ value: 'Комарова 36 / 4 эт.' }] });
    expect(missesOf(one('eq', 'Комарова 36/4 эт.'), col)[0].replace).toBe('Комарова 36 / 4 эт.');
  });

  it('в списке меняется одно это значение', () => {
    const cond = list('in', ['Комарова 36', 'Березовая 4']);
    const [action] = missActions(cond, missesOf(cond)[0]);
    expect(action.next.values).toEqual(['Комарова 36', 'Берёзовая 4']);
  });

  it('два похожих — пометка есть, замены нет', () => {
    const col = column({ complete: true, items: [{ value: 'Берёзовая 4' }, { value: 'Березовая 4' }] });
    const cond = one('eq', 'Березовая  4');
    const [miss] = missesOf(cond, col);
    expect(miss.value).toBe('Березовая  4');
    expect(missActions(cond, miss)).toEqual([]);
  });

  it('переименование — не мягкое совпадение: замены нет', () => {
    const col = column({ complete: true, items: [{ value: 'Комарова 36А' }] });
    expect(missesOf(list('in', ['Комарова 36']), col)).toEqual([{ value: 'Комарова 36', replace: undefined }]);
  });
});

describe('пояснение вместо замены', () => {
  const sections = column({
    complete: true,
    items: [{ value: 'Комарова 36 / 4 эт.' }, { value: 'Комарова 36 / без раздела' }],
    explain: [{
      value: 'Комарова 36', text: 'это стройка, а не раздел',
      actions: [
        { label: 'Все разделы стройки', op: 'starts_with', value: 'Комарова 36 /' },
        { label: 'Без раздела', op: 'eq', value: 'Комарова 36 / без раздела' },
      ],
    }],
  });

  it('у «равно» — оба действия, каждое со своим оператором', () => {
    const cond = one('eq', 'Комарова 36');
    const [miss] = missesOf(cond, sections);
    expect(miss.explain?.text).toBe('это стройка, а не раздел');
    expect(missActions(cond, miss).map(a => [a.label, a.next.op, a.next.value])).toEqual([
      ['Все разделы стройки', 'starts_with', 'Комарова 36 /'],
      ['Без раздела', 'eq', 'Комарова 36 / без раздела'],
    ]);
  });

  it('в списке — только то, что меняет одно значение', () => {
    const cond = list('in', ['Комарова 36', 'Комарова 36 / 4 эт.']);
    const actions = missActions(cond, missesOf(cond, sections)[0]);
    expect(actions.map(a => a.label)).toEqual(['Без раздела']);
    expect(actions[0].next.values).toEqual(['Комарова 36 / без раздела', 'Комарова 36 / 4 эт.']);
  });
});
