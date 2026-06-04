<script setup lang="ts">
import { computed } from 'vue'

const props = defineProps<{
  tags: string[]
  selected: string[]
}>()

const emit = defineEmits<{
  (e: 'update:selected', tags: string[]): void
}>()

function toggle(tag: string) {
  const set = new Set(props.selected)
  if (set.has(tag)) set.delete(tag)
  else set.add(tag)
  emit('update:selected', Array.from(set))
}

function selectAll() {
  emit('update:selected', [...props.tags])
}

function selectNone() {
  emit('update:selected', [])
}

const allSelected = computed(() => props.selected.length === props.tags.length)
</script>

<template>
  <div class="tag-selector">
    <div class="actions">
      <button @click="selectAll" :disabled="allSelected" class="btn-sm">All</button>
      <button @click="selectNone" :disabled="selected.length === 0" class="btn-sm">None</button>
      <span class="count">{{ selected.length }}/{{ tags.length }}</span>
    </div>
    <div class="tag-list">
      <label
        v-for="tag in tags"
        :key="tag"
        class="tag-item"
        :class="{ active: selected.includes(tag) }"
      >
        <input
          type="checkbox"
          :checked="selected.includes(tag)"
          @change="toggle(tag)"
          class="sr-only"
        />
        {{ tag }}
      </label>
    </div>
  </div>
</template>

<style scoped>
.tag-selector {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.actions {
  display: flex;
  align-items: center;
  gap: 6px;
}

.btn-sm {
  font-size: 11px;
  padding: 2px 8px;
  border: 1px solid var(--border-color);
  background: transparent;
  color: var(--text-secondary);
  border-radius: 4px;
  cursor: pointer;
}

.btn-sm:hover:not(:disabled) {
  border-color: var(--accent);
  color: var(--accent);
}

.btn-sm:disabled {
  opacity: 0.4;
  cursor: default;
}

.count {
  font-size: 11px;
  color: var(--text-secondary);
  margin-left: auto;
}

.tag-list {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
}

.tag-item {
  font-size: 11px;
  padding: 3px 8px;
  border-radius: 4px;
  background: rgba(255, 255, 255, 0.06);
  color: var(--text-secondary);
  cursor: pointer;
  user-select: none;
  transition: all 0.15s;
}

.tag-item:hover {
  background: rgba(255, 255, 255, 0.1);
}

.tag-item.active {
  background: rgba(59, 130, 246, 0.15);
  color: var(--accent);
}

.sr-only {
  position: absolute;
  width: 1px;
  height: 1px;
  overflow: hidden;
  clip: rect(0, 0, 0, 0);
}
</style>
