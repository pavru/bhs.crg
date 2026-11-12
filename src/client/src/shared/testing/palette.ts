import { screen } from '@testing-library/react';

/**
 * Подписи найденного в командной палитре — без подсказки «Enter», замыкающего текста и группы, которую
 * слышит только диктор. Общая для тестов палитры: разметка пункта меняется в одном месте, и копия в
 * соседнем тесте давала бы ложно-зелёный или ложно-красный ответ.
 */
export function paletteLabels(): (string | undefined)[] {
  return paletteOptions().map(o => {
    const copy = o.querySelector('[data-palette-label]')!.cloneNode(true) as HTMLElement;
    copy.querySelectorAll('.sr-only').forEach(n => n.remove());
    return copy.textContent?.trim();
  });
}

/** Варианты палитры (`ListOption`, М-2): строки `role="option"` её списка. */
export function paletteOptions(): HTMLElement[] {
  return screen.queryAllByRole('option', { hidden: true });
}
