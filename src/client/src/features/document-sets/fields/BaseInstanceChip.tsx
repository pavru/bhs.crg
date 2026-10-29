import { useState } from 'react';
import * as DropdownMenu from '@radix-ui/react-dropdown-menu';
import { Icon } from '@/shared/ui/Icon';
import { ChangeCircle, Database, Description, KeyboardArrowDown, LinkIcon, LinkOff, Warning } from '@/shared/ui/icons';
import { BaseCandidatePicker } from './BaseCandidatePicker';
import type { BaseCandidate } from './baseCandidates';
import { Tooltip } from '@/shared/ui/Tooltip';

/**
 * Chip «Основа» в шапке документа (issue #71, перенос из раздела формы в шапку — вариант друга 1a).
 * Базовый экземпляр — документ-левел мета-настройка (откуда предзаполняются поля), поэтому живёт в
 * шапке рядом со статусом, а не среди равноправных разделов формы. Презентационный: состояние базы и
 * формат `_baseRef` считает вызывающий (см. BaseCandidatePicker). Пикер — модалка из chip.
 *
 * `editable=false` — редактирование недоступно (напр. открыт не на вкладке реквизитов): chip только
 * показывает текущую основу, действия Заменить/Отвязать скрыты.
 */
export function BaseInstanceChip({
  selected, missing, candidates, editable = true, onSelect, onClear,
}: {
  selected: BaseCandidate | undefined;   // выбранная база (по _baseRef), если найдена среди кандидатов
  missing: boolean;                      // ссылка задана, но кандидат не найден (удалён/вне видимости)
  candidates: BaseCandidate[];
  editable?: boolean;
  onSelect: (c: BaseCandidate) => void;
  onClear: () => void;
}) {
  const [pickerOpen, setPickerOpen] = useState(false);

  const icon = selected?.proxy ? LinkIcon : selected?.kind === 'catalog' ? Database : Description;
  const chipLabel = selected && `${selected.proxy ? 'Роль' : 'Основа'}: ${selected.name}`;

  // База выбрана — тональный chip с меню (Заменить / Отвязать); read-only при !editable.
  const selectedChip = selected && (
    <DropdownMenu.Root>
      <DropdownMenu.Trigger asChild disabled={!editable}>
        <button type="button"
          className={`flex items-center gap-1.5 h-7 pl-2.5 pr-2 rounded-lg text-xs font-medium bg-primary-container text-brand-hover max-w-[16rem] transition-colors ${
            editable ? 'hover:bg-primary-container/70 cursor-pointer' : 'cursor-default'}`}>
          <Icon icon={icon} size={18} className="shrink-0" />
          {/* Полный текст обрезанной подписи — нативным title: подсказка на самой кнопке не видна,
              пока она неактивна (только чтение), а обрезана подпись и тогда. */}
          <span className="truncate" title={chipLabel}>{chipLabel}</span>
          {editable && <Icon icon={KeyboardArrowDown} size={18} className="shrink-0 opacity-70" />}
        </button>
      </DropdownMenu.Trigger>
      <DropdownMenu.Portal>
        <DropdownMenu.Content align="end" sideOffset={4}
          className="z-50 min-w-[12rem] rounded-md border border-outline-variant bg-surface py-1"
          style={{ boxShadow: 'var(--md-sys-elevation-level3)' }}>
          <DropdownMenu.Item onSelect={() => setPickerOpen(true)}
            className="flex items-center gap-2 px-3 py-1.5 text-sm cursor-pointer select-none outline-none text-on-surface-variant data-[highlighted]:bg-surface-container-low">
            <Icon icon={ChangeCircle} size={18} className="text-on-surface-variant" /> Заменить основу…
          </DropdownMenu.Item>
          <DropdownMenu.Separator className="my-1 h-px bg-outline-variant" />
          <DropdownMenu.Item onSelect={onClear}
            className="flex items-center gap-2 px-3 py-1.5 text-sm cursor-pointer select-none outline-none text-error data-[highlighted]:bg-surface-container-low">
            <Icon icon={LinkOff} size={18} /> Отвязать
          </DropdownMenu.Item>
        </DropdownMenu.Content>
      </DropdownMenu.Portal>
    </DropdownMenu.Root>
  );

  // Ссылка задана, но кандидат не найден — предупреждающий chip.
  const missingChip = !selected && missing && (
    <Tooltip content="Базовый экземпляр недоступен (удалён или вне области видимости) — нажмите, чтобы отвязать">
      <button type="button" onClick={editable ? onClear : undefined} disabled={!editable}
        className="flex items-center gap-1.5 h-7 px-2.5 rounded-lg text-xs font-medium bg-warning-container text-on-warning-container max-w-[16rem]">
        <Icon icon={Warning} size={18} className="shrink-0" />
        <span className="truncate">Основа недоступна</span>
      </button>
    </Tooltip>
  );

  // База не выбрана — dashed-chip «Выбрать основу».
  const emptyChip = !selected && !missing && editable && (
    <Tooltip content="Предзаполнить поля из базового экземпляра или роли">
      <button type="button" onClick={() => setPickerOpen(true)}
        className="flex items-center gap-1.5 h-7 px-2.5 rounded-lg text-xs font-medium border border-dashed border-primary-container text-primary hover:bg-primary-container transition-colors">
        <Icon icon={LinkIcon} size={18} className="shrink-0" />
        <span>Выбрать основу</span>
      </button>
    </Tooltip>
  );

  return (
    <>
      {selectedChip || missingChip || emptyChip}
      <BaseCandidatePicker open={pickerOpen} onOpenChange={setPickerOpen} candidates={candidates} onSelect={onSelect} />
    </>
  );
}
