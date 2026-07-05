import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      "/api": { target: "http://localhost:5201", changeOrigin: true },
      "/ws": { target: "ws://localhost:5201", ws: true },
    },
  },
  build: {
    outDir: "../server/Wimux.Web/wwwroot",
    emptyOutDir: true,
  },
});
