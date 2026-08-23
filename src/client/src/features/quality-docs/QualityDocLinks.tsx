import { useMemo, useState } from 'react';
import { AlertTriangle, Link2, Unlink, Replace } from 'lucide-react';
import { Button } from '@/shared/ui/Button';
import { ScopeIcon } from '@/shared/ui/ScopeIcon';
import { linkAnomaly } from './linkScopes';
import { ScopeReachNote } from './ScopeReachNote';
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
export function QualityDocLinks({ links, allLinks, allDocTypes, search }: {
  links: MaterialQualityLink[];
  /** Связки ВСЕЙ библиотеки — для поиска спора двух документов за один материал (issue #649):
   *  внутри одного документа такой спор безвреден, в PDF всё равно попадёт он же. */
  allLinks: MaterialQualityLink[];
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
    try {
      await removeLink.mutateAsync(link.id);
      toast.success(`Связь снята: ${nameOf(link)}`);
    } catch (e) {
      toast.apiError(e, 'Не удалось разорвать связь');
    }
  }

  // Действуем над ВИДИМЫМИ строками: иначе поиск, сузивший список, оставил бы в наборе строки,
  // которых человек уже не видит, а диалог показывает только число — поймать ошибку было бы нечем.
  const chosen = useMemo(() => shown.filter(l => selected.has(l.id)), [shown, selected]);

  /** Все выбранные связки одной области — тогда пикер можно показать в ней же. */
  const sameScope = chosen.every(l => l.scope === chosen[0]?.scope && (l.scopeId ?? null) === (chosen[0]?.scopeId ?? null));

  function toggle(id: string) {
    setSelected(prev => { const n = new Set(prev); n.has(id) ? n.delete(id) : n.add(id); return n; });
  }

  const anomalyOf = (link: MaterialQualityLink) => linkAnomaly(link, { inDocument: links, all: allLinks });

  async function relinkMany(docId: string) {
    // Перепривязка идёт ПО ГРУППАМ ОБЛАСТЕЙ. Команда апсертит по тройке (область, объект области,
    // ключ) — если выбраны связки разных областей и отправить их одной областью, чужие не
    // перенацелятся, зато родятся дубли, а при генерации победит узкая (несменённая) связка. То есть
    // ровно тот дефект, ради починки которого экран и делался.
    const byScope = new Map<string, MaterialQualityLink[]>();
    for (const l of chosen) {
      const k = `${l.scope}|${l.scopeId ?? ''}`;
      const arr = byScope.get(k); if (arr) arr.push(l); else byScope.set(k, [l]);
    }
    const n = chosen.length;
    try {
      for (const group of byScope.values()) {
        await setLinks.mutateAsync({
          scope: group[0].scope, scopeId: group[0].scopeId ?? null,
          materials: group.map(l => ({ key: l.materialKey })), qualityDocumentId: docId,
        });
      }
      setBulkRelink(false); setSelected(new Set());
      toast.success(`Перепривязано материалов: ${n}`);
    } catch (e) {
      // Молчаливый отказ здесь теряет сразу N перепривязок — человек нажал бы ещё раз.
      toast.apiError(e, 'Не удалось перепривязать');
    }
  }

  async function breakMany() {
    try {
      const { removed } = await removeLinks.mutateAsync(chosen.map(l => l.id));
      setSelected(new Set());
      toast.success(`Снято связей: ${removed}`);
    } catch (e) {
      toast.apiError(e, 'Не удалось разорвать связи');
    }
  }

  async function relink(docId: string) {
    if (!relinking) return;
    try {
    // Метку не шлём: здесь человеческого имени под рукой нет, а пустая метка не затирает
    // добытую при первой привязке (issue #554).
      await setLinks.mutateAsync({
        scope: relinking.scope, scopeId: relinking.scopeId ?? null,
        materials: [{ key: relinking.materialKey }], qualityDocumentId: docId,
      });
      setRelinking(null);
      toast.success('Материал перепривязан');
    } catch (e) {
      toast.apiError(e, 'Не удалось перепривязать');
    }
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
          {/* Уровень связи (issue #649): до этого он не показывался вовсе, хотя управляет и
              перепривязкой (она идёт по группам областей), и тем, какая связка победит в PDF. */}
          <ScopeIcon scope={link.scope as CatalogScope} className="mt-1" />
          <div className="min-w-0 flex-1">
            {/* Метка первой строкой во всю ширину: имена материалов доходят до сотни знаков,
                колонки на четверть экрана здесь противопоказаны. Ключ — второй строкой и
                моноширинным: это машинная строка, и вид должен об этом говорить. */}
            <p className="text-sm text-fg1 leading-snug break-words">{nameOf(link)}</p>
            {link.materialLabel && (
              <p className="text-xs text-fg4 font-mono break-all">{link.materialKey}</p>
            )}
          </div>
          {/* Знак аномалии — рядом со значком уровня, но цветом: он говорит не «какой уровень»,
              а «здесь что-то не так». Тот же приём, что у коллизий идентичности в QualityLinksTab. */}
          {anomalyOf(link) && (
            <span title={anomalyOf(link)!} aria-label={anomalyOf(link)!} role="img"
              className="mt-1 shrink-0 text-warning"><AlertTriangle size={13} /></span>
          )}
          <div className="flex items-center gap-1 shrink-0">
            <Button variant="text" size="sm" icon={<Replace size={13} />}
              onClick={() => setRelinking(link)}>Перепривязать</Button>
            <Button variant="text" size="sm" icon={<Unlink size={13} />}
              onClick={() => setBreaking(link)}>Разорвать</Button>
          </div>
        </div>
      ))}

      {/* Число — по ВИДИМЫМ выбранным: действия работают именно над ними, и подпись обязана
          совпадать с тем, что произойдёт. Скрытые поиском строки в счёт не идут. */}
      {chosen.length > 0 && (
        <div className="sticky bottom-0 flex items-center gap-2 rounded-md border border-stroke bg-surface px-3 py-2 shadow-sm">
          <span className="text-sm text-fg2">Выбрано: {chosen.length}</span>
          <Button variant="filled" size="sm" icon={<Replace size={13} />}
            onClick={() => setBulkRelink(true)}>Перепривязать ({chosen.length})</Button>
          <Button variant="outlined" size="sm" icon={<Unlink size={13} />}
            onClick={() => setBulkBreak(true)}>Разорвать ({chosen.length})</Button>
          <button type="button" onClick={() => setSelected(new Set())}
            className="ml-auto text-xs text-fg4 hover:text-fg2">Снять выбор</button>
        </div>
      )}

      <ConfirmDialog
        open={bulkBreak} onOpenChange={setBulkBreak}
        title={`Разорвать связи: ${chosen.length}?`}
        description={<>
          <p>Выбранные материалы останутся без документов качества — при генерации поле
            документа качества будет пустым.</p>
          <ScopeReachNote links={chosen} />
        </>}
        confirmLabel="Разорвать"
        onConfirm={() => breakMany()}
      />

      {bulkRelink && chosen.length > 0 && (
        <LinkPickerModal
          open onClose={() => setBulkRelink(false)} allDocTypes={allDocTypes}
          /* Область нужна пикеру, чтобы показать библиотеку; при разнородном выборе берём общую —
             она видна отовсюду. На запись это не влияет: там области разбираются по группам. */
          scope={(sameScope ? chosen[0].scope : 'System') as CatalogScope}
          scopeId={sameScope ? chosen[0].scopeId ?? null : null}
          materials={chosen.map(l => ({ key: l.materialKey, label: nameOf(l), idValues: [nameOf(l)] }))}
          onPick={doc => void relinkMany(doc.id)}
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
            <ScopeReachNote links={breaking ? [breaking] : []} />
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
          onPick={doc => void relink(doc.id)}
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
