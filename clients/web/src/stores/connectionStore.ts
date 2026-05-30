import { defineStore } from 'pinia'
import { ref, computed } from 'vue'
import { useBridgeWs } from '@/composables/useBridgeWs'
import type { Source } from '@/types/bridge'

export const useConnectionStore = defineStore('connection', () => {
  const { state, rttMs, connect, disconnect, request, nextId, onMessage } = useBridgeWs()

  const sources = ref<Source[]>([])
  const bridgeMode = ref<string>('')
  const bridgeVersion = ref<string>('')

  const isConnected = computed(() => state.value === 'connected')

  /** Connect and fetch initial metadata */
  async function init(url: string, apiKey: string) {
    connect(url, apiKey)

    // Listen for source updates
    onMessage((data) => {
      if (data.type === 'sourcesUpdated' && Array.isArray(data.sources)) {
        sources.value = data.sources as Source[]
      }
    })

    // Wait a bit for connection, then fetch sources
    await waitConnected(5000)
    await refreshSources()
    await fetchInfo()
  }

  async function waitConnected(timeoutMs: number): Promise<void> {
    if (state.value === 'connected') return
    return new Promise((resolve) => {
      const check = setInterval(() => {
        if (state.value === 'connected') {
          clearInterval(check)
          resolve()
        }
      }, 100)
      setTimeout(() => {
        clearInterval(check)
        resolve()
      }, timeoutMs)
    })
  }

  async function refreshSources() {
    try {
      const resp = await request({ op: 'getSources', id: nextId('src') })
      if (Array.isArray(resp.sources)) {
        sources.value = resp.sources as Source[]
      }
    } catch { /* ignore timeout */ }
  }

  async function fetchInfo() {
    try {
      const resp = await request({ op: 'getInfo', id: nextId('info') })
      bridgeMode.value = (resp.mode as string) ?? ''
      bridgeVersion.value = (resp.version as string) ?? ''
    } catch { /* ignore */ }
  }

  return {
    state,
    rttMs,
    isConnected,
    sources,
    bridgeMode,
    bridgeVersion,
    init,
    disconnect,
    refreshSources,
  }
})
