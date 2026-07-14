import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
// Roboto — семейство MD3 (issue #110, фаза 0). Self-hosted через @fontsource (офлайн, без Google Fonts/CSP).
import '@fontsource/roboto/400.css';
import '@fontsource/roboto/500.css';
import '@fontsource/roboto/700.css';
import '@fontsource/roboto-mono/400.css';
import '@fontsource/roboto-mono/500.css';
import './index.css';
import App from './App.tsx';

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
