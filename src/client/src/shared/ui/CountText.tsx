import type { ReactNode } from 'react';

/**
 * Число у подписи кнопки, вкладки или строки списка ([UI-45], [UI-74]): сколько условий отбора, сколько
 * записей. Текст `label-medium` `on-surface-variant`, цифры одной ширины — соседние счётчики не пляшут; без
 * заливки и тона: решение владельца 01.11.2026 (макет А) заменило вариант `count` пометки с тоном
 * (22.10.2026). Число входит в доступное имя через саму подпись — рядом с ней, а не вместо неё.
 *
 * <p>Число на знаке, пункте навигации и вкладке — `Badge`.</p>
 */
export function CountText({ onContainer = false, disabled = false, className = '', children }: {
  /** Строка выделена заливкой контейнера (`primary-container`) — число её цветом текста, иначе сливается. */
  onContainer?: boolean;
  /** Недоступный пункт — цвет недоступного текста MD3 (`on-surface` 38 %), как у его подписи. */
  disabled?: boolean;
  className?: string;
  children: ReactNode;
}) {
  const color = disabled ? 'text-on-surface/38' : onContainer ? 'text-on-primary-container' : 'text-on-surface-variant';
  return (
    <span className={`text-label-medium tabular-nums ${color} ${className}`}>
      {children}
    </span>
  );
}
