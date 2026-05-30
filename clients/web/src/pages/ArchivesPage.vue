<script setup lang="ts">
import { ref, watch } from 'vue'
import { useConnectionStore } from '@/stores/connectionStore'
import { useQuery } from '@/composables/useQuery'
import QualityBadge from '@/components/QualityBadge.vue'
import type { ChunkInfo, Quality } from '@/types/bridge'

const conn = useConnectionStore()
const query = useQuery()

const selectedSource = ref('')
const chunks = ref<ChunkInfo[]>([])

watch(selectedSource, async (src) => {
  if (!src) { chunks.value = []; return }
  chunks.value = await query.getChunks(src)
})

function stateToQuality(state: string): Quality {
  return state.toLowerCase() as Quality
}

function formatTs(ts: string): string {
  try { return new Date(ts).toLocaleString() }
  catch { return ts }
}
</script>

<template>
  <div class="archives-page">
    <header class="page-header">
      <h2>Archives</h2>
    </header>

    <section class="panel">
      <label class="form-label">
        Source
        <select v-model="selectedSource" class="form-input">
          <option value="">Select source...</option>
          <option v-for="s in conn.sources" :key="s.id" :value="s.id">{{ s.id }}</option>
        </select>
      </label>
    </section>

    <div v-if="query.loading.value" class="loading">Loading chunks...</div>

    <section v-if="chunks.length > 0" class="panel">
      <h3 class="panel-title">Chunks ({{ chunks.length }})</h3>
      <div class="chunk-table-wrap">
        <table class="chunk-table">
          <thead>
            <tr>
              <th>Chunk ID</th>
              <th>From</th>
              <th>To</th>
              <th>Records</th>
              <th>MsgId</th>
              <th>State</th>
              <th>Quality</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="c in chunks" :key="c.chunkId">
              <td class="mono">{{ c.chunkId.slice(0, 8) }}</td>
              <td class="mono">{{ formatTs(c.fromTs) }}</td>
              <td class="mono">{{ formatTs(c.toTs) }}</td>
              <td>{{ c.recordCount.toLocaleString() }}</td>
              <td class="mono">{{ c.firstMsgId }}-{{ c.lastMsgId }}</td>
              <td>
                <span class="state-badge" :class="c.state.toLowerCase()">{{ c.state }}</span>
              </td>
              <td><QualityBadge :quality="stateToQuality(c.quality)" /></td>
            </tr>
          </tbody>
        </table>
      </div>
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

.form-label {
  display: flex;
  flex-direction: column;
  gap: 4px;
  font-size: 11px;
  color: var(--text-secondary);
  text-transform: uppercase;
  letter-spacing: 0.5px;
  max-width: 300px;
}

.form-input {
  padding: 6px 10px;
  border: 1px solid var(--border-color);
  border-radius: 6px;
  background: var(--bg-primary);
  color: var(--text-primary);
  font-size: 13px;
}

.loading {
  text-align: center;
  padding: 20px;
  color: var(--text-secondary);
}

.chunk-table-wrap {
  overflow-x: auto;
}

.chunk-table {
  width: 100%;
  border-collapse: collapse;
  font-size: 12px;
}

.chunk-table th {
  text-align: left;
  padding: 8px;
  color: var(--text-secondary);
  font-weight: 500;
  font-size: 10px;
  text-transform: uppercase;
  letter-spacing: 0.5px;
  border-bottom: 1px solid var(--border-color);
}

.chunk-table td {
  padding: 6px 8px;
  border-bottom: 1px solid rgba(255, 255, 255, 0.03);
}

.mono {
  font-family: var(--font-mono);
  font-size: 11px;
}

.state-badge {
  font-size: 10px;
  padding: 1px 6px;
  border-radius: 4px;
  text-transform: uppercase;
}

.state-badge.open {
  background: rgba(34, 197, 94, 0.15);
  color: var(--success);
}

.state-badge.sealed {
  background: rgba(255, 255, 255, 0.06);
  color: var(--text-secondary);
}
</style>
