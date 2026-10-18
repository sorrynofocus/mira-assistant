import { defineConfig } from 'astro/config';

export default defineConfig({
  output: 'static',
  vite: {
    server: {
      proxy: {
        '/health': 'http://127.0.0.1:5080',
      },
    },
  },
});
