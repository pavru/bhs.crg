import { createRoot } from 'react-dom/client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router';
import { LocaleContext } from '@/shared/hooks/useLocale';
import { setFormatLocale } from '@/shared/format/format';
import '@fontsource/roboto/400.css';
import '@fontsource/roboto/500.css';
import '@fontsource/roboto/700.css';
import '@fontsource/roboto-mono/400.css';
import './catalog.css';
import { Catalog } from './Catalog';

setFormatLocale('ru-RU');
const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
createRoot(document.getElementById('root')!).render(
  <QueryClientProvider client={client}>
    <MemoryRouter>
      <LocaleContext.Provider value={['ru-RU', () => {}, 'idle']}>
        <Catalog />
      </LocaleContext.Provider>
    </MemoryRouter>
  </QueryClientProvider>,
);
