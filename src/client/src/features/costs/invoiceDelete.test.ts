import { describe, expect, it } from 'vitest';
import { QueryClient } from '@tanstack/react-query';
import { QK, forgetInvoice, type InvoiceView } from '@/shared/api/invoices';
import {
  canDelete, confirmLabel, deleteDraftLater, deleteInvoice, deletesNow, goingAway, invoiceTitle, isFresh, markCreatedHere, resetFresh,
  watchFreshness,
} from './invoiceDelete';

function view(over: {
  requisites?: Record<string, unknown>; paid?: boolean; lines?: { parts: number }[]; book?: number;
} = {}): InvoiceView {
  return {
    id: 'i1', version: '7',
    requisites: { Состояние: 'Черновик', ...over.requisites },
    payment: { paid: over.paid ?? false },
    lines: (over.lines ?? []).map(line => ({ allocation: { parts: Array(line.parts).fill({}) } })),
    duplicates: [],
    sameFile: [],
    book: over.book,
  } as unknown as InvoiceView;
}

const refusal = (status: number, error: string) => ({ response: { status, data: { error } } });

describe('canDelete', () => {
  it('черновик без оплаты удаляет тот, кто правит счета', () => {
    expect(canDelete(view(), true)).toBe(true);
  });

  it('у черновика с книгой исправлений действия нет — сервер ответил бы 409', () => {
    expect(canDelete(view({ book: 2 }), true)).toBe(false);
    expect(canDelete(view({ book: 0 }), true)).toBe(true);
  });

  it('без права правки действия нет', () => {
    expect(canDelete(view(), false)).toBe(false);
  });

  it.each(['Разобран', 'Отклонён'])('у счёта «%s» действия нет — сначала его возвращают в черновик', state => {
    expect(canDelete(view({ requisites: { Состояние: state } }), true)).toBe(false);
  });

  it('у оплаченного черновика действия нет — сначала отменяют оплату', () => {
    expect(canDelete(view({ paid: true }), true)).toBe(false);
  });
});

describe('как счёт назван', () => {
  it('номером и датой', () => {
    const named = view({ requisites: { Номер: '123', Дата: '2026-10-01' } });
    expect(invoiceTitle(named)).toBe('счёт № 123 от 01.10.2026');
    expect(confirmLabel(named)).toBe('Удалить счёт № 123');
  });

  it('без номера — файлом: больше опознать его нечем', () => {
    const scanned = view({ requisites: { Скан: { fileName: 'Счёт 417.pdf' } } });
    expect(invoiceTitle(scanned)).toBe('счёт без номера');
    expect(confirmLabel(scanned)).toBe('Удалить счёт из файла „Счёт 417.pdf“');
  });

  it('без номера и файла — пустой черновик', () => {
    expect(confirmLabel(view())).toBe('Удалить пустой черновик');
  });
});

describe('goingAway', () => {
  it('называет только существующее', () => {
    expect(goingAway(view())).toEqual([]);
    expect(goingAway(view({ lines: [{ parts: 0 }, { parts: 0 }] }))).toEqual(['строки счёта — 2']);
  });

  it('строки, разноска и файл', () => {
    expect(goingAway(view({ lines: [{ parts: 2 }], requisites: { Скан: { fileName: 'а.pdf' } } }))).toEqual([
      'строки счёта — 1', 'разноска по объектам и статьям', 'файл счёта «а.pdf» и прочитанное из него',
    ]);
  });
});

// Ревью PR #1808: отложенное удаление разбирает отказы так же, как диалог, — своими словами, без окна.
describe('deleteDraftLater', () => {
  it('ответа нет — исход неизвестен, а не «не удалён»', async () => {
    await expect(deleteDraftLater(() => Promise.reject(new Error('Network Error')), () => Promise.reject(new Error('—'))))
      .rejects.toThrow('Удалён ли черновик — неизвестно');
  });

  it('409, а черновик по-прежнему удаляемый — свежий счёт прочитан, слова без «окна»', async () => {
    let read = 0;
    const error = await deleteDraftLater(() => Promise.reject(refusal(409, 'Счёт изменили.')),
      () => { read += 1; return Promise.resolve(view()); }).then(() => new Error('удалён'), (e: Error) => e);
    expect(read).toBe(1);
    expect(error.message).toContain('Черновик не удалён: его тем временем изменили');
    expect(error.message).not.toContain('окно');
  });

  it('404 — «уже удалён»', async () => {
    expect(await deleteDraftLater(() => Promise.reject(refusal(404, 'Счёт не найден.')), () => Promise.reject(new Error('—'))))
      .toBe('already');
  });
});

describe('deleteInvoice', () => {
  const unread = () => Promise.reject(new Error('читать не должны'));

  it('успех — удалён', async () => {
    expect(await deleteInvoice(() => Promise.resolve(), unread)).toBe('deleted');
  });

  it('404 — не отказ: счёта уже нет, и человек хотел именно этого', async () => {
    expect(await deleteInvoice(() => Promise.reject(refusal(404, 'Счёт не найден.')), unread)).toBe('already');
  });

  it('409, а счёт по-прежнему удаляемый — отказ был о версии: свои слова и без тихого повтора', async () => {
    let calls = 0;
    // Текст сервера нарочно ДРУГОЙ: признак — свежий счёт, а не слова отказа.
    const send = () => { calls++; return Promise.reject(refusal(409, 'Счёт изменили одновременно с этой правкой.')); };

    await expect(deleteInvoice(send, () => Promise.resolve(view()))).rejects.toThrow('или дочитали его файл');
    expect(calls).toBe(1);
  });

  // Сервер на этом пути говорит о ВЕРСИИ («перечитайте и повторите»): её он сверяет раньше условия
  // удаления. Повторить нечего — и экран называет, чем счёт стал и какой у этого выход (ревью PR #1299).
  it('409, а счёт тем временем оплатили — названа оплата и выход, а не «повторите»', async () => {
    const stale = refusal(409, 'Счёт тем временем изменили. Перечитайте счёт и повторите.');
    await expect(deleteInvoice(() => Promise.reject(stale), () => Promise.resolve(view({ paid: true }))))
      .rejects.toThrow('сначала отмените оплату');
  });

  it('409, а счёт тем временем разобрали — названо состояние и выход', async () => {
    const stale = refusal(409, 'Счёт тем временем изменили. Перечитайте счёт и повторите.');
    await expect(deleteInvoice(() => Promise.reject(stale),
      () => Promise.resolve(view({ requisites: { Состояние: 'Разобран' } }))))
      .rejects.toThrow('теперь он — разобран');
  });

  it('409, а перечитывание отвечает «нет такого» — счёт удалил тот, с кем столкнулись', async () => {
    const lost = refusal(409, 'Счёт изменили одновременно с этой правкой.');
    expect(await deleteInvoice(() => Promise.reject(lost), () => Promise.reject(refusal(404, 'Счёт не найден.'))))
      .toBe('already');
  });

  it('409, а счёт не перечитался — остаётся исходный отказ', async () => {
    const lost = refusal(409, 'Счёт изменили.');
    await expect(deleteInvoice(() => Promise.reject(lost), () => Promise.reject(new Error('Network Error')))).rejects.toBe(lost);
  });

  it('ответа нет — исход неизвестен, а не «не удалён»', async () => {
    await expect(deleteInvoice(() => Promise.reject(new Error('Network Error')), unread)).rejects.toThrow('неизвестно');
  });
});

describe('«свежий» черновик (costs-invoice-screen-13-undo, D-13.3, задачи 3.1 и 3.5)', () => {
  const written = (qc: QueryClient, id: string) =>
    qc.getMutationCache().build(qc, { mutationFn: async () => ({ id }), meta: { invoiceWrite: true } }).execute(undefined);

  it('заведённый из файла этой вкладкой — свежий; чужой и прошлого сеанса — нет', () => {
    resetFresh();
    markCreatedHere('i1');
    expect(isFresh('i1')).toBe(true);
    expect(isFresh('i2')).toBe(false);
  });

  it('своя запись снимает признак — после записи поставщика удаление спросит окном', async () => {
    resetFresh();
    const qc = new QueryClient();
    watchFreshness(qc);
    watchFreshness(qc);
    markCreatedHere('i1');
    markCreatedHere('i2');
    await written(qc, 'i1');
    expect(isFresh('i1')).toBe(false);
    expect(isFresh('i2')).toBe(true);
  });

  it('сдвиг версии распознаванием (не запись этой вкладки) признак не снимает', () => {
    resetFresh();
    const qc = new QueryClient();
    watchFreshness(qc);
    markCreatedHere('i1');
    qc.setQueryData(['costs-invoices', 'i1'], { id: 'i1', version: '9' });
    expect(isFresh('i1')).toBe(true);
  });

  it('вход «Это дубль» спрашивает окном и у свежего: дубликат узнают по сохранённым реквизитам', () => {
    const entry = { now: true, ask: () => {} };
    expect(deletesNow(entry, 'wrong-file')).toBe(true);
    expect(deletesNow(entry, 'same-file')).toBe(true);
    expect(deletesNow(entry, 'menu')).toBe(true);
    expect(deletesNow(entry, 'duplicate')).toBe(false);
    expect(deletesNow({ now: false, ask: () => {} }, 'menu')).toBe(false);
    expect(deletesNow(null, 'menu')).toBe(false);
  });
});

// Ревью PR #1808: скрытый на время «Вернуть» черновик открывается обратно, как только запрос удался, — и до ответа
// перечитывания стоял бы строкой, которая ведёт в «счёта нет».
describe('forgetInvoice', () => {
  it('удалённый уходит из списков в кэше сразу, остальные строки на месте', () => {
    const qc = new QueryClient();
    qc.setQueryData([QK, 'list', null], [{ id: 'a' }, { id: 'b' }]);
    qc.setQueryData([QK, 'list', 'parsing'], [{ id: 'a' }]);
    qc.setQueryData([QK, 'a'], { id: 'a' });
    forgetInvoice(qc, 'a');
    expect(qc.getQueryData([QK, 'list', null])).toEqual([{ id: 'b' }]);
    expect(qc.getQueryData([QK, 'list', 'parsing'])).toEqual([]);
    expect(qc.getQueryData([QK, 'a'])).toBeUndefined();
  });
});
