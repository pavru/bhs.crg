/**
 * Цвет ведущего знака о состоянии — роль, а не тон контейнера ([UI-68]): у чипа (`Chip`) и у пункта выбора
 * (`SelectItem`, [UI-69]) один и тот же. Отдельным модулем: из файла компонента константа ломала бы горячую
 * замену (`react-refresh/only-export-components`).
 */
export const LEAD_TONE = { error: 'text-error', warning: 'text-warning' } as const;
