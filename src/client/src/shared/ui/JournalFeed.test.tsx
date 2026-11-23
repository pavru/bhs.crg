// @vitest-environment jsdom
import { cleanup, render, screen, within } from '@testing-library/react';
import { afterEach, expect, it } from 'vitest';
import { JournalFeed } from './JournalFeed';

afterEach(cleanup);

it('показывает новые записи первыми по номеру даже при несовпадающих отметках времени', () => {
  render(<JournalFeed entries={[
    { sequence: 1, title: 'Отчёт создан', author: 'Монтажник', serverTime: '2026-11-22T16:00:00Z' },
    { sequence: 2, title: 'Отчёт подан', author: 'Монтажник', serverTime: '2026-11-22T15:00:00Z' },
  ]} />);
  const rows = screen.getAllByRole('listitem');
  expect(within(rows[0]).getByText('Отчёт подан')).toBeTruthy();
  expect(within(rows[1]).getByText('Отчёт создан')).toBeTruthy();
});

it('показывает автора ввода и исполнителя, когда это разные люди', () => {
  render(<JournalFeed entries={[
    { sequence: 1, title: 'Отчёт создан', author: 'Ирина Иванова', executor: 'Петр Петров', serverTime: '2026-11-22T16:00:00Z' },
  ]} />);
  expect(screen.getByText('Ирина Иванова')).toBeTruthy();
  expect(screen.getByText('Выполнил · Петр Петров')).toBeTruthy();
});
