import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import type { BulkLinkAssessment } from './qualityMatch';
import type { MaterialRow } from './materialRow';

/**
 * Сводка перед массовой привязкой (issue #552; вынесена из вкладки в #1032). Появляется, ТОЛЬКО
 * если что-то не сходится: у артикулов сравнивать нечего, и показывать её всегда значило бы
 * приучить нажимать «да». Условие живёт у вызывающего — в `handlePick` вкладки.
 */
export function BulkLinkMismatchDialog({ pendingLink, setPendingLink, setSingleTarget, linkChosen }: {
  pendingLink: {
    docId: string; docName: string; chosen: MaterialRow[];
    assessment: BulkLinkAssessment<MaterialRow>;
  } | null;
  setPendingLink: (v: null) => void;
  setSingleTarget: (v: null) => void;
  linkChosen: (docId: string, chosen: MaterialRow[]) => Promise<void>;
}) {
  return (
    <ConfirmDialog
      open={!!pendingLink} onOpenChange={o => { if (!o) { setPendingLink(null); setSingleTarget(null); } }}
      title="Материалы не похожи на этот документ"
      description={pendingLink ? (
        <div className="space-y-2">
          {/* Один материал называем по имени: «из 1 выбранных материалов ему соответствуют 0» —
              это отчёт о выборке там, где речь об одной строке (issue #680). */}
          {pendingLink.chosen.length === 1 ? (
            <p>
              Материал <b>{pendingLink.chosen[0].label}</b> не похож на документ
              {' '}<b>{pendingLink.docName}</b>: ни одно его слово в документе не встречается.
            </p>
          ) : (
            <p>
              Документ <b>{pendingLink.docName}</b>: из {pendingLink.chosen.length} выбранных
              материалов ему соответствуют {pendingLink.assessment.fits.length},
              {' '}не похожи — <b>{pendingLink.assessment.mismatched.length}</b>
              {pendingLink.assessment.unverifiable.length > 0
                && `, ещё ${pendingLink.assessment.unverifiable.length} проверить нечем (только артикул)`}.
            </p>
          )}
          {pendingLink.chosen.length > 1 && (
            <ul className="text-xs text-fg3 space-y-0.5 max-h-40 overflow-y-auto">
              {pendingLink.assessment.mismatched.slice(0, 8).map(m => (
                <li key={m.key} className="truncate">• {m.label}</li>
              ))}
              {pendingLink.assessment.mismatched.length > 8 && (
                <li>… и ещё {pendingLink.assessment.mismatched.length - 8}</li>
              )}
            </ul>
          )}
          <p className="text-xs text-fg4">
            Проверка приблизительная — она сравнивает слова материала с текстом документа. Если
            документ действительно тот, привязывайте.
          </p>
        </div>
      ) : ''}
      confirmLabel="Всё равно привязать" errorTitle="Не удалось привязать"
      onConfirm={() => { if (pendingLink) return linkChosen(pendingLink.docId, pendingLink.chosen); }}
    />
  );
}
