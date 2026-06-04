<script setup lang="ts">
import { useConnectionStore } from '@/stores/connectionStore'

const conn = useConnectionStore()
</script>

<template>
  <div class="settings-page">
    <header class="page-header">
      <h2>Settings</h2>
    </header>

    <section class="panel">
      <h3 class="panel-title">Connection</h3>
      <div class="info-grid">
        <div class="info-item">
          <span class="info-label">Status</span>
          <span class="info-value">{{ conn.state }}</span>
        </div>
        <div class="info-item">
          <span class="info-label">RTT</span>
          <span class="info-value mono">{{ conn.rttMs }}ms</span>
        </div>
        <div class="info-item">
          <span class="info-label">Mode</span>
          <span class="info-value">{{ conn.bridgeMode || '-' }}</span>
        </div>
        <div class="info-item">
          <span class="info-label">Version</span>
          <span class="info-value">{{ conn.bridgeVersion || '-' }}</span>
        </div>
        <div class="info-item">
          <span class="info-label">Sources</span>
          <span class="info-value">{{ conn.sources.length }}</span>
        </div>
      </div>
    </section>

    <section class="panel">
      <h3 class="panel-title">About</h3>
      <p class="about-text">
        Bridge Web Client — Industrial data bridge monitoring dashboard.
        Connects to Bridge backend via WebSocket for real-time telemetry visualization.
      </p>
    </section>
  </div>
</template>

<style scoped>
.page-header {
  margin-bottom: 20px;
}

.page-header h2 {
  font-size: 20px;
  font-weight: 600;
}

.panel {
  background: var(--bg-card);
  border: 1px solid var(--border-color);
  border-radius: 8px;
  padding: 16px;
  margin-bottom: 16px;
}

.panel-title {
  font-size: 13px;
  font-weight: 600;
  margin-bottom: 12px;
}

.info-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(140px, 1fr));
  gap: 12px;
}

.info-item {
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.info-label {
  font-size: 10px;
  color: var(--text-secondary);
  text-transform: uppercase;
  letter-spacing: 0.5px;
}

.info-value {
  font-size: 14px;
  font-weight: 500;
}

.mono {
  font-family: var(--font-mono);
  font-size: 12px;
}

.about-text {
  font-size: 13px;
  color: var(--text-secondary);
  line-height: 1.6;
}
</style>
