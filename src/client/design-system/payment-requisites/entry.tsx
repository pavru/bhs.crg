import { createRoot } from 'react-dom/client';
import { MemoryRouter } from 'react-router';
import { PaymentRequisitesMockup } from './PaymentRequisitesMockup';
import '@fontsource/roboto/400.css';
import '@fontsource/roboto/500.css';
import '../catalog.css';

const theme = new URLSearchParams(location.search).get('theme');
document.documentElement.dataset.theme = theme === 'dark' ? 'dark' : 'light';

createRoot(document.getElementById('root')!).render(
  <MemoryRouter><PaymentRequisitesMockup /></MemoryRouter>,
);
