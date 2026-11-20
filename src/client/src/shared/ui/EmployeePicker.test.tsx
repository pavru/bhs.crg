// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import type { AssignmentCandidate } from '@/shared/api/work';
import { EmployeePicker } from './EmployeePicker';

/**
 * Выбор сотрудника (change `work-12-assignments-ui`, задача 2.2): кандидаты и причины — от сервера, исключённый не
 * пропадает, пометка учётной записи — по полю ответа, «справочник не заведён» — словами.
 */

afterEach(cleanup);

const cand = (name: string, over: Partial<AssignmentCandidate> = {}): AssignmentCandidate => ({
  employeeId: name, name, personnelNumber: '01', eligible: true, reason: null, assigned: null,
  account: { state: 'linked' }, ...over,
});

function mount(props: Partial<Parameters<typeof EmployeePicker>[0]> = {}) {
  const onChange = vi.fn();
  render(
    <EmployeePicker candidates={[]} selected={new Set()} onChange={onChange} search="" onSearch={() => {}} {...props} />,
  );
  return onChange;
}

describe('EmployeePicker', () => {
  it('исключённый сервером кандидат — в списке, неактивен, с причиной рядом', () => {
    mount({ candidates: [cand('Зайцев А. И.', { eligible: false, reason: 'уволен с 15.11.2026' })] });
    const box = screen.getByRole('checkbox', { name: 'Отметить: Зайцев А. И.' }) as HTMLInputElement;
    expect(box.disabled).toBe(true);
    expect(box.checked).toBe(false);
    expect(screen.getByText('уволен с 15.11.2026')).toBeTruthy();
  });

  it('account.state = none — пометка «нет учётной записи», выбрать можно', () => {
    const onChange = mount({ candidates: [cand('Волков О. П.', { account: { state: 'none' } })] });
    expect(screen.getByText('нет учётной записи')).toBeTruthy();
    fireEvent.click(screen.getByRole('checkbox', { name: 'Отметить: Волков О. П.' }));
    expect([...onChange.mock.calls[0][0]]).toEqual(['Волков О. П.']);
  });

  it('удалённая учётная запись — своя пометка, а не «нет учётной записи»', () => {
    mount({ candidates: [cand('Орлов В. А.', { account: { state: 'deleted' } })] });
    expect(screen.getByText('учётная запись удалена')).toBeTruthy();
    expect(screen.queryByText('нет учётной записи')).toBeNull();
  });

  it('«Отметить всех годных» не отмечает негодных', () => {
    const onChange = mount({ candidates: [cand('А'), cand('Б', { eligible: false, reason: 'принят с 10.12.2026' }), cand('В')] });
    fireEvent.click(screen.getByRole('checkbox', { name: 'Отметить всех годных' }));
    expect([...onChange.mock.calls[0][0]].sort()).toEqual(['А', 'В']);
  });

  it('отказ записи — причина у названного цветом ошибки поверх причины сервера', () => {
    mount({ candidates: [cand('Мельник С. И.')], selected: new Set(['Мельник С. И.']), refused: { 'Мельник С. И.': 'уволен с 20.12.2026' } });
    const reason = screen.getByText('уволен с 20.12.2026');
    expect(reason.className).toContain('text-error');
    expect((screen.getByRole('checkbox', { name: 'Отметить: Мельник С. И.' }) as HTMLInputElement).checked).toBe(true);
  });

  it('справочник не заведён — словами сервера, а не «никого не нашлось»', () => {
    mount({ candidates: undefined, missing: 'Справочник «Сотрудник» не заведён.' });
    expect(screen.getByRole('alert').textContent).toBe('Справочник «Сотрудник» не заведён.');
    expect(screen.queryByRole('table')).toBeNull();
  });

  it('первый запрос в пути — «подбираем», а не «задайте период»', () => {
    mount({ candidates: undefined, loading: true });
    expect(screen.getByText('Подбираем кандидатов…')).toBeTruthy();
    expect(screen.queryByText(/Задайте период/)).toBeNull();
  });

  it('выдача обрезана — строка «показаны первые N»', () => {
    mount({ candidates: [cand('А')], more: true, limit: 200 });
    expect(screen.getByText(/Показаны первые 200/)).toBeTruthy();
  });
});
