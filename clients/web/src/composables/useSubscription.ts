import { ref, onUnmounted, type Ref } from 'vue'
import { useBridgeWs } from './useBridgeWs'
import { CompactLayoutTracker } from '@/lib/compact-layout'
import type { TagValue, CompactLayout } from '@/types/bridge'

const layoutTracker = new CompactLayoutTracker()

export type TagCallback = (values: TagValue[]) => void

/**
 * Subscribe to live tag data from a source.
 * Handles compact and verbose protocols transparently.
 */
export function useSubscription() {
  const { send, request, nextId, onMessage } = useBridgeWs()
  const subscribed = ref(false)

  let unsub: (() => void) | null = null
  let currentSource: string | null = null
  let currentTags: string[] = []

  /**
   * Subscribe to tags on a source. Requests compact mode by default.
   */
  async function subscribe(
    source: string,
    tags: string[],
    callback: TagCallback,
    compact = true,
  ) {
    // Unsubscribe previous if any
    if (unsub) unsubscribe()

    currentSource = source
    currentTags = tags

    // Register message handler
    unsub = onMessage((data) => {
      // Layout definition → store it
      if (data.type === 'layout') {
        layoutTracker.setLayout(data as unknown as CompactLayout)
        return
      }

      // Compact batch → decode
      if (data.type === 'd') {
        const msg = data as unknown as { l: number; ts: string; m: number; v: (number | null)[] }
        const values = layoutTracker.decode(msg)
        if (values.length > 0 && values[0].source === source) {
          callback(values)
        }
        return
      }

      // Layout update
      if (data.type === 'layoutUpdate') {
        layoutTracker.setLayout(data as unknown as CompactLayout)
        return
      }

      // Verbose telemetry push
      if (data.type === 'telemetry' && data.source === source) {
        callback([data as unknown as TagValue])
        return
      }

      // Verbose batch push (array of values)
      if (data.type === 'telemetryBatch' && Array.isArray(data.values)) {
        const values = (data.values as unknown as TagValue[]).filter(
          (v) => v.source === source,
        )
        if (values.length > 0) callback(values)
      }
    })

    // Send subscribe request
    const id = nextId('sub')
    try {
      await request({
        op: 'subscribe',
        id,
        source,
        tags,
        compact,
      })
      subscribed.value = true
    } catch {
      // Timeout — still listening, server may send data later
      subscribed.value = true
    }
  }

  function unsubscribe() {
    if (currentSource && currentTags.length > 0) {
      send({
        op: 'unsubscribe',
        source: currentSource,
        tags: currentTags,
      })
    }
    unsub?.()
    unsub = null
    subscribed.value = false
    currentSource = null
    currentTags = []
  }

  onUnmounted(() => {
    unsubscribe()
  })

  return {
    subscribed,
    subscribe,
    unsubscribe,
  }
}
