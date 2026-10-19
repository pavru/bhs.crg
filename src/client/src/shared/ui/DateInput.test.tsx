// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { useState } from 'react';
import { DateInput } from './DateInput';
import { DateField } from './DateField';

/**
 * Честный ввод даты (issue #1460). Проверяется и «отдано правильное», и «НЕ отдано»: несуществующая
 * и недописанная дата наружу не уходят, а набранное при уходе из поля не исчезает молча.
 */

afterEach(() => { cleanup(); vi.useRealTimers(); });

/** Поле с настоящим состоянием — как в форме: отданное значение возвращается в `value`. */
function Controlled({ initial = '', onChange, field = false }: { initial?: string; onChange?: (v: string) => void; field?: boolean }) {
  const [value, setValue] = useState(initial);
  const change = (v: string) => { onChange?.(v); setValue(v); };
  return (
    <>
      {field
        ? <DateField label="Дата платежа" value={value} onChange={change} hint="от неё считается срок" />
        : <DateInput value={value} onChange={change} calendar={false} />}
      <output data-testid="value">{value}</output>
      <button type="button">дальше</button>
    </>
  );
}

const seg = (ph: 'ДД' | 'ММ' | 'ГГГГ') => screen.getByPlaceholderText(ph) as HTMLInputElement;

function typeDate(d: string, m: string, y: string) {
  fireEvent.focus(seg('ДД')); fireEvent.change(seg('ДД'), { target: { value: d } });
  fireEvent.focus(seg('ММ')); fireEvent.change(seg('ММ'), { target: { value: m } });
  fireEvent.focus(seg('ГГГГ')); fireEvent.change(seg('ГГГГ'), { target: { value: y } });
}

/** Уход из поля: blur последнего сегмента и таймер, которым поле отличает переход между сегментами. */
function leave(from: HTMLInputElement = seg('ГГГГ')) {
  fireEvent.blur(from);
  act(() => { vi.advanceTimersByTime(200); });
}

describe('DateInput: несуществующая дата', () => {
  it('31.02.2026 НЕ отдаётся и названа ошибкой', () => {
    vi.useFakeTimers();
    const onChange = vi.fn();
    // Ноябрь — 30 дней: «31» не даёт даты уже на первом сегменте, и ни один шаг ввода её не отдаёт.
    render(<Controlled initial="2026-11-18" onChange={onChange} field />);
    typeDate('31', '02', '2026');
    expect(onChange).not.toHaveBeenCalled();
    expect(screen.getByTestId('value').textContent).toBe('2026-11-18');
    expect(screen.getByText(/Такой даты нет/)).toBeTruthy();
    leave();
    expect(seg('ДД').value).toBe('31');
    expect(seg('ДД').getAttribute('aria-invalid')).toBe('true');
  });

  it('месяц 13 и день 39 — тоже не отдаются', () => {
    const onChange = vi.fn();
    render(<Controlled onChange={onChange} />);
    typeDate('18', '13', '2026');
    typeDate('39', '10', '2026');
    expect(onChange).not.toHaveBeenCalled();
  });

  it('исправленная дата отдаётся, ошибка уходит', () => {
    const onChange = vi.fn();
    render(<Controlled onChange={onChange} field />);
    typeDate('31', '02', '2026');
    fireEvent.change(seg('ДД'), { target: { value: '28' } });
    expect(onChange).toHaveBeenLastCalledWith('2026-02-28');
    expect(screen.queryByText(/Такой даты нет/)).toBeNull();
  });
});

describe('DateInput: вставка', () => {
  it.each([['18.10.2026'], ['2026-10-18'], ['18/10/2026']])('%s в любой сегмент заполняет поле', text => {
    for (const ph of ['ДД', 'ММ', 'ГГГГ'] as const) {
      const onChange = vi.fn();
      render(<Controlled onChange={onChange} />);
      fireEvent.paste(seg(ph), { clipboardData: { getData: () => text } });
      expect(onChange).toHaveBeenCalledWith('2026-10-18');
      expect([seg('ДД').value, seg('ММ').value, seg('ГГГГ').value]).toEqual(['18', '10', '2026']);
      cleanup();
    }
  });

  it('вставка несуществующей даты не отдаётся', () => {
    const onChange = vi.fn();
    render(<Controlled onChange={onChange} />);
    fireEvent.paste(seg('ДД'), { clipboardData: { getData: () => '31.02.2026' } });
    expect(onChange).not.toHaveBeenCalled();
    expect(seg('ММ').value).toBe('02');
  });
});

describe('DateInput: двузначный год и недописанное', () => {
  it('«18.10.26» при уходе — 18.10.2026', () => {
    vi.useFakeTimers();
    const onChange = vi.fn();
    render(<Controlled onChange={onChange} />);
    typeDate('18', '10', '26');
    expect(onChange).not.toHaveBeenCalled();
    leave();
    expect(onChange).toHaveBeenLastCalledWith('2026-10-18');
    expect(seg('ГГГГ').value).toBe('2026');
  });

  it('уход с недописанным: набранное видно, ошибка под полем, значение НЕ сохранено', () => {
    vi.useFakeTimers();
    const onChange = vi.fn();
    render(<Controlled initial="2026-10-18" onChange={onChange} field />);
    fireEvent.focus(seg('ДД'));
    fireEvent.change(seg('ДД'), { target: { value: '' } });
    fireEvent.focus(seg('ММ'));
    fireEvent.change(seg('ММ'), { target: { value: '1' } });
    expect(screen.queryByText(/Дата не дописана/)).toBeNull(); // посреди ввода — не ошибка
    leave(seg('ММ'));
    expect(onChange).not.toHaveBeenCalled();
    expect(screen.getByTestId('value').textContent).toBe('2026-10-18');
    expect(seg('ДД').value).toBe('');
    expect(seg('ММ').value).toBe('01'); // одна цифра месяца дополняется нулём, как и раньше
    expect(screen.getByText('Дата не дописана. От неё считается срок')).toBeTruthy();
  });

  it('Esc возвращает прежнее значение и гасится в поле', () => {
    const onChange = vi.fn();
    const outer = vi.fn();
    render(<div onKeyDown={outer}><Controlled initial="2026-10-18" onChange={onChange} /></div>);
    fireEvent.focus(seg('ДД'));
    fireEvent.change(seg('ДД'), { target: { value: '3' } });
    fireEvent.keyDown(seg('ДД'), { key: 'Escape' });
    expect(seg('ДД').value).toBe('18');
    expect(outer).not.toHaveBeenCalled();
    expect(onChange).not.toHaveBeenCalled();
    // Без набранного Esc поле не трогает — его получает окно.
    fireEvent.keyDown(seg('ДД'), { key: 'Escape' });
    expect(outer).toHaveBeenCalledTimes(1);
  });

  it('Esc возвращает значение, с которым вошли в поле, а не промежуточное', () => {
    // «31» поверх 10.2026 складывается в 31.10 и отдаётся по пути к 31.02 (живая проба окна оплаты).
    const onChange = vi.fn();
    render(<Controlled initial="2026-10-19" onChange={onChange} />);
    fireEvent.focus(seg('ДД'));
    fireEvent.change(seg('ДД'), { target: { value: '31' } });
    expect(onChange).toHaveBeenLastCalledWith('2026-10-31');
    fireEvent.focus(seg('ММ'));
    fireEvent.change(seg('ММ'), { target: { value: '02' } });
    fireEvent.keyDown(seg('ММ'), { key: 'Escape' });
    expect(onChange).toHaveBeenLastCalledWith('2026-10-19');
    expect([seg('ДД').value, seg('ММ').value, seg('ГГГГ').value]).toEqual(['19', '10', '2026']);
  });

  it('значение сменили снаружи — набранное к прежнему не держится', () => {
    vi.useFakeTimers();
    const { rerender } = render(<DateInput value="2026-10-18" onChange={() => {}} calendar={false} />);
    fireEvent.focus(seg('ГГГГ'));
    fireEvent.change(seg('ГГГГ'), { target: { value: '202' } });
    leave();
    expect(seg('ГГГГ').value).toBe('202');
    rerender(<DateInput value="2025-01-05" onChange={() => {}} calendar={false} />);
    expect([seg('ДД').value, seg('ММ').value, seg('ГГГГ').value]).toEqual(['05', '01', '2025']);
  });
});

describe('DateInput: значение показывается, а не пропадает', () => {
  it('ISO со временем и ДД.ММ.ГГГГ из ячейки — не пустое поле', () => {
    render(<DateInput value="2026-10-18T00:00:00" onChange={() => {}} calendar={false} />);
    expect([seg('ДД').value, seg('ММ').value, seg('ГГГГ').value]).toEqual(['18', '10', '2026']);
    cleanup();
    render(<DateInput value="18.10.2026" onChange={() => {}} compact />);
    expect(seg('ДД').value).toBe('18');
  });

  it('не дата — текстом с пометкой; переписать её можно', () => {
    const onChange = vi.fn();
    render(<DateInput value="вчера" onChange={onChange} calendar={false} />);
    const text = screen.getByRole('button', { name: /вчера — не дата/ });
    expect(screen.getByRole('alert').textContent).toMatch(/не дата/);
    vi.useFakeTimers();
    fireEvent.click(text);
    act(() => { vi.runOnlyPendingTimers(); });
    typeDate('18', '10', '2026');
    expect(onChange).toHaveBeenLastCalledWith('2026-10-18');
  });
});
