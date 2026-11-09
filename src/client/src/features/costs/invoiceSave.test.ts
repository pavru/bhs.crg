import { describe, expect, it, vi } from 'vitest';
import type { InvoiceView } from '@/shared/api/invoices';
import {
  LINES_NOT_READY, classify, createSaveQueue, editsAfterSave, saveModel, type SaveSides, type SaveStatus,
} from './invoiceSave';

/** Очередь записи формы счёта (costs-invoice-screen-12-autosave, D-12.1, ТЗ COST-6.5). */

const viewAt = (version: string) => ({ id: 'i1', version } as InvoiceView);

/** Обещание, которое тест разрешает сам: запись «в полёте», пока он не скажет. */
function gate<T>() {
  let open!: (value: T) => void;
  let fail!: (error: unknown) => void;
  const promise = new Promise<T>((resolve, reject) => { open = resolve; fail = reject; });
  return { promise, open, fail };
}

/** Сбой связи — так его отдаёт axios: без ответа, с `isAxiosError`. */
const netError = () => Object.assign(new Error('Network Error'), { isAxiosError: true });
const refusal = (status: number, error = 'Номер счёта занят') => ({ response: { status, data: { error } } });

function setup(sides: Partial<SaveSides> & { dirty: SaveSides['dirty'] }) {
  const states: SaveStatus['kind'][] = [];
  const reported: unknown[] = [];
  const now: SaveSides = { header: async () => viewAt('v2'), lines: async () => viewAt('v3'), ...sides };
  const queue = createSaveQueue(() => now, s => states.push(s.kind), e => reported.push(e));
  return { queue, now, states, reported };
}

describe('createSaveQueue', () => {
  it('реквизиты раньше строк, строки — с видом из ответа реквизитов', async () => {
    const calls: string[] = [];
    const { queue } = setup({
      dirty: { header: true, lines: true },
      header: async () => { calls.push('шапка'); return viewAt('v2'); },
      lines: async after => { calls.push(`строки после ${after?.version}`); return viewAt('v3'); },
    });
    queue.mark('lines');
    queue.mark('header');
    await queue.pump();
    expect(calls).toEqual(['шапка', 'строки после v2']);
    expect(queue.status.kind).toBe('idle');
  });

  it('flush помнит вид из ответа последней записи — версию для «Разобран» следом (D-12.4)', async () => {
    const { queue, now } = setup({ dirty: { header: true, lines: true } });
    now.header = async () => { now.dirty.header = false; return viewAt('v2'); };
    now.lines = async () => { now.dirty.lines = false; return viewAt('v3'); };
    await queue.flush();
    expect(queue.written?.version).toBe('v3');
    // Следующая flush, которой писать нечего, о прежней записи не говорит.
    await queue.flush();
    expect(queue.written).toBeNull();
  });

  it('одна запись в полёте: пометка во время записи ждёт её конца', async () => {
    const first = gate<InvoiceView>();
    const header = vi.fn(() => first.promise);
    const lines = vi.fn(async () => viewAt('v3'));
    const { queue, now } = setup({ dirty: { header: true, lines: false }, header, lines });
    queue.mark('header');
    const running = queue.pump();
    now.dirty.lines = true;
    queue.mark('lines');
    void queue.pump();
    expect(lines).not.toHaveBeenCalled();
    first.open(viewAt('v2'));
    await running;
    expect(lines).toHaveBeenCalledTimes(1);
    expect(lines).toHaveBeenCalledWith(viewAt('v2'));
  });

  it('правка во время записи уходит следующей записью — телом, собранным в момент отправки', async () => {
    const first = gate<InvoiceView>();
    const sent: string[] = [];
    let screen = 'количество 1';
    const lines = vi.fn(async () => {
      sent.push(screen);
      return sent.length === 1 ? first.promise : viewAt('v4');
    });
    const { queue } = setup({ dirty: { header: false, lines: true }, lines });
    queue.mark('lines');
    const running = queue.pump();
    await Promise.resolve();
    screen = 'количество 10';
    queue.mark('lines');
    first.open(viewAt('v3'));
    await running;
    expect(sent).toEqual(['количество 1', 'количество 10']);
  });

  it('строки после строк не называют прежний ответ реквизитов', async () => {
    const lines = vi.fn(async () => viewAt('v3'));
    const { queue } = setup({ dirty: { header: false, lines: true }, lines });
    queue.mark('lines');
    await queue.pump();
    expect(lines).toHaveBeenCalledWith(null);
  });

  it('отказ реквизитов строки не пишет и называет причину словами сервера', async () => {
    const lines = vi.fn(async () => viewAt('v3'));
    const { queue } = setup({
      dirty: { header: true, lines: true },
      header: async () => { throw refusal(400); },
      lines,
    });
    queue.mark('header');
    queue.mark('lines');
    await queue.pump();
    expect(lines).not.toHaveBeenCalled();
    expect(queue.status).toEqual({ kind: 'refused', text: 'Номер счёта занят' });
  });

  it('отказ держится до новой пометки: пометка — и повтор', async () => {
    let refuse = true;
    const header = vi.fn(async () => { if (refuse) throw refusal(400); return viewAt('v2'); });
    const { queue } = setup({ dirty: { header: true, lines: false }, header });
    queue.mark('header');
    await queue.pump();
    await queue.pump();
    expect(header).toHaveBeenCalledTimes(1);
    refuse = false;
    queue.mark('header');
    await queue.pump();
    expect(header).toHaveBeenCalledTimes(2);
    expect(queue.status.kind).toBe('idle');
  });

  it('сбой связи — «повторить», своих повторов нет; «Повторить» отправляет снова', async () => {
    let down = true;
    const header = vi.fn(async () => { if (down) throw netError(); return viewAt('v2'); });
    const { queue, reported } = setup({ dirty: { header: true, lines: false }, header });
    queue.mark('header');
    await queue.pump();
    expect(queue.status.kind).toBe('failed');
    expect(reported).toEqual([]);
    down = false;
    queue.retry();
    await vi.waitFor(() => expect(queue.status.kind).toBe('idle'));
    expect(header).toHaveBeenCalledTimes(2);
  });

  it('сбой сервера — «повторить» и снэкбар с «Сообщить»', async () => {
    const { queue, reported } = setup({
      dirty: { header: true, lines: false }, header: async () => { throw refusal(500, 'ошибка'); },
    });
    queue.mark('header');
    await queue.pump();
    expect(queue.status.kind).toBe('failed');
    expect(reported).toHaveLength(1);
  });

  it('помеченное, но уже чистое (перечитали) — не пишется', async () => {
    const header = vi.fn(async () => viewAt('v2'));
    const { queue } = setup({ dirty: { header: false, lines: false }, header });
    queue.mark('header');
    await queue.pump();
    expect(header).not.toHaveBeenCalled();
    expect(queue.pending).toBe(false);
  });

  it('flush дописывает всё грязное, даже без пометки, и говорит, дошло ли', async () => {
    const calls: string[] = [];
    const { queue } = setup({
      dirty: { header: true, lines: true },
      header: async () => { calls.push('шапка'); return viewAt('v2'); },
      lines: async () => { calls.push('строки'); return viewAt('v3'); },
    });
    expect(await queue.flush()).toEqual({ ok: true });
    expect(calls).toEqual(['шапка', 'строки']);
  });

  it('flush ждёт запись в полёте и отвечает её исходом', async () => {
    const first = gate<InvoiceView>();
    const { queue, now } = setup({ dirty: { header: false, lines: true }, lines: () => first.promise });
    queue.mark('lines');
    void queue.pump();
    const flushed = queue.flush();
    now.dirty.lines = false;
    first.fail(refusal(409, 'Счёт тем временем изменили'));
    expect(await flushed).toEqual({
      ok: false, status: { kind: 'refused', text: 'Счёт тем временем изменили' }, reported: false,
    });
  });

  it('flush без грязного — сразу «записано»', async () => {
    const { queue } = setup({ dirty: { header: false, lines: false } });
    expect(await queue.flush()).toEqual({ ok: true });
  });

  it('строки грязные, а шага нет — отказ ДО реквизитов', async () => {
    const header = vi.fn(async () => viewAt('v2'));
    const { queue } = setup({ dirty: { header: true, lines: true }, header, lines: null });
    const outcome = await queue.flush();
    expect(header).not.toHaveBeenCalled();
    expect(outcome).toEqual({ ok: false, status: { kind: 'refused', text: LINES_NOT_READY }, reported: false });
  });

  it('отказ снимается, когда грязного не осталось', async () => {
    const { queue, now } = setup({ dirty: { header: true, lines: false }, header: async () => { throw refusal(400); } });
    queue.mark('header');
    await queue.pump();
    now.dirty.header = false;
    queue.forgetRefusal();
    expect(queue.status.kind).toBe('idle');
    expect(queue.pending).toBe(false);
  });
});

describe('classify', () => {
  it('нет ответа — сбой связи', () => {
    expect(classify(netError())).toEqual({ status: { kind: 'failed' }, report: false });
  });
  it('4xx — отказ словами сервера', () => {
    expect(classify(refusal(409, 'изменили'))).toEqual({ status: { kind: 'refused', text: 'изменили' }, report: false });
  });
  it('5xx — сбой, о котором скажет снэкбар', () => {
    expect(classify(refusal(503))).toEqual({ status: { kind: 'failed' }, report: true });
  });
  it('исключение не от запроса — ошибка программы: снэкбар назовёт её, а не «нет связи»', () => {
    const spy = vi.spyOn(console, 'error').mockImplementation(() => {});
    expect(classify(new TypeError('x is undefined'))).toEqual({ status: { kind: 'failed' }, report: true });
    expect(spy).toHaveBeenCalled();
    spy.mockRestore();
  });
});

describe('editsAfterSave', () => {
  it('ушедшие правки снимаются, набранное во время записи остаётся', () => {
    const sent = { number: '104', total: '100' };
    const now = { number: '104', total: '1000', date: '2026-11-09' };
    expect(editsAfterSave(sent, now)).toEqual({ total: '1000', date: '2026-11-09' });
  });
  it('ничего не правили во время записи — правок нет', () => {
    expect(editsAfterSave({ number: '104' }, { number: '104' })).toEqual({});
  });
});

describe('saveModel', () => {
  const view = (paid: boolean) => ({ requisites: {}, payment: { paid } } as never);
  it('черновик без оплаты пишется сам', () => expect(saveModel(view(false), true, false)).toBe('auto'));
  it('оплаченный черновик — «Сохранить» в «Сроках и оплате»', () => expect(saveModel(view(true), true, false)).toBe('terms'));
  it('разобранный — читается до «Изменить»', () => expect(saveModel(view(false), true, true)).toBe('read'));
  it('режим «Изменить» держится, даже если сервер опустил счёт в черновик посреди «Разобран»', () => {
    expect(saveModel(view(false), true, true, true)).toBe('edit');
    expect(saveModel(view(false), true, false, true)).toBe('edit');
    expect(saveModel(view(true), true, false, true)).toBe('edit');
  });
  it('без права режим не открывается', () => expect(saveModel(view(false), false, true, true)).toBeNull());
  it('без права, запертый, исправление — никак', () => expect(saveModel(view(false), false, false)).toBeNull());
});
