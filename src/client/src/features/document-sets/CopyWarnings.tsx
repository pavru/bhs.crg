import { useState } from 'react';
import type { CopyWarning } from '@/shared/api/documentSets';

/** Порог, с которого имена в предупреждении сворачиваются за «показать» (иначе диалог распухает). */
const NAMES_INLINE_MAX = 3;

/**
 * Список предупреждений о затронутых ссылках при копировании/переносе (issue #283/#287), сгруппирован
 * по виду. Длинный перечень имён (>3) сворачивается за «показать» — кнопка подтверждения не уезжает.
 * Пустой список → ничего не рендерит (вызывающий сам решает, что показать).
 */
export function CopyWarnings({ warnings }: { warnings: CopyWarning[] }) {
  if (warnings.length === 0) return null;
  return (
    <ul className="list-disc pl-4 space-y-0.5">
      {warnings.map(w => <WarningLine key={w.kind} w={w} />)}
    </ul>
  );
}

function WarningLine({ w }: { w: CopyWarning }) {
  const [open, setOpen] = useState(false);
  const collapsed = w.names.length > NAMES_INLINE_MAX;
  return (
    <li>
      {w.label}{w.count > 1 ? ` (${w.count})` : ''}
      {w.names.length > 0 && (collapsed ? (
        <>
          {' '}
          <button type="button" onClick={() => setOpen(o => !o)}
            className="text-brand hover:text-brand-hover text-xs">
            {open ? 'скрыть' : 'показать'}
          </button>
          {open && <div className="text-fg4 mt-0.5">{w.names.join(', ')}</div>}
        </>
      ) : (
        <span className="text-fg4">: {w.names.join(', ')}</span>
      ))}
    </li>
  );
}
