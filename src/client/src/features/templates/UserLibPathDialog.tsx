import { useState } from 'react';
import { Modal } from '@/shared/ui/Modal';
import { Button } from '@/shared/ui/Button';
import { validatePath } from './userLibTree';

/**
 * Путь файла библиотеки (issue #473). Создание и переименование — одна форма: и то и другое суть
 * присвоение новой строки, а отдельной команды «создать папку» нет — папка возникает из «/» в пути,
 * пустая папка в Typst бессмысленна.
 */
export function UserLibPathDialog({
  mode, initialPath, existing, referencing, onSubmit, onCancel,
}: {
  mode: 'create' | 'rename';
  initialPath: string;
  existing: string[];
  /** Файлы, ссылающиеся на переименовываемый — их импорты сломаются. */
  referencing: string[];
  onSubmit: (path: string) => void;
  onCancel: () => void;
}) {
  const [path, setPath] = useState(initialPath);
  const [touched, setTouched] = useState(false);
  const error = validatePath(path, existing, mode === 'rename' ? initialPath : undefined);

  return (
    <Modal open onOpenChange={o => { if (!o) onCancel(); }} title={mode === 'create' ? 'Новый файл библиотеки' : 'Изменить путь'}>
      <div className="space-y-3">
        <div>
          <label htmlFor="userlib-path" className="block text-xs text-fg3 mb-1">
            Путь внутри <code className="font-mono">userlib/</code>
          </label>
          <input
            id="userlib-path" autoFocus value={path}
            onChange={e => { setPath(e.target.value); setTouched(true); }}
            onKeyDown={e => { if (e.key === 'Enter' && !error) onSubmit(path.trim().replace(/\\/g, '/')); }}
            placeholder="gost/forms/f3.typ"
            className="w-full h-8 px-2.5 text-sm font-mono rounded-md border border-stroke bg-surface text-fg1
                       outline-none focus:ring-2 focus:ring-brand/40"
          />
          <p className="mt-1 text-xs text-fg4">
            Папки создаются сами из «/» в пути. Файл подключится к точке входа автоматически.
          </p>
          {touched && error && <p className="mt-1 text-xs text-danger">{error}</p>}
        </div>

        {/* Автоматически переписывать чужие импорты не беремся: это текстовая трансформация
            пользовательского кода, ошибиться в ней тоньше, чем не делать. Говорим поимённо. */}
        {mode === 'rename' && referencing.length > 0 && (
          <p className="text-xs text-warning">
            На этот файл ссылаются: {referencing.join(', ')}. Импорты в них придётся поправить вручную.
          </p>
        )}

        <div className="flex justify-end gap-2 pt-1">
          <Button variant="text" size="sm" onClick={onCancel}>Отмена</Button>
          <Button variant="filled" size="sm" disabled={!!error}
            onClick={() => onSubmit(path.trim().replace(/\\/g, '/'))}>
            {mode === 'create' ? 'Создать' : 'Изменить'}
          </Button>
        </div>
      </div>
    </Modal>
  );
}
