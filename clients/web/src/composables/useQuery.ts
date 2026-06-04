import { ref } from 'vue'
import { useBridgeWs } from './useBridgeWs'
import type { TagValue, ChunkInfo } from '@/types/bridge'

/**
 * Query historical data from the Bridge API via WebSocket.
 */
export function useQuery() {
  const { request, nextId } = useBridgeWs()
  const loading = ref(false)
  const error = ref<string | null>(null)

  /**
   * Query telemetry data for given tags in a time range.
   */
  async function queryTelemetry(
    source: string,
    tags: string[],
    from: string,
    to: string,
  ): Promise<TagValue[]> {
    loading.value = true
    error.value = null
    try {
      const resp = await request({
        op: 'queryTelemetry',
        id: nextId('q'),
        source,
        tags,
        from,
        to,
      })
      return (resp.values as TagValue[]) ?? []
    } catch (e) {
      error.value = (e as Error).message
      return []
    } finally {
      loading.value = false
    }
  }

  /**
   * Get chunk list for a source.
   */
  async function getChunks(source: string): Promise<ChunkInfo[]> {
    loading.value = true
    error.value = null
    try {
      const resp = await request({
        op: 'getChunks',
        id: nextId('ch'),
        source,
      })
      return (resp.chunks as ChunkInfo[]) ?? []
    } catch (e) {
      error.value = (e as Error).message
      return []
    } finally {
      loading.value = false
    }
  }

  return {
    loading,
    error,
    queryTelemetry,
    getChunks,
  }
}
