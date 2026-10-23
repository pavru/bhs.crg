import { screen } from '@testing-library/react';

/**
 * Подписи найденного в командной палитре — без подсказки «Enter» и без группы, которую слышит
 * только диктор. Общая для тестов палитры: разметка пункта меняется в одном месте, и копия в
 * соседнем тесте давала бы ложно-зелёный или ложно-красный ответ.
 */
export function paletteLabels(): (string | undefined)[] {
  return screen.queryAllByRole('button', { hidden: true }).filter(b => b.closest('ul')).map(b => {
    const copy = b.cloneNode(true) as HTMLElement;
    copy.querySelectorAll('.sr-only').forEach(n => n.remove());
    return copy.textContent?.trim().replace(/\s*Enter$/, '');
  });
}
