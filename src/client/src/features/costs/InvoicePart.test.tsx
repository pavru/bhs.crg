// @vitest-environment jsdom
import { useState } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { InvoicePart } from './InvoicePart';

/** Часть формы счёта (costs-invoice-screen-10-layout, D-10.1; шапка — цель, costs-invoice-part-header-toggle). */

afterEach(cleanup);

/** Часть со своим состоянием раскрытия — как в форме счёта. */
function Part({ initial = false, onPay }: { initial?: boolean; onPay?: () => void }) {
  const [open, setOpen] = useState(initial);
  return (
    <InvoicePart title="Сроки и оплата" step={4} line="оплатить до 20.11.2026" open={open} onToggle={() => setOpen(o => !o)}
      actions={<button type="button" onClick={onPay}>Внести оплату…</button>}>
      <input aria-label="Количество" />
    </InvoicePart>
  );
}

describe('InvoicePart', () => {
  it('свёрнутая — строка-итог видна, тело спрятано; кнопка называет действие и держит aria-expanded', () => {
    const toggle = vi.fn();
    render(
      <InvoicePart title="Кто и сколько" step={1} line="ООО «Кабель-Торг» → ООО «СЭМ»" open={false} onToggle={toggle}>
        <input aria-label="Номер" />
      </InvoicePart>,
    );
    expect(screen.getByText('ООО «Кабель-Торг» → ООО «СЭМ»')).toBeTruthy();
    // Спрятано, а не размонтировано (ревью PR #1787): в теле живёт черновик строк.
    expect(screen.queryByRole('textbox', { name: 'Номер' })).toBeNull();
    expect(screen.getByLabelText('Номер').closest('[hidden]')).not.toBeNull();
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
    expect(screen.getByRole('textbox', { name: 'Номер' })).toBeTruthy();
    expect(screen.queryByText('строк: 6')).toBeNull();
    const button = screen.getByRole('button', { name: 'Свернуть «Строки»' });
    expect(button.getAttribute('aria-expanded')).toBe('true');
    // Описания у раскрытой нет: строка-итог пуста.
    expect(button.getAttribute('aria-describedby')).toBeNull();
  });

  it('цель — кнопка в заголовке части; у заголовка имя — название части; строка-итог — описание цели, тело — её цель', () => {
    render(<InvoicePart title="Кто и сколько" step={1} line="ООО «СЭМ»" open={false} onToggle={() => {}}>тело</InvoicePart>);
    // Список заголовков диктора читает названия частей, а не «Развернуть …».
    const heading = screen.getByRole('heading', { level: 3, name: 'Кто и сколько' });
    const button = within(heading).getByRole('button', { name: 'Развернуть «Кто и сколько»' });
    expect(button.getAttribute('aria-describedby')).toBeTruthy();
    expect(document.getElementById(button.getAttribute('aria-describedby')!)?.textContent).toBe('ООО «СЭМ»');
    expect(document.getElementById(button.getAttribute('aria-controls')!)?.textContent).toBe('тело');
    // Слой состояния — у шапки, нажатие и фокус — по цели (`state-layer-row` находит её в заголовке). Кому
    // достаётся щелчок по строке-итогу и шеврону, jsdom не знает: это проверяет живой прогон
    // (`invoice-terms-collapse-checks`).
    const header = heading.parentElement!;
    expect(header.className).toMatch(/state-layer-row/);
    expect(button.hasAttribute('data-li-target')).toBe(true);
    // Шеврон — не вторая цель: кнопка в шапке одна.
    expect(within(header).getAllByRole('button')).toHaveLength(1);
  });

  it('действие шапки делает своё и часть не переключает: оно над целью, а не внутри неё', async () => {
    const pay = vi.fn();
    render(<Part onPay={pay} />);
    const action = screen.getByRole('button', { name: 'Внести оплату…' });
    expect(action.closest('h3')).toBeNull();
    expect(action.closest('[data-li-side]')).not.toBeNull();
    await userEvent.click(action);
    expect(pay).toHaveBeenCalledOnce();
    expect(screen.getByRole('button', { name: 'Развернуть «Сроки и оплата»' }).getAttribute('aria-expanded')).toBe('false');
  });

  it('клавиатура: Enter и Пробел на шапке переключают часть, имя и aria-expanded следуют', async () => {
    const user = userEvent.setup();
    render(<Part />);
    await user.tab();
    expect(document.activeElement).toBe(screen.getByRole('button', { name: 'Развернуть «Сроки и оплата»' }));
    await user.keyboard('{Enter}');
    const open = screen.getByRole('button', { name: 'Свернуть «Сроки и оплата»' });
    expect(open.getAttribute('aria-expanded')).toBe('true');
    await user.keyboard(' ');
    expect(screen.getByRole('button', { name: 'Развернуть «Сроки и оплата»' }).getAttribute('aria-expanded')).toBe('false');
  });

  it('набранное в теле переживает свёртку и раскрытие щелчком по шапке', async () => {
    render(<Part initial />);
    fireEvent.change(screen.getByLabelText('Количество'), { target: { value: '12' } });
    await userEvent.click(screen.getByRole('button', { name: 'Свернуть «Сроки и оплата»' }));
    expect(screen.queryByRole('textbox', { name: 'Количество' })).toBeNull();
    await userEvent.click(screen.getByRole('button', { name: 'Развернуть «Сроки и оплата»' }));
    expect((screen.getByLabelText('Количество') as HTMLInputElement).value).toBe('12');
  });

  it('подсказка с полным текстом строки-итога — у цели и только у обрезанной строки', () => {
    render(<InvoicePart title="Кто и сколько" line="очень длинная строка-итог" open={false} onToggle={() => {}} />);
    const button = screen.getByRole('button');
    const line = screen.getByText('очень длинная строка-итог');
    Object.defineProperty(line, 'clientWidth', { configurable: true, value: 100 });
    Object.defineProperty(line, 'scrollWidth', { configurable: true, value: 300 });
    fireEvent.pointerEnter(button);
    expect(button.getAttribute('title')).toBe('очень длинная строка-итог');
    Object.defineProperty(line, 'scrollWidth', { configurable: true, value: 100 });
    fireEvent.pointerEnter(button);
    expect(button.hasAttribute('title')).toBe(false);
  });

  it('раскрытая часть подсказку свёрнутой не держит', () => {
    const { rerender } = render(<InvoicePart title="Кто и сколько" line="очень длинная строка-итог" open={false} onToggle={() => {}} />);
    const button = screen.getByRole('button');
    const line = screen.getByText('очень длинная строка-итог');
    Object.defineProperty(line, 'clientWidth', { configurable: true, value: 100 });
    Object.defineProperty(line, 'scrollWidth', { configurable: true, value: 300 });
    fireEvent.pointerEnter(button);
    expect(button.getAttribute('title')).toBe('очень длинная строка-итог');
    rerender(<InvoicePart title="Кто и сколько" line="очень длинная строка-итог" open onToggle={() => {}} />);
    fireEvent.pointerEnter(button);
    expect(button.hasAttribute('title')).toBe(false);
  });

  it('часть — именованная область: к ней ведёт читалка и живой прогон', () => {
    render(<InvoicePart title="Разноска" open onToggle={() => {}}>тело</InvoicePart>);
    expect(screen.getByRole('region', { name: 'Разноска' })).toBeTruthy();
  });
});
