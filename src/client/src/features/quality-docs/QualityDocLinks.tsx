import { useMemo, useState } from 'react';
import { Link2, Unlink, Replace } from 'lucide-react';
import { Button } from '@/shared/ui/Button';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import { EmptyState } from '@/shared/ui/EmptyState';
import { useToast } from '@/shared/ui/Toast';
import { useRemoveMaterialLink, useSetMaterialLinks, type MaterialQualityLink } from '@/shared/api/qualityDocs';
import { LinkPickerModal } from '@/features/document-sets/editor/QualityLinksTab';
import type { CatalogScope, DocumentType } from '@/shared/api/types';

/**
 * Связки материалов документа качества — правая часть экрана контроля (issue #555).
 *
 * Экран строится не как витрина, а как инструмент поиска дефекта: на живых данных у сертификата на
 * модель AV-125 оказалось 69 связок, из которых ключ содержит «av-125» ровно в одной (#552). Поэтому:
 *
 * <ul>
 *   <li>главное действие — ПЕРЕПРИВЯЗАТЬ, а не разорвать: разрыв кабеля с сертификата на автоматы
 *       меняет одну ошибку («не тот документ») на другую («документа нет»);</li>
 *   <li>сортировка по алфавиту — артикульные семейства (mb15-*, mcb10-*) встают рядом, и чужак
 *       виден глазом без всякого счёта;</li>
 *   <li>процент релевантности НЕ показываем: из 113 живых связок 58 дают ровно 0 %, и почти все нули
 *       это артикулы, где сопоставлять нечего. Ноль означает «непроверяемо», а не «неверно».</li>
 * </ul>
 */
export function QualityDocLinks({ links, allDocTypes, search }: {
  links: MaterialQualityLink[];
  allDocTypes: DocumentType[];
  /** Строка поиска — строки фильтруются на клиенте (113 связок фильтруются мгновенно). */
  search: string;
}) {
  const toast = useToast();
  const removeLink = useRemoveMaterialLink();
  const setLinks = useSetMaterialLinks();
  const [breaking, setBreaking] = useState<MaterialQualityLink | null>(null);
  const [relinking, setRelinking] = useState<MaterialQualityLink | null>(null);

  const shown = useMemo(() => {
    const q = search.trim().toLowerCase();
    const list = q
      ? links.filter(l => matchesLink(l, q))
      : links;
    return [...list].sort((a, b) => nameOf(a).localeCompare(nameOf(b), 'ru'));
  }, [links, search]);

  async function breakLink(link: MaterialQualityLink) {
    await removeLink.mutateAsync(link.id);
    toast.success(`Связь снята: ${nameOf(link)}`);
  }

  async function relink(docId: string) {
    if (!relinking) return;
    // Метку не шлём: здесь человеческого имени под рукой нет, а пустая метка не затирает
    // добытую при первой привязке (issue #554).
    await setLinks.mutateAsync({
      scope: relinking.scope, scopeId: relinking.scopeId ?? null,
      materials: [{ key: relinking.materialKey }], qualityDocumentId: docId,
    });
    setRelinking(null);
    toast.success('Материал перепривязан');
  }

  if (links.length === 0) {
    return (
      <EmptyState icon={<Link2 size={28} />} title="Связок нет"
        description="К этому документу не привязан ни один материал. Привязка делается на вкладке «Документы качества» внутри документа комплекта." />
    );
  }

  return (
    <div className="space-y-1">
      {shown.length === 0 && (
        <p className="text-sm text-fg4 py-4 text-center">Ни одна связка не подходит под поиск.</p>
      )}
      {shown.map(link => (
        <div key={link.id} className="group flex items-start gap-3 rounded-md px-3 py-2 hover:bg-base">
          <Link2 size={13} className="text-fg4 shrink-0 mt-1" />
          <div className="min-w-0 flex-1">
            {/* Метка первой строкой во всю ширину: имена материалов доходят до сотни знаков,
                колонки на четверть экрана здесь противопоказаны. Ключ — второй строкой и
                моноширинным: это машинная строка, и вид должен об этом говорить. */}
            <p className="text-sm text-fg1 leading-snug break-words">{nameOf(link)}</p>
            {link.materialLabel && (
              <p className="text-xs text-fg4 font-mono break-all">{link.materialKey}</p>
            )}
          </div>
          <div className="flex items-center gap-1 shrink-0">
            <Button variant="text" size="sm" icon={<Replace size={13} />}
              onClick={() => setRelinking(link)}>Перепривязать</Button>
            <Button variant="text" size="sm" icon={<Unlink size={13} />}
              onClick={() => setBreaking(link)}>Разорвать</Button>
          </div>
        </div>
      ))}

      <ConfirmDialog
        open={!!breaking} onOpenChange={o => { if (!o) setBreaking(null); }}
        title="Разорвать связь?"
        description={
          <>
            <p className="mb-2">{breaking ? nameOf(breaking) : ''}</p>
            <p>Материал останется без документа качества — при генерации поле документа качества
              будет пустым.</p>
          </>
        }
        confirmLabel="Разорвать"
        onConfirm={() => { if (breaking) return breakLink(breaking); }}
      />

      {relinking && (
        <LinkPickerModal
          open onClose={() => setRelinking(null)} allDocTypes={allDocTypes}
          scope={relinking.scope as CatalogScope} scopeId={relinking.scopeId ?? null}
          materials={[{ key: relinking.materialKey, label: nameOf(relinking), idValues: [nameOf(relinking)] }]}
          onPick={docId => void relink(docId)}
        />
      )}
    </div>
  );
}

/** Имя материала: метка, если она есть, иначе машинный ключ (у связок до #554 метки нет). */
export function nameOf(link: MaterialQualityLink): string {
  return link.materialLabel?.trim() || link.materialKey;
}

/** Поиск идёт по ключу И по метке: артикул ищут одним, человеческое имя — другим. */
export function matchesLink(link: MaterialQualityLink, lowerQuery: string): boolean {
  return link.materialKey.toLowerCase().includes(lowerQuery)
    || (link.materialLabel ?? '').toLowerCase().includes(lowerQuery);
}
