import { useState } from 'react';
import Editor from '@/shared/ui/CodeEditor';
import { registerTypstLanguage } from '@/shared/ui/typstLanguage';
import { useTheme } from '@/shared/ui/ThemeProvider';
import { useTypeBlocks, type TypeBlockFile } from '@/shared/api/typstUserLib';
import { NavSection } from '@/shared/ui/ListDetailShell';
import { FileCode, LogIn, Lock } from 'lucide-react';

/** Точка входа — её импортирует шаблон; модули лежат в подпапке `typeblocks/`. */
const ENTRYPOINT = 'typeblocks.typ';

/** Модульная константа, а не `?? []` в теле: новый массив на каждый рендер запускает цикл memo→effect. */
const EMPTY: TypeBlockFile[] = [];

/**
 * Просмотр собранных блоков типов (issue #770) — только чтение.
 *
 * <p>Шаблон пишут против этих файлов, а увидеть их до сих пор можно было лишь обходом: скачать
 * debug-бандл (он привязан к конкретному документу и приходит ZIP) либо собрать в голове из карточек
 * «Typst-блоки» у каждого типа. Второе не работает даже в принципе: порядок внутри модуля задаёт
 * топосорт по зависимостям (#309), импорты между модулями эмитит сборка, а диспетч-часть (#768) не
 * показана ни в одной карточке.</p>
 *
 * <p>Список файлов, а не одна простыня: с #772 блоки разложены по файлу на тип, и склеивать их назад
 * значило бы отменять адресность ошибок ровно там, где по ней ищут — Typst сообщает
 * «typeblocks/Организация.typ:12», и найти эту строку человек должен в том же файле.</p>
 *
 * <p>Правка отсюда не предусмотрена намеренно: файлы производные, единственное место блоков —
 * схема типа. Панель отвечает на вопрос «что реально уходит в Typst», а не заменяет редактор.</p>
 */
export function TypeBlocksPanel() {
  const { resolvedTheme } = useTheme();
  const { data, isLoading, isError } = useTypeBlocks();
  const [selected, setSelected] = useState(ENTRYPOINT);

  if (isLoading) {
    return <div className="flex-1 flex items-center justify-center text-fg4 text-sm">Загрузка...</div>;
  }
  if (isError) {
    return (
      <div className="flex-1 flex items-center justify-center text-danger text-sm">
        Не удалось собрать блоки типов
      </div>
    );
  }

  const files = data?.files ?? EMPTY;
  const modules = files.filter(f => f.path !== ENTRYPOINT);
  // Выбранного файла может не быть после того, как у типа убрали последний блок, — тогда его модуль
  // исчезает. Молча падать на undefined тут нельзя, откат на точку входа безопасен.
  const current = files.find(f => f.path === selected) ?? files[0];

  // «Блоков нет» определяем СЧЁТЧИКОМ с сервера, а не пустотой содержимого: сборка всегда пишет
  // агрегатор с диспетч-частью (#768), поэтому файлы непустые даже при нуле блоков — проверка по
  // тексту никогда бы не сработала, и на свежей установке человек видел бы каркас без объяснения.
  const empty = (data?.blockCount ?? 0) === 0;

  return (
    <div className="flex flex-col h-full">
      <div className="flex items-center gap-2 px-4 py-2 border-b border-stroke bg-surface">
        <Lock size={13} className="text-fg4 shrink-0" />
        <p className="text-xs text-fg3">
          Собранные блоки типов — только чтение. Шаблон подключает импортом{' '}
          <code className="font-mono">#import "typeblocks.typ": *</code> (в новых шаблонах строка уже
          стоит) одну точку входа, а она реэкспортирует модули, поэтому имена блоков доступны как
          прежде. Порядок внутри модуля задаёт зависимость между блоками и может отличаться от
          порядка в схеме. Блоки правятся у типа: Типы документов → Typst-блоки.
        </p>
      </div>

      {empty ? (
        <div className="flex-1 flex items-center justify-center text-fg4 text-sm text-center px-6">
          Ни у одного типа нет Typst-блоков.
          <br />
          Блоки добавляются в схеме типа, раздел «Typst-блоки»; здесь появятся собранные файлы.
        </div>
      ) : (
        <div className="flex-1 min-h-0 flex">
          <div className="w-56 shrink-0 border-r border-stroke overflow-y-auto py-1">
            <NavSection label="Точка входа" />
            <FileRow
              label={ENTRYPOINT} icon={<LogIn size={13} />} title="Импортирует модули и диспетч по типу"
              active={current?.path === ENTRYPOINT} onClick={() => setSelected(ENTRYPOINT)}
            />
            <NavSection label={`Модули типов (${modules.length})`} />
            {modules.map(f => (
              <FileRow
                key={f.path} label={f.path.replace(/^typeblocks\//, '')} icon={<FileCode size={13} />}
                title={f.path} active={current?.path === f.path} onClick={() => setSelected(f.path)}
              />
            ))}
          </div>
          <div className="flex-1 min-w-0 overflow-hidden">
            <Editor
              // key по пути: без него Monaco сохраняет позицию курсора и скролл от прошлого файла,
              // и переключение выглядит как «открылся тот же файл, только текст другой».
              key={current?.path}
              height="100%"
              defaultLanguage="typst"
              theme={resolvedTheme === 'dark' ? 'vs-dark' : 'vs'}
              value={current?.content ?? ''}
              beforeMount={registerTypstLanguage}
              options={{
                readOnly: true,
                domReadOnly: true,
                minimap: { enabled: false },
                fontSize: 13,
                fontFamily: "'Cascadia Code', 'Fira Code', Consolas, monospace",
                wordWrap: 'on',
                lineNumbers: 'on',
                scrollBeyondLastLine: false,
                automaticLayout: true,
                tabSize: 2,
              }}
            />
          </div>
        </div>
      )}
    </div>
  );
}

function FileRow({ label, icon, title, active, onClick }: {
  label: string; icon: React.ReactNode; title?: string; active: boolean; onClick: () => void;
}) {
  return (
    <button
      type="button" onClick={onClick} title={title} aria-current={active ? 'true' : undefined}
      className={`w-full flex items-center gap-1.5 px-3 py-1.5 text-left text-sm
                  ${active ? 'bg-brand-subtle' : 'hover:bg-muted'}`}
    >
      <span className={`shrink-0 ${active ? 'text-brand' : 'text-fg3'}`}>{icon}</span>
      <span className={`truncate ${active ? 'text-fg1 font-medium' : 'text-fg2'}`}>{label}</span>
    </button>
  );
}
