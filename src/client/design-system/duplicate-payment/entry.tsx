import { createRoot } from 'react-dom/client';
import { MemoryRouter } from 'react-router';
import { DuplicatePaymentMockup, FileExample } from './DuplicatePaymentMockup';
import { invoices } from './fixtures';
import '@fontsource/roboto/400.css';
import '@fontsource/roboto/500.css';
import '../catalog.css';
import './sample.css';

const params = new URLSearchParams(location.search);
const file = invoices.find(invoice => invoice.id === params.get('file'));
document.documentElement.dataset.theme = params.get('theme') === 'dark' ? 'dark' : 'light';

createRoot(document.getElementById('root')!).render(
  <MemoryRouter>{file ? <FileExample invoice={file} /> : <DuplicatePaymentMockup />}</MemoryRouter>,
);
