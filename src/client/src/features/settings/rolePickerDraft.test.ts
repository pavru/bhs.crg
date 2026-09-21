import { describe, it, expect } from 'vitest';
import { toggleRole, sameRoles } from './rolePickerDraft';

describe('черновик выбора ролей', () => {
  it('две галки подряд накапливаются, а не отменяют друг друга', () => {
    // Та самая ошибка ревью #984: второй щелчок собирался из ИСХОДНОГО состава, и первая
    // выбранная роль пропадала. Здесь второй щелчок идёт от накопленного.
    const было = ['Admin'];
    const после = toggleRole(toggleRole(было, 'Supplier'), 'Accountant');

    expect(после).toEqual(['Admin', 'Supplier', 'Accountant']);
    expect(было).toEqual(['Admin']);   // исходный список не тронут
  });

  it('повторная галка снимает роль', () => {
    expect(toggleRole(['Admin', 'Supplier'], 'Supplier')).toEqual(['Admin']);
  });

  it('перестановка — не правка: запрос из-за неё уходить не должен', () => {
    // Иначе закрытие меню, в котором ничего не меняли, отправляло бы PUT — а тот обнуляет
    // человеку отметку безопасности, то есть выбивает у него токен на ровном месте.
    expect(sameRoles(['Admin', 'Supplier'], ['Supplier', 'Admin'])).toBe(true);
    expect(sameRoles(['Admin'], ['Admin', 'Supplier'])).toBe(false);
    expect(sameRoles([], [])).toBe(true);
  });
});
