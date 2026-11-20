// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { Timeline } from './Timeline';
import { barPlace, timelineWindow } from './timelineWindow';

/** Лента периодов (change `work-12-assignments-ui`): окно по неделям, обрезка полос с краёв, строка вне окна. */

afterEach(cleanup);

describe('timelineWindow', () => {
  it('окно начинается неделей за две до недели «сегодня», с понедельника', () => {
    // 20.11.2026 — пятница; неделя — с 16.11, окно — с 02.11 по 27.12.
    const w = timelineWindow('2026-11-20', 0, 8, 1);
    expect(new Date(w.start * 86_400_000).toISOString().slice(0, 10)).toBe('2026-11-02');
    expect(new Date(w.end * 86_400_000).toISOString().slice(0, 10)).toBe('2026-12-27');
    expect(w.todayAt).toBe(18);
  });

  it('«Позже» сдвигает окно на неделю', () => {
    expect(timelineWindow('2026-11-20', 1, 8, 1).start - timelineWindow('2026-11-20', 0, 8, 1).start).toBe(7);
  });
});

describe('barPlace', () => {
  const { start, days } = timelineWindow('2026-11-20', 0, 8, 1);

  it('бессрочный период до края — обрезан справа и открыт', () => {
    expect(barPlace('2026-09-01', null, start, days)).toMatchObject({ left: 0, width: 1, cutStart: true, cutEnd: true, open: true });
  });

  it('период внутри окна — доли от ширины', () => {
    const p = barPlace('2026-11-16', '2026-11-29', start, days)!;
    expect(p.left).toBeCloseTo(14 / 56);
    expect(p.width).toBeCloseTo(14 / 56);
    expect(p.cutStart || p.cutEnd).toBe(false);
  });

  it('период целиком вне окна — полосы нет', () => {
    expect(barPlace('2026-10-01', '2026-10-31', start, days)).toBeNull();
    expect(barPlace('2027-01-01', '2027-01-31', start, days)).toBeNull();
  });
});

describe('Timeline', () => {
  it('строка вне окна остаётся и говорит «вне окна»; полоса — кнопка с полным именем', () => {
    let opened = '';
    render(
      <Timeline aria-label="Лента" nameLabel="Сотрудник" today="2026-11-20" rows={[
        { key: '1', label: 'Белов', from: '2026-11-01', to: '2026-11-30', tone: 'primary', barLabel: '01.11 — 30.11', aria: 'Белов, 01.11.2026 — 30.11.2026', onOpen: () => { opened = '1'; } },
        { key: '2', label: 'Ушедший', from: '2026-10-01', to: '2026-10-15', tone: 'muted', barLabel: '01.10 — 15.10', aria: 'Ушедший' },
      ]} />,
    );
    fireEvent.click(screen.getByRole('button', { name: 'Белов, 01.11.2026 — 30.11.2026' }));
    expect(opened).toBe('1');
    expect(screen.getByText('вне окна · 01.10 — 15.10')).toBeTruthy();
    // Три недели назад окно начинается 12.10 — период 01.10–15.10 в него входит.
    for (let i = 0; i < 3; i++) fireEvent.click(screen.getByRole('button', { name: 'Раньше на неделю' }));
    expect(screen.queryByText('вне окна · 01.10 — 15.10')).toBeNull();
  });
});
