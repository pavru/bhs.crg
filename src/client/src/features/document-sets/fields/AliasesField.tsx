import { useState } from 'react';
import { Chip } from '@/shared/ui/Chip';
import { TextField } from '@/shared/ui/TextField';

/**
 * Псевдонимы записи общих данных (issue #74) — доп. имена для поиска записи при связывании с источниками.
 * Поле ввода и input chips под ним ([UI-68]): значение и крестик, нажать в чипе больше нечего. Enter,
 * запятая или уход из поля добавляют набранное; повтор без учёта регистра не добавляется. Одно на форму
 * записи каталога и окно выноса объекта в общие данные — правила ввода у них общие.
 */
export function AliasesField({ aliases, onChange }: { aliases: string[]; onChange: (next: string[]) => void }) {
  const [draft, setDraft] = useState('');
  function add() {
    const t = draft.trim();
    if (t && !aliases.some(a => a.toLowerCase() === t.toLowerCase())) onChange([...aliases, t]);
    setDraft('');
  }
  return (
    <div className="space-y-2">
      <TextField label="Псевдоним" value={draft} onChange={e => setDraft(e.target.value)}
        onKeyDown={e => { if (e.key === 'Enter' || e.key === ',') { e.preventDefault(); add(); } }}
        onBlur={add}
        hint="Доп. имя для поиска при связывании с источниками; Enter — добавить." />
      {aliases.length > 0 && (
        <div role="group" aria-label="Псевдонимы" className="flex flex-wrap gap-2">
          {aliases.map(a => (
            <Chip key={a} removeOnly removeLabel={`Удалить «${a}»`}
              onRemove={() => onChange(aliases.filter(x => x !== a))}>{a}</Chip>
          ))}
        </div>
      )}
    </div>
  );
}
