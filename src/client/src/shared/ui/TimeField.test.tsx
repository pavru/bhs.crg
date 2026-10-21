// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { TimeField } from './TimeField';
import { parseTime, readTime, timeSegmentsOf } from './timeSegments';

vi.mock('@/shared/hooks/useLocale', async importOriginal => ({
  ...(await importOriginal<typeof import('@/shared/hooks/useLocale')>()),
  useLocale: () => ['ru-RU', () => {}, 'idle'],
}));

/** Поле времени ЧЧ:ММ (issue #1468, вариант А): набор, вставка, ошибки и подпись под рамкой. */

afterEach(cleanup);

const seg = (ph: 'ЧЧ' | 'ММ') =>
  within(screen.getByRole('group', { name: 'Время копии' })).getByPlaceholderText(ph) as HTMLInputElement;

describe('TimeField', () => {
  it('«4» в пустых часах — сразу 04 и к минутам: «444» даёт 04:44', () => {
    const onChange = vi.fn();
    render(<TimeField label="Время копии" value="" onChange={onChange} />);
    seg('ЧЧ').focus();
    fireEvent.change(seg('ЧЧ'), { target: { value: '4' } });
    expect(seg('ЧЧ').value).toBe('04');
    expect(document.activeElement).toBe(seg('ММ'));
    fireEvent.change(seg('ММ'), { target: { value: '44' } });
    expect(onChange).toHaveBeenLastCalledWith('04:44');
  });

  it('вставка из английского письма переводится: «4:44 PM» → 16:44', () => {
    const onChange = vi.fn();
    render(<TimeField label="Время копии" value="03:00" onChange={onChange} />);
    fireEvent.paste(seg('ММ'), { clipboardData: { getData: () => '4:44 PM' } });
    expect(onChange).toHaveBeenLastCalledWith('16:44');
  });

  it('25 часов наружу не уходят — форме сразу «такого времени нет»', () => {
    const onChange = vi.fn(), onIncomplete = vi.fn();
    render(<TimeField label="Время копии" value="03:00" onChange={onChange} onIncomplete={onIncomplete} />);
    seg('ЧЧ').focus();
    fireEvent.change(seg('ЧЧ'), { target: { value: '25' } });
    expect(onIncomplete).toHaveBeenLastCalledWith('Такого времени нет');
    expect(onChange).not.toHaveBeenCalled();
  });

  it('подпись под рамкой описывает сегменты — пояс слышит и скринридер', () => {
    render(<TimeField label="Время копии" value="04:00" onChange={() => {}} hint="По поясу компании: Москва (UTC+03:00)" />);
    const describedBy = seg('ЧЧ').getAttribute('aria-describedby');
    expect(describedBy).toBeTruthy();
    expect(document.getElementById(describedBy!)?.textContent).toBe('По поясу компании: Москва (UTC+03:00)');
  });

  it('выключенное поле не правится', () => {
    render(<TimeField label="Время копии" value="04:00" onChange={() => {}} disabled />);
    expect(seg('ЧЧ').disabled).toBe(true);
    expect(seg('ММ').disabled).toBe(true);
  });
});

describe('timeSegments', () => {
  it.each([
    ['4:44', '04', '44'], ['04.44', '04', '44'], ['4,44', '04', '44'], ['0444', '04', '44'], ['444', '04', '44'],
    ['16:44:30', '16', '44'], ['12:05 AM', '00', '05'], ['12:05 pm', '12', '05'], ['4:44 p.m.', '16', '44'],
  ])('вставка «%s» → %s:%s', (text, h, min) => expect(parseTime(text)).toEqual({ h, min }));

  it('не время — не разбирается', () => expect(parseTime('завтра')).toBeNull());

  it('«16:44 PM» — ошибка, а не молчаливые 16:44', () =>
    expect(readTime(parseTime('16:44 PM')!)).toEqual({ kind: 'impossible' }));

  it('значение с секундами читается, чужое — нет', () => {
    expect(timeSegmentsOf('04:00:00')).toEqual({ h: '04', min: '00' });
    expect(timeSegmentsOf('утром')).toBeNull();
  });
});
