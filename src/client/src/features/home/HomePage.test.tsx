// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import type { HomeQueue } from '@/shared/api/homeQueues';
import type { RecentRecord, RecentRecords } from '@/shared/api/recentRecords';
import type { NavItem } from '@/shared/ui/navConfig';
import { workNav } from '@/shared/ui/navConfig';
import { HomePage } from './HomePage';

/** Главная «Продолжить работу» (shell-08, ТЗ UI-33…UI-37). */

const home = workNav.find(i => i.to === '/home')!;
const sites = workNav.find(i => i.to === '/document-sets')!;

let nav: { work: NavItem[]; settings: NavItem[] } = { work: [home, sites], settings: [] };
vi.mock('@/shared/ui/navSections', () => ({ useNavSections: () => nav }));

let queues: { data?: HomeQueue[]; isPending: boolean; isError: boolean } = { data: [], isPending: false, isError: false };
const refetch = vi.fn();
vi.mock('@/shared/api/homeQueues', () => ({ useHomeQueues: () => ({ ...queues, refetch }) }));

let jobs: { id: string; title: string }[] = [];
vi.mock('@/shared/api/jobs', () => ({ useActiveJobList: () => jobs }));
vi.mock('@/features/jobs/JobRow', () => ({ JobRow: ({ job }: { job: { title: string } }) => <div>{job.title}</div> }));

let recent: { data?: RecentRecords; isPending: boolean; isError: boolean } = { data: { items: [], refused: [] }, isPending: false, isError: false };
vi.mock('@/shared/api/recentRecords', () => ({ useRecentRecords: () => ({ ...recent, refetch }) }));

let permissions: string[] = [];
let modules: { code: string; title: string; available: boolean }[] = [];
let accessPending = false;
vi.mock('@/shared/api/access', async importOriginal => ({
  ...await importOriginal<typeof import('@/shared/api/access')>(),
  useAccess: () => (accessPending
    ? { data: undefined, isPending: true }
    : { data: { permissions, modules }, isPending: false }),
}));
vi.mock('@/shared/hooks/useAuth', () => ({ useAuth: () => ({ user: { id: 'u1' } }) }));
vi.mock('@/shared/api/userSettings', () => ({ useUserSettings: () => ({ isPending: false }) }));

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
  nav = { work: [home, sites], settings: [] };
  accessPending = false;
  queues = { data: [], isPending: false, isError: false };
  jobs = [];
  recent = { data: { items: [], refused: [] }, isPending: false, isError: false };
  permissions = [];
  modules = [];
  tipsShown = true;
});

const queue = (id: string, label: string, count: number | null, extra: Partial<HomeQueue> = {}): HomeQueue => ({
  module: 'costs', moduleTitle: 'Счета и накладные', id, label, hint: 'пояснение', quiet: false,
  count, doubt: null, refused: count === null ? 'проверка ссылок не ответила' : null, ...extra,
});

const ID = '3fa85f64-5717-4562-b3fc-2c963f66afa6';
const invoice = (extra: Partial<RecentRecord> = {}): RecentRecord => ({
  recordType: 'СчётНаОплату', group: 'invoices', groupTitle: 'Счета', module: 'costs', id: ID,
  title: '№ 418 · ООО «КабельТорг»', detail: null, place: 'ЖК «Северный»', amount: null, level: null,
  openedAt: new Date(Date.now() - 12 * 60_000).toISOString(), ...extra,
});
const costsReader = () => {
  permissions = ['costs.invoice.read'];
  modules = [{ code: 'costs', title: 'Счета и накладные', available: true }];
};

const shown = () => render(<MemoryRouter><HomePage /></MemoryRouter>);

describe('Главная', () => {
  it('заголовок и подзаголовок; без очередей и задач — «Недавние» и «Полезно знать», не отказ', () => {
    shown();
    expect(screen.getByRole('heading', { level: 1, name: 'Продолжить работу' })).toBeTruthy();
    expect(screen.getByText('Что ждёт вас и что вы открывали недавно')).toBeTruthy();
    expect(screen.queryByRole('heading', { name: 'Ждут вас' })).toBeNull();
    expect(screen.queryByRole('heading', { name: 'Выполняется' })).toBeNull();
    expect(screen.getByRole('heading', { name: 'Полезно знать' })).toBeTruthy();
    expect(screen.getByRole('heading', { name: 'Недавние' })).toBeTruthy();
    expect(screen.getByText('Вы пока ничего не открывали')).toBeTruthy();
  });

  it('доступ в пути — ни блоков, ни «нет доступа»: меню из одной Главной ещё не ответ', () => {
    accessPending = true;
    nav = { work: [home], settings: [] };
    const { container } = shown();
    expect(container.textContent).toBe('');
  });

  it('кроме Главной ничего не открыто — страница «нет доступа» тем же правилом, что расчёт входа', () => {
    nav = { work: [home], settings: [] };
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

  it('перечитывание отказало при прежних данных — «не обновились» и без старых чисел', () => {
    queues = { data: [queue('parsing', 'Разобрать', 3)], isPending: false, isError: true };
    shown();
    expect(screen.getByText('Числа очередей не обновились.')).toBeTruthy();
    expect(screen.queryByRole('link', { name: 'Разобрать' })).toBeNull();
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

  describe('Недавние', () => {
    it('строка — ссылка на запись: название, раздел и стройка, когда', () => {
      costsReader();
      recent = { data: { items: [invoice()], refused: [] }, isPending: false, isError: false };
      shown();
      const row = screen.getByTestId('home-recent-row');
      expect(row.getAttribute('href')).toBe(`/invoices?invoice=${ID}`);
      expect(row.textContent).toContain('№ 418 · ООО «КабельТорг»');
      expect(row.textContent).toContain('Счета · ЖК «Северный»');
      expect(row.textContent).toContain('12 мин назад');
      expect(screen.queryByText('Вы пока ничего не открывали')).toBeNull();
    });

    it('экран записи закрыт — строки нет: ссылка вела бы в отказ', () => {
      recent = { data: { items: [invoice()], refused: [] }, isPending: false, isError: false };
      shown();
      expect(screen.queryByTestId('home-recent-row')).toBeNull();
    });

    it('группа не ответила — сказано словами, а не пусто', () => {
      recent = { data: { items: [], refused: [{ group: 'invoices', title: 'Счета', reason: 'модуль не ответил' }] }, isPending: false, isError: false };
      shown();
      expect(screen.getByText('Счета: не показаны — модуль не ответил.')).toBeTruthy();
      expect(screen.queryByText('Вы пока ничего не открывали')).toBeNull();
    });

    it('отказ ответа — строка с «Повторить», прежних строк нет', () => {
      costsReader();
      recent = { data: { items: [invoice()], refused: [] }, isPending: false, isError: true };
      shown();
      expect(screen.getByText('Недавние не обновились.')).toBeTruthy();
      expect(screen.queryByTestId('home-recent-row')).toBeNull();
    });
  });
});
