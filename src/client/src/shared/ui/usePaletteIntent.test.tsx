// @vitest-environment jsdom
import { StrictMode } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { MemoryRouter, useLocation, useSearchParams } from 'react-router';
import type { AccessInfo } from '@/shared/api/access';
import { PALETTE_ACTIONS } from './paletteActions';
import { usePaletteIntent } from './usePaletteIntent';

let access: AccessInfo | undefined;
vi.mock('@/shared/api/access', () => ({
  useAccess: () => ({ data: access }),
  hasPermission: (a: AccessInfo, code: string) => a.permissions.includes(code),
}));

afterEach(() => { cleanup(); access = undefined; info.mockClear(); });

const info = vi.fn();
vi.mock('./Snackbar', () => ({ useSnackbar: () => ({ info }) }));

/** Экран, принимающий «Новый счёт» из палитры (issue #1433). */
function Screen({ run, busy }: { run: () => void; busy?: boolean }) {
  usePaletteIntent(PALETTE_ACTIONS.invoiceNew, run, { busy });
  const { pathname, search } = useLocation();
  return <output aria-label="адрес">{pathname + search}</output>;
}

function shown(url: string, busy = false) {
  const run = vi.fn();
  // Новый элемент на каждую отрисовку: тот же объект React пропустил бы, не перерисовав.
  const view = () => (
    <StrictMode>
      <MemoryRouter initialEntries={[url]}><Screen run={run} busy={busy} /></MemoryRouter>
    </StrictMode>
  );
  const { rerender } = render(view());
  return { run, again: () => rerender(view()) };
}

const address = () => screen.getByLabelText('адрес').textContent;

describe('экран принимает действие палитры', () => {
  it('исполняет ровно раз — и в строгом режиме — и снимает параметр из адреса', () => {
    access = { permissions: ['costs.invoice.edit'], modules: [] };
    const { run } = shown('/invoices?do=invoice-new&q=x');
    expect(run).toHaveBeenCalledTimes(1);
    expect(address()).toBe('/invoices?q=x');
  });

  it('чужое действие не трогает', () => {
    access = { permissions: ['costs.invoice.edit'], modules: [] };
    const { run } = shown('/invoices?do=invoice-scan');
    expect(run).not.toHaveBeenCalled();
    expect(address()).toBe('/invoices?do=invoice-scan');
  });

  it('ждёт загрузки прав, потом исполняет', () => {
    const { run, again } = shown('/invoices?do=invoice-new');
    expect(run).not.toHaveBeenCalled();
    access = { permissions: ['costs.invoice.edit'], modules: [] };
    again();
    expect(run).toHaveBeenCalledTimes(1);
  });

  /**
   * Ревью PR #1478: накладная открывает заведённый черновик своим `setSearchParams` — и строила адрес от
   * снимка, где `do` ещё стоял. Параметр возвращался, действие исполнялось снова: 297 черновиков.
   */
  it('экран, меняющий адрес после действия, не возвращает параметр и не исполняет его снова', async () => {
    access = { permissions: ['costs.invoice.edit'], modules: [] };
    const run = vi.fn();
    function Opener() {
      const [, setParams] = useSearchParams();
      usePaletteIntent(PALETTE_ACTIONS.invoiceNew, () => {
        run();
        // Предохранитель: сломанный хук зацикливается, и тест иначе повис бы, а не упал.
        if (run.mock.calls.length > 3) return;
        setParams(prev => { const next = new URLSearchParams(prev); next.set('invoice', '7'); return next; });
      });
      const { pathname, search } = useLocation();
      return <output aria-label="адрес">{pathname + search}</output>;
    }
    render(<StrictMode><MemoryRouter initialEntries={['/invoices?do=invoice-new']}><Opener /></MemoryRouter></StrictMode>);
    await new Promise(r => setTimeout(r, 20));
    expect(address()).toBe('/invoices?invoice=7');
    expect(run).toHaveBeenCalledTimes(1);
  });

  /** Ревью PR #1478: палитра не принимает того, чего сейчас не примет кнопка. */
  it('занятая кнопка не исполняется — и об этом сказано', () => {
    access = { permissions: ['costs.invoice.edit'], modules: [] };
    const { run } = shown('/invoices?do=invoice-new', true);
    expect(run).not.toHaveBeenCalled();
    expect(info).toHaveBeenCalledTimes(1);
    expect(info.mock.calls[0][0]).toContain('«Новый счёт» не выполнено');
    expect(address()).toBe('/invoices');
  });

  it('без права кнопки параметр снимается молча', () => {
    access = { permissions: [], modules: [] };
    const { run } = shown('/invoices?do=invoice-new');
    expect(run).not.toHaveBeenCalled();
    expect(address()).toBe('/invoices');
  });
});
