import * as DropdownMenu from '@radix-ui/react-dropdown-menu';
import { Check, ChevronDown } from 'lucide-react';
import { useRoles } from '@/shared/api/roles';

/**
 * Выбор НЕСКОЛЬКИХ ролей (ТЗ AUTH-3, issue #984): действующие права — объединение прав всех
 * выбранных ролей.
 *
 * Меню с галками, а не выпадающий список: список с единственным выбором обещал бы, что роль одна,
 * и снимал бы прежнюю молча. Здесь видно и что выбрано, и что доступно, и каждый щелчок
 * прибавляет либо убавляет ровно одну роль.
 *
 * ⚠️ Список ролей ЖИВОЙ — он же источник и для редактора ролей. Перечень имён на клиенте означал
 * бы, что роль, заведённую администратором, назначить нечем: она есть, права у неё есть, а в меню
 * её нет.
 *
 * ⚠️ Пусто — это «без ролей», а не «не выбрано». Состояние законное (доступ отозван, учётная
 * запись цела), и подпись говорит об этом словами: молчащая кнопка читалась бы как «не загрузилось».
 */
export function RolePicker({ value, onChange, disabled, ariaLabel = 'Роли', className = '' }: {
  value: string[];
  onChange: (roles: string[]) => void;
  disabled?: boolean;
  ariaLabel?: string;
  className?: string;
}) {
  const { data: roles = [] } = useRoles();
  const chosen = roles.filter(r => value.includes(r.name));
  const label = chosen.length > 0 ? chosen.map(r => r.title).join(', ') : 'Без ролей';

  function toggle(name: string) {
    onChange(value.includes(name) ? value.filter(n => n !== name) : [...value, name]);
  }

  return (
    <DropdownMenu.Root>
      <DropdownMenu.Trigger asChild disabled={disabled}>
        <button type="button" aria-label={ariaLabel}
          className={`inline-flex items-center justify-between gap-2 w-full h-9 px-3 rounded-md border
            border-stroke-strong bg-surface text-sm text-left transition-colors
            focus:outline-none focus-visible:ring-2 focus-visible:ring-brand
            data-[state=open]:ring-2 data-[state=open]:ring-brand
            disabled:opacity-50 disabled:pointer-events-none
            ${chosen.length > 0 ? 'text-fg1' : 'text-fg4'} ${className}`}>
          <span className="min-w-0 flex-1 truncate">{label}</span>
          <ChevronDown size={15} className="text-fg4 shrink-0" aria-hidden="true" />
        </button>
      </DropdownMenu.Trigger>
      <DropdownMenu.Portal>
        <DropdownMenu.Content align="start" sideOffset={4}
          className="z-50 min-w-[16rem] max-h-80 overflow-auto rounded-md border border-stroke bg-surface py-1"
          style={{ boxShadow: 'var(--f-shadow28)' }}>
          {roles.map(r => (
            // onSelect гасим: меню закрывается по выбору пункта, а здесь выбирают НЕСКОЛЬКО —
            // закрытие после первой галки заставляло бы открывать его заново на каждую роль.
            <DropdownMenu.CheckboxItem key={r.name} checked={value.includes(r.name)}
              onSelect={e => e.preventDefault()} onCheckedChange={() => toggle(r.name)}
              className="flex items-start gap-2 px-3 py-1.5 text-sm cursor-pointer select-none outline-none
                data-[highlighted]:bg-base">
              <span className="w-4 shrink-0 pt-0.5 text-brand">
                {value.includes(r.name) && <Check size={14} />}
              </span>
              <span className="min-w-0">
                <span className="block text-fg1">{r.title}</span>
                {r.summary && <span className="block text-xs text-fg4">{r.summary}</span>}
              </span>
            </DropdownMenu.CheckboxItem>
          ))}
        </DropdownMenu.Content>
      </DropdownMenu.Portal>
    </DropdownMenu.Root>
  );
}
