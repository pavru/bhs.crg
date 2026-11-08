// Типов Node у приложения нет нарочно (tsconfig.app) — как в monacoTheme.test.ts.
// @ts-expect-error — модуль Node без типов
import { readFileSync } from 'node:fs';
// @ts-expect-error — модуль Node без типов
import path from 'node:path';
import { describe, expect, it } from 'vitest';
import { invoiceLeave, type InvoiceLeaveInput } from './invoiceLeave';

/**
 * Уход из формы счёта (#1653, `costs-invoice-lines-leave-guard`, [COST-7]): условие охраны и вопрос — то, что
 * форма берёт отсюда (1.1, 1.2, 1.4). Как вопрос держится и пишет — `shared/ui/LeaveGuard.test.tsx`.
 */

const at = (over: Partial<InvoiceLeaveInput>): InvoiceLeaveInput =>
  ({ header: false, lines: false, correction: { active: false, dirty: false }, gone: false, ...over });

describe('охрана видит строки наравне с реквизитами (1.1, D1)', () => {
  it('несохранённые строки — держит', () => {
    expect(invoiceLeave(at({ lines: true }))).toMatchObject({ held: true, guard: true });
  });

  it('всё сохранено — не держит', () => {
    expect(invoiceLeave(at({}))).toMatchObject({ held: false, guard: false });
  });

  it('счёт удалён — молчит: сохранять некуда; несохранённое при этом есть', () => {
    expect(invoiceLeave(at({ lines: true, gone: true }))).toMatchObject({ held: true, guard: false });
  });
});

describe('вопрос называет, что не сохранено (1.2, D2)', () => {
  it('строки, реквизиты, оба — и пишет перед уходом', () => {
    expect(invoiceLeave(at({ lines: true }))).toMatchObject({
      question: 'Не сохранены строки счёта. Сохранить их перед переходом?', savable: true,
    });
    expect(invoiceLeave(at({ header: true })).question).toBe('Не сохранены реквизиты счёта. Сохранить их перед переходом?');
    expect(invoiceLeave(at({ header: true, lines: true })).question)
      .toMatch(/^Не сохранены реквизиты и строки счёта\..*затем строки\.$/);
  });

  it('ни о чём не сохранённом реквизитов не называет', () => {
    expect(invoiceLeave(at({})).question).not.toMatch(/реквизит|строк/);
  });

  it('живой прогон ждёт тот же текст о строках', () => {
    // Строка вопроса продублирована в e2e (обвязка TS не читает) — сверка здесь, в обязательной проверке.
    const e2e = readFileSync(path.resolve('e2e/invoice-screen-layout-checks.mjs'), 'utf8');
    const quoted = /const LINES_UNSAVED = '([^']+)';/.exec(e2e)?.[1];
    expect(quoted).toBe(invoiceLeave(at({ lines: true })).question);
  });
});

describe('исправление (1.4, D3)', () => {
  it('строки, набранные в исправлении, держат; вопрос — об исправлении, записи не обещает', () => {
    expect(invoiceLeave(at({ lines: true, correction: { active: true, dirty: false } }))).toMatchObject({
      guard: true, savable: false,
      question: expect.stringMatching(/^Исправление не записано: оно записывается только кнопкой «Проверить исправление…»/),
    });
  });

  it('причина или разноска исправления держат и без правок шапки и строк', () => {
    expect(invoiceLeave(at({ correction: { active: true, dirty: true } }))).toMatchObject({ guard: true, savable: false });
  });

  it('исправление начато, ничего не набрано — не держит', () => {
    expect(invoiceLeave(at({ correction: { active: true, dirty: false } }))).toMatchObject({ held: false, guard: false });
  });
});
