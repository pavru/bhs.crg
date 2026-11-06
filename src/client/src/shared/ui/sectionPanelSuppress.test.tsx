// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { useState } from 'react';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { SectionPanelDrawer } from './SectionRail';
import { SuppressSectionPanelContext, useSectionPanel, useSuppressSectionPanel } from './sectionPanelSuppress';

/**
 * Подпанель при открытом счёте (costs-invoice-screen-10-layout, D-10.5, ТЗ COST-6.3): подавление не пишет
 * выбор человека, «Показать панель» открывает drawer поверх, Esc и щелчок вне его закрывают.
 */

afterEach(cleanup);

/** Оболочка в миниатюре: подпанель в раскладке, drawer поверх и страница, которая подавляет. */
function Shell({ expand, page }: { expand: () => void; page: boolean }) {
  const panel = useSectionPanel({ has: true, wanted: true, expand });
  return (
    <>
      {panel.shown && <nav data-testid="in-layout">подпанель</nav>}
      <button type="button" onClick={panel.show}>Показать панель</button>
      <SectionPanelDrawer open={panel.drawer} onClose={panel.closeDrawer} title="Учёт затрат">
        <a href="#invoices">Счета</a>
      </SectionPanelDrawer>
      <SuppressSectionPanelContext.Provider value={panel.suppress}>
        {page && <Page />}
      </SuppressSectionPanelContext.Provider>
    </>
  );
}

function Page() {
  useSuppressSectionPanel(true);
  return <main data-testid="form">форма счёта</main>;
}

function Toggle({ expand }: { expand: () => void }) {
  const [page, setPage] = useState(true);
  return (
    <>
      <button type="button" onClick={() => setPage(p => !p)}>счёт</button>
      <Shell expand={expand} page={page} />
    </>
  );
}

describe('подавление подпанели', () => {
  it('пока страница подавляет — подпанели в раскладке нет; ушла — вернулась, выбор не записан', () => {
    const expand = vi.fn();
    render(<Toggle expand={expand} />);
    expect(screen.queryByTestId('in-layout')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'счёт' }));
    expect(screen.getByTestId('in-layout')).toBeTruthy();
    expect(expand).not.toHaveBeenCalled();
  });

  it('«Показать панель» при подавлении — drawer поверх, раскладка и выбор не меняются', () => {
    const expand = vi.fn();
    render(<Shell expand={expand} page />);
    fireEvent.click(screen.getByRole('button', { name: 'Показать панель' }));
    expect(screen.getByRole('dialog', { name: 'Учёт затрат' })).toBeTruthy();
    expect(screen.queryByTestId('in-layout')).toBeNull();
    expect(expand).not.toHaveBeenCalled();
  });

  it('без подавления «Показать панель» пишет выбор, как раньше', () => {
    const expand = vi.fn();
    render(<Shell expand={expand} page={false} />);
    fireEvent.click(screen.getByRole('button', { name: 'Показать панель' }));
    expect(expand).toHaveBeenCalledOnce();
    expect(screen.queryByRole('dialog')).toBeNull();
  });

  it('drawer закрывается Esc', () => {
    render(<Shell expand={vi.fn()} page />);
    fireEvent.click(screen.getByRole('button', { name: 'Показать панель' }));
    fireEvent.keyDown(screen.getByRole('dialog'), { key: 'Escape' });
    expect(screen.queryByRole('dialog')).toBeNull();
  });

  it('drawer закрывается щелчком вне', async () => {
    render(<Shell expand={vi.fn()} page />);
    fireEvent.click(screen.getByRole('button', { name: 'Показать панель' }));
    // Radix подписывается на `pointerdown` документа таймером и отвечает по следующему `click`.
    await act(async () => { await new Promise(r => setTimeout(r, 0)); });
    fireEvent.pointerDown(document.body);
    fireEvent.click(document.body);
    expect(screen.queryByRole('dialog')).toBeNull();
  });

  it('выбор пункта закрывает drawer', () => {
    render(<Shell expand={vi.fn()} page />);
    fireEvent.click(screen.getByRole('button', { name: 'Показать панель' }));
    fireEvent.click(screen.getByRole('link', { name: 'Счета' }));
    expect(screen.queryByRole('dialog')).toBeNull();
  });

  it('форма под drawer остаётся на месте', () => {
    render(<Shell expand={vi.fn()} page />);
    fireEvent.click(screen.getByRole('button', { name: 'Показать панель' }));
    expect(screen.getByTestId('form')).toBeTruthy();
  });
});
