// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { InvoicePart } from './InvoicePart';

/** Часть формы счёта (costs-invoice-screen-10-layout, D-10.1). */

afterEach(cleanup);

describe('InvoicePart', () => {
  it('свёрнутая — строка-итог видна, тела нет; кнопка называет действие и держит aria-expanded', () => {
    const toggle = vi.fn();
    render(
      <InvoicePart title="Кто и сколько" step={1} line="ООО «Кабель-Торг» → ООО «СЭМ»" open={false} onToggle={toggle}>
        <input aria-label="Номер" />
      </InvoicePart>,
    );
    expect(screen.getByText('ООО «Кабель-Торг» → ООО «СЭМ»')).toBeTruthy();
    expect(screen.queryByLabelText('Номер')).toBeNull();
    const button = screen.getByRole('button', { name: 'Развернуть «Кто и сколько»' });
    expect(button.getAttribute('aria-expanded')).toBe('false');
    fireEvent.click(button);
    expect(toggle).toHaveBeenCalledOnce();
  });

  it('раскрытая — тело на месте, строки-итога нет, кнопка «Свернуть»', () => {
    render(
      <InvoicePart title="Строки" step={2} line="строк: 6" open onToggle={() => {}}>
        <input aria-label="Номер" />
      </InvoicePart>,
    );
    expect(screen.getByLabelText('Номер')).toBeTruthy();
    expect(screen.queryByText('строк: 6')).toBeNull();
    expect(screen.getByRole('button', { name: 'Свернуть «Строки»' }).getAttribute('aria-expanded')).toBe('true');
  });

  it('часть — именованная область: к ней ведёт читалка и живой прогон', () => {
    render(<InvoicePart title="Разноска" open onToggle={() => {}}>тело</InvoicePart>);
    expect(screen.getByRole('region', { name: 'Разноска' })).toBeTruthy();
  });
});
