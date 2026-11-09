import { describe, expect, it } from 'vitest';
import type { InvoiceView } from '@/shared/api/invoices';
import { baseOf, stepBase } from './draftBase';

const base = { version: '10', signature: 'строки-А' };

describe('основа черновика', () => {
  it('тот же вид — та же основа', () => {
    expect(stepBase(base, '10', 'строки-А', true)).toEqual({ kind: 'same', base });
  });

  // Сосед поправил шапку, я правлю строки: версия у счёта одна, и она ушла. Строки при этом те же —
  // мой черновик собран по тому, что лежит в базе, и назвать новую версию честно.
  it('версия ушла, часть та же — основа переезжает, правки целы', () => {
    expect(stepBase(base, '11', 'строки-А', true))
      .toEqual({ kind: 'same', base: { version: '11', signature: 'строки-А' } });
  });

  it('часть изменилась, правок нет — черновик пересобирается', () => {
    expect(stepBase(base, '11', 'строки-Б', false))
      .toEqual({ kind: 'rebuild', base: { version: '11', signature: 'строки-Б' } });
  });

  // Ради этого случая всё и заведено: назови форма свежую версию — сервер принял бы набор строк,
  // собранный по прежним, и затёр бы чужие.
  it('часть изменилась под правками — основа прежняя, и версию называют прежнюю', () => {
    expect(stepBase(base, '11', 'строки-Б', true)).toEqual({ kind: 'stale', base });
  });

  // Шапка: подпись — реквизиты целиком, а «моя часть» — только правленые поля (ревью PR #1208).
  // Подпись из одних правленых полей менялась с первой же правкой, без смены версии: основа помнила
  // «правок нет», и чужая правка строк читалась как правка шапки.
  describe('своё сравнение подписей', () => {
    const fields = { version: '10', signature: { 'Номер': 'А-1', 'Назначение': 'кабель' } as Record<string, unknown> };
    const mine = (keys: string[]) => (a: Record<string, unknown>, b: Record<string, unknown>) =>
      keys.every(key => a[key] === b[key]);

    it('версия ушла, моих полей не трогали — основа переезжает', () => {
      const next = { 'Номер': 'А-1', 'Назначение': 'труба' };
      expect(stepBase(fields, '11', next, true, mine(['Номер'])))
        .toEqual({ kind: 'same', base: { version: '11', signature: next } });
    });

    it('изменили поле, которое я правлю, — устарело', () => {
      const next = { 'Номер': 'Б-2', 'Назначение': 'кабель' };
      expect(stepBase(fields, '11', next, true, mine(['Номер'])).kind).toBe('stale');
    });
  });

  it('устаревшее остаётся устаревшим, пока человек не перечитает', () => {
    const stale = stepBase(base, '11', 'строки-Б', true);
    expect(stepBase(stale.base, '12', 'строки-В', true).kind).toBe('stale');
    // Перечитал — правок нет: основа свежая.
    expect(stepBase(stale.base, '12', 'строки-В', false).kind).toBe('rebuild');
  });

  // Ответ своей записи строк (issue #1656): версия новая и подпись другая (у новых строк появились id), а
  // черновик грязный — человек правил во время записи. Принятый за основу, ответ — не «строки изменили».
  describe('свой ответ записи (adopt)', () => {
    const saved = { version: '11', lines: 'строки-Б' } as unknown as InvoiceView;
    const signatureOf = (view: InvoiceView) => (view as unknown as { lines: string }).lines;
    const adopted = baseOf(saved, signatureOf);

    it('при грязном черновике даёт same, и следующая запись называет версию ответа', () => {
      expect(adopted).toEqual({ version: '11', signature: 'строки-Б' });
      expect(stepBase(adopted, saved.version, signatureOf(saved), true)).toEqual({ kind: 'same', base: adopted });
    });

    it('без принятия тот же ответ читался бы чужой правкой', () => {
      expect(stepBase(base, saved.version, signatureOf(saved), true).kind).toBe('stale');
    });

    it('чужая правка строк после принятого ответа — по-прежнему stale', () => {
      expect(stepBase(adopted, '12', 'строки-В', true)).toEqual({ kind: 'stale', base: adopted });
    });
  });
});

/** Очередь записи (costs-invoice-screen-12-autosave, D-12.1): ответ своей записи и сдвиг распознаванием — не «изменили». */
describe('основа под автосохранением', () => {
  const reqs = (number: string, total: string) => ({ Номер: number, Итого: total });
  const view = (version: string, requisites: Record<string, string>) => ({ version, requisites } as unknown as InvoiceView);
  const sig = (of: InvoiceView) => of.requisites as Record<string, unknown>;
  // Шапка правит только «Итого»: моя часть — правленые поля (как `same` в `InvoiceForm`).
  const mine = (a: Record<string, unknown>, b: Record<string, unknown>) => a.Итого === b.Итого;

  it('свой ответ, пока набрано новое после отправки: основа — ответ, черновик не «устарел»', () => {
    const saved = view('v2', reqs('104', '100'));
    const adopted = baseOf(saved, sig);
    // Человек уже набрал «1000» — правки есть, а вид пришёл тот же, что принят.
    expect(stepBase(adopted, 'v2', sig(saved), true, mine)).toEqual({ kind: 'same', base: adopted });
  });

  it('без принятия свой ответ под правками читался бы чужой правкой', () => {
    const before = baseOf(view('v1', reqs('104', '10')), sig);
    expect(stepBase(before, 'v2', sig(view('v2', reqs('104', '100'))), true, mine).kind).toBe('stale');
  });

  it('распознавание дописало пустое поле и сдвинуло версию — моя часть та же, основа переезжает', () => {
    const at = baseOf(view('v2', reqs('', '100')), sig);
    const scanned = view('v3', reqs('104', '100'));
    expect(stepBase(at, 'v3', sig(scanned), true, mine)).toEqual({ kind: 'same', base: { version: 'v3', signature: sig(scanned) } });
  });
});
