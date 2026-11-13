import { formatBytes } from '@/shared/api/attachments';
import type { BackupModuleWeight } from '@/shared/api/types';

/**
 * «из них файлы модулей: …» — доля файлов модулей в весе копии (work-05, WORK-35).
 *
 * Модули без файлов в строку не попадают: сервер перечисляет их с нулём, чтобы «нет файлов» не
 * путалось с «модуль не учтён», но человеку «Планирование: 0 Б» ничего не говорит. Нет файлов ни у
 * одного модуля — строки нет вовсе (у состава «Настройка» так всегда).
 */
export function backupModuleShare(modules: readonly BackupModuleWeight[]): string | null {
  const withFiles = modules.filter(m => m.count > 0);
  if (withFiles.length === 0) return null;
  const total = withFiles.reduce((sum, m) => sum + m.bytes, 0);
  const parts = withFiles.map(m => `${m.title}: ${formatBytes(m.bytes)}, файлов ${m.count}`);
  return `из них файлы модулей: ${formatBytes(total)} (${parts.join('; ')})`;
}
