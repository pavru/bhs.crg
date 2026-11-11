// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import type { HomeQueue } from '@/shared/api/homeQueues';
import type { NavItem } from '@/shared/ui/navConfig';
import { workNav } from '@/shared/ui/navConfig';
import { HomePage } from './HomePage';

/** Главная «Продолжить работу» (shell-08, ТЗ UI-33…UI-37). */

const home = workNav.find(i => i.to === '/home')!;
const sites = workNav.find(i => i.to === '/document-sets')!;

let nav: { allWork: NavItem[]; settings: NavItem[] } = { allWork: [home, sites], settings: [] };
vi.mock('@/shared/ui/navSections', () => ({ useNavSections: () => nav }));

let queues: { data?: HomeQueue[]; isPending: boolean; isError: boolean } = { data: [], isPending: false, isError: false };
const refetch = vi.fn();
vi.mock('@/shared/api/homeQueues', () => ({ useHomeQueues: () => ({ ...queues, refetch }) }));

let jobs: { id: string; title: string }[] = [];
vi.mock('@/shared/api/jobs', () => ({ useActiveJobList: () => jobs }));
vi.mock('@/features/jobs/JobRow', () => ({ JobRow: ({ job }: { job: { title: string } }) => <div>{job.title}</div> }));

let permissions: string[] = [];
vi.mock('@/shared/api/access', async importOriginal => ({
  ...await importOriginal<typeof import('@/shared/api/access')>(),
  useAccess: () => ({ data: { permissions, modules: [] } }),
}));

let tipsShown = true;
const setTipsShown = vi.fn();
vi.mock('@/shared/hooks/useHomeTips', () => ({
  useHomeTips: () => ({ shown: tipsShown, setShown: setTipsShown, save: 'idle' }),
}));

const info = vi.fn();
vi.mock('@/shared/ui/Snackbar', () => ({ useSnackbar: () => ({ info, error: vi.fn() }) }));

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
  nav = { allWork: [home, sites], settings: [] };
  queues = { data: [], isPending: false, isError: false };
  jobs = [];
  permissions = [];
  tipsShown = true;
});

const queue = (id: string, label: string, count: number | null, extra: Partial<HomeQueue> = {}): HomeQueue => ({
  module: 'costs', moduleTitle: 'Счета и накладные', id, label, hint: 'пояснение', quiet: false,
  count, doubt: null, refused: count === null ? 'проверка ссылок не ответила' : null, ...extra,
});

const shown = () => render(<MemoryRouter><HomePage /></MemoryRouter>);

describe('Главная', () => {
  it('заголовок и подзаголовок; без очередей и задач — только «Полезно знать», не отказ', () => {
    shown();
    expect(screen.getByRole('heading', { level: 1, name: 'Продолжить работу' })).toBeTruthy();
    expect(screen.getByText('Что ждёт вас и что вы открывали недавно')).toBeTruthy();
    expect(screen.queryByRole('heading', { name: 'Ждут вас' })).toBeNull();
    expect(screen.queryByRole('heading', { name: 'Выполняется' })).toBeNull();
    expect(screen.getByRole('heading', { name: 'Полезно знать' })).toBeTruthy();
  });

  it('кроме Главной ничего не открыто — страница «нет доступа» тем же правилом, что расчёт входа', () => {
    nav = { allWork: [home], settings: [] };
    shown();
    expect(screen.getByRole('heading', { name: 'Нет доступных разделов' })).toBeTruthy();
    expect(screen.queryByRole('heading', { name: 'Продолжить работу' })).toBeNull();
  });
});

describe('«Ждут вас»', () => {
  it('карточка — ссылка в очередь экрана, с числом и модулем; ноль без оговорки — карточки нет', () => {
    queues.data = [queue('parsing', 'Разобрать', 3), queue('lost', 'Удалённые записи', 0)];
    shown();
    const card = screen.getByRole('link', { name: 'Разобрать' });
    expect(card.getAttribute('href')).toBe('/invoices?queue=parsing');
    const described = document.getElementById(card.getAttribute('aria-describedby')!)!;
    expect(described.textContent).toContain('3');
    expect(described.textContent).toContain('Счета и накладные');
    expect(screen.queryByRole('link', { name: 'Удалённые записи' })).toBeNull();
  });

  it('число не пришло — карточка остаётся со словами «не посчитано», а не нулём', () => {
    queues.data = [queue('lost', 'Удалённые записи', null)];
    shown();
    const card = screen.getByTestId('home-queue-costs-lost');
    expect(within(card).getByText('не посчитано')).toBeTruthy();
    expect(within(card).getByText('проверка ссылок не ответила')).toBeTruthy();
  });

  it('проверено не всё — число с «?», ноль с оговоркой карточку не прячет, знак — сомнения', () => {
    queues.data = [queue('lost', 'Удалённые записи', 0, { doubt: 'часть ссылок не проверена' })];
    shown();
    const card = screen.getByTestId('home-queue-costs-lost');
    expect(within(card).getByText('?')).toBeTruthy();
    expect(within(card).getByLabelText('проверено не всё')).toBeTruthy();
  });

  it('все открытые пусты — одна строка «Сейчас ничего не ждёт»', () => {
    queues.data = [queue('parsing', 'Разобрать', 0)];
    shown();
    expect(screen.getByText('Сейчас ничего не ждёт')).toBeTruthy();
  });

  it('ответ в пути — полоса; отказ всего ответа — строка с «Повторить»', () => {
    queues = { data: undefined, isPending: true, isError: false };
    shown();
    expect(screen.getByRole('progressbar', { name: 'Очереди загружаются' })).toBeTruthy();
    cleanup();

    queues = { data: undefined, isPending: false, isError: true };
    shown();
    fireEvent.click(screen.getByRole('button', { name: 'Повторить' }));
    expect(refetch).toHaveBeenCalled();
  });

  it('очередь без адреса на клиенте карточки не даёт — она звала бы туда, куда не попасть', () => {
    queues.data = [queue('nowhere', 'Неизвестно', 5, { module: 'plan' })];
    shown();
    expect(screen.queryByRole('link', { name: 'Неизвестно' })).toBeNull();
  });
});

describe('«Выполняется» и «Полезно знать»', () => {
  it('без задач блока нет; с задачей — строка задачи', () => {
    jobs = [{ id: 'j1', title: 'Распознаётся счёт' }];
    shown();
    expect(screen.getByRole('heading', { name: 'Выполняется' })).toBeTruthy();
    expect(screen.getByText('Распознаётся счёт')).toBeTruthy();
  });

  it('строка о сканах — только с правом на правку счетов', () => {
    shown();
    expect(screen.queryByText(/Перетащите сканы/)).toBeNull();
    cleanup();
    permissions = ['costs.invoice.edit'];
    shown();
    expect(screen.getByText(/Перетащите сканы/)).toBeTruthy();
  });

  it('«Скрыть» пишет ключ и предлагает «Вернуть»; скрытого блока нет', () => {
    shown();
    fireEvent.click(screen.getByRole('button', { name: 'Скрыть' }));
    expect(setTipsShown).toHaveBeenCalledWith(false);
    const [, options] = info.mock.calls[0];
    options.action.onClick();
    expect(setTipsShown).toHaveBeenLastCalledWith(true);
    cleanup();

    tipsShown = false;
    shown();
    expect(screen.queryByRole('heading', { name: 'Полезно знать' })).toBeNull();
  });
});
