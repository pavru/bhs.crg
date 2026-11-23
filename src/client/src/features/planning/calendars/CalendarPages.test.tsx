// @vitest-environment jsdom
import type { ReactNode } from 'react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { afterEach, beforeAll, beforeEach, expect, it, vi } from 'vitest';
import { apiClient } from '@/shared/api/client';
import type { CompanyCalendar, ConstructionCalendar } from '@/shared/api/planningCalendars';
import { LocaleContext } from '@/shared/hooks/useLocale';
import { CompanyCalendarPage } from './CompanyCalendarPage';
import { CompanyWeekDialog } from './CompanyCalendarDialogs';
import { ConstructionCalendarPanel } from './ConstructionCalendarPanel';

vi.mock('@/shared/api/client', () => ({ apiClient: { get: vi.fn(), put: vi.fn() } }));
vi.mock('@/shared/format/companyDay', () => ({ companyToday: () => '2027-02-20' }));
let rights: string[];
vi.mock('@/shared/api/access', () => ({ useCan: () => ({ permission: (code: string) => rights.includes(code) }) }));

const company: CompanyCalendar = {
  year: 2027, version: '70', configured: true, workingWeek: 31, hoursPerShift: 8,
  days: [{ date: '2027-02-20', kind: 'short' }],
  importFileName: null, importedAt: null, gaps: [],
};
const site: ConstructionCalendar = {
  constructionId: 'site', timeZoneId: 'Asia/Vladivostok', workingWeek: null, shifts: 1, days: [], version: '91',
};

function mount(child = <CompanyCalendarPage />) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const wrap = (page: ReactNode) => <QueryClientProvider client={client}>
    <LocaleContext.Provider value={['ru-RU', () => {}, 'idle']}><MemoryRouter>{page}</MemoryRouter></LocaleContext.Provider>
  </QueryClientProvider>;
  const view = render(wrap(child));
  return (page: ReactNode) => view.rerender(wrap(page));
}

function choose(label: string, name: string) {
  fireEvent.keyDown(screen.getByRole('combobox', { name: label }), { key: 'Enter' });
  fireEvent.click(screen.getByRole('option', { name }));
}

beforeAll(() => {
  Element.prototype.hasPointerCapture ??= () => false;
  Element.prototype.releasePointerCapture ??= () => {};
  Element.prototype.scrollIntoView ??= () => {};
});
beforeEach(() => {
  vi.stubGlobal('ResizeObserver', class { observe() {} disconnect() {} });
  rights = ['plan.schedule.read'];
});
afterEach(() => { cleanup(); vi.unstubAllGlobals(); vi.resetAllMocks(); });

it('чтение показывает исключение без действий редактирования года', async () => {
  vi.mocked(apiClient.get).mockResolvedValue({ data: company });
  mount();
  await screen.findByRole('tab', { name: 'I квартал' });
  expect(screen.getByRole('button', { name: /20 .*феврал.*Сокращённый день/i })).toBeTruthy();
  expect(screen.queryByRole('button', { name: 'Загрузить календарь…' })).toBeNull();
  expect(screen.queryByRole('button', { name: 'Рабочая неделя и часы…' })).toBeNull();
  expect(screen.queryByRole('button', { name: 'Сохранить день' })).toBeNull();
});

it('правка дня называет считанную версию и предлагает перечитать год после 409', async () => {
  rights = ['plan.calendar.edit'];
  vi.mocked(apiClient.get).mockResolvedValueOnce({ data: company }).mockResolvedValue({
    data: { ...company, version: '71', days: [{ date: '2027-02-20', kind: 'working' }] },
  });
  vi.mocked(apiClient.put).mockRejectedValue({ response: { status: 409 } });
  mount();
  await screen.findByRole('combobox', { name: 'Вид дня' });
  choose('Вид дня', 'Нерабочий день');
  fireEvent.click(screen.getByRole('button', { name: 'Сохранить день' }));
  await screen.findByText('Год тем временем изменили. Перечитайте календарь.');
  expect(apiClient.put).toHaveBeenCalledWith('/plan/calendars/company/2027', {
    workingWeek: 31, hoursPerShift: 8, days: [{ date: '2027-02-20', kind: 'holiday' }],
  }, { headers: { 'If-Match': '70' } });
  expect((screen.getByRole('button', { name: 'Сохранить день' }) as HTMLButtonElement).disabled).toBe(true);
  fireEvent.click(screen.getByRole('button', { name: 'Перечитать' }));
  await screen.findByRole('button', { name: /20 .*феврал.*Рабочий выходной/i });
});

it('сохраняет рабочую неделю и сменность стройки с одной версией прочитанного календаря', async () => {
  rights = ['plan.schedule.read', 'plan.schedule.edit'];
  vi.mocked(apiClient.get).mockResolvedValue({ data: site });
  let finish!: (answer: { data: ConstructionCalendar }) => void;
  vi.mocked(apiClient.put).mockImplementation(() => new Promise(resolve => { finish = resolve; }));
  mount(<ConstructionCalendarPanel constructionId="site" />);
  await screen.findByRole('heading', { name: 'Календарь стройки' });
  choose('Режим рабочей недели', 'Своя рабочая неделя');
  fireEvent.click(screen.getByRole('checkbox', { name: 'Суббота' }));
  choose('Смен в день', '2');
  fireEvent.click(screen.getByRole('button', { name: 'Сохранить календарь' }));
  await waitFor(() => expect(apiClient.put).toHaveBeenCalledWith('/plan/calendars/constructions/site', {
    workingWeek: 63, shifts: 2, days: [],
  }, { headers: { 'If-Match': '91' } }));
  for (const name of ['Режим рабочей недели', 'Смен в день']) {
    expect((screen.getByRole('combobox', { name }) as HTMLButtonElement).disabled).toBe(true);
  }
  expect((screen.getByRole('checkbox', { name: 'Суббота' }) as HTMLInputElement).disabled).toBe(true);
  expect((screen.getByRole('button', { name: 'Добавить исключение…' }) as HTMLButtonElement).disabled).toBe(true);
  await act(async () => { finish({ data: { ...site, version: '92', workingWeek: 63, shifts: 2 } }); });
  await waitFor(() => expect((screen.getByRole('combobox', { name: 'Смен в день' }) as HTMLButtonElement).disabled).toBe(false));
});

it('открытая форма недели сохраняет версию и исключения исходного календаря при обновлении props', async () => {
  vi.mocked(apiClient.put).mockRejectedValue({ response: { status: 409 } });
  const onClose = vi.fn();
  const onReload = vi.fn();
  const rerender = mount(<CompanyWeekDialog calendar={company} onClose={onClose} onReload={onReload} />);
  fireEvent.change(screen.getByRole('spinbutton', { name: 'Часов в смене' }), { target: { value: '7' } });
  rerender(<CompanyWeekDialog calendar={{ ...company, version: '71', workingWeek: 63, days: [] }}
    onClose={onClose} onReload={onReload} />);
  fireEvent.click(screen.getByRole('button', { name: 'Сохранить' }));
  await screen.findByText('Год тем временем изменили. Перечитайте календарь.');
  expect(apiClient.put).toHaveBeenCalledWith('/plan/calendars/company/2027', {
    workingWeek: 31, hoursPerShift: 7, days: company.days,
  }, { headers: { 'If-Match': '70' } });
  expect(onClose).not.toHaveBeenCalled();
});
