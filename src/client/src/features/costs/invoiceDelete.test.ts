import { describe, expect, it } from 'vitest';
import type { InvoiceView } from '@/shared/api/invoices';
import { canDelete, confirmLabel, deleteInvoice, goingAway, invoiceTitle } from './invoiceDelete';

function view(over: {
  requisites?: Record<string, unknown>; paid?: boolean; lines?: { parts: number }[];
} = {}): InvoiceView {
  return {
    id: 'i1', version: '7',
    requisites: { Состояние: 'Черновик', ...over.requisites },
    payment: { paid: over.paid ?? false },
    lines: (over.lines ?? []).map(line => ({ allocation: { parts: Array(line.parts).fill({}) } })),
    duplicates: [],
    sameFile: [],
  } as unknown as InvoiceView;
}

const refusal = (status: number, error: string) => ({ response: { status, data: { error } } });

describe('canDelete', () => {
  it('черновик без оплаты удаляет тот, кто правит счета', () => {
    expect(canDelete(view(), true)).toBe(true);
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
