import { createRoot } from 'react-dom/client';
import { StoryProviders } from '../story-providers';
import { Review } from './Review';
import '@fontsource/roboto/400.css';
import '@fontsource/roboto/500.css';
import '../catalog.css';
import './work21.css';

const theme = new URLSearchParams(location.search).get('theme') === 'dark' ? 'dark' : 'light';
document.documentElement.dataset.theme = theme;
createRoot(document.getElementById('root')!).render(
  <StoryProviders theme={theme} component="Work21"><Review /></StoryProviders>,
);
