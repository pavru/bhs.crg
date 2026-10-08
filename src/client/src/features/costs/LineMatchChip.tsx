import * as Popover from '@radix-ui/react-popover';
import type { ReactNode } from 'react';
import { X } from 'lucide-react';
import { formatDate } from '@/shared/format/format';
import type { LineDraft } from './invoiceLines';
import { byWord, lineKey, matchTrouble, usable, type MatchOffer, type MemoryFate } from './supplierMatches';

/**
 * Пометка у позиции строки счёта (задача C3, issue #1079, ТЗ COST-7.1): «запомнено» у подставленной,
 * «запомнится» у выбранной руками, «отменено · вернуть» у отменённой.
 *
 * <p><b>Тихая, а не жёлтая.</b> В счёте на сорок строк подставлено бывает двадцать пять — пометка обязана
 * читаться столбцом, не споря с «выбрать позицию», которое действительно ждёт человека. Жёлтой она
 * становится только там, где есть о чём предупредить: выбор заменит запомненное раньше, либо
 * соответствие с тех пор изменили.</p>
 *
 * <p>⚠️ Крестик отменяет подстановку БЕЗ вопроса: это одно действие по ТЗ, и обратимо оно тут же —
 * «вернуть» стоит на том же месте до сохранения строк.</p>
 */
export function LineMatchChip({ draft, offer, fate, onCancel, onRestore, onRemember }: {
  draft: LineDraft;
  /** Запомненное для этой строки, известное форме. */
  offer: MatchOffer | null | undefined;
  fate: MemoryFate;
  onCancel: () => void;
  onRestore: (offer: MatchOffer) => void;
  onRemember: (remember: boolean) => void;
}) {
  if (draft.matchedBy !== null) {
    const trouble = matchTrouble(draft.match);
    return (
      <span className="shrink-0 inline-flex items-center">
        <Explained label="запомнено" warning={trouble !== null}>
          <p>
            Подставлено из соответствий поставщика: {byWord(draft.match?.by ?? null)}
            {draft.match?.source ? ` «${draft.match.source}»` : ''} → «{draft.nomenclatureName ?? 'позиция без названия'}».
          </p>
          {draft.match?.rememberedAt && (
            <p className="text-fg4">
              Запомнено {formatDate(draft.match.rememberedAt)}{draft.match.rememberedBy ? `, ${draft.match.rememberedBy}` : ''}.
            </p>
          )}
          {trouble && (
            <p className="text-warning">
              Внимание: {trouble}. В этой строке позиция осталась прежней — проверьте, та ли она.
            </p>
          )}
          <p className="text-fg4">Крестик отменяет подстановку в этой строке. Само соответствие остаётся.</p>
        </Explained>
        <button type="button" onClick={onCancel} title="Отменить подстановку"
          className="text-fg4 hover:text-fg p-0.5">
          <X size={12} />
        </button>
      </span>
    );
  }

  if (draft.nomenclatureId === null) {
    if (draft.declined && usable(offer))
      return (
        <span className="shrink-0 text-[11px] text-fg4">
          отменено ·{' '}
          <button type="button" className="text-brand hover:underline" onClick={() => onRestore(offer)}>вернуть</button>
        </span>
      );

    // Запомненное есть, а подставить его нельзя — сказано словами: иначе строка выглядела бы
    // незнакомой, и человек запомнил бы её заново, не узнав, что прежний выбор в архиве.
    if (offer && offer.issue !== null)
      return (
        <Explained label={offer.issue === 'archived' ? 'запомненное в архиве' : 'запомненное удалено'} warning>
          <p>
            Для этой строки запомнена позиция «{offer.nomenclatureName ?? 'без названия'}», но{' '}
            {offer.issue === 'archived'
              ? 'она в архиве — в новые строки архивные позиции не подставляются.'
              : 'её в справочнике больше нет.'}
          </p>
          <p className="text-fg4">Выберите действующую позицию — выбор заменит запомненное.</p>
        </Explained>
      );

    return null;
  }

  const key = lineKey(draft.supplierCode, draft.supplierText);
  const what = key ? `${byWord(key.by)} «${(key.by === 'code' ? draft.supplierCode : draft.supplierText).trim()}»` : '';
  const name = draft.nomenclatureName ?? 'позиция без названия';

  if (fate === 'remember')
    return (
      <Explained label="запомнится">
        <p>При сохранении строк запомнится: {what} → «{name}».</p>
        <p className="text-fg4">Следующий счёт этого поставщика получит эту позицию сам.</p>
        <Action onClick={() => onRemember(false)}>Не запоминать</Action>
      </Explained>
    );

  if (fate === 'replace')
    return (
      <Explained label="запомнится вместо прежнего" warning>
        <p>
          Для этой строки у поставщика запомнено «{offer?.nomenclatureName ?? 'позиция без названия'}». При
          сохранении строк оно будет заменено: {what} → «{name}».
        </p>
        <p className="text-fg4">Строки других счетов замена не переписывает.</p>
        <Action onClick={() => onRemember(false)}>Только в этой строке</Action>
      </Explained>
    );

  if (fate === 'off')
    return (
      <Explained label="не запомнится">
        <p>Выбор останется только в этой строке: {what} запоминаться не будет.</p>
        <Action onClick={() => onRemember(true)}>Запоминать</Action>
      </Explained>
    );

  return null;
}

/** Слово-кнопка с раскрытием: пометка обязана уметь объяснить себя, не уводя со строки. */
function Explained({ label, warning = false, children }: { label: string; warning?: boolean; children: ReactNode }) {
  return (
    <Popover.Root>
      <Popover.Trigger asChild>
        <button type="button"
          className={`shrink-0 text-[11px] hover:underline ${warning ? 'text-warning' : 'text-fg3'}`}>
          {label}
        </button>
      </Popover.Trigger>
      <Popover.Portal>
        <Popover.Content align="start" sideOffset={6}
          className="z-50 w-[340px] max-w-[90vw] rounded-lg bg-surface border border-stroke p-3 text-xs text-fg2 space-y-2 focus:outline-none"
          style={{ boxShadow: 'var(--f-shadow16)' }}>
          {children}
        </Popover.Content>
      </Popover.Portal>
    </Popover.Root>
  );
}

function Action({ onClick, children }: { onClick: () => void; children: ReactNode }) {
  return (
    <Popover.Close asChild>
      <button type="button" onClick={onClick} className="text-brand hover:underline">{children}</button>
    </Popover.Close>
  );
}
