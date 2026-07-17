import { useState } from 'react';
import { Layers } from 'lucide-react';
import { DataSetsResource } from './DataSetsResource';
import { ProcessingTemplatesDialog } from './ProcessingTemplatesDialog';

/**
 * Системные наборы данных (scope='System'). Тонкая обёртка над общим `DataSetsResource`
 * (issue #210, ось видимости — единый браузер наборов на всех уровнях scope) + «Шаблоны обработки».
 */
export function DataSetsPage() {
  const [templatesOpen, setTemplatesOpen] = useState(false);

  return (
    <div className="px-6 py-4 max-w-3xl">
      <div className="flex items-center justify-between mb-1">
        <h1 className="text-xl font-semibold text-fg1">Наборы данных</h1>
        <button onClick={() => setTemplatesOpen(true)}
          className="flex items-center gap-2 text-sm font-medium px-3 py-1.5 rounded-md transition-colors text-fg2 bg-muted hover:bg-brand-subtle hover:text-brand">
          <Layers size={14} /> Шаблоны обработки
        </button>
      </div>
      <p className="text-xs mb-4 text-fg4">
        Системные наборы доступны во всех комплектах. Наборы уровня стройки, раздела и комплекта управляются на соответствующих страницах.
      </p>

      <DataSetsResource scope="System" />

      <p className="mt-3 text-xs text-fg4">Поддерживаемые форматы: CSV, TXT, XLSX, XLS, XML, JSON, ZIP, PDF.</p>

      {templatesOpen && <ProcessingTemplatesDialog onClose={() => setTemplatesOpen(false)} />}
    </div>
  );
}
