import { useState, type ReactNode } from 'react';
import { scopePurgeOffered, usePurgeScope, type PurgeLevel, type ScopePurgeOffer } from '@/shared/api/scopePurge';
import { CascadeList, type RefusalExit } from '@/shared/ui/ConfirmDialog';
import { PurgeDialog } from './catalog/PurgeDialog';
import { purgeFailure } from './catalog/purgeRecord';
import { exitNote, leavingLines, levelWords, scopeConsequences, scopePurgedToast } from './scopePurge';

interface Target { id: string; name: string }

/**
 * Принудительное удаление уровня — стройки, раздела, комплекта (issue #1243): выход из отказа
 * обычного удаления и окно подтверждения. Одна связка на все места, где уровень удаляют.
 *
 * Входа помимо отказа нет: предложение приходит ПОЛЕМ ответа, и до него о держателях экран не
 * знает. Права нет — строка без кнопки.
 *
 * @param closeConfirm Закрыть диалог обычного удаления: окно подтверждения открывается вместо него,
 *   а не поверх.
 * @param onDone После успеха — уйти со страницы удалённого уровня (в списке — ничего).
 */
export function useScopePurge(level: PurgeLevel, closeConfirm: () => void, onDone?: () => void) {
  const purge = usePurgeScope(level);
  const [purging, setPurging] = useState<{ target: Target; offer: ScopePurgeOffer } | null>(null);
  const words = levelWords(level);

  const onRefused = (target: Target | null | undefined) => (e: unknown): RefusalExit | null => {
    const offer = target && scopePurgeOffered(e);
    if (!offer) return null;
    if (!offer.allowed) return { note: null, secondary: { note: exitNote(level, false) } };
    return {
      note: null,
      secondary: {
        note: exitNote(level, true),
        action: {
          label: 'Удалить, потеряв ссылки…',
          onClick: () => { closeConfirm(); setPurging({ target, offer }); },
        },
      },
    };
  };

  const dialog: ReactNode = purging && (
    <PurgeDialog offer={purging.offer} pending={purge.isPending}
      onClose={() => setPurging(null)}
      onDone={() => { setPurging(null); onDone?.(); }}
      subject={{
        title: `Удалить ${words.whom} «${purging.target.name}», потеряв ссылки?`,
        intro: words.intro,
        above: offer => {
          const lines = leavingLines(offer.leaving);
          return lines.length > 0 && (
            <div className="mb-3 text-xs text-fg2">
              <p>{words.together}</p>
              <CascadeList items={lines} />
            </div>
          );
        },
        lines: offer => scopeConsequences(level, offer),
        submit: `Удалить ${words.whom}, потеряв ссылки`,
        run: references => purge.mutateAsync({ id: purging.target.id, references }),
        done: references => scopePurgedToast(level, purging.target.name, references),
        failure: (e, shown) => purgeFailure(e, shown, words.outcome, scopePurgeOffered),
      }} />
  );

  return { onRefused, dialog };
}
