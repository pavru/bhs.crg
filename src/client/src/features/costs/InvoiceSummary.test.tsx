// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { InvoiceSummary } from './InvoiceSummary';
import { summarySave, type SummarySave } from './invoiceSummarySave';
import { parsedHold, payHold } from './invoicePrimary';
import type { SaveModel, SaveStatus } from './invoiceSave';

/** Носитель записи в сводке счёта (costs-invoice-screen-12-autosave, D-12.1, ТЗ COST-6.5). */

afterEach(cleanup);

const noop = () => {};
/** Запись сводки — тем же построителем, что у формы. */
const built = (model: SaveModel, at: { status?: SaveStatus; unsaved?: boolean; onRetry?: () => void; onEdit?: () => void;
  onCancel?: () => void; onGo?: () => void } = {}): SummarySave => summarySave(model, {
  status: at.status ?? { kind: 'idle' }, savedAt: '12:04', unsaved: at.unsaved ?? false, onRetry: at.onRetry ?? noop,
  onEdit: at.onEdit ?? noop, onCancel: at.onCancel ?? noop, onGo: at.onGo ?? noop,
})!;
const auto = (status: SaveStatus, onRetry = noop): SummarySave => built('auto', { status, onRetry });

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

});

describe('явная запись (D-12.3 – D-12.5): кнопки «Сохранить» в сводке нет ни у одного положения', () => {
  const models = ['auto', 'read', 'edit', 'terms'] as const;
  it.each(models)('%s — «Сохранить» нет', model => {
    mount(built(model, { unsaved: true }));
    expect(screen.queryByRole('button', { name: 'Сохранить' })).toBeNull();
  });

  it('разобранный читается — «Изменить», носителя нет', () => {
    const edit = vi.fn();
    mount(built('read', { onEdit: edit }));
    expect(screen.queryByTestId('invoice-save-state')).toBeNull();
    fireEvent.click(screen.getAllByRole('button', { name: 'Изменить' })[0]);
    expect(edit).toHaveBeenCalledOnce();
  });

  it('режим «Изменить» с правкой — «Не сохранено» и «Отменить правку»', () => {
    const cancel = vi.fn();
    mount(built('edit', { unsaved: true, onCancel: cancel }));
    expect(carrier().dataset.state).toBe('unsaved');
    expect(carrier().textContent).toContain('Не сохранено');
    fireEvent.click(screen.getAllByRole('button', { name: 'Отменить правку' })[0]);
    expect(cancel).toHaveBeenCalledOnce();
  });

  it('режим «Изменить» без правки — «Сохранено · время»', () => {
    mount(built('edit'));
    expect(carrier().dataset.state).toBe('saved');
  });

  it('оплаченный черновик с правкой — носитель называет «Сохранить» в «Сроках и оплате» и ведёт туда', () => {
    const go = vi.fn();
    mount(built('terms', { unsaved: true, onGo: go }));
    expect(carrier().dataset.state).toBe('unsaved');
    const to = screen.getAllByRole('button', { name: /«Сохранить» в «Сроках и оплате»/ })[0];
    fireEvent.click(to);
    expect(go).toHaveBeenCalledOnce();
  });

  it('оплаченный черновик, отказ сервера — его словами', () => {
    mount(built('terms', { status: { kind: 'refused', text: 'Оплаченный счёт обязан оставаться сведённым' }, unsaved: true }));
    expect(carrier().dataset.state).toBe('refused');
    expect(carrier().textContent).toContain('Оплаченный счёт обязан оставаться сведённым');
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
