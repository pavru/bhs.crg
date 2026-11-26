// @vitest-environment jsdom
import { cleanup, render, screen } from '@testing-library/react';
import { afterEach, expect, it } from 'vitest';
import type { FactDiagnostics } from '@/shared/api/progress';
import { ScheduleFactCell, ScheduleFactNotice } from './ScheduleFact';

afterEach(cleanup);

const diagnostics: FactDiagnostics = {
  scope: { constructionId: 'site', from: null, to: null }, awaitingAcceptance: 3, excludedCrewGroups: 2,
  crewExclusions: [{ reason: 'MultipleKeys', groups: 2 }], withoutPlanItem: 1, unsubmitted: 1,
};

it('без Work показывает состояние подключения без чисел факта', () => {
  const fact = { state: 'not-connected' } as const;
  render(<><ScheduleFactNotice fact={fact} /><ScheduleFactCell fact={fact} unitName="м" /></>);
  expect(screen.getByText('Учёт работ не подключён')).toBeTruthy();
  expect(screen.queryByText(/^0\sм$/)).toBeNull();
});

it('показывает включённый объём и причины неполного факта в охвате стройки', () => {
  const shown = { state: 'partial', volume: 12 } as const;
  render(<>
    <ScheduleFactNotice fact={shown} diagnostics={diagnostics} />
    <ScheduleFactCell fact={shown} unitName="м" />
    <ScheduleFactCell fact={{ state: 'partial', volume: null }} unitName="м" />
  </>);
  expect(screen.getByText('Факт неполный')).toBeTruthy();
  expect(screen.getByText('Диагностика факта по всей стройке за весь период')).toBeTruthy();
  expect(screen.getByText(/разные позиции, разделы или единицы: 2/)).toBeTruthy();
  expect(screen.getByText(/^12\sм$/)).toBeTruthy();
  expect(screen.getByLabelText('Нет включённого объёма')).toBeTruthy();
  expect(screen.queryByText(/^0\sм$/)).toBeNull();
});

it('при закрытом факте показывает право и скрывает диагностику', () => {
  const fact = { state: 'hidden', permission: 'work.facts.read' } as const;
  render(<ScheduleFactNotice fact={fact} diagnostics={diagnostics} />);
  expect(screen.getByText('work.facts.read')).toBeTruthy();
  expect(screen.queryByText('Факт неполный')).toBeNull();
  expect(screen.queryByText(/Отчётов ожидают приёмки/)).toBeNull();
});

it('число неподанных участников остаётся предупреждением при полном факте', () => {
  render(<ScheduleFactNotice fact={{ state: 'ok', volume: 12 }} diagnostics={{
    ...diagnostics, awaitingAcceptance: 0, excludedCrewGroups: 0, crewExclusions: [], withoutPlanItem: 0,
  }} />);
  expect(screen.getByText('Неподанные участники по бригадным группам: 1')).toBeTruthy();
  expect(screen.queryByText('Факт неполный')).toBeNull();
});
