// @vitest-environment jsdom
import { useState } from 'react';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import { Modal } from './Modal';
import { PhotoGallery } from './PhotoGallery';

afterEach(cleanup);

it('открывает выбранное фото и листает в обе стороны с клавиатуры и кнопкой', () => {
  render(<PhotoGallery title="Фото работ" count={3} initialIndex={1} onClose={vi.fn()}
    renderPhoto={index => <p>Снимок {index + 1}</p>} />);
  expect(screen.getByText('Снимок 2')).toBeTruthy();
  const previous = screen.getByRole('button', { name: 'Предыдущее фото' });
  fireEvent.keyDown(screen.getByRole('dialog', { name: 'Фото работ' }), { key: 'ArrowRight' });
  expect(screen.getByText('Снимок 3')).toBeTruthy();
  expect(screen.getByRole('button', { name: 'Следующее фото' }).getAttribute('aria-disabled')).toBe('true');
  fireEvent.click(previous);
  expect(screen.getByText('Снимок 2')).toBeTruthy();
});

it('Esc закрывает полноэкранную галерею поверх другого окна', async () => {
  const closeParent = vi.fn();
  function Example() {
    const [open, setOpen] = useState(true);
    return <Modal open nothingToLose title="Карточка отчёта" onOpenChange={closeParent}>
      {open && <PhotoGallery fullScreen title="Фото строки" count={1} initialIndex={0}
        onClose={() => setOpen(false)} renderPhoto={() => <p>Снимок</p>} />}
    </Modal>;
  }
  render(<Example />);
  fireEvent.keyDown(screen.getByRole('dialog', { name: 'Фото строки' }), { key: 'Escape' });
  await waitFor(() => expect(screen.queryByText('Снимок')).toBeNull());
  expect(screen.getByRole('dialog', { name: 'Карточка отчёта' })).toBeTruthy();
  expect(closeParent).not.toHaveBeenCalled();
});
