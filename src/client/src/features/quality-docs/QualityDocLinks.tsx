import { useMemo, useState } from 'react';
import { Link2, Unlink, Replace } from 'lucide-react';
import { Button } from '@/shared/ui/Button';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import { EmptyState } from '@/shared/ui/EmptyState';
import { useToast } from '@/shared/ui/Toast';
import {
  useRemoveMaterialLink, useRemoveMaterialLinks, useSetMaterialLinks, type MaterialQualityLink,
} from '@/shared/api/qualityDocs';
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
  const removeLinks = useRemoveMaterialLinks();
  const setLinks = useSetMaterialLinks();
  const [breaking, setBreaking] = useState<MaterialQualityLink | null>(null);
  const [relinking, setRelinking] = useState<MaterialQualityLink | null>(null);
  // Массовые действия (issue #556): чинить 68 неверных связок по одной неприемлемо.
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [bulkRelink, setBulkRelink] = useState(false);
  const [bulkBreak, setBulkBreak] = useState(false);

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

  const chosen = useMemo(() => links.filter(l => selected.has(l.id)), [links, selected]);

  function toggle(id: string) {
    setSelected(prev => { const n = new Set(prev); n.has(id) ? n.delete(id) : n.add(id); return n; });
  }

  async function relinkMany(docId: string) {
    // Ложится на существующий контракт: массив ключей + один документ, существующие связки
    // ПЕРЕНАЦЕЛИВАЮТСЯ (Retarget), а не дублируются.
    await setLinks.mutateAsync({
      scope: chosen[0].scope, scopeId: chosen[0].scopeId ?? null,
      materials: chosen.map(l => ({ key: l.materialKey })), qualityDocumentId: docId,
    });
    const n = chosen.length;
    setBulkRelink(false); setSelected(new Set());
    toast.success(`Перепривязано материалов: ${n}`);
  }

  async function breakMany() {
    const { removed } = await removeLinks.mutateAsync(chosen.map(l => l.id));
    setSelected(new Set());
    toast.success(`Снято связей: ${removed}`);
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
          <input type="checkbox" checked={selected.has(link.id)} onChange={() => toggle(link.id)}
            aria-label={`Выбрать ${nameOf(link)}`} className="mt-1 shrink-0" />
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

      {selected.size > 0 && (
        <div className="sticky bottom-0 flex items-center gap-2 rounded-md border border-stroke bg-surface px-3 py-2 shadow-sm">
          <span className="text-sm text-fg2">Выбрано: {selected.size}</span>
          <Button variant="filled" size="sm" icon={<Replace size={13} />}
            onClick={() => setBulkRelink(true)}>Перепривязать ({selected.size})</Button>
          <Button variant="outlined" size="sm" icon={<Unlink size={13} />}
            onClick={() => setBulkBreak(true)}>Разорвать ({selected.size})</Button>
          <button type="button" onClick={() => setSelected(new Set())}
            className="ml-auto text-xs text-fg4 hover:text-fg2">Снять выбор</button>
        </div>
      )}

      <ConfirmDialog
        open={bulkBreak} onOpenChange={setBulkBreak}
        title={`Разорвать связи: ${selected.size}?`}
        description={<p>Выбранные материалы останутся без документов качества — при генерации поле
          документа качества будет пустым.</p>}
        confirmLabel="Разорвать"
        onConfirm={() => breakMany()}
      />

      {bulkRelink && chosen.length > 0 && (
        <LinkPickerModal
          open onClose={() => setBulkRelink(false)} allDocTypes={allDocTypes}
          scope={chosen[0].scope as CatalogScope} scopeId={chosen[0].scopeId ?? null}
          materials={chosen.map(l => ({ key: l.materialKey, label: nameOf(l), idValues: [nameOf(l)] }))}
          onPick={docId => void relinkMany(docId)}
        />
      )}

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
