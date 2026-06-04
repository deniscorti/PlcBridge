<script setup lang="ts">
import type { Quality } from '@/types/bridge'

defineProps<{ quality: Quality }>()
</script>

<template>
  <span class="badge" :class="quality">{{ quality }}</span>
</template>

<style scoped>
.badge {
  display: inline-block;
  font-size: 10px;
  padding: 1px 6px;
  border-radius: 4px;
  text-transform: uppercase;
  letter-spacing: 0.5px;
  font-weight: 600;
}
.live { background: rgba(34, 197, 94, 0.15); color: var(--success); }
.full { background: rgba(59, 130, 246, 0.15); color: var(--accent); }
.loaded { background: rgba(234, 179, 8, 0.15); color: var(--warning); }
.mixed { background: rgba(139, 92, 246, 0.15); color: #a78bfa; }
.none { background: rgba(255, 255, 255, 0.06); color: var(--text-secondary); }
</style>
