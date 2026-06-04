<script setup lang="ts">
import { useConnectionStore } from '@/stores/connectionStore'

const conn = useConnectionStore()
</script>

<template>
  <footer class="statusbar">
    <div class="status-left">
      <span class="status-dot" :class="conn.state"></span>
      <span class="status-text">{{ conn.state }}</span>
      <span class="rtt" v-if="conn.isConnected">{{ conn.rttMs }}ms</span>
    </div>
    <div class="status-right">
      <span v-if="conn.bridgeVersion">v{{ conn.bridgeVersion }}</span>
      <span>{{ conn.sources.length }} sources</span>
    </div>
  </footer>
</template>

<style scoped>
.statusbar {
  grid-column: 2;
  background: var(--bg-secondary);
  border-top: 1px solid var(--border-color);
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 0 16px;
  font-size: 11px;
  color: var(--text-secondary);
}

.status-left,
.status-right {
  display: flex;
  align-items: center;
  gap: 8px;
}

.status-dot {
  width: 7px;
  height: 7px;
  border-radius: 50%;
  background: var(--text-secondary);
}

.status-dot.connected {
  background: var(--success);
}

.status-dot.connecting {
  background: var(--warning);
  animation: pulse 1s infinite;
}

.status-dot.disconnected {
  background: var(--danger);
}

.rtt {
  font-family: var(--font-mono);
  font-size: 10px;
}

@keyframes pulse {
  0%, 100% { opacity: 1; }
  50% { opacity: 0.4; }
}
</style>
