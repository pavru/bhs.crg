import { useState } from 'react';
import * as DropdownMenu from '@radix-ui/react-dropdown-menu';
import { Link2, FileText, Database, ChevronDown, Repeat, Unlink, AlertTriangle } from 'lucide-react';
import { BaseCandidatePicker, type BaseCandidate } from './BaseCandidatePicker';

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

  const Icon = selected?.proxy ? Link2 : selected?.kind === 'catalog' ? Database : FileText;

  // База выбрана — тональный chip с меню (Заменить / Отвязать); read-only при !editable.
  const selectedChip = selected && (
    <DropdownMenu.Root>
      <DropdownMenu.Trigger asChild disabled={!editable}>
        <button type="button"
          className={`flex items-center gap-1.5 h-7 pl-2.5 pr-2 rounded-lg text-xs font-medium bg-brand-subtle text-brand-hover max-w-[16rem] transition-colors ${
            editable ? 'hover:bg-brand-subtle/70 cursor-pointer' : 'cursor-default'}`}
          title={selected.proxy ? `Роль → ${selected.name}` : `Основа: ${selected.name}`}>
          <Icon size={13} className="shrink-0" />
          <span className="truncate">{selected.proxy ? 'Роль' : 'Основа'}: {selected.name}</span>
          {editable && <ChevronDown size={13} className="shrink-0 opacity-70" />}
        </button>
      </DropdownMenu.Trigger>
      <DropdownMenu.Portal>
        <DropdownMenu.Content align="end" sideOffset={4}
          className="z-50 min-w-[12rem] rounded-md border border-stroke bg-surface py-1"
          style={{ boxShadow: 'var(--f-shadow28)' }}>
          <DropdownMenu.Item onSelect={() => setPickerOpen(true)}
            className="flex items-center gap-2 px-3 py-1.5 text-sm cursor-pointer select-none outline-none text-fg2 data-[highlighted]:bg-base">
            <Repeat size={14} className="text-fg4" /> Заменить основу…
          </DropdownMenu.Item>
          <DropdownMenu.Separator className="my-1 h-px bg-stroke" />
          <DropdownMenu.Item onSelect={onClear}
            className="flex items-center gap-2 px-3 py-1.5 text-sm cursor-pointer select-none outline-none text-danger data-[highlighted]:bg-base">
            <Unlink size={14} /> Отвязать
          </DropdownMenu.Item>
        </DropdownMenu.Content>
      </DropdownMenu.Portal>
    </DropdownMenu.Root>
  );

  // Ссылка задана, но кандидат не найден — предупреждающий chip.
  const missingChip = !selected && missing && (
    <button type="button" onClick={editable ? onClear : undefined} disabled={!editable}
      className="flex items-center gap-1.5 h-7 px-2.5 rounded-lg text-xs font-medium bg-warning-subtle text-warning max-w-[16rem]"
      title="Базовый экземпляр недоступен (удалён или вне области видимости) — нажмите, чтобы отвязать">
      <AlertTriangle size={13} className="shrink-0" />
      <span className="truncate">Основа недоступна</span>
    </button>
  );

  // База не выбрана — dashed-chip «Выбрать основу».
  const emptyChip = !selected && !missing && editable && (
    <button type="button" onClick={() => setPickerOpen(true)}
      className="flex items-center gap-1.5 h-7 px-2.5 rounded-lg text-xs font-medium border border-dashed border-brand-subtle text-brand hover:bg-brand-subtle transition-colors"
      title="Предзаполнить поля из базового экземпляра или роли">
      <Link2 size={13} className="shrink-0" />
      <span>Выбрать основу</span>
    </button>
  );

  return (
    <>
      {selectedChip || missingChip || emptyChip}
      <BaseCandidatePicker open={pickerOpen} onOpenChange={setPickerOpen} candidates={candidates} onSelect={onSelect} />
    </>
  );
}
