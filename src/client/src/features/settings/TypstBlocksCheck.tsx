import { useState } from 'react';
import { CheckCircle2, AlertCircle, AlertTriangle, ArrowRight } from 'lucide-react';
import { useToast } from '@/shared/ui/Toast';
import { useValidateTypstBlocks, type TypstBlockProblem } from '@/shared/api/documentTypes';
import type { TypstRender } from '@/shared/api/schema';

/**
 * Проверка сборки Typst-блоков (issue #309, фаза 2). Глобальна и межтипова: результат живёт в двух
 * зонах — блоки ТЕКУЩЕГО типа показываем инлайн-панелью здесь, а проблему в ДРУГОМ типе (цикл/чужая
 * ссылка) — тостом-указателем «Перейти» (инвариант тостов: тост для off-screen результата).
 */
const CODE_LABEL: Record<string, string> = {
  cycle: 'взаимные ссылки',
  'duplicate-fn': 'дубликат имени',
  syntax: 'синтаксис',
  'checker-unavailable': 'проверка недоступна',
};

export function useTypstBlocksCheck(typeId: string, onSelectType: (id: string) => void) {
  const validate = useValidateTypstBlocks();
  const toast = useToast();
  const [problems, setProblems] = useState<TypstBlockProblem[] | null>(null);

  async function run(renders: TypstRender[]) {
    try {
      const res = await validate.mutateAsync({ typeId, renders });
      setProblems(res);
      // Off-screen проблема (в другом типе) → тост-указатель с навигацией.
      const other = res.find(p => p.severity === 'error' && p.typeId && p.typeId !== typeId);
      if (other?.typeId) {
        toast.error(`Блок «${other.fnName ?? '—'}» в типе «${other.typeName ?? '—'}» больше не собирается`, {
          action: { label: 'Перейти', onClick: () => onSelectType(other.typeId!) },
        });
      }
    } catch {
      toast.error('Не удалось проверить блоки');
    }
  }

  return { problems, checking: validate.isPending, run, reset: () => setProblems(null) };
}

/** Карта fnName → худшая severity для блоков ТЕКУЩЕГО типа — для бейджей на карточках. */
export function blocksCheckProblemsByFn(problems: TypstBlockProblem[] | null, typeId: string): Record<string, 'error' | 'warning'> {
  const m: Record<string, 'error' | 'warning'> = {};
  for (const p of problems ?? []) {
    if (!p.fnName || (p.typeId && p.typeId !== typeId)) continue;
    if (p.severity === 'error' || m[p.fnName] !== 'error') m[p.fnName] = p.severity;
  }
  return m;
}

export function TypstBlocksPanel({ problems, currentTypeId, onSelectType }: {
  problems: TypstBlockProblem[];
  currentTypeId: string;
  onSelectType: (id: string) => void;
}) {
  if (problems.length === 0)
    return (
      <div className="flex items-center gap-2 p-2.5 rounded-md text-sm bg-success-subtle text-success">
        <CheckCircle2 size={15} className="shrink-0" /> Все Typst-блоки собираются
      </div>
    );

  const here = problems.filter(p => !p.typeId || p.typeId === currentTypeId);
  const other = problems.filter(p => p.typeId && p.typeId !== currentTypeId);
  const errors = problems.filter(p => p.severity === 'error').length;
  const warns = problems.length - errors;

  return (
    <div className="rounded-md border border-stroke overflow-hidden text-xs">
      <div className="px-3 py-2 font-medium bg-base text-fg2">
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
      <div className="px-3 pt-2 pb-1 text-[10px] font-semibold uppercase tracking-wide text-fg4">{title}</div>
      <div className="divide-y divide-muted">
        {items.map((p, i) => (
          <div key={i} className="flex items-start gap-2 px-3 py-2">
            {p.severity === 'error'
              ? <AlertCircle size={14} className="text-danger shrink-0 mt-0.5" />
              : <AlertTriangle size={14} className="text-warning shrink-0 mt-0.5" />}
            <div className="min-w-0 flex-1">
              {p.fnName && (
                <div className="text-fg3">
                  <code className="text-fg2">{p.fnName}</code>
                  {p.line != null ? ` · строка ${p.line}` : ''} · {CODE_LABEL[p.code] ?? p.code}
                </div>
              )}
              <p className={p.severity === 'error' ? 'text-danger' : 'text-fg2'}>{p.message}</p>
              {onSelectType && p.typeId && (
                <button type="button" onClick={() => onSelectType(p.typeId!)}
                  className="inline-flex items-center gap-1 mt-1 text-brand hover:text-brand-hover">
                  Перейти к «{p.typeName ?? 'типу'}» <ArrowRight size={12} />
                </button>
              )}
            </div>
          </div>
        ))}
      </div>
    </div>
  );
}
