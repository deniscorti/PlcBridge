<script setup lang="ts">
import { onMounted } from 'vue'
import { useConnectionStore } from '@/stores/connectionStore'
import AppSidebar from '@/components/layout/AppSidebar.vue'
import StatusBar from '@/components/layout/StatusBar.vue'

const conn = useConnectionStore()

onMounted(() => {
  const url = import.meta.env.VITE_BRIDGE_WS_URL ?? 'ws://localhost:5080/ws'
  const key = import.meta.env.VITE_BRIDGE_API_KEY ?? ''
  conn.init(url, key)
})
</script>

<template>
  <div class="app-layout">
    <AppSidebar />
    <main class="app-main">
      <router-view />
    </main>
    <StatusBar />
  </div>
</template>

<style>
:root {
  --sidebar-width: 220px;
  --statusbar-height: 32px;
  --bg-primary: #0f1419;
  --bg-secondary: #1a1f2e;
  --bg-card: #1e2538;
  --border-color: #2a3142;
  --text-primary: #e4e8f1;
  --text-secondary: #8892a4;
  --accent: #3b82f6;
  --accent-hover: #2563eb;
  --success: #22c55e;
  --warning: #eab308;
  --danger: #ef4444;
  --font-mono: 'JetBrains Mono', 'Fira Code', 'Consolas', monospace;
}

* {
  margin: 0;
  padding: 0;
  box-sizing: border-box;
}

body {
  font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
  background: var(--bg-primary);
  color: var(--text-primary);
  overflow: hidden;
}

.app-layout {
  display: grid;
  grid-template-columns: var(--sidebar-width) 1fr;
  grid-template-rows: 1fr var(--statusbar-height);
  height: 100vh;
}

.app-main {
  overflow-y: auto;
  padding: 16px 20px;
}
</style>
