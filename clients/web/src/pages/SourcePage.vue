<script setup lang="ts">
import { ref, computed, watch, onUnmounted } from 'vue'
import { useConnectionStore } from '@/stores/connectionStore'
import { useSubscription } from '@/composables/useSubscription'
import { useTimeSeriesBuffer } from '@/composables/useTimeSeriesBuffer'
import UPlotChart from '@/components/charts/UPlotChart.vue'
import TagSelector from '@/components/TagSelector.vue'
import type uPlot from 'uplot'

const props = defineProps<{ id: string }>()

const conn = useConnectionStore()
const sub = useSubscription()
const buffer = useTimeSeriesBuffer()

const source = computed(() => conn.sources.find((s) => s.id === props.id))
const allTags = computed(() => source.value?.tags ?? [])
const selectedTags = ref<string[]>([])
const isLive = ref(false)

// Series config for uPlot
const seriesConfig = computed<uPlot.Series[]>(() => {
  const colors = [
    '#3b82f6', '#22c55e', '#eab308', '#ef4444', '#a78bfa',
    '#f97316', '#06b6d4', '#ec4899', '#84cc16', '#f43f5e',
  ]
  return selectedTags.value.map((tag, i) => ({
    label: tag,
    stroke: colors[i % colors.length],
    width: 1.5,
    points: { show: false },
  }))
})

// Start/stop live subscription
function startLive() {
  if (selectedTags.value.length === 0) return
  buffer.setTags(selectedTags.value)
  sub.subscribe(props.id, selectedTags.value, (values) => {
    buffer.push(values)
  })
  isLive.value = true
}

function stopLive() {
  sub.unsubscribe()
  isLive.value = false
}

// Auto-select first 4 tags when source loads
watch(allTags, (tags) => {
  if (tags.length > 0 && selectedTags.value.length === 0) {
    selectedTags.value = tags.slice(0, Math.min(4, tags.length))
  }
}, { immediate: true })

// Stop subscription on tag change
watch(selectedTags, () => {
  if (isLive.value) {
    stopLive()
  }
  buffer.clear()
})

onUnmounted(() => {
  stopLive()
})
</script>

<template>
  <div class="source-page">
    <header class="page-header">
      <div>
        <h2>{{ id }}</h2>
        <span class="subtitle" v-if="source">{{ source.tags.length }} tags</span>
      </div>
      <div class="header-actions">
        <button v-if="!isLive" @click="startLive" :disabled="selectedTags.length === 0" class="btn btn-primary">
          Start Live
        </button>
        <button v-else @click="stopLive" class="btn btn-danger">
          Stop
        </button>
      </div>
    </header>

    <div v-if="!source" class="empty-state">
      <p>Source "{{ id }}" not found</p>
    </div>

    <template v-else>
      <section class="panel">
        <h3 class="panel-title">Tags</h3>
        <TagSelector
          :tags="allTags"
          v-model:selected="selectedTags"
        />
      </section>

      <section class="panel chart-panel" v-if="selectedTags.length > 0">
        <h3 class="panel-title">
          Live Chart
          <span v-if="isLive" class="live-indicator">LIVE</span>
        </h3>
        <UPlotChart
          :data="buffer.data.value"
          :series="seriesConfig"
          :height="350"
        />
      </section>

      <section class="panel" v-if="source.buffer">
        <h3 class="panel-title">Buffer Info</h3>
        <div class="info-grid">
          <div class="info-item">
            <span class="info-label">Chunks</span>
            <span class="info-value">{{ source.buffer.chunkCount }}</span>
          </div>
          <div class="info-item">
            <span class="info-label">Records</span>
            <span class="info-value">{{ source.buffer.totalRecords.toLocaleString() }}</span>
          </div>
          <div class="info-item">
            <span class="info-label">Oldest</span>
            <span class="info-value mono">{{ source.buffer.oldestTs }}</span>
          </div>
          <div class="info-item">
            <span class="info-label">Newest</span>
            <span class="info-value mono">{{ source.buffer.newestTs }}</span>
          </div>
          <div class="info-item">
            <span class="info-label">MsgId Range</span>
            <span class="info-value mono">{{ source.buffer.oldestMsgId }} - {{ source.buffer.newestMsgId }}</span>
          </div>
        </div>
      </section>
    </template>
  </div>
</template>

<style scoped>
.page-header {
  display: flex;
  justify-content: space-between;
  align-items: flex-start;
  margin-bottom: 20px;
}

.page-header h2 {
  font-size: 20px;
  font-weight: 600;
}

.subtitle {
  font-size: 12px;
  color: var(--text-secondary);
}

.header-actions {
  display: flex;
  gap: 8px;
}

.btn {
  font-size: 12px;
  padding: 6px 14px;
  border: none;
  border-radius: 6px;
  cursor: pointer;
  font-weight: 500;
}

.btn:disabled {
  opacity: 0.4;
  cursor: default;
}

.btn-primary {
  background: var(--accent);
  color: #fff;
}

.btn-primary:hover:not(:disabled) {
  background: var(--accent-hover);
}

.btn-danger {
  background: var(--danger);
  color: #fff;
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
  display: flex;
  align-items: center;
  gap: 8px;
}

.live-indicator {
  font-size: 9px;
  padding: 2px 6px;
  border-radius: 4px;
  background: rgba(239, 68, 68, 0.2);
  color: var(--danger);
  animation: pulse 1.5s infinite;
}

.chart-panel {
  padding-bottom: 8px;
}

.info-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(160px, 1fr));
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

.empty-state {
  text-align: center;
  padding: 60px 20px;
  color: var(--text-secondary);
}

@keyframes pulse {
  0%, 100% { opacity: 1; }
  50% { opacity: 0.4; }
}
</style>
