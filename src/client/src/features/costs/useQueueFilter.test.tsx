// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MemoryRouter, useLocation, useNavigate, useNavigationType } from 'react-router';
import { useComposedSearchParams } from '@/shared/hooks/useComposedSearchParams';
import { useQueueFilter, type InvoiceFilter } from './useQueueFilter';
import { INVOICE_QUEUES, parseQueue } from './invoiceQueues';

afterEach(cleanup);

/** Экран в миниатюре: отбор, адрес и глубина истории. */
function Probe() {
  const [params, update] = useComposedSearchParams();
  const [filter, setFilter] = useQueueFilter(params, update);
  const { search } = useLocation();
  const navigate = useNavigate();
  const set = (next: InvoiceFilter) => () => setFilter(next);
  return (
    <>
      <output aria-label="отбор">{filter ?? 'нет'}</output>
      <output aria-label="адрес">{search}</output>
      <output aria-label="переход">{useNavigationType()}</output>
      <button onClick={set('lost')}>удалённые</button>
      <button onClick={set('upload')}>загрузка</button>
      <button onClick={set(null)}>все</button>
      {/* Новый черновик из файла: отбор снят и счёт выбран одним обработчиком. */}
      <button onClick={() => { setFilter(null); update(p => p.set('invoice', '7'), { replace: true }); }}>
        черновик
      </button>
      <button onClick={() => navigate('/invoices?queue=archived')}>ссылка</button>
    </>
  );
}

const at = (url: string) => render(<MemoryRouter initialEntries={[url]}><Probe /></MemoryRouter>);
const text = (label: string) => screen.getByLabelText(label).textContent;

/** Очередь адресом экрана счетов (shell-08, ТЗ COST-6.2). */
describe('очередь в адресе', () => {
  it('адрес с очередью открывает экран под ней — так же после обновления страницы', () => {
    at('/invoices?queue=parsing');
    expect(text('отбор')).toBe('parsing');
  });

  it('незнакомое значение — без отбора', () => {
    at('/invoices?queue=everything');
    expect(text('отбор')).toBe('нет');
    // Первый выбор чипа его заменяет.
    fireEvent.click(screen.getByText('удалённые'));
    expect(text('адрес')).toBe('?queue=lost');
  });

  it('чип пишет очередь в адрес заменой записи, а снятие — убирает её', () => {
    at('/invoices');
    fireEvent.click(screen.getByText('удалённые'));
    expect(text('адрес')).toBe('?queue=lost');
    fireEvent.click(screen.getByText('все'));
    expect(text('адрес')).toBe('');
    // Замена, а не новая запись: «Назад» уводит с экрана, а не перебирает чипы.
    expect(text('переход')).toBe('REPLACE');
  });

  it('«эта загрузка» в адрес не идёт', () => {
    at('/invoices?queue=lost');
    fireEvent.click(screen.getByText('загрузка'));
    expect(text('отбор')).toBe('upload');
    expect(text('адрес')).toBe('');
  });

  it('отбор снят и счёт выбран одним обработчиком — в адресе остаётся счёт и нет очереди', () => {
    at('/invoices?queue=lost');
    act(() => { fireEvent.click(screen.getByText('черновик')); });
    expect(text('адрес')).toBe('?invoice=7');
    expect(text('отбор')).toBe('нет');
  });

  it('переход по ссылке очереди на открытом экране меняет отбор', () => {
    at('/invoices?queue=lost');
    fireEvent.click(screen.getByText('ссылка'));
    expect(text('отбор')).toBe('archived');
  });

  it('экран принимает каждую очередь чипов', () => {
    for (const q of INVOICE_QUEUES) expect(parseQueue(q)).toBe(q);
    expect(INVOICE_QUEUES).toEqual(['parsing', 'unrecognized', 'lost', 'archived']);
  });
});
