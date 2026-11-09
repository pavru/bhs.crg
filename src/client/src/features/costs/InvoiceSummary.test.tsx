// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { InvoiceSummary, type SummarySave } from './InvoiceSummary';
import { parsedHold, payHold } from './invoicePrimary';
import type { SaveStatus } from './invoiceSave';

/** Носитель записи в сводке счёта (costs-invoice-screen-12-autosave, D-12.1, ТЗ COST-6.5). */

afterEach(cleanup);

const auto = (status: SaveStatus, onRetry = () => {}): SummarySave => ({ kind: 'auto', status, savedAt: '12:04', onRetry });

function mount(save: SummarySave | null, hold: string | null = null) {
  return render(
    <InvoiceSummary number="СЧ-104" states={null} meta="ООО «Кабель-Торг» · от 03.09.2026" save={save}
      primary={{ kind: 'parsed', hold, onClick: () => {} }} />,
  );
}

/** Носитель в широкой сводке — первый: узкая копия лежит рядом, спрятанной классом. */
const carrier = () => screen.getAllByTestId('invoice-save-state')[0];

describe('носитель записи', () => {
  it('очередь пуста — «Сохранено · время», кнопки «Сохранить» нет', () => {
    mount(auto({ kind: 'idle' }));
    expect(carrier().dataset.state).toBe('saved');
    expect(carrier().textContent).toContain('Сохранено · 12:04');
    expect(screen.queryByRole('button', { name: 'Сохранить' })).toBeNull();
  });

  it('запись в пути — «Сохраняю…», «Разобран» неактивна и называет причину', () => {
    mount(auto({ kind: 'saving' }), parsedHold([], false, true));
    expect(carrier().dataset.state).toBe('saving');
    expect(carrier().textContent).toContain('Сохраняю…');
    const parse = screen.getAllByRole('button', { name: 'Разобран' })[0];
    expect(parse.hasAttribute('disabled') || parse.getAttribute('aria-disabled') === 'true').toBe(true);
  });

  it('сбой связи — «Не сохранено» и «Повторить», который отправляет снова', () => {
    const retry = vi.fn();
    mount(auto({ kind: 'failed' }, retry));
    expect(carrier().dataset.state).toBe('failed');
    expect(carrier().textContent).toContain('Не сохранено');
    fireEvent.click(screen.getAllByRole('button', { name: 'Повторить' })[0]);
    expect(retry).toHaveBeenCalledOnce();
  });

  it('отказ сервера — его словами', () => {
    mount(auto({ kind: 'refused', text: 'Номер счёта уже занят у этого поставщика' }));
    expect(carrier().dataset.state).toBe('refused');
    expect(carrier().textContent).toContain('Не сохранено: Номер счёта уже занят у этого поставщика');
    expect(screen.queryByRole('button', { name: 'Повторить' })).toBeNull();
  });

  it('там, где автосохранения нет, — прежняя кнопка «Сохранить»', () => {
    mount({ kind: 'button', unsaved: true, saving: false, savedAt: '09.11.2026 12:04', onSave: () => {} });
    expect(screen.queryByTestId('invoice-save-state')).toBeNull();
    expect(screen.getAllByRole('button', { name: 'Сохранить' }).length).toBeGreaterThan(0);
  });
});

describe('«Разобран» и оплата ждут очередь', () => {
  it('запись в пути гасит обе', () => {
    expect(parsedHold([], false, true)).toMatch(/сохраняется/);
    expect(payHold(false, null, true)).toMatch(/сохраняется/);
  });
  it('запись не дошла — обе называют несохранённое', () => {
    expect(parsedHold([], true, false)).toMatch(/Правки не сохранены/);
    expect(payHold(true, null, false)).toMatch(/Правки не сохранены/);
  });
});
