<script setup lang="ts">
import { useConnectionStore } from '@/stores/connectionStore'

const conn = useConnectionStore()
</script>

<template>
  <aside class="sidebar">
    <div class="sidebar-header">
      <h1 class="logo">Bridge</h1>
      <span class="mode-badge" v-if="conn.bridgeMode">{{ conn.bridgeMode }}</span>
    </div>

    <nav class="sidebar-nav">
      <router-link to="/" class="nav-item">
        <span class="nav-icon">&#9632;</span>
        Dashboard
      </router-link>

      <div class="nav-section" v-if="conn.sources.length">
        <span class="nav-section-title">Sources</span>
        <router-link
          v-for="s in conn.sources"
          :key="s.id"
          :to="`/source/${s.id}`"
          class="nav-item nav-sub"
        >
          <span class="source-dot" :class="{ active: conn.isConnected }"></span>
          {{ s.id }}
          <span class="tag-count">{{ s.tags.length }}</span>
        </router-link>
      </div>

      <router-link to="/query" class="nav-item">
        <span class="nav-icon">&#128269;</span>
        Query
      </router-link>

      <router-link to="/archives" class="nav-item">
        <span class="nav-icon">&#128451;</span>
        Archives
      </router-link>

      <router-link to="/settings" class="nav-item">
        <span class="nav-icon">&#9881;</span>
        Settings
      </router-link>
    </nav>
  </aside>
</template>

<style scoped>
.sidebar {
  background: var(--bg-secondary);
  border-right: 1px solid var(--border-color);
  display: flex;
  flex-direction: column;
  grid-row: 1 / -1;
  overflow-y: auto;
}

.sidebar-header {
  padding: 16px;
  border-bottom: 1px solid var(--border-color);
  display: flex;
  align-items: center;
  gap: 8px;
}

.logo {
  font-size: 18px;
  font-weight: 700;
  color: var(--accent);
}

.mode-badge {
  font-size: 10px;
  padding: 2px 6px;
  border-radius: 4px;
  background: var(--accent);
  color: #fff;
  text-transform: uppercase;
  letter-spacing: 0.5px;
}

.sidebar-nav {
  padding: 8px 0;
  flex: 1;
}

.nav-item {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 8px 16px;
  color: var(--text-secondary);
  text-decoration: none;
  font-size: 13px;
  transition: all 0.15s;
}

.nav-item:hover {
  color: var(--text-primary);
  background: rgba(255, 255, 255, 0.04);
}

.nav-item.router-link-active {
  color: var(--accent);
  background: rgba(59, 130, 246, 0.08);
  border-right: 2px solid var(--accent);
}

.nav-sub {
  padding-left: 24px;
  font-size: 12px;
}

.nav-icon {
  font-size: 14px;
  width: 20px;
  text-align: center;
}

.nav-section {
  margin-top: 8px;
}

.nav-section-title {
  display: block;
  padding: 4px 16px;
  font-size: 10px;
  text-transform: uppercase;
  letter-spacing: 1px;
  color: var(--text-secondary);
  opacity: 0.6;
}

.source-dot {
  width: 6px;
  height: 6px;
  border-radius: 50%;
  background: var(--text-secondary);
}

.source-dot.active {
  background: var(--success);
}

.tag-count {
  margin-left: auto;
  font-size: 10px;
  color: var(--text-secondary);
  background: rgba(255, 255, 255, 0.06);
  padding: 1px 5px;
  border-radius: 8px;
}
</style>
