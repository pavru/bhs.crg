import { useState } from 'react';
import { Icon } from '@/shared/ui/Icon';
import { Layers } from '@/shared/ui/icons';
import { DataSetsResource } from './DataSetsResource';
import { ProcessingTemplatesDialog } from './ProcessingTemplatesDialog';
import { Button } from '@/shared/ui/Button';
import { PageHeader } from '@/shared/ui/PageHeader';

/**
 * Системные наборы данных (scope='System'). Тонкая обёртка над общим `DataSetsResource`
 * (issue #210, ось видимости — единый браузер наборов на всех уровнях scope) + «Шаблоны обработки».
 */
export function DataSetsPage() {
  const [templatesOpen, setTemplatesOpen] = useState(false);

  return (
    <div className="px-6 py-4">
      <PageHeader title="Наборы данных" className="mb-4"
        subtitle="Системные наборы доступны во всех комплектах. Наборы уровня стройки, раздела и комплекта управляются на соответствующих страницах."
        actions={
          <Button variant="tonal" size="sm" icon={<Icon icon={Layers} size={18} />} onClick={() => setTemplatesOpen(true)}>
            Шаблоны обработки
          </Button>
        } />

      <DataSetsResource scope="System" />

      <p className="mt-3 text-xs text-fg4">Поддерживаемые форматы: CSV, TXT, XLSX, XLS, XML, JSON, ZIP, PDF.</p>

      {templatesOpen && <ProcessingTemplatesDialog onClose={() => setTemplatesOpen(false)} />}
    </div>
  );
}
