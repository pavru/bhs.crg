import { Loader2 } from 'lucide-react';
import { useExpressionPreview, type ExpressionPreviewSpec } from '@/shared/api/datasets';
import { useDebouncedValue } from '@/shared/hooks/useDebouncedValue';

interface JsonPathBuilderProps {
  value: string;
  onChange: (value: string) => void;
  placeholder?: string;
  /**
   * Даёт предпросмотр данных, которые матчит текущее выражение — вычисляется от debounced
   * value. Возвращает null, если предпросмотр сейчас невозможен (например, для колонки не
   * задан ещё row-selector). Для row-selector'а — вернуть { fileId, rowSelector: value } (без
   * expr — предпросмотр самого пути); для колонки — { fileId, rowSelector: <контекст>, expr: value }.
   */
  preview?: (value: string) => ExpressionPreviewSpec | null;
}

/**
 * Редактор JSONPath-выражения (текстовое поле + живой предпросмотр). В отличие от XPathBuilder
 * — без визуального конструктора шагов: точечная нотация JSONPath ($.a.b[0]) интуитивнее XPath
 * для человека без подготовки, поэтому для первой версии визуальный слой избыточен.
 */
export function JsonPathBuilder({ value, onChange, placeholder, preview }: JsonPathBuilderProps) {
  const debouncedValue = useDebouncedValue(value, 400);
  const previewSpec = preview ? preview(debouncedValue) : null;

  return (
    <div className="space-y-1.5">
      <input
        value={value}
        onChange={e => onChange(e.target.value)}
        placeholder={placeholder ?? '$.items[*]'}
        spellCheck={false}
        className="w-full border border-stroke-strong rounded-md px-2 py-1.5 text-xs font-mono bg-surface focus:outline-none focus-visible:ring-2 focus-visible:ring-brand"
      />
      {preview && <JsonPathPreviewPanel spec={previewSpec} />}
    </div>
  );
}

function JsonPathPreviewPanel({ spec }: { spec: ExpressionPreviewSpec | null }) {
  const { data, isFetching, error } = useExpressionPreview(spec);

  if (!spec) return null;

  return (
    <div className="text-xs rounded-md border border-stroke bg-base px-2 py-1.5">
      {isFetching ? (
        <span className="flex items-center gap-1.5 text-fg4">
          <Loader2 size={11} className="animate-spin" /> Проверка...
        </span>
      ) : error ? (
        <span className="text-danger">
          {(error as { response?: { data?: { error?: string } } })?.response?.data?.error ?? 'Не удалось проверить выражение'}
        </span>
      ) : data ? (
        <div className="space-y-0.5">
          <span className="font-medium text-fg2">
            {spec.expr ? `Значений найдено: ${data.matchCount}` : `Узлов найдено: ${data.matchCount}`}
          </span>
          {data.samples.length > 0 && (
            <ul className="text-fg4">
              {data.samples.map((s, i) => <li key={i} className="truncate">{s}</li>)}
            </ul>
          )}
        </div>
      ) : null}
    </div>
  );
}
