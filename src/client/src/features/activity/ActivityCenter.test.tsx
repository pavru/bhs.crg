// @vitest-environment jsdom
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import type { ActiveJob } from '@/shared/api/jobs';
import type { ComponentHealth, NotificationDto } from '@/shared/api/notifications';
import { NavigationGuardProvider } from '@/shared/ui/NavigationGuardProvider';
import { ActivityCenter } from './ActivityCenter';

/**
 * Кнопка активности (issue #1431): одна вместо пилюли задач и колокольчика. Её имя называет и задачу,
 * и число уведомлений; поповер открывается с клавиатуры; состояние системы — строкой, только когда
 * что-то не в норме.
 */

let jobs: ActiveJob[] = [];
let items: NotificationDto[] = [];
let health: ComponentHealth[] = [];
const cancel = vi.fn();
const markAll = vi.fn();
const clearAll = vi.fn();
vi.mock('@/shared/api/jobs', () => ({
  useActiveJobs: () => jobs,
  useCancelJob: () => ({ mutate: cancel, isPending: false }),
}));
vi.mock('@/shared/api/notifications', () => ({
  useNotifications: () => ({ data: { items, unreadCount: items.filter(n => !n.isRead).length } }),
  useHealth: () => ({ data: health }),
  useMarkNotificationRead: () => ({ mutate: vi.fn() }),
  useMarkAllNotificationsRead: () => ({ mutate: markAll }),
  useDismissNotification: () => ({ mutate: vi.fn() }),
  useClearNotifications: () => ({ mutate: clearAll }),
}));

beforeAll(() => {
  // Слой поповера меряет якорь — в jsdom этого нет.
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
});
afterEach(() => { cleanup(); vi.clearAllMocks(); jobs = []; items = []; health = []; });

const at = new Date().toISOString();
const job = (id: string, title: string, status: ActiveJob['status'] = 'Running'): ActiveJob =>
  ({ id, kind: 'RecognizeDocument', targetId: 't', status, title, progress: null, createdAt: at, startedAt: at });
const note = (id: string, severity: NotificationDto['severity'] = 'Info', isRead = false) =>
  ({ id, title: `Уведомление ${id}`, message: 'текст', severity, isRead, createdAt: at, source: null, linkUrl: null, linkLabel: null }) as unknown as NotificationDto;
const component = (code: string, name: string, state: ComponentHealth['state']): ComponentHealth =>
  ({ code, name, state, healthy: state === 'Up', checkedAt: at });

function shown() {
  render(<MemoryRouter><NavigationGuardProvider><ActivityCenter /></NavigationGuardProvider></MemoryRouter>);
  return screen.getByTestId('activity');
}

function open() {
  const button = shown();
  act(() => button.focus());
  fireEvent.keyDown(button, { key: 'Enter' });
  fireEvent.click(button);
  return screen.getByRole('dialog', { name: 'Активность' });
}

describe('кнопка активности', () => {
  it('без задач — колокольчик, число новых — на brand', () => {
    items = [note('1'), note('2'), note('3', 'Info', true)];
    const button = shown();
    expect(button.getAttribute('aria-label')).toBe('Уведомления — новых уведомлений: 2');
    expect(within(button).getByText('2').className).toContain('bg-brand');
  });

  it('новая ошибка красит число и названа в имени', () => {
    items = [note('1'), note('2', 'Error')];
    const button = shown();
    expect(button.getAttribute('aria-label')).toBe('Уведомления — новых уведомлений: 2, среди них ошибка');
    expect(within(button).getByText('2').className).toContain('bg-danger');
  });

  it('с задачей — пилюля, имя называет и задачу, и уведомления', () => {
    jobs = [job('j1', 'Распознаётся счёт 293011.pdf')];
    const button = shown();
    expect(button.getAttribute('aria-label')).toBe('Выполняется: Распознаётся счёт 293011.pdf; новых уведомлений нет');
    expect(within(button).getByText('Распознаётся счёт 293011.pdf')).toBeTruthy();
  });

  it('несколько задач — их число', () => {
    jobs = [job('j1', 'А'), job('j2', 'Б')];
    items = [note('1')];
    expect(shown().getAttribute('aria-label')).toBe('Задач выполняется: 2; новых уведомлений: 1');
  });

  it('поповер: «Выполняется» над «Уведомлениями», Esc возвращает фокус', async () => {
    jobs = [job('j1', 'Сборка комплекта')];
    items = [note('1')];
    const dialog = open();
    // Фокус — на поповере, а не на первой кнопке: её подсказка забрала бы первый Esc.
    expect(document.activeElement).toBe(dialog);
    const headings =within(dialog).getAllByRole('heading').map(h => h.textContent);
    expect(headings).toEqual(['Выполняется', 'Уведомления']);
    expect(within(dialog).getByText('Сборка комплекта')).toBeTruthy();
    expect(within(dialog).getByText('Уведомление 1')).toBeTruthy();
    fireEvent.keyDown(dialog, { key: 'Escape' });
    await waitFor(() => expect(document.activeElement).toBe(screen.getByTestId('activity')));
  });

  it('без задач раздела «Выполняется» нет', () => {
    const dialog = open();
    expect(within(dialog).getAllByRole('heading').map(h => h.textContent)).toEqual(['Уведомления']);
    expect(within(dialog).getByText('Нет новых уведомлений')).toBeTruthy();
  });

  it('задачу в очереди можно отменить', () => {
    jobs = [job('j1', 'Ждёт', 'Queued')];
    fireEvent.click(within(open()).getByRole('button', { name: 'Отменить задачу' }));
    expect(cancel).toHaveBeenCalledWith('j1');
  });

  it('«Отметить все прочитанными» и «Очистить все»', () => {
    items = [note('1')];
    const dialog = open();
    fireEvent.click(within(dialog).getByRole('button', { name: 'Отметить все прочитанными' }));
    fireEvent.click(within(dialog).getByRole('button', { name: 'Очистить все' }));
    expect(markAll).toHaveBeenCalled();
    expect(clearAll).toHaveBeenCalled();
  });

  it('состояние системы в норме — строки нет', () => {
    health = [component('db', 'База данных', 'Up'), component('s3', 'Хранилище', 'Up')];
    expect(within(open()).queryByText(/Состояние системы/)).toBeNull();
  });

  it('что-то не в норме — строка, по ней раскрывается весь список', () => {
    health = [component('db', 'База данных', 'Up'), component('conv', 'Конвертер офисных файлов', 'Down')];
    const dialog = open();
    const line = within(dialog).getByRole('button', { name: /Состояние системы: Конвертер офисных файлов — недоступен/ });
    expect(line.getAttribute('aria-expanded')).toBe('false');
    expect(within(dialog).queryByText('База данных')).toBeNull();
    fireEvent.click(line);
    expect(line.getAttribute('aria-expanded')).toBe('true');
    expect(within(dialog).getByText('База данных')).toBeTruthy();
    expect(within(dialog).getByText('в норме')).toBeTruthy();
  });

  it('несколько не в норме — их число; «не проверяли» не в счёт', () => {
    health = [
      component('px', 'Прокси', 'Down'), component('ai', 'Распознавание', 'Unknown'),
      component('mail', 'Почта', 'Flapping'), component('db', 'База данных', 'Up'),
    ];
    expect(within(open()).getByRole('button', { name: /Состояние системы: не в норме: 2 из 4/ })).toBeTruthy();
  });
});
