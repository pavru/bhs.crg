// @vitest-environment jsdom
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { DateField } from './DateField';

vi.mock('@/shared/hooks/useLocale', async importOriginal => ({
  ...(await importOriginal<typeof import('@/shared/hooks/useLocale')>()),
  useLocale: () => ['ru-RU', () => {}, 'idle'],
}));

/**
 * Набранное — не дата — уходит форме сразу (ревью PR #1556): недописанная дата наружу не отдаётся, и
 * форма, пишущая по кнопке, иначе записала бы прежнюю дату молча.
 */

beforeAll(() => {
  globalThis.ResizeObserver ??= class { observe() {} unobserve() {} disconnect() {} } as unknown as typeof ResizeObserver;
});
afterEach(cleanup);

const seg = (ph: 'ДД' | 'ММ' | 'ГГГГ') =>
  within(screen.getByRole('group', { name: 'Дата отпуска' })).getByPlaceholderText(ph) as HTMLInputElement;

describe('DateField: onIncomplete', () => {
  it('недописанная дата — форме сразу, ещё в поле; дописанная — снимает', () => {
    const onChange = vi.fn(), onIncomplete = vi.fn();
    render(<DateField label="Дата отпуска" value="2026-09-01" onChange={onChange} onIncomplete={onIncomplete} />);
    fireEvent.focus(seg('ГГГГ'));
    fireEvent.change(seg('ГГГГ'), { target: { value: '2' } });
    expect(onIncomplete).toHaveBeenLastCalledWith('Дата не дописана');
    expect(onChange).not.toHaveBeenCalled();

    fireEvent.change(seg('ГГГГ'), { target: { value: '2027' } });
    expect(onChange).toHaveBeenLastCalledWith('2027-09-01');
    expect(onIncomplete).toHaveBeenLastCalledWith(null);
  });

  it('несуществующая дата — форме сразу', () => {
    const onIncomplete = vi.fn();
    render(<DateField label="Дата отпуска" value="2026-09-01" onChange={() => {}} onIncomplete={onIncomplete} />);
    fireEvent.focus(seg('ДД'));
    fireEvent.change(seg('ДД'), { target: { value: '31' } });
    expect(onIncomplete).toHaveBeenLastCalledWith('Такой даты нет');
  });
});
