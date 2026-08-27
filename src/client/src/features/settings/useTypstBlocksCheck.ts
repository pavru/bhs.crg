import { useState } from 'react';
import { useToast } from '@/shared/ui/Toast';
import { useValidateTypstBlocks, type TypstBlockProblem } from '@/shared/api/documentTypes';
import type { TypstRender } from '@/shared/api/schema';

// Отдельным файлом от компонента (issue #858): модуль, экспортирующий и компонент, и
// функцию, не может быть границей горячей подмены — правка поднимается вверх по импортам.

/**
 * Проверка сборки Typst-блоков (issue #309, фаза 2). Глобальна и межтипова: результат живёт в двух
 * зонах — блоки ТЕКУЩЕГО типа показываем инлайн-панелью здесь, а проблему в ДРУГОМ типе (цикл/чужая
 * ссылка) — тостом-указателем «Перейти» (инвариант тостов: тост для off-screen результата).
 */
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
    } catch (e) {
      toast.apiError(e, 'Не удалось проверить блоки');
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
