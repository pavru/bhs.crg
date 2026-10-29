import { Icon } from '@/shared/ui/Icon';
import { ArrowForward, CheckCircle, ErrorIcon, Warning } from '@/shared/ui/icons';
import type { TypstBlockProblem } from '@/shared/api/documentTypes';

/**
 * Инлайн-панель проблем сборки Typst-блоков ТЕКУЩЕГО типа (issue #309, фаза 2). Проверку запускает
 * `useTypstBlocksCheck` — он же показывает тост-указатель на проблему в ДРУГОМ типе; здесь только
 * отображение.
 */
const CODE_LABEL: Record<string, string> = {
  cycle: 'взаимные ссылки',
  'cycle-cross-type': 'взаимные ссылки типов',
  'duplicate-fn': 'дубликат имени',
  'reserved-fn': 'имя занято системой',
  'relative-path': 'путь не от корня',
  syntax: 'синтаксис',
  'checker-unavailable': 'проверка недоступна',
};

export function TypstBlocksPanel({ problems, currentTypeId, onSelectType }: {
  problems: TypstBlockProblem[];
  currentTypeId: string;
  onSelectType: (id: string) => void;
}) {
  if (problems.length === 0)
    return (
      <div className="flex items-center gap-2 p-2.5 rounded-sm text-body-medium bg-success-container text-on-success-container">
        <Icon icon={CheckCircle} size={18} className="shrink-0" /> Все Typst-блоки собираются
      </div>
    );

  const here = problems.filter(p => !p.typeId || p.typeId === currentTypeId);
  const other = problems.filter(p => p.typeId && p.typeId !== currentTypeId);
  const errors = problems.filter(p => p.severity === 'error').length;
  const warns = problems.length - errors;

  return (
    <div className="rounded-sm border border-outline-variant overflow-hidden text-body-small">
      <div className="px-3 py-2 font-medium bg-surface-container-low text-on-surface-variant">
        Проблемы сборки блоков: {errors} ошиб.{warns ? `, ${warns} предупр.` : ''}
      </div>
      {here.length > 0 && <Group title="В этом типе" items={here} />}
      {other.length > 0 && <Group title="В других типах" items={other} onSelectType={onSelectType} />}
    </div>
  );
}

function Group({ title, items, onSelectType }: {
  title: string; items: TypstBlockProblem[]; onSelectType?: (id: string) => void;
}) {
  return (
    <div>
      <div className="px-3 pt-2 pb-1 text-title-small text-on-surface-variant">{title}</div>
      <div className="divide-y divide-surface-container">
        {items.map((p, i) => (
          <div key={i} className="flex items-start gap-2 px-3 py-2">
            {p.severity === 'error'
              ? <Icon icon={ErrorIcon} size={18} className="text-error shrink-0 mt-0.5" />
              : <Icon icon={Warning} size={18} className="text-warning shrink-0 mt-0.5" />}
            <div className="min-w-0 flex-1">
              {p.fnName && (
                <div className="text-on-surface-variant">
                  <code className="text-on-surface-variant">{p.fnName}</code>
                  {p.line != null ? ` · строка ${p.line}` : ''} · {CODE_LABEL[p.code] ?? p.code}
                </div>
              )}
              <p className={p.severity === 'error' ? 'text-error' : 'text-on-surface-variant'}>{p.message}</p>
              {onSelectType && p.typeId && (
                <button type="button" onClick={() => onSelectType(p.typeId!)}
                  className="inline-flex items-center gap-1 mt-1 text-primary state-layer">
                  Перейти к «{p.typeName ?? 'типу'}» <Icon icon={ArrowForward} size={18} />
                </button>
              )}
            </div>
          </div>
        ))}
      </div>
    </div>
  );
}
