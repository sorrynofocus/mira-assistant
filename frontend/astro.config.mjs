import { defineConfig } from 'astro/config';

// In development the Astro server proxies API calls so the browser sees one origin
// (http://127.0.0.1:4321), which keeps SameSite=Strict cookies and CSRF checks working.
const api = 'http://127.0.0.1:5080';

export default defineConfig({
  output: 'static',
  vite: {
    server: {
      proxy: {
        '/health': api,
        '/api': api,
      },
    },
  },
});
