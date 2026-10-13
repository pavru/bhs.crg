import { useCan } from '@/shared/api/access';
import type { ModuleMappingSignal as Signal } from '@/shared/api/moduleSettings';
import { Button } from '@/shared/ui/Button';
import { useGuardedNavigate } from '@/shared/ui/NavigationGuard';

/**
 * Строка-сигнал о сопоставлении полей в разделе модуля (ТЗ TYPE-23, issue #1101): всё ли, что
 * модуль читает из справочников, назначено — и что не работает, если нет.
 *
 * Назначают на отдельной странице, под правом на типы. Здесь — только знание: тот, кто настраивает
 * систему, обязан увидеть, что модуль работает не весь, даже если назначить поле не может. Ссылки у
 * него поэтому нет — действие, которое ответит отказом, не показываем.
 */
export function ModuleMappingSignal({ signal }: { signal: Signal }) {
  const can = useCan();
  const go = useGuardedNavigate();
  const complete = signal.unmet === 0;

  return (
    <div className="flex flex-wrap items-baseline gap-x-3 gap-y-1" data-mapping-signal>
      <p className={`text-xs max-w-xl ${complete ? 'text-fg3' : 'text-warning'}`}>
        {complete
          ? `Поля справочников: назначены все ${signal.total}.`
          : `Поля справочников: не назначено ${signal.unmet} из ${signal.total}. ${signal.notWorking.join(' ')}`}
      </p>
      {can.permission('core.types.edit') && (
        <Button variant="text" size="sm" onClick={() => go('/field-mapping')}>Сопоставление полей</Button>
      )}
    </div>
  );
}
