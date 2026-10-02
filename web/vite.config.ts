import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

// The F# server listens on 5080; proxy the API so the browser sees one origin.
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: { '/api': 'http://localhost:5080' },
  },
})
