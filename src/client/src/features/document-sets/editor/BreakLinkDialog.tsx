import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import { ScopeReachNote } from '@/features/quality-docs/ScopeReachNote';
import type { MaterialQualityLink } from '@/shared/api/qualityDocs';
import { SCOPE_LABELS } from '@/shared/api/types';

/**
 * Подтверждение разрыва связи (issue #682; вынесено из вкладки в #1032).
 *
 * `shadowedBy` отвечает на вопрос, который по экрану не виден: под снимаемой связкой может лежать
 * другая, с уровня пошире, — и тогда материал без документа качества НЕ останется.
 */
export function BreakLinkDialog({ breaking, setBreaking, shadowedBy, removeLink }: {
  breaking: { link: MaterialQualityLink; label: string } | null;
  setBreaking: (v: null) => void;
  shadowedBy: (link: MaterialQualityLink) => MaterialQualityLink | null;
  removeLink: (id: string) => Promise<unknown>;
}) {
  return (
    <ConfirmDialog
      open={!!breaking} onOpenChange={o => { if (!o) setBreaking(null); }}
      title="Разорвать связь?"
      description={breaking ? (
        <>
          <p className="mb-2">{breaking.label}</p>
          {shadowedBy(breaking.link) ? (
            <p>Материал без документа качества НЕ останется: под этой связкой лежит другая, уровня
              {' '}«{SCOPE_LABELS[shadowedBy(breaking.link)!.scope]}», с документом
              {' '}<b>{shadowedBy(breaking.link)!.qualityDocumentName}</b> — при генерации
              подставится он. Чтобы материал остался пустым, снимите и её.</p>
          ) : (
            <p>Материал останется без документа качества — при генерации поле документа качества
              будет пустым.</p>
          )}
          <ScopeReachNote links={[breaking.link]} />
        </>
      ) : ''}
      confirmLabel="Разорвать" errorTitle="Не удалось снять связь"
      onConfirm={() => { if (breaking) return removeLink(breaking.link.id); }}
    />
  );
}
