import { describe, expect, it } from 'vitest';
import type { WaybillView } from '@/shared/api/waybills';
import { canDeleteWaybill, deleteWaybill, waybillConfirmLabel, waybillTitle } from './waybillDelete';

const view = (over: Partial<WaybillView> = {}): WaybillView => ({
  id: 'w1', version: '7', number: null, issuedOn: null, state: 'Draft', lines: [], ...over,
} as WaybillView);

const refusal = (status: number, error: string) => ({ response: { status, data: { error } } });

describe('canDeleteWaybill', () => {
  it('черновик удаляет тот, кто правит накладные', () => {
    expect(canDeleteWaybill(view(), true)).toBe(true);
  });

  it('без права правки действия нет', () => {
    expect(canDeleteWaybill(view(), false)).toBe(false);
  });

  it('у проведённой действия нет — сначала её возвращают в черновик', () => {
    expect(canDeleteWaybill(view({ state: 'Posted' }), true)).toBe(false);
  });

  it('у исправлявшейся после закрытия действия нет и черновиком — сервер откажет: история не удаляется', () => {
    expect(canDeleteWaybill(view({ currentVersion: 2 }), true)).toBe(false);
    expect(canDeleteWaybill(view({ currentVersion: 1 }), true)).toBe(true);
  });
});

describe('как накладная названа', () => {
  it('номером и датой', () => {
    const named = view({ number: '45', issuedOn: '2026-10-02' });
    expect(waybillTitle(named)).toBe('накладная № 45 от 02.10.2026');
    expect(waybillConfirmLabel(named)).toBe('Удалить накладную № 45');
  });

  it('без номера — так и сказано', () => {
    expect(waybillTitle(view())).toBe('накладная без номера');
    expect(waybillConfirmLabel(view())).toBe('Удалить накладную без номера');
  });
});

describe('deleteWaybill', () => {
  const unread = () => Promise.reject(new Error('читать не должны'));

  it('успех — удалена', async () => {
    expect(await deleteWaybill(() => Promise.resolve(), unread)).toBe('deleted');
  });

  it('404 — не отказ: накладной уже нет, и человек хотел именно этого', async () => {
    expect(await deleteWaybill(() => Promise.reject(refusal(404, 'Накладная не найдена.')), unread)).toBe('already');
  });

  it('409, а накладная по-прежнему черновик — отказ был о версии: свои слова и без тихого повтора', async () => {
    let calls = 0;
    const send = () => { calls++; return Promise.reject(refusal(409, 'Накладную тем временем изменили.')); };

    await expect(deleteWaybill(send, () => Promise.resolve(view()))).rejects.toThrow('Она не удалена');
    expect(calls).toBe(1);
  });

  // Сервер на этом пути говорит о ВЕРСИИ: её он сверяет раньше состояния. «Перечитайте и повторите»
  // про накладную, которую уже не удалить, вело бы в никуда (ревью PR #1299).
  it('409, а накладную тем временем провели — названо проведение и выход, а не «повторите»', async () => {
    const stale = refusal(409, 'Накладную тем временем изменили. Перечитайте накладную и повторите.');
    await expect(deleteWaybill(() => Promise.reject(stale), () => Promise.resolve(view({ state: 'Posted' }))))
      .rejects.toThrow('верните накладную в черновик');
  });

  it('409, а перечитывание отвечает «нет такой» — накладную удалил тот, с кем столкнулись', async () => {
    const lost = refusal(409, 'Накладную изменили одновременно с этой правкой.');
    expect(await deleteWaybill(() => Promise.reject(lost), () => Promise.reject(refusal(404, 'Накладная не найдена.'))))
      .toBe('already');
  });

  it('409, а накладная не перечиталась — остаётся исходный отказ', async () => {
    const lost = refusal(409, 'Накладную изменили.');
    await expect(deleteWaybill(() => Promise.reject(lost), () => Promise.reject(new Error('Network Error')))).rejects.toBe(lost);
  });

  it('ответа нет — исход неизвестен, а не «не удалена»', async () => {
    await expect(deleteWaybill(() => Promise.reject(new Error('Network Error')), unread)).rejects.toThrow('неизвестно');
  });
});
