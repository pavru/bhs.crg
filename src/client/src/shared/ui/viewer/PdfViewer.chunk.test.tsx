// @vitest-environment jsdom
import { afterEach, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { supportedBrowser } from './testEngine';

/**
 * Ленивый кусок просмотрщика не загрузился — после выпуска прежний хеш пропадает с сервера (issue #1490,
 * [UI-17]). Говорится «приложение обновилось», а не «файл повреждён»: файл цел.
 */

vi.mock('./viewerGate', async orig => ({
  ...(await orig<typeof import('./viewerGate')>()),
  loadViewer: () => Promise.reject(new TypeError('Failed to fetch dynamically imported module')),
}));

afterEach(cleanup);

it('сбой ленивого куска — «приложение обновилось», выходы «Обновить страницу» и «Открыть во вкладке»', async () => {
  const restore = supportedBrowser();
  vi.spyOn(console, 'error').mockImplementation(() => {});
  try {
    const { PdfViewer } = await import('./PdfViewer');
    render(<PdfViewer source={new Uint8Array([1])} documentKey="a" title="Файл" />);
    const alert = await screen.findByRole('alert');
    expect(alert.getAttribute('data-state')).toBe('chunk');
    expect(alert.textContent).toContain('Приложение обновилось');
    expect(screen.getByRole('button', { name: 'Обновить страницу' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Открыть во вкладке' })).toBeTruthy();
  } finally {
    restore();
  }
});
