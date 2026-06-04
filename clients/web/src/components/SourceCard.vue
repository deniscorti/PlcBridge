<script setup lang="ts">
import type { Source } from '@/types/bridge'

defineProps<{ source: Source }>()
</script>

<template>
  <router-link :to="`/source/${source.id}`" class="card">
    <div class="card-header">
      <h3 class="source-name">{{ source.id }}</h3>
      <span class="tag-count">{{ source.tags.length }} tags</span>
    </div>

    <div class="card-body" v-if="source.buffer">
      <div class="stat">
        <span class="stat-label">Chunks</span>
        <span class="stat-value">{{ source.buffer.chunkCount }}</span>
      </div>
      <div class="stat">
        <span class="stat-label">Records</span>
        <span class="stat-value">{{ source.buffer.totalRecords.toLocaleString() }}</span>
      </div>
      <div class="stat">
        <span class="stat-label">Latest</span>
        <span class="stat-value mono">{{ formatTime(source.buffer.newestTs) }}</span>
      </div>
    </div>

    <div class="card-tags">
      <span v-for="tag in source.tags.slice(0, 6)" :key="tag" class="tag-chip">{{ tag }}</span>
      <span v-if="source.tags.length > 6" class="tag-chip more">+{{ source.tags.length - 6 }}</span>
    </div>
  </router-link>
</template>

<script lang="ts">
function formatTime(ts: string): string {
  try {
    return new Date(ts).toLocaleTimeString()
  } catch {
    return ts
  }
}
</script>

<style scoped>
.card {
  display: block;
  background: var(--bg-card);
  border: 1px solid var(--border-color);
  border-radius: 8px;
  padding: 16px;
  text-decoration: none;
  color: inherit;
  transition: border-color 0.15s, box-shadow 0.15s;
}

.card:hover {
  border-color: var(--accent);
  box-shadow: 0 0 0 1px var(--accent);
}

.card-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: 12px;
}

.source-name {
  font-size: 15px;
  font-weight: 600;
}

.tag-count {
  font-size: 11px;
  color: var(--text-secondary);
}

.card-body {
  display: flex;
  gap: 16px;
  margin-bottom: 12px;
}

.stat {
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.stat-label {
  font-size: 10px;
  color: var(--text-secondary);
  text-transform: uppercase;
  letter-spacing: 0.5px;
}

.stat-value {
  font-size: 13px;
  font-weight: 500;
}

.mono {
  font-family: var(--font-mono);
  font-size: 12px;
}

.card-tags {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
}

.tag-chip {
  font-size: 10px;
  padding: 2px 6px;
  border-radius: 4px;
  background: rgba(255, 255, 255, 0.06);
  color: var(--text-secondary);
}

.tag-chip.more {
  color: var(--accent);
}
</style>
