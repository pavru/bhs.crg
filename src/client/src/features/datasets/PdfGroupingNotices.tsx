import { Scissors, TriangleAlert } from 'lucide-react';
import { useRecutGrouping } from '@/shared/api/datasets';
import { Button } from '@/shared/ui/Button';
import { useToast } from '@/shared/ui/Toast';
import { ruCount } from '@/shared/utils/pluralize';

/**
 * Документы набора без файла — и выход (issue #1534).
 *
 * <p>Части PDF-набора пропадают из хранилища без правки набора: восстановление из копии, снятой до
 * #1399, или копия источника, не обновлённая при прежней перерезке. Копия и восстановление называют
 * выход — это действие; прежде они посылали «сохранить разбиение», а «Сохранить» неактивна, пока
 * разбиение не правили.</p>
 *
 * <p>Полоса стоит только тогда, когда документов без файла не ноль, — то есть ровно там, где действие
 * что-то вернёт. Файл набора при этом на месте: без него редактор не открылся бы вовсе (листы
 * рисуются из файла). Пока разбиение правят, кнопки нет: сохранение вырежет документы и так, а
 * перерезка по прежнему разбиению рядом с несохранённой правкой читалась бы как её сохранение.</p>
 */
export function MissingPartsNotice({ fileId, missing, dirty }: {
  fileId: string;
  /** Сколько документов без файла — `missingParts` из чтения страниц. */
  missing: number;
  dirty: boolean;
}) {
  const recut = useRecutGrouping(fileId);
  const toast = useToast();
  if (missing <= 0) return null;

  return (
    <div role="alert" data-testid="missing-parts" className="mb-3 shrink-0 flex items-start gap-2 rounded-lg
      border border-warning-border bg-warning-container px-3 py-2 text-xs text-warning">
      <TriangleAlert size={13} className="shrink-0 mt-0.5" />
      <p className="flex-1">
        {ruCount(missing, 'документ', 'документа', 'документов')} без файла в хранилище: генерация выпустит
        их без вложений.{' '}
        {dirty
          ? 'Сохранение разбиения вырежет их заново.'
          : 'Вырезать их можно заново из файла набора — по текущему разбиению, оно не изменится.'}
      </p>
      {!dirty && (
        <Button size="sm" variant="outlined" icon={<Scissors size={13} />} loading={recut.isPending}
          onClick={() => recut.mutate(undefined, {
            onSuccess: r => r.missingParts > 0
              ? toast.error(`Вырезаны не все: без файла осталось ${ruCount(r.missingParts, 'документ', 'документа', 'документов')}.`)
              : toast.success('Документы вырезаны заново.'),
            onError: e => toast.apiError(e, 'Не удалось вырезать документы'),
          })}>
          Вырезать документы заново
        </Button>
      )}
    </div>
  );
}

/** Отказ сохранения разбиения — строкой под шапкой. */
export function GroupingSaveError({ error }: { error: unknown }) {
  return (
    <p className="text-sm text-error mb-3">
      {(error as { response?: { data?: { error?: string } } })?.response?.data?.error ?? 'Не удалось сохранить'}
    </p>
  );
}
