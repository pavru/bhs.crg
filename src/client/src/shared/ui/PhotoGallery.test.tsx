// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import { PhotoGallery } from './PhotoGallery';

afterEach(cleanup);

it('открывает выбранное фото и листает в обе стороны с клавиатуры и кнопкой', () => {
  render(<PhotoGallery title="Фото работ" count={3} initialIndex={1} onClose={vi.fn()}
    renderPhoto={index => <p>Снимок {index + 1}</p>} />);
  expect(screen.getByText('Снимок 2')).toBeTruthy();
  const previous = screen.getByRole('button', { name: 'Предыдущее фото' });
  fireEvent.keyDown(previous, { key: 'ArrowRight' });
  expect(screen.getByText('Снимок 3')).toBeTruthy();
  expect(screen.getByRole('button', { name: 'Следующее фото' }).getAttribute('aria-disabled')).toBe('true');
  fireEvent.click(previous);
  expect(screen.getByText('Снимок 2')).toBeTruthy();
});
