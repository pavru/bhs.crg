// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { Modal } from './Modal';
import { ModalBanner } from './ModalBanner';

/**
 * Сообщение окна ([UI-73], Р1-А): banner из глубины тела встаёт под шапку, над прокруткой, — а тело остаётся
 * телом окна: свои отступы, черта и подвал «Закрыть» (ревью PR #1760).
 */

afterEach(cleanup);

function inWindow() {
  render(
    <Modal open onOpenChange={vi.fn()} title="Окно">
      <div>
        <p>Факты окна</p>
        <ModalBanner tone="warning">Не сопоставлено строк: 3.</ModalBanner>
      </div>
    </Modal>,
  );
}

describe('ModalBanner', () => {
  it('встаёт над прокруткой тела — один раз', () => {
    inWindow();
    const banners = screen.getAllByRole('status').filter(n => n.textContent === 'Не сопоставлено строк: 3.');
    expect(banners).toHaveLength(1);
    const scroll = screen.getByText('Факты окна').closest('.overflow-y-auto')!;
    expect(scroll).toBeTruthy();
    expect(scroll.contains(banners[0])).toBe(false);
    // Раньше тела в разметке: диктор читает сообщение до фактов, как оно и видно.
    expect(banners[0].compareDocumentPosition(scroll) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });

  it('подвал «Закрыть» у окна остаётся: banner своей раскладки тела не требует', () => {
    inWindow();
    expect(screen.getByRole('button', { name: 'Закрыть' })).toBeTruthy();
  });

  it('вне окна — обычный banner по месту', () => {
    render(<div data-testid="place"><ModalBanner tone="info">Данные обновляются раз в час.</ModalBanner></div>);
    expect(screen.getByTestId('place').textContent).toContain('Данные обновляются раз в час.');
  });
});
