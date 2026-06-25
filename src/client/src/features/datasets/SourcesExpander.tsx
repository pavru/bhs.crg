import { useState } from 'react';
import { ChevronDown, ChevronRight } from 'lucide-react';
import { parseSourceColumnNames } from '@/shared/api/datasetHelpers';
import type { DataSetSource } from '@/shared/api/types';

/** Collapsible list of a file's data sources with a preview of their column names. */
export function SourcesExpander({
  sources,
  maxColumns = 8,
}: {
  sources: DataSetSource[];
  maxColumns?: number;
}) {
  const [open, setOpen] = useState(false);

  if (sources.length === 0)
    return <span className="text-xs text-fg4">Нет источников</span>;

  return (
    <div>
      <button
        onClick={() => setOpen(o => !o)}
        className="flex items-center gap-1 text-xs text-brand"
      >
        {open ? <ChevronDown size={12} /> : <ChevronRight size={12} />}
        {sources.length} {sources.length === 1 ? 'источник' : 'источника(-ов)'}
      </button>
      {open && (
        <div className="mt-2 space-y-2 pl-3">
          {sources.map(src => {
            const cols = parseSourceColumnNames(src.cachedSchema);
            return (
              <div key={src.id} className="text-xs rounded-md p-2 bg-muted">
                <div className="font-medium mb-0.5 text-fg1">
                  {src.name}
                  <span className="ml-2 font-normal text-fg4">{src.cachedRowCount} строк</span>
                </div>
                {cols.length > 0 && (
                  <div className="text-fg3">
                    {cols.slice(0, maxColumns).join(', ')}{cols.length > maxColumns ? ` +${cols.length - maxColumns}` : ''}
                  </div>
                )}
              </div>
            );
          })}
        </div>
      )}
    </div>
  );
}
