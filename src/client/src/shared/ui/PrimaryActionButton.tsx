import type { ReactNode } from 'react';
import { Button, type ButtonProps } from './Button';
import type { PaletteAction } from './paletteActions';
import { useReportsPrimaryBusy } from './primaryFabContext';

/**
 * Кнопка основного действия экрана (issue #1434, ревью PR #1502) — filled-кнопка с подписью действия
 * из `PALETTE_ACTIONS`. На телефоне её место занимает плавающая кнопка (`PrimaryFab`), поэтому:
 * - сама она там прячется — и в шапке, и в пустом состоянии: одно действие дважды на экране не стоит;
 * - свою занятость (`loading`) она сообщает плавающей: та показывает спиннер вместо этой.
 *
 * Связь «основное действие ↔ его кнопка» держит этот компонент, а не класс, поставленный руками на
 * каждом экране. Что экран рисует основное действие им, а не своей кнопкой, сверяет
 * `paletteActions.test.ts`.
 */
export function PrimaryActionButton({ action, icon, loading = false, className = '', ...rest }:
  Omit<ButtonProps, 'variant' | 'children' | 'icon'> & { action: PaletteAction; icon?: ReactNode }) {
  useReportsPrimaryBusy(action.id, loading);
  const Icon = action.icon;
  return (
    <Button variant="filled" icon={icon ?? <Icon size={16} />} loading={loading} className={`max-md:hidden ${className}`} {...rest}>
      {action.label}
    </Button>
  );
}
