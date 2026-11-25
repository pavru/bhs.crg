// @vitest-environment jsdom
import { useState } from 'react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { AxiosError } from 'axios';
import { afterEach, expect, it, vi } from 'vitest';
import { apiClient } from '@/shared/api/client';
import type { MaterialHintTier, MaterialHintTierId } from '@/shared/api/workMaterialsHint';
import { MaterialPicker } from './MaterialPicker';

vi.mock('@/shared/api/client', () => ({ apiClient: { get: vi.fn() } }));
afterEach(cleanup);

async function open(estimate: MaterialHintTier, reportId?: string) {
  const response: Record<MaterialHintTierId, MaterialHintTier> = {
    issued: { state: 'not-connected', reason: 'Не подключён модуль учёта затрат.', items: [] },
    estimate,
    catalog: { state: 'ok', items: [] },
  };
  vi.mocked(apiClient.get).mockResolvedValue({ data: response });
  if (reportId) vi.mocked(apiClient.get).mockImplementation(async path => {
    if (path !== '/work/materials/hint') return { data: { items: [{ id: 'box', displayName: 'Коробка' }] } };
    throw Object.assign(new AxiosError('Стройка недоступна'), { response: { status: 404 } });
  });
  function Line() {
    const [selected, setSelected] = useState<string[]>([]);
    return <MaterialPicker scope={{ siteId: 'site', date: '2026-11-26', mode: 'own', reportId }}
      line={{ workTypeId: 'work', unitId: 'unit', sectionId: null }} excluded={selected}
      onPick={item => setSelected(previous => [...previous, item.id])} />;
  }
  render(<QueryClientProvider client={new QueryClient()}><MemoryRouter><Line /></MemoryRouter></QueryClientProvider>);
  fireEvent.click(screen.getByRole('button', { name: 'Добавить материал' }));
  return within(await screen.findByRole('dialog'));
}

it('показывает причину недоступности каждого источника', async () => {
  const reason = 'Скрыто правами: plan.estimate.hint';
  const picker = await open({ state: 'hidden', reason, items: [] });
  expect(await picker.findByText('Не подключён модуль учёта затрат.')).toBeTruthy();
  fireEvent.click(picker.getByRole('tab', { name: 'Смета' }));
  expect(await picker.findByText(reason)).toBeTruthy();
});

it('сохраняет выбор из справочника своего отчёта после отказа по стройке', async () => {
  const picker = await open({ state: 'ok', items: [] }, 'saved');
  const item = await picker.findByRole('option', { name: 'Коробка' });
  expect(apiClient.get).toHaveBeenCalledWith('/work/entry/sites/site/catalog/materials', expect.objectContaining({
    params: expect.objectContaining({ reportId: 'saved' }),
  }));
  fireEvent.click(item);
  expect(picker.getByText('Добавлено: 1')).toBeTruthy();
  fireEvent.click(picker.getByRole('tab', { name: 'Смета' }));
  expect(picker.getByText('Стройка недоступна')).toBeTruthy();
});

it('помечает ресурс по ответу сервера и оставляет лист открытым после выбора', async () => {
  const picker = await open({ state: 'ok', items: [
    { id: 'box', displayName: 'Коробка', notIssued: true }, { id: 'pipe', displayName: 'Труба' },
  ] });
  fireEvent.click(picker.getByRole('tab', { name: 'Смета' }));
  const box = await picker.findByRole('option', { name: /Коробка/ });
  expect(within(box).getByText('Не отпущен')).toBeTruthy();
  expect(within(picker.getByRole('option', { name: 'Труба' })).queryByText('Не отпущен')).toBeNull();
  fireEvent.click(box);
  expect(picker.getByText('Добавлено: 1')).toBeTruthy();
  expect(picker.queryByRole('option', { name: /Коробка/ })).toBeNull();
  fireEvent.click(picker.getByRole('button', { name: 'Готово' }));
  expect(screen.queryByRole('dialog')).toBeNull();
});
