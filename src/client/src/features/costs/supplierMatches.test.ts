import { describe, expect, it } from 'vitest';
import type { InvoiceLineView } from '@/shared/api/invoices';
import { emptyDraft, toDrafts, toPayload, type LineDraft } from './invoiceLines';
import {
  applyOffer, cancelMatch, lineKey, matchTrouble, memoryFate, memoryToast, pending, pickByHand, usable,
  type MatchOffer,
} from './supplierMatches';

const offer: MatchOffer = {
  matchId: 'm-1', by: 'name', source: 'Кабель силовой 3х2,5', nomenclatureId: 'n-1',
  nomenclatureName: 'Кабель ВВГнг-LS 3х2,5', nomenclatureType: 'Номенклатура', issue: null,
  rememberedAt: '2026-10-09T10:00:00Z', rememberedBy: 'Снабженец',
};

function draft(patch: Partial<LineDraft> = {}): LineDraft {
  return { ...emptyDraft(), supplierText: 'Кабель силовой 3х2,5', ...patch };
}

function saved(patch: Partial<InvoiceLineView> = {}): InvoiceLineView {
  return {
    id: 'l-1', ordinal: 1, nomenclatureId: null, nomenclatureName: null, nomenclatureLost: false,
    supplierText: 'Кабель силовой 3х2,5', supplierCode: null, unit: null, quantity: null, price: null,
    vatRate: null, vatAmount: null, amount: null, note: null,
    allocation: {} as InvoiceLineView['allocation'], ...patch,
  };
}

describe('ключ строки', () => {
  it('артикул старше наименования', () => {
    expect(lineKey('RZ-2W', 'Розетка')).toEqual({ by: 'code', value: 'rz-2w' });
    expect(lineKey('  ', 'Розетка')).toEqual({ by: 'name', value: 'розетка' });
  });

  it('регистр, «ё» и пробелы строку не меняют, а запятая и точка — меняют', () => {
    expect(lineKey(null, '  Счётчик   ТРЁХФАЗНЫЙ ')).toEqual(lineKey(null, 'счетчик трехфазный'));
    expect(lineKey(null, 'Кабель 3х2,5')).not.toEqual(lineKey(null, 'Кабель 3х2.5'));
  });

  it('узнавать не по чему — ключа нет', () => {
    expect(lineKey(null, '   ')).toBeNull();
  });
});

describe('подстановка и отмена', () => {
  it('подстановка кладёт позицию вместе с пометкой, и обе уезжают на сервер', () => {
    const placed = { ...draft(), ...applyOffer(offer) };

    expect(placed.nomenclatureId).toBe('n-1');
    expect(placed.match?.state).toBe('current');
    expect(toPayload([placed])[0]).toMatchObject({ matchedBy: 'm-1' });
  });

  it('отмена снимает позицию и пометку, а запомненное оставляет под рукой — вернуть', () => {
    const placed = { ...draft(), ...applyOffer(offer) };
    const cancelled = { ...placed, ...cancelMatch(placed) };

    expect(cancelled.nomenclatureId).toBeNull();
    expect(cancelled.matchedBy).toBeNull();
    expect(cancelled.declined).toBe(true);
    expect(toPayload([cancelled])[0]).toMatchObject({ nomenclature: null, matchedBy: null });

    // Отменённую «Подставить запомненное» не возвращает — иначе отмена жила бы до первой кнопки.
    expect(pending([cancelled], d => d.offer)).toEqual([]);
    expect({ ...cancelled, ...applyOffer(cancelled.offer!) }.matchedBy).toBe('m-1');
  });

  it('отмена у сохранённой помеченной строки тоже возвратима', () => {
    const [line] = toDrafts([saved({
      nomenclatureId: 'n-1', nomenclatureName: 'Кабель ВВГнг-LS 3х2,5',
      match: { id: 'm-1', by: 'name', state: 'current', source: 'Кабель силовой 3х2,5', rememberedAt: null, rememberedBy: null },
    })]);

    expect(line.matchedBy).toBe('m-1');
    expect(cancelMatch(line).offer).toMatchObject({ matchId: 'm-1', nomenclatureId: 'n-1' });
  });

  it('выбор руками пометку снимает', () => {
    const placed = { ...draft(), ...applyOffer(offer) };

    expect({ ...placed, ...pickByHand('n-2', 'Другая') }).toMatchObject({ nomenclatureId: 'n-2', matchedBy: null });
  });

  it('архивную и удалённую позицию не подставляем', () => {
    expect(usable({ ...offer, issue: 'archived' })).toBe(false);
    expect(usable({ ...offer, issue: 'lost' })).toBe(false);
    expect(pending([draft()], () => ({ ...offer, issue: 'archived' }))).toEqual([]);
    expect(pending([draft()], () => offer)).toHaveLength(1);
  });
});

describe('что запомнится сохранением', () => {
  const chosen = draft({ nomenclatureId: 'n-2', nomenclatureName: 'Другая' });

  it('новый выбор запомнится, а без поставщика — нет', () => {
    expect(memoryFate(chosen, undefined, true, null)).toBe('remember');
    expect(memoryFate(chosen, undefined, false, null)).toBe('none');
  });

  it('подставленное не запоминается: оно и есть запомненное', () => {
    expect(memoryFate({ ...draft(), ...applyOffer(offer) }, undefined, true, offer)).toBe('none');
  });

  it('другой выбор заменит запомненное, тот же — ничего не изменит', () => {
    expect(memoryFate(chosen, undefined, true, offer)).toBe('replace');
    expect(memoryFate(draft({ nomenclatureId: 'n-1' }), undefined, true, offer)).toBe('none');
  });

  it('строка, лежащая без изменений, — не новость', () => {
    expect(memoryFate({ ...chosen, id: 'l-1' }, saved({ nomenclatureId: 'n-2' }), true, null)).toBe('none');
    // Сменился ключ — новость, хотя позиция та же.
    expect(memoryFate({ ...chosen, id: 'l-1', supplierText: 'Кабель другой' }, saved({ nomenclatureId: 'n-2' }), true, null))
      .toBe('remember');
  });

  it('«не запоминать» уезжает на сервер отказом, а умолчание — нет', () => {
    const off = { ...chosen, remember: false };

    expect(memoryFate(off, undefined, true, null)).toBe('off');
    expect(toPayload([off])[0]).toMatchObject({ remember: false });
    expect(toPayload([chosen])[0]).not.toHaveProperty('remember');
  });

  it('узнавать строку не по чему — и запоминать нечего', () => {
    expect(memoryFate(draft({ nomenclatureId: 'n-2', supplierText: '' }), undefined, true, null)).toBe('none');
  });
});

describe('слова', () => {
  it('тост называет число запомненного и замены — и молчит, когда запоминать было нечего', () => {
    expect(memoryToast({ remembered: 3, replaced: 0 })).toBe('Строки сохранены. Запомнено соответствий: 3.');
    expect(memoryToast({ remembered: 3, replaced: 1 })).toBe('Строки сохранены. Запомнено соответствий: 3, из них заменено: 1.');
    expect(memoryToast({ remembered: 0, replaced: 0 })).toBeNull();
    expect(memoryToast(undefined)).toBeNull();
  });

  it('изменённое, забытое и чужое соответствие названы, а действующее — нет', () => {
    const match = { id: 'm', by: 'name' as const, source: null, rememberedAt: null, rememberedBy: null };

    expect(matchTrouble({ ...match, state: 'current' })).toBeNull();
    expect(matchTrouble({ ...match, state: 'changed' })).toContain('другую позицию');
    expect(matchTrouble({ ...match, state: 'gone' })).toContain('забыли');
    expect(matchTrouble({ ...match, state: 'foreign' })).toContain('другого поставщика');
  });
});
