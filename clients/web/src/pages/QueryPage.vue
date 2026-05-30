<script setup lang="ts">
import { ref, computed } from 'vue'
import { useConnectionStore } from '@/stores/connectionStore'
import { useQuery } from '@/composables/useQuery'
import { useTimeSeriesBuffer } from '@/composables/useTimeSeriesBuffer'
import UPlotChart from '@/components/charts/UPlotChart.vue'
import TagSelector from '@/components/TagSelector.vue'
import type uPlot from 'uplot'

const conn = useConnectionStore()
const query = useQuery()
const buffer = useTimeSeriesBuffer()

const selectedSource = ref('')
const selectedTags = ref<string[]>([])
const fromDate = ref('')
const toDate = ref('')
const hasResult = ref(false)

const sourceTags = computed(() => {
  const s = conn.sources.find((s) => s.id === selectedSource.value)
  return s?.tags ?? []
})

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

async function runQuery() {
  if (!selectedSource.value || selectedTags.value.length === 0 || !fromDate.value || !toDate.value) return

  buffer.setTags(selectedTags.value)
  const values = await query.queryTelemetry(
    selectedSource.value,
    selectedTags.value,
    new Date(fromDate.value).toISOString(),
    new Date(toDate.value).toISOString(),
  )
  buffer.loadHistory(values)
  hasResult.value = true
}

// Set default date range: last 1 hour
const now = new Date()
const oneHourAgo = new Date(now.getTime() - 3600000)
toDate.value = now.toISOString().slice(0, 16)
fromDate.value = oneHourAgo.toISOString().slice(0, 16)
</script>

<template>
  <div class="query-page">
    <header class="page-header">
      <h2>Query Historical Data</h2>
    </header>

    <section class="panel">
      <div class="form-row">
        <label class="form-label">
          Source
          <select v-model="selectedSource" class="form-input">
            <option value="">Select source...</option>
            <option v-for="s in conn.sources" :key="s.id" :value="s.id">{{ s.id }}</option>
          </select>
        </label>
        <label class="form-label">
          From
          <input type="datetime-local" v-model="fromDate" class="form-input" />
        </label>
        <label class="form-label">
          To
          <input type="datetime-local" v-model="toDate" class="form-input" />
        </label>
        <button @click="runQuery" :disabled="query.loading.value || selectedTags.length === 0" class="btn btn-primary btn-query">
          {{ query.loading.value ? 'Loading...' : 'Query' }}
        </button>
      </div>

      <div v-if="sourceTags.length > 0" class="tags-section">
        <TagSelector :tags="sourceTags" v-model:selected="selectedTags" />
      </div>
    </section>

    <div v-if="query.error.value" class="error-msg">{{ query.error.value }}</div>

    <section v-if="hasResult" class="panel chart-panel">
      <h3 class="panel-title">Results</h3>
      <UPlotChart
        :data="buffer.data.value"
        :series="seriesConfig"
        :height="400"
      />
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

.chart-panel {
  padding-bottom: 8px;
}

.form-row {
  display: flex;
  gap: 12px;
  align-items: flex-end;
  flex-wrap: wrap;
}

.form-label {
  display: flex;
  flex-direction: column;
  gap: 4px;
  font-size: 11px;
  color: var(--text-secondary);
  text-transform: uppercase;
  letter-spacing: 0.5px;
}

.form-input {
  padding: 6px 10px;
  border: 1px solid var(--border-color);
  border-radius: 6px;
  background: var(--bg-primary);
  color: var(--text-primary);
  font-size: 13px;
  min-width: 180px;
}

.form-input:focus {
  outline: none;
  border-color: var(--accent);
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

.btn-query {
  align-self: flex-end;
}

.tags-section {
  margin-top: 12px;
  padding-top: 12px;
  border-top: 1px solid var(--border-color);
}

.error-msg {
  background: rgba(239, 68, 68, 0.1);
  border: 1px solid rgba(239, 68, 68, 0.3);
  color: var(--danger);
  padding: 8px 12px;
  border-radius: 6px;
  font-size: 13px;
  margin-bottom: 16px;
}
</style>
