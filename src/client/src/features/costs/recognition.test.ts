import { describe, it, expect } from 'vitest';
import type { InvoiceListItem, InvoiceView } from '@/shared/api/invoices';
import type { InvoiceParty, InvoiceRecognition } from '@/shared/api/invoiceRecognition';
import { K } from './invoiceFields';
import {
  explainedByRecognition, failureNote, fieldOffer, inheritsFrom, partyLine, partyWhy, pickOrder, rowScan, unusedLinesNote,
} from './recognition';

/** Распознавание скана на экране счетов (issue #1077): отказ назван, «не знаем» не выглядит как «нет». */

const item = (recognition: InvoiceListItem['recognition']) => ({ recognition }) as InvoiceListItem;

const recognition = (patch: Partial<InvoiceRecognition> = {}): InvoiceRecognition => ({
  state: 'done', reason: null, error: null, engine: null, progress: null, values: null, offers: null,
  lines: null, notes: [], startedAt: null, finishedAt: null, canStart: true, whyNot: null, parties: null, byFormerImage: false, ...patch,
});

const view = (requisites: Record<string, unknown>, unconfirmed: string[] = []) =>
  ({ requisites, unconfirmed }) as unknown as InvoiceView;

const party = (patch: Partial<InvoiceParty>): InvoiceParty => ({
  state: 'absent', name: 'ООО «Ромашка»', taxId: '7701234567', why: 'почему', candidates: [], unreadable: [],
  match: null, ...patch,
});

const noNames = () => null;

describe('строка списка', () => {
  it('у каждого состояния свои слова, а отказ назван причиной', () => {
    expect(rowScan(item({ state: 'running', reason: null }))).toMatchObject({ text: 'распознаётся…', running: true });
    expect(rowScan(item({ state: 'failed', reason: 'NotConfigured' })))
      .toMatchObject({ text: 'не распознан — распознавание не настроено', tone: 'danger' });
    expect(rowScan(item({ state: 'done', reason: null }))).toMatchObject({ text: 'строки не прочитаны', tone: 'warning' });
    expect(rowScan(item({ state: 'none', reason: null }))).toMatchObject({ text: 'не распознавался' });
  });

  it('без пометки строка молчит, а незнакомое состояние — нет', () => {
    expect(rowScan(item(null))).toBeNull();
    expect(rowScan(item({ state: 'новое' as never, reason: null }))?.text).toBe('файл требует внимания');
  });
});

describe('полоса отказа', () => {
  it('«не настроено» — тихо, остальное — отказом с текстом сервера', () => {
    expect(failureNote(recognition({ state: 'failed', reason: 'NotConfigured' })).quiet).toBe(true);
    const note = failureNote(recognition({ state: 'failed', reason: 'Unavailable', error: 'Превышено время ожидания.' }));
    expect(note.quiet).toBe(false);
    expect(note.text).toContain('Превышено время ожидания.');
  });

  it('незнакомая причина всё равно отказ', () => {
    expect(failureNote(recognition({ state: 'failed', reason: 'Новая', error: 'Что-то.' })))
      .toEqual({ text: 'Файл не распознан. Что-то.', quiet: false });
  });
});

describe('предложение под полем', () => {
  it('занятое строковое поле: прочитанное предлагается и берётся как есть', () => {
    const offer = fieldOffer(K.number, view({ [K.number]: 'СЧ-1' }), {}, recognition({ offers: { [K.number]: 'СЧ-417' } }), noNames);
    expect(offer).toEqual({ text: '«СЧ-417»', take: 'СЧ-417' });
  });

  it('совпало с полем — предлагать нечего', () => {
    expect(fieldOffer(K.number, view({ [K.number]: 'СЧ-417' }), {}, recognition({ offers: { [K.number]: 'СЧ-417' } }), noNames))
      .toBeNull();
  });

  it('дату и сумму клиент не разбирает: показано, «Взять» нет', () => {
    const offer = fieldOffer(K.total, view({ [K.total]: 100 }), {}, recognition({ offers: { [K.total]: '1.234' } }), noNames);
    expect(offer).toEqual({ text: '«1.234»' });
  });

  it('сторона: предлагается найденная запись, а не текст', () => {
    const offers = { [K.supplier]: { text: 'ООО «Ромашка», ИНН 7701234567', entryId: 'b' } };
    const stored = view({ [K.supplier]: { $ref: 'catalog', entryId: 'a' } });
    expect(fieldOffer(K.supplier, stored, {}, recognition({ offers }), id => (id === 'b' ? 'Ромашка' : null)))
      .toMatchObject({ text: 'Ромашка', take: { $ref: 'catalog', entryId: 'b' } });
    // Её уже выбрали — предложение уходит.
    expect(fieldOffer(K.supplier, stored, { [K.supplier]: { $ref: 'catalog', entryId: 'b' } }, recognition({ offers }), noNames))
      .toBeNull();
  });

  it('набрано поверх распознанного: набранное на экране, прочитанное предлагается', () => {
    const stored = view({ [K.number]: 'СЧ-417' }, [K.number]);
    expect(fieldOffer(K.number, stored, { [K.number]: 'моё' }, recognition(), noNames))
      .toEqual({ text: '«СЧ-417»', take: 'СЧ-417' });
    // Без правки — обычное распознанное поле с меткой, предлагать нечего.
    expect(fieldOffer(K.number, stored, {}, recognition(), noNames)).toBeNull();
  });

  it('предложение сервера по стороне уступает нынешнему сопоставлению', () => {
    // Запись из offers сохранена на момент распознавания; с тех пор её могли убрать в архив. Когда
    // стороны в ответе есть, найденную предлагает строка стороны, а не сохранённое предложение.
    const offers = { [K.supplier]: { text: 'ООО «Ромашка», ИНН 7701234567', entryId: 'старая' } };
    const stored = view({ [K.supplier]: { $ref: 'catalog', entryId: 'a' } });
    expect(fieldOffer(K.supplier, stored, {}, recognition({ offers, parties: { supplier: null, payer: null } }), noNames))
      .toBeNull();
  });

  it('набранное поверх даты и суммы показано видом формы, а не хранения', () => {
    const stored = view({ [K.date]: '2026-10-01', [K.total]: 1234.5 }, [K.date, K.total]);
    expect(fieldOffer(K.date, stored, { [K.date]: '2026-10-02' }, recognition(), noNames)?.text).toBe('01.10.2026');
    expect(fieldOffer(K.total, stored, { [K.total]: '1' }, recognition(), noNames)?.text).not.toContain('1234.5');
  });

  it('«в файле» — только про нынешний скан: без исхода «прочитано» предложений нет', () => {
    const stored = view({ [K.number]: 'СЧ-417' }, [K.number]);
    for (const state of ['none', 'failed', 'running'] as const)
      expect(fieldOffer(K.number, stored, { [K.number]: 'моё' }, recognition({ state }), noNames)).toBeNull();
    expect(fieldOffer(K.number, stored, { [K.number]: 'моё' }, undefined, noNames)).toBeNull();
  });

  it('пока распознавание не кончилось, предложений сервера нет', () => {
    expect(fieldOffer(K.number, view({ [K.number]: 'СЧ-1' }), {}, recognition({ state: 'running', offers: { [K.number]: 'x' } }), noNames))
      .toBeNull();
  });
});

describe('что изменилось под правками — распознавание или сосед', () => {
  it('поле было пустым и легло с меткой — распознавание', () => {
    const now = view({ [K.number]: 'СЧ-417', [K.purpose]: 'x' }, [K.number]);
    expect(explainedByRecognition({ [K.number]: 'моё' }, { [K.purpose]: 'x' }, now)).toBe(true);
  });

  it('поле изменилось без метки — правка соседа, даже если пришла тем же перечитыванием', () => {
    const now = view({ [K.purpose]: 'соседское' }, []);
    expect(explainedByRecognition({ [K.purpose]: 'моё' }, { [K.purpose]: null }, now)).toBe(false);
  });

  it('поле было занято и изменилось — не распознавание: оно пишет только в пустые', () => {
    const now = view({ [K.number]: 'СЧ-2' }, [K.number]);
    expect(explainedByRecognition({ [K.number]: 'моё' }, { [K.number]: 'СЧ-1' }, now)).toBe(false);
  });

  it('изменилось то, что человек не правит, — его правок это не касается', () => {
    const now = view({ [K.number]: 'моё-прежнее', [K.purpose]: 'соседское' }, []);
    expect(explainedByRecognition({ [K.number]: 'моё' }, { [K.number]: 'моё-прежнее', [K.purpose]: null }, now)).toBe(true);
  });
});

describe('сторона', () => {
  it('завести предлагается только у «нет» и только с правом', () => {
    expect(partyLine(party({ state: 'absent' }), null, true)?.action).toBe('create');
    expect(partyLine(party({ state: 'absent' }), null, false)?.action).toBe('details');
    expect(partyWhy(party({ state: 'absent' }), false)).toContain('Права заводить организации у вас нет');
  });

  it('«не знаем» и незнакомое состояние — не «нет»: завести нельзя', () => {
    for (const state of ['unknown', 'unavailable', 'badTaxId', 'archived', 'noTaxId', 'новое'] as never[])
      expect(partyLine(party({ state }), null, true)?.action).not.toBe('create');
  });

  it('найденная предлагается, пока в поле стоит не она', () => {
    const found = party({ state: 'matched', match: 'a', candidates: [{ id: 'a', name: 'Ромашка', type: 'Организация', archived: false, inheritedFrom: null }] });
    expect(partyLine(found, null, true)).toMatchObject({ text: 'В файле: Ромашка', action: 'take' });
    expect(partyLine(found, 'a', true)).toBeNull();
  });

  it('несколько записей: выбор, пока человек не выбрал одну из них', () => {
    const several = party({
      state: 'several',
      candidates: [
        { id: 'a', name: 'А', type: 'Организация', archived: true, inheritedFrom: null },
        { id: 'b', name: 'Б', type: 'Поставщик', archived: false, inheritedFrom: 'a' },
      ],
    });
    expect(partyLine(several, null, true)).toMatchObject({ text: 'Записей с этим ИНН: 2', action: 'pick' });
    expect(partyLine(several, 'b', true)).toBeNull();
    // Архивные — в конце; роль называет основу.
    expect(pickOrder(several.candidates).map(c => c.id)).toEqual(['b', 'a']);
    expect(inheritsFrom(several.candidates[1], several.candidates)).toBe('роль: реквизиты наследует от «А»');
  });
});

describe('распознанные строки, которые не легли', () => {
  it('названы числом и путём', () => {
    expect(unusedLinesNote(recognition({ lines: [{}, {}] }))).toContain('В файле прочитаны 2 строки.');
    expect(unusedLinesNote(recognition({ lines: [] }))).toBeNull();
    expect(unusedLinesNote(undefined)).toBeNull();
  });
});
