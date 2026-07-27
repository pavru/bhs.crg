import type * as Monaco from 'monaco-editor';
import { useEffect } from 'react';
import { useListTemplateAssets, type TemplateAssetDto } from '@/shared/api/templateAssets';
import { imageAssetPath } from '@/shared/api/templateAssetRef';

/**
 * Автокомплит ассетов в Typst-редакторах (issue #476): внутри `image("` — пути картинок, внутри
 * `#set text(font: "` — имена семейств.
 *
 * Решает то, ради чего предлагали поднять ассеты папками в список файлов: не «видеть рядом», а НЕ
 * ПОМНИТЬ при письме. Соседство эту задачу решает плохо — список слева, курсор справа, имя всё
 * равно набирается руками.
 *
 * Побочно снимает асимметрию картинка/шрифт: подсказка сама вставляет правильный вид идентификатора
 * для своего контекста, и правило «на картинку ссылаются путём, на шрифт — семейством» знать не
 * нужно. Зеркалит устройство userLibCompletion: модульный список обновляется хуком, провайдер
 * регистрируется один раз и читает список лениво.
 */
interface AssetDef { insert: string; detail: string; kind: 'Image' | 'Font'; }

let _images: AssetDef[] = [];
let _fonts: AssetDef[] = [];
let _registered = false;

export function setAssetDefs(assets: TemplateAssetDto[]): void {
  _images = assets
    .filter(a => a.kind === 'Image')
    .map(a => ({ insert: imageAssetPath(a), detail: a.fileName, kind: 'Image' as const }));
  _fonts = assets
    .filter(a => a.kind === 'Font' && a.fontFamilyName)
    // Шрифт без распознанного семейства подсказывать нечем: подставить имя ассета значило бы
    // предложить строку, которая молча не сработает.
    .map(a => ({ insert: a.fontFamilyName!, detail: a.fileName, kind: 'Font' as const }));
}

/** Контекст строки до курсора: внутри какой строки-литерала мы находимся. */
export function assetContextAt(lineUpToCursor: string): 'image' | 'font' | null {
  // Незакрытая кавычка после `image(` / `font:` — берём ПОСЛЕДНЕЕ вхождение, чтобы вложенные вызовы
  // в одной строке не сбивали определение.
  const quote = lineUpToCursor.lastIndexOf('"');
  if (quote < 0) return null;
  const before = lineUpToCursor.slice(0, quote);
  const after = lineUpToCursor.slice(quote + 1);
  if (after.includes('"')) return null;             // строка уже закрыта — мы вне литерала
  if (/font\s*:\s*$/.test(before)) return 'font';
  if (/image\s*\(\s*$/.test(before)) return 'image';
  return null;
}

export function registerAssetCompletion(monaco: typeof Monaco): void {
  if (_registered) return;
  _registered = true;
  monaco.languages.registerCompletionItemProvider('typst', {
    triggerCharacters: ['"', '/'],
    provideCompletionItems(model, position) {
      const line = model.getValueInRange({
        startLineNumber: position.lineNumber, startColumn: 1,
        endLineNumber: position.lineNumber, endColumn: position.column,
      });
      const context = assetContextAt(line);
      if (context === null) return { suggestions: [] };

      const defs = context === 'image' ? _images : _fonts;
      if (defs.length === 0) return { suggestions: [] };

      // Заменяем то, что уже набрано внутри кавычек, — иначе подсказка допишется к огрызку.
      const typed = line.slice(line.lastIndexOf('"') + 1);
      const range = {
        startLineNumber: position.lineNumber, endLineNumber: position.lineNumber,
        startColumn: position.column - typed.length, endColumn: position.column,
      };

      return {
        suggestions: defs.map(d => ({
          label: d.insert,
          kind: context === 'image'
            ? monaco.languages.CompletionItemKind.File
            : monaco.languages.CompletionItemKind.Color,
          insertText: d.insert,
          range,
          detail: d.detail,
          documentation: context === 'image'
            ? 'Картинка-ассет — путь во временной папке генерации'
            : 'Имя семейства шрифта-ассета (файл подключается через --font-path)',
        })),
      };
    },
  });
}

/**
 * Хук: подтягивает системные ассеты и обновляет список подсказок. Уровень System, потому что
 * редакторы, где это включено, к конкретному шаблону могут и не относиться (файлы библиотеки —
 * общие). Ассеты уровней типа и шаблона перекрывают системные ПО ИМЕНИ уже при генерации.
 */
export function useAssetCompletion(): void {
  const { data } = useListTemplateAssets('System', null);
  useEffect(() => { setAssetDefs(data ?? []); }, [data]);
}
