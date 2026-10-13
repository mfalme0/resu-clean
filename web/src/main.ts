import './styles/base.css';
import App from './App.svelte';

const target = document.getElementById('app');
if (!target) throw new Error('Missing #app mount point in index.html.');

/**
 * The chosen theme is applied before the app mounts, so the first paint is already in the right
 * colours. Doing it in onMount would show a flash of Newsprint on every load in the other two,
 * which is the sort of thing people notice and blame on the app.
 *
 * Kept here rather than in a component because it has to run before App exists. localStorage can
 * throw in private browsing, so it is read defensively and the default simply stands.
 */
export const THEMES = ['newsprint', 'grove', 'confetti'] as const;
export type Theme = (typeof THEMES)[number];
const THEME_KEY = 'resu-clean.theme';

function readTheme(): Theme {
  try {
    const saved = localStorage.getItem(THEME_KEY);
    return THEMES.includes(saved as Theme) ? (saved as Theme) : 'newsprint';
  } catch {
    return 'newsprint';
  }
}

function applyTheme(theme: Theme) {
  document.documentElement.setAttribute('data-theme', theme);
}

applyTheme(readTheme());

export function setTheme(theme: Theme) {
  applyTheme(theme);
  try {
    localStorage.setItem(THEME_KEY, theme);
  } catch {
    /* private browsing: the choice just will not be remembered */
  }
}

// Svelte 4 constructor-style mounting: no framework wrapper, nothing extra in the bundle.
export default new App({ target });