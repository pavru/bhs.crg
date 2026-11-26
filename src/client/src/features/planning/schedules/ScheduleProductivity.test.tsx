// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { afterEach, expect, it } from 'vitest';
import type { ScheduleProductivity as Value } from '@/shared/api/planningScheduleTypes';
import { ScheduleProductivity } from './ScheduleProductivity';

afterEach(cleanup);

function show(value: Value) {
  return render(<ScheduleProductivity value={value} unit="м" name="Кабель" />);
}

it('показывает последний доступный месяц и раскрывает месяцы с их выборками', () => {
  show({ state: 'ok', minSample: { reports: 10, constructions: 2 }, months: [
    { month: '2026-10-01', productivity: { state: 'ok', volumePerHour: 5, sample: { reports: 14, constructions: 3 } } },
    { month: '2026-11-01', productivity: { state: 'too-little-data', sample: { reports: 4, constructions: 1 } } },
  ] });
  expect(screen.getByText('5 м/ч')).toBeTruthy();
  expect(screen.getByText(/14 отчётов · 3 стройки/)).toBeTruthy();
  expect(screen.queryByText('Данных мало')).toBeNull();
  const button = screen.getByRole('button', { name: 'Показать выработку по месяцам: Кабель' });
  fireEvent.click(button);
  expect(button.getAttribute('aria-expanded')).toBe('true');
  const panel = within(document.getElementById(button.getAttribute('aria-controls')!)!);
  expect(panel.getByText('Данных мало')).toBeTruthy();
  expect(panel.getByText('4 отчёта · 1 стройка')).toBeTruthy();
  fireEvent.click(button);
  expect(screen.queryByText('Данных мало')).toBeNull();
});

it('сохраняет состояние малой выборки без нулевой выработки', () => {
  show({ state: 'too-little-data', minSample: { reports: 10, constructions: 2 }, months: [
    { month: '2026-11-01', productivity: { state: 'too-little-data', sample: { reports: 4, constructions: 1 } } },
  ] });
  expect(screen.getByText('Данных мало')).toBeTruthy();
  expect(screen.getByText(/4 отчёта · 1 стройка/)).toBeTruthy();
  expect(screen.queryByText(/м\/ч/)).toBeNull();
});

it('называет право при скрытой выработке', () => {
  show({ state: 'hidden', permission: 'work.facts.read' });
  expect(screen.getByText(/Скрыто правами/)).toBeTruthy();
  expect(screen.getByText('work.facts.read')).toBeTruthy();
  expect(screen.queryByRole('button')).toBeNull();
});

it('показывает выключенный Work как состояние', () => {
  show({ state: 'not-connected' });
  expect(screen.getByText('Учёт работ не подключён')).toBeTruthy();
  expect(screen.queryByRole('button')).toBeNull();
});
