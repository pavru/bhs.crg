import { createRoot } from 'react-dom/client';
import { StoryProviders } from '../story-providers';
import { setFormatLocale } from '@/shared/format/format';
import { Review } from './Review';
import '@fontsource/roboto/400.css';
import '@fontsource/roboto/500.css';
import '../catalog.css';

const params = new URLSearchParams(location.search);
const theme = params.get('theme') === 'dark' ? 'dark' : 'light';
document.documentElement.dataset.theme = theme;
setFormatLocale('ru-RU');
createRoot(document.getElementById('root')!).render(
  <StoryProviders theme={theme} component="Plan27"><Review params={params} /></StoryProviders>,
);
