<script setup lang="ts">
import { useConnectionStore } from '@/stores/connectionStore'
import SourceCard from '@/components/SourceCard.vue'

const conn = useConnectionStore()
</script>

<template>
  <div class="dashboard">
    <header class="page-header">
      <h2>Dashboard</h2>
      <button @click="conn.refreshSources()" class="btn-refresh">Refresh</button>
    </header>

    <div v-if="!conn.isConnected" class="empty-state">
      <p>Connecting to Bridge...</p>
    </div>

    <div v-else-if="conn.sources.length === 0" class="empty-state">
      <p>No sources available</p>
    </div>

    <div v-else class="source-grid">
      <SourceCard v-for="s in conn.sources" :key="s.id" :source="s" />
    </div>
  </div>
</template>

<style scoped>
.page-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: 20px;
}

.page-header h2 {
  font-size: 20px;
  font-weight: 600;
}

.btn-refresh {
  font-size: 12px;
  padding: 4px 12px;
  border: 1px solid var(--border-color);
  background: transparent;
  color: var(--text-secondary);
  border-radius: 6px;
  cursor: pointer;
}

.btn-refresh:hover {
  border-color: var(--accent);
  color: var(--accent);
}

.source-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(320px, 1fr));
  gap: 16px;
}

.empty-state {
  text-align: center;
  padding: 60px 20px;
  color: var(--text-secondary);
}
</style>
