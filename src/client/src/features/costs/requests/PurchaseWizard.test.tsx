// @vitest-environment jsdom
import { cleanup, render, screen } from '@testing-library/react';
import type { ReactNode } from 'react';
import { afterEach, expect, it, vi } from 'vitest';
import { PurchaseWizard } from './PurchaseWizard';

vi.mock('@/shared/api/purchaseRequests', () => ({
  purchaseRefusal: () => ({ message: '' }),
  useCreatePurchase: () => ({ isPending: false }),
  usePurchaseProposal: () => ({ isPending: false }),
  usePurchaseEstimateItems: () => ({
    data: { state: 'hidden', permission: 'plan.estimate.materials', message: 'Материалы сметы скрыты.' },
  }),
}));

vi.mock('@/shared/ui/Modal', () => ({
  Modal: ({ children, footer }: { children: ReactNode; footer: ReactNode }) => <div>{children}{footer}</div>,
  ModalButtons: ({ children }: { children: ReactNode }) => <div>{children}</div>,
  ModalExit: () => null,
  ModalAction: () => null,
  ModalPrimary: ({ children, disabled }: { children: ReactNode; disabled?: boolean }) => <button disabled={disabled}>{children}</button>,
}));

afterEach(cleanup);

it('показывает отказ доступа к материалам и закрывает расчёт', () => {
  render(<PurchaseWizard constructions={[{ id: 'site', name: 'Стройка', sections: [] }]} initialSite="site"
    onClose={vi.fn()} onCreated={vi.fn()} />);
  expect(screen.getByText(/Материалы сметы скрыты.*plan\.estimate\.materials/)).toBeTruthy();
  expect(screen.queryByText('В действующих сметах нет позиций перечня с сопоставленными материалами.')).toBeNull();
  expect((screen.getByRole('button', { name: 'Рассчитать предложение' }) as HTMLButtonElement).disabled).toBe(true);
});
