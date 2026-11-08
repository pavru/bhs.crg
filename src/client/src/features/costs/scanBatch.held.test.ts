import { beforeEach, describe, expect, it } from 'vitest';
import type { InvoiceSameFile } from '@/shared/api/invoices';
import type { SameFileAnswer } from '@/shared/api/invoiceSameFile';
import {
  acceptHeld, batchTally, batchTitle, currentBatch, holdFile, releaseHeld, resetBatchForTests, retryRejected, startBatch,
  type BatchPort,
} from './scanBatch';
import { checkDrop, heldWords, lostDraft, twinOpenLabel, twinTitle } from './scanIntake';

/**
 * Повтор до выгрузки в пакете (costs-invoice-screen-14, D3, D5): проверки → отпечатки → вопрос → решение →
 * отправка. Повторы придерживаются, а не заводятся; отказ вопроса — «не проверено», а не остановка.
 */

const pdf = (name: string, content = name) => new File([content], name, { type: 'application/pdf' });
/** Отпечаток в тестах — по содержимому: «тот же файл под другим именем» — то же содержимое. */
const printOf = async (file: File) => (await file.text()).padEnd(64, '0').slice(0, 64);

const twin = (id: string, over: Partial<InvoiceSameFile> = {}): InvoiceSameFile => ({
  id, number: id, issuedOn: '2026-10-22', total: 100, scanFileName: `${id}.pdf`, createdAt: '2026-10-22T06:14:00Z',
  supplierId: null, supplierName: 'ООО «ЭлектроКомплект»', supplierArchived: false, state: 'Черновик', payment: 'Не оплачен',
  createdByName: 'Потоцкий А.', mine: false, ...over,
});

interface Calls { sent: { name: string; acceptRepeat: boolean }[]; asked: string[][] }

function port(twins: Record<string, InvoiceSameFile[]>, calls: Calls, opts: {
  ask?: (prints: string[]) => Promise<SameFileAnswer[]>;
  send?: (file: File) => Promise<string>;
} = {}): BatchPort {
  return {
    owner: 'я', kinds: undefined, refresh: () => {},
    fingerprint: printOf,
    ask: opts.ask ?? (async prints => {
      calls.asked.push(prints);
      return prints.map(fingerprint => ({ fingerprint, twins: twins[fingerprint.replace(/0+$/, '')] ?? [] }));
    }),
    send: async (file, o) => {
      calls.sent.push({ name: file.name, acceptRepeat: !!o?.acceptRepeat });
      return { id: opts.send ? await opts.send(file) : `id-${file.name}`, sameFile: [] };
    },
  };
}

async function settled() {
  for (let i = 0; i < 2000 && currentBatch()?.phase !== 'done'; i++) await new Promise(r => setTimeout(r, 0));
  return currentBatch()!;
}

let calls: Calls;
beforeEach(() => { resetBatchForTests(); calls = { sent: [], asked: [] }; });

describe('пакет с повторами — без окна', () => {
  it('10 файлов: 2 уже приложены, 1 выбран дважды → 7 отправлено, 2 придержано', async () => {
    const files = Array.from({ length: 9 }, (_, i) => pdf(`скан ${i + 1}.pdf`));
    files.push(pdf('копия скана 3.pdf', 'скан 3.pdf'));
    startBatch(files, [], port({ 'скан 2.pdf': [twin('2611')], 'скан 5.pdf': [twin('2612')] }, calls));
    const batch = await settled();

    expect(calls.sent).toHaveLength(7);
    expect(calls.sent.every(s => !s.acceptRepeat)).toBe(true);
    expect(batch.held.map(h => h.name)).toEqual(['скан 2.pdf', 'скан 5.pdf']);
    // Первым по порядку идёт «копия скана 3» — заводится она, а «скан 3» назван выбранным дважды.
    expect(batch.doubled).toEqual(['скан 3.pdf']);
    // Вопрос — один на бросок и без повторов отпечатков.
    expect(calls.asked).toHaveLength(1);
    expect(calls.asked[0]).toHaveLength(9);
    expect(batchTally(batch)).toBe('Заведено 7 · придержано 2 (тот же файл) · 1 выбран дважды');
    expect(batch.unchecked).toBe(false);
  });

  it('вопрос отвечает 503 — все отправлены, «не проверено», пакет не остановлен', async () => {
    startBatch([pdf('а.pdf'), pdf('б.pdf')], [], port({}, calls, {
      ask: async () => { throw { response: { status: 503 } }; },
    }));
    const batch = await settled();
    expect(calls.sent.map(s => s.name)).toEqual(['а.pdf', 'б.pdf']);
    expect(batch).toMatchObject({ unchecked: true, halted: null, held: [] });
  });

  it('отпечаток не посчитался — файл уходит, повтор не проверен', async () => {
    const p = port({}, calls);
    p.fingerprint = async file => { if (file.name === 'б.pdf') throw new Error('не прочитан'); return printOf(file); };
    startBatch([pdf('а.pdf'), pdf('б.pdf')], [], p);
    const batch = await settled();
    expect(calls.sent).toHaveLength(2);
    expect(batch.unchecked).toBe(true);
  });

  it('«Завести всё равно» — ровно один черновик как сознательный повтор, строка уходит', async () => {
    const p = port({ 'а.pdf': [twin('2611')] }, calls);
    startBatch([pdf('а.pdf'), pdf('б.pdf')], [], p);
    const first = await settled();
    expect(first.held).toHaveLength(1);

    acceptHeld(first.held[0].print, p);
    const batch = await settled();
    expect(calls.sent).toEqual([{ name: 'б.pdf', acceptRepeat: false }, { name: 'а.pdf', acceptRepeat: true }]);
    expect(batch.held).toEqual([]);
    expect(batch.created).toEqual(['id-б.pdf', 'id-а.pdf']);
    // Сознательный повтор строкой «уже были приложены» не называется.
    expect(batch.repeats).toEqual([]);
  });

  it('«Повторить» после «ответ не пришёл» сначала спрашивает: свой черновик найден — второго заведения нет', async () => {
    let lost = true;
    const answers: Record<string, InvoiceSameFile[]> = {};
    const p = port(answers, calls, {
      send: async file => {
        if (lost) { lost = false; throw new Error('Network Error'); }
        return `id-${file.name}`;
      },
    });
    startBatch([pdf('а.pdf')], [], p);
    const first = await settled();
    expect(first.rejected[0].lost).toBeTruthy();
    expect(calls.sent).toHaveLength(1);

    // Запрос тогда дошёл: сервер завёл черновик, и вопрос по отпечатку его теперь находит.
    answers['а.pdf'] = [twin('мой', { number: null, mine: true })];
    retryRejected(p);
    const batch = await settled();
    expect(calls.sent).toHaveLength(1);
    expect(batch.created).toEqual(['мой']);
    expect(batch.recovered).toEqual(['а.pdf']);
    expect(batch.rejected).toEqual([]);
  });

  it('чужой двойник при повторе — не «свой черновик»: файл уходит', async () => {
    expect(await lostDraft('x', [], { ask: async () => [{ fingerprint: 'x', twins: [twin('чужой')] }] })).toBeNull();
    // Известный до отправки свой двойник — не новый черновик.
    expect(await lostDraft('x', ['прежний'], {
      ask: async () => [{ fingerprint: 'x', twins: [twin('прежний', { mine: true })] }],
    })).toBeNull();
    expect(await lostDraft('x', [], { ask: async () => { throw new Error('нет связи'); } })).toBeNull();
  });
});

describe('один файл, по которому не решили', () => {
  it('держится строкой полосы; «Не заводить» её снимает вместе с пустым пакетом', async () => {
    const [held] = (await checkDrop([pdf('а.pdf')], [], port({ 'а.pdf': [twin('2611')] }, calls))).held;
    holdFile(held, 'я');
    expect(currentBatch()).toMatchObject({ phase: 'done', total: 0 });
    expect(batchTitle(currentBatch()!)).toBe('Файл не заведён: он уже приложен к счёту.');
    releaseHeld(held.print);
    expect(currentBatch()).toBeNull();
  });
});

describe('проверка броска', () => {
  it('отказы предпроверки и папки — до отпечатков, вопрос — только о годных', async () => {
    const empty = new File([], 'пустой.pdf', { type: 'application/pdf' });
    const checked = await checkDrop([pdf('а.pdf'), empty], ['Сканы'], port({}, calls));
    expect(checked.refused.map(r => r.name)).toEqual(['Сканы', 'пустой.pdf']);
    expect(calls.asked).toEqual([[await printOf(pdf('а.pdf'))]]);
  });

  it('ответ без записи о спрошенном отпечатке — «не знаем», а не «двойников нет»', async () => {
    const checked = await checkDrop([pdf('а.pdf')], [], port({}, calls, { ask: async () => [] }));
    expect(checked).toMatchObject({ unchecked: true, held: [] });
    expect(checked.send).toHaveLength(1);
  });

  it('слова двойника — по листу «Ф»', () => {
    const t = twin('2611');
    expect(twinTitle(t)).toBe('Счёт 2611 от 22.10.2026');
    expect(twinOpenLabel(t)).toBe('Открыть счёт 2611 от 22.10.2026');
    expect(heldWords([t])).toBe('уже у счёта 2611 от 22.10.2026 · ООО «ЭлектроКомплект» · черновик');
    expect(heldWords([twin('x', { number: null, mine: true, supplierName: null })]))
      .toMatch(/^уже у черновика из файла от .+ — номер не вписан · завели вы$/);
  });
});
