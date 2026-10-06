import { defineConfig } from 'vite';
import { svelte } from '@sveltejs/vite-plugin-svelte';

// The SPA is built straight into the backend's wwwroot so `npm start` serves
// the UI and the API from one port with no copy step.
export default defineConfig({
  plugins: [svelte()],
  build: {
    outDir: '../server/wwwroot',
    emptyOutDir: true,
    target: 'es2020',
    // Keeps the bundle inside the 150 KB gzipped budget: no vendor chunk bloat,
    // no large preloaded assets, and source maps off by default.
    cssCodeSplit: true,
    sourcemap: false,
    chunkSizeWarningLimit: 200,
    rollupOptions: {
      output: {
        manualChunks: undefined
      }
    }
  },
  server: {
    port: 5173,
    strictPort: false,
    // Everything under /api goes to the backend; the UI runs on its own port in dev.
    proxy: {
      '/api': {
        target: 'http://127.0.0.1:5177',
        changeOrigin: false
      }
    }
  },
  preview: {
    port: 4173
  },
  test: {
    // The smoke tests exercise router link handling and local storage, so they need a DOM.
    environment: 'happy-dom',
    include: ['src/**/*.test.ts']
  }
});