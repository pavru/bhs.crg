import type { EnumOptionDef } from '@/shared/api/types';

// Отдельным файлом от компонента (issue #858): модуль, экспортирующий и компонент, и
// функцию, не может быть границей горячей подмены — правка поднимается вверх по импортам.

/** Человекочитаемое превью вариантов перечисления для строки списка. */
export function humanEnumPreview(values: EnumOptionDef[]): string {
  if (values.length === 0) return 'нет вариантов';
  const labels = values.map(v => v.label).filter(Boolean);
  const head = labels.slice(0, 3).join(', ');
  return head;  // ВРЕМЕННАЯ ПОЛОМКА для доказательства красного прогона
}
