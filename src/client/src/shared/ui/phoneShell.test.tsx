// @vitest-environment jsdom
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MemoryRouter, useLocation } from 'react-router';
import { BottomNav } from './BottomNav';
import { PrimaryFab } from './PrimaryFab';
import { PrimaryActionButton } from './PrimaryActionButton';
import { PrimaryFabContext, useCoversPrimaryFab, usePrimaryFabState } from './primaryFabContext';
import { PALETTE_ACTIONS } from './paletteActions';
import { NavigationGuardProvider } from './NavigationGuardProvider';
import { PageHeader } from './PageHeader';
import { railDef, type RailSection } from './railSections';
import { SectionMenuContext, type SectionMenuValue } from './sectionMenuContext';

/**
 * Оболочка телефона (issue #1434): разделы — нижней навигацией, страницы раздела — меню заголовка
 * страницы вместо подпанели.
 */

beforeAll(() => {
  // Слой меню меряет якорь — в jsdom этого нет.
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
});
afterEach(cleanup);

const page = (to: string, label: string, rail: RailSection['def']['id'], group?: string) =>
  ({ to, label, icon: railDef(rail).icon, rail, group });

const sections: RailSection[] = [
  { def: railDef('constructions'), groups: [{ items: [page('/document-sets', 'Стройки', 'constructions')] }], home: '/document-sets' },
  { def: railDef('costs'), groups: [{ items: [page('/invoices', 'Счета', 'costs'), page('/waybills', 'Накладные', 'costs')] }], home: '/invoices' },
  { def: railDef('system'), groups: [{ items: [page('/users', 'Пользователи', 'system')] }], home: '/users' },
];

const costs: SectionMenuValue = {
  def: railDef('costs'),
  groups: [
    { items: [page('/invoices', 'Счета', 'costs'), page('/waybills', 'Накладные', 'costs')] },
    { label: 'Отчёты и закрытие месяца', items: [page('/periods', 'Учётный период', 'costs', 'Отчёты и закрытие месяца')] },
  ],
  outside: [page('/datasets', 'Наборы данных', 'data')],
};

function Address() {
  const { pathname, search } = useLocation();
  return <output aria-label="адрес">{pathname + search}</output>;
}

function shown(ui: React.ReactNode, at = '/invoices', menu: SectionMenuValue | null = null) {
  render(
    <MemoryRouter initialEntries={[at]}>
      <NavigationGuardProvider>
        <SectionMenuContext.Provider value={menu}>
          {ui}
          <Address />
        </SectionMenuContext.Provider>
      </NavigationGuardProvider>
    </MemoryRouter>,
  );
}

// Какие разделы внизу (рабочие, без «Системы»), решает `phoneChrome` — см. его тест.
const work = sections.filter(s => !s.def.unscoped);

describe('нижняя навигация', () => {
  it('разделы с подписями; свой отмечен', () => {
    shown(<BottomNav sections={work} active="costs" />);
    const nav = screen.getByRole('navigation', { name: 'Разделы' });
    const links = nav.querySelectorAll('a');
    expect([...links].map(a => a.textContent)).toEqual(['Стройки', 'Затраты']);
    expect([...links].map(a => a.getAttribute('aria-current'))).toEqual([null, 'true']);
  });

  it('раздел ведёт на свою первую страницу', () => {
    shown(<BottomNav sections={work} active="costs" />);
    fireEvent.click(screen.getByRole('link', { name: 'Стройки' }));
    expect(screen.getByLabelText('адрес').textContent).toBe('/document-sets');
  });

  it('без рабочих разделов строки нет', () => {
    shown(<BottomNav sections={[]} active={null} />);
    expect(screen.queryByRole('navigation')).toBeNull();
  });
});

describe('плавающая кнопка основного действия', () => {
  it('названа действием и зовёт кнопку экрана, не закрывая открытую запись', () => {
    shown(<PrimaryFab action={PALETTE_ACTIONS.invoiceScan} />, '/invoices?invoice=7');
    fireEvent.click(screen.getByRole('button', { name: 'Счёт из файла' }));
    expect(screen.getByLabelText('адрес').textContent).toBe('/invoices?invoice=7&do=invoice-scan');
  });
});

/** Оболочка в миниатюре: состояние плавающей кнопки и то, что ей сообщают экраны (ревью PR #1502). */
function FabShell({ children }: { children: React.ReactNode }) {
  const state = usePrimaryFabState();
  return (
    <PrimaryFabContext.Provider value={state.report}>
      {children}
      {!state.covered && <PrimaryFab action={PALETTE_ACTIONS.waybillNew} busy={state.isBusy(PALETTE_ACTIONS.waybillNew.id)} />}
    </PrimaryFabContext.Provider>
  );
}

function Cover({ on }: { on: boolean }) {
  useCoversPrimaryFab(on);
  return null;
}

describe('что экран сообщает плавающей кнопке', () => {
  const fab = () => screen.queryByTestId('primary-fab');

  it('занятая кнопка экрана делает плавающую занятой — со спиннером и запретом', () => {
    const view = (loading: boolean) => (
      <FabShell><PrimaryActionButton action={PALETTE_ACTIONS.waybillNew} loading={loading} onClick={() => {}} /></FabShell>);
    const { rerender } = render(<MemoryRouter>{view(true)}</MemoryRouter>);
    expect(fab()?.hasAttribute('disabled')).toBe(true);
    expect(fab()?.getAttribute('aria-busy')).toBe('true');
    rerender(<MemoryRouter>{view(false)}</MemoryRouter>);
    expect(fab()?.hasAttribute('disabled')).toBe(false);
  });

  it('кнопка экрана подписана действием и прячется на телефоне', () => {
    render(<MemoryRouter><FabShell><PrimaryActionButton action={PALETTE_ACTIONS.waybillNew} onClick={() => {}} /></FabShell></MemoryRouter>);
    const own = screen.getAllByRole('button', { name: 'Новая накладная' }).find(b => b !== fab())!;
    expect(own.className).toContain('max-md:hidden');
  });

  it('«закрыто» держится, пока закрывает хоть один', () => {
    const view = (a: boolean, b: boolean) => <MemoryRouter><FabShell><Cover on={a} /><Cover on={b} /></FabShell></MemoryRouter>;
    const { rerender } = render(view(true, true));
    expect(fab()).toBeNull();
    // Один снял — второй всё ещё закрывает: флажок «последний пишущий побеждает» открыл бы кнопку.
    rerender(view(false, true));
    expect(fab()).toBeNull();
    rerender(view(false, false));
    expect(fab()).not.toBeNull();
  });
});

describe('меню раздела в заголовке страницы', () => {
  const trigger = () => screen.getByTestId('section-menu-trigger');
  function open() {
    act(() => trigger().focus());
    fireEvent.keyDown(trigger(), { key: 'Enter' });
    return screen.getByRole('menu');
  }

  it('на рабочем столе заголовок — просто заголовок', () => {
    shown(<PageHeader title="Счета на оплату" />);
    expect(screen.queryByTestId('section-menu-trigger')).toBeNull();
    expect(screen.getByRole('heading', { level: 1 }).textContent).toBe('Счета на оплату');
  });

  it('на телефоне заголовок раскрывает страницы раздела, над ним — имя раздела', () => {
    shown(<PageHeader title="Счета на оплату" />, '/invoices', costs);
    // Заголовок звучит названием страницы, о меню — описание кнопки.
    expect(screen.getByRole('heading', { level: 1, name: 'Счета на оплату' })).toBeTruthy();
    expect(trigger().getAttribute('aria-describedby')).toBeTruthy();
    expect(document.getElementById(trigger().getAttribute('aria-describedby')!)?.textContent)
      .toBe('Другие страницы раздела «Учёт затрат»');
    expect(screen.getByText('Учёт затрат')).toBeTruthy();
    expect(trigger().getAttribute('aria-haspopup')).toBe('menu');
    open();
    expect(screen.getAllByRole('menuitem').map(i => i.textContent)).toEqual(
      ['Счета', 'Накладные', 'Учётный период', 'Наборы данных']);
    // Группы подписаны, страницы вне пространства — своей группой в конце, как в палитре.
    expect(screen.getByText('Отчёты и закрытие месяца')).toBeTruthy();
    expect(screen.getByText('Вне пространства')).toBeTruthy();
    expect(screen.getByRole('menuitem', { name: 'Счета' }).getAttribute('aria-current')).toBe('page');
  });

  it('выбор страницы ведёт на неё', () => {
    shown(<PageHeader title="Счета на оплату" />, '/invoices', costs);
    open();
    fireEvent.click(screen.getByRole('menuitem', { name: 'Накладные' }));
    expect(screen.getByLabelText('адрес').textContent).toBe('/waybills');
  });

  it('заголовок-контрол оставлен своему делу — меню открывает кнопка рядом', () => {
    shown(<PageHeader title={<button type="button">Представление</button>} />, '/invoices', costs);
    expect(trigger().getAttribute('aria-label')).toBe('Другие страницы раздела «Учёт затрат»');
    expect(screen.getByRole('heading', { level: 1 }).contains(trigger())).toBe(false);
    open();
  });
});
