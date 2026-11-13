import type { TableData, TableDeclaration } from '@/shared/api/tables';

/**
 * Граница выдачи, которой подписан экран таблицы (ТЗ CORE-24.3; work-07): из ответа СТРОК — там она
 * того исхода отбора, что применился («отчёты ваших строек (4)»), — а пока строк нет, из описания.
 *
 * <p>Описание читается без строк, и у таблицы с несколькими исходами границы не называет вовсе: подпись
 * «первым исходом» сказала бы менеджеру «ваши отчёты» там, где ему отдали четыре стройки.</p>
 */
export function tableBoundary(decl: Pick<TableDeclaration, 'boundary'>, data?: Pick<TableData, 'boundary'> | null): string | null {
  return data?.boundary || decl.boundary || null;
}

/** Подзаголовок экрана: зерно и, если она известна, граница выдачи. */
export function tableSubtitle(grain: string, boundary: string | null): string {
  return boundary ? `Строка — ${grain}. ${boundary.replace(/\.$/, '')}.` : `Строка — ${grain}.`;
}
