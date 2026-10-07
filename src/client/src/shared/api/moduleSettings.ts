import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiClient } from './client';

/**
 * Настройки модулей (issue #1070). Модуль объявляет настройку на сервере — с подписью, следствием,
 * видом и границами, — и экран рисует её по объявлению: клиент о смысле настройки не знает ничего.
 */
export interface ModuleSetting {
  /** Ключ вида «модуль.объект.настройка». */
  key: string;
  title: string;
  /** Что настройка меняет — следствие, а не пересказ названия. */
  effect: string;
  /** Вид значения. Сегодня один — число; новый вид приедет вместе с настройкой, которой он нужен. */
  kind: 'number' | string;
  /** Действующее значение — в том виде, в каком оно хранится (число с точкой). */
  value: string;
  /** Сохранённое годное значение в хранимом виде; `null` — действует умолчание. */
  stored: string | null;
  /**
   * Что лежит в базе и этой версией не принимается; `null` — такого нет. Признак считает сервер:
   * сравнивать строки здесь нельзя — «1» и «1.00» различаются записью, а не значением.
   */
  stale: string | null;
  default: string;
  min: number | null;
  max: number | null;
  scale: number | null;
  unit: string | null;
  /** О чём предупредить ДО сохранения нового значения: настройка действует на записанные данные. */
  changeWarning: string | null;
}

export interface ModuleSettings {
  code: string;
  title: string;
  settings: ModuleSetting[];
}

export const MODULE_SETTINGS_KEY = ['settings', 'modules'] as const;

/** Только модули, у которых настройки есть: модуль без настроек секции не получает. */
export function useModuleSettings() {
  return useQuery<ModuleSettings[]>({
    queryKey: MODULE_SETTINGS_KEY,
    queryFn: () => apiClient.get('/settings/modules').then(r => r.data.modules),
  });
}

/**
 * Сохранить настройки модуля. Уходят только ИЗМЕНЁННЫЕ ключи: форма, открытая давно, иначе
 * записала бы поверх чужой правки соседнего ключа своё устаревшее значение. `null` — снять
 * настройку, вернуться к умолчанию.
 */
export function useSaveModuleSettings() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ code, values }: { code: string; values: Record<string, string | null> }) =>
      apiClient.put(`/settings/modules/${code}`, { values }),
    // И после отказа: сервер мог отвергнуть значение, потому что настройку тем временем изменили.
    onSettled: () => qc.invalidateQueries({ queryKey: MODULE_SETTINGS_KEY }),
  });
}
