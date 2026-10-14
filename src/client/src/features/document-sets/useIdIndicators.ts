import { usePlanSummary } from '@/shared/api/plans';
import { useProblemSummary } from '@/shared/api/reconciliations';
import { useModuleShown } from '@/shared/ui/workspaceContext';

/**
 * Показатели модуля ИД на страницах строек (issue #1001, ревью PR #1330): готовность по плану и
 * «требует разбора» (сверка объявлена тем же модулем). Одно место на три страницы — копии в каждой
 * расходились бы.
 *
 * <p>В пространстве без ИД они не показываются и НЕ запрашиваются. Обнулять приходится и данные: у
 * выключенного запроса React Query отдаёт прежний ответ из кэша — тот, что пришёл в «Все доступные».</p>
 */
export function useIdIndicators(scope: 'System' | 'Construction' | 'Section', scopeId?: string) {
  const shown = useModuleShown('id');
  const plan = usePlanSummary(scope, scopeId, shown).data;
  const problems = useProblemSummary(scope, scopeId, shown).data;
  return { shown, plan: shown ? plan : undefined, problems: shown ? problems : undefined };
}
