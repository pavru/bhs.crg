// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { FromFileButton, InvoiceListHeader } from './InvoicePageChrome';

/**
 * «Счёт из файла» при открытом счёте — в сводке, всегда (costs-invoice-screen-14, D7): значок с именем и
 * подсказкой «Счёт из файла — или перетащите файл в окно»; в шапке списка его нет.
 */

afterEach(cleanup);

describe('«Счёт из файла» в сводке', () => {
  it('значок называет себя и бросок в окно, нажатие — тот же исполнитель', () => {
    const onClick = vi.fn();
    render(<FromFileButton loading={false} title="Один или несколько файлов: PDF." onClick={onClick} />);
    fireEvent.click(screen.getByRole('button', { name: 'Счёт из файла — или перетащите файл в окно' }));
    expect(onClick).toHaveBeenCalledTimes(1);
  });

  it('шапка списка при открытом счёте его не несёт', () => {
    render(<MemoryRouter><InvoiceListHeader actions={[]} /></MemoryRouter>);
    expect(screen.queryByRole('button', { name: /Счёт из файла/ })).toBeNull();
  });

  it('счёт ещё грузится или не найден — сводки нет, и значок встаёт в шапку списка', () => {
    render(<MemoryRouter><InvoiceListHeader actions={[]}
      fromFile={<FromFileButton loading={false} title="Один или несколько файлов: PDF." onClick={() => {}} />} /></MemoryRouter>);
    expect(screen.getByRole('button', { name: /Счёт из файла/ })).toBeTruthy();
  });
});
