/// <reference types="vitest/config" />
import { fileURLToPath, URL } from 'node:url'
import tailwindcss from '@tailwindcss/vite'
import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

// In development the SPA calls relative /api URLs; Vite forwards them to the API,
// so no CORS configuration is needed. Override the target with VERSO_API_URL.
const apiTarget = process.env.VERSO_API_URL ?? 'http://localhost:5122'

// Tests read the shared synthetic fixtures (tests/fixtures) and the OpenAPI snapshot (docs/openapi) from the
// repository root. Only under Vitest: the dev server keeps Vite's default of serving the web project only.
const repoRoot = fileURLToPath(new URL('../..', import.meta.url))

export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: {
    alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) },
  },
  server: {
    // Not Vite's default 5173, which other local projects commonly occupy.
    port: 5180,
    strictPort: true,
    ...(process.env.VITEST ? { fs: { allow: [repoRoot] } } : {}),
    proxy: {
      '/api': { target: apiTarget, changeOrigin: false },
      // SignalR hub (WebSockets).
      '/hubs': { target: apiTarget, changeOrigin: false, ws: true },
    },
  },
  test: {
    environment: 'jsdom',
    setupFiles: ['./src/test/setup.ts'],
    css: false,
    restoreMocks: true,
  },
})
