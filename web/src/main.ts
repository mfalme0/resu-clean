import './styles/base.css';
import App from './App.svelte';

const target = document.getElementById('app');
if (!target) throw new Error('Missing #app mount point in index.html.');

// Svelte 4 constructor-style mounting: no framework wrapper, nothing extra in the bundle.
export default new App({ target });