import { ref, shallowRef } from 'vue'
import type { TagValue } from '@/types/bridge'

/**
 * Ring buffer that maintains uPlot-compatible aligned data.
 *
 * uPlot expects: [ timestamps[], seriesA[], seriesB[], ... ]
 * where timestamps is sorted ascending (seconds since epoch).
 *
 * This buffer keeps a configurable max number of points and
 * provides efficient append + setData for uPlot.
 */
export function useTimeSeriesBuffer(maxPoints = 10000) {
  // Tag names in order → series index mapping
  const tagNames = ref<string[]>([])
  // tagName → index in data arrays (index 0 is timestamps)
  const tagIndex = new Map<string, number>()

  // uPlot aligned data: [timestamps, series1, series2, ...]
  // Using shallowRef so uPlot can detect changes via reference
  const data = shallowRef<(number | null)[][]>([[]])

  let pointCount = 0

  function setTags(tags: string[]) {
    tagNames.value = tags
    tagIndex.clear()
    tags.forEach((t, i) => tagIndex.set(t, i + 1)) // +1 because index 0 = timestamps

    // Initialize arrays: [timestamps, ...one per tag]
    const arrays: (number | null)[][] = new Array(tags.length + 1)
    for (let i = 0; i < arrays.length; i++) arrays[i] = []
    data.value = arrays
    pointCount = 0
  }

  /**
   * Push new values. Values with the same timestamp are merged into one row.
   */
  function push(values: TagValue[]) {
    if (tagNames.value.length === 0) return

    const d = data.value
    const seriesCount = d.length

    // Group by timestamp
    const grouped = new Map<number, TagValue[]>()
    for (const v of values) {
      const ts = new Date(v.timestamp).getTime() / 1000 // uPlot uses seconds
      const arr = grouped.get(ts)
      if (arr) arr.push(v)
      else grouped.set(ts, [v])
    }

    // Sort timestamps ascending
    const timestamps = Array.from(grouped.keys()).sort((a, b) => a - b)

    for (const ts of timestamps) {
      const group = grouped.get(ts)!

      // Check if last row has same timestamp → merge
      const lastTs = d[0].length > 0 ? d[0][d[0].length - 1] : null
      if (lastTs === ts) {
        // Merge into last row
        for (const v of group) {
          const idx = tagIndex.get(v.tag)
          if (idx !== undefined) {
            d[idx][d[idx].length - 1] = v.value as number | null
          }
        }
      } else {
        // New row
        d[0].push(ts)
        for (let i = 1; i < seriesCount; i++) d[i].push(null)
        for (const v of group) {
          const idx = tagIndex.get(v.tag)
          if (idx !== undefined) {
            d[idx][d[idx].length - 1] = v.value as number | null
          }
        }
        pointCount++
      }
    }

    // Trim if over max
    if (pointCount > maxPoints) {
      const excess = pointCount - maxPoints
      for (let i = 0; i < seriesCount; i++) {
        d[i].splice(0, excess)
      }
      pointCount = maxPoints
    }

    // Trigger reactivity by replacing reference
    data.value = d.slice()
  }

  /**
   * Load historical data (replaces current buffer).
   */
  function loadHistory(values: TagValue[]) {
    if (tagNames.value.length === 0) return

    const seriesCount = tagNames.value.length + 1
    const arrays: (number | null)[][] = new Array(seriesCount)
    for (let i = 0; i < seriesCount; i++) arrays[i] = []

    // Group and sort
    const byTs = new Map<number, TagValue[]>()
    for (const v of values) {
      const ts = new Date(v.timestamp).getTime() / 1000
      const arr = byTs.get(ts)
      if (arr) arr.push(v)
      else byTs.set(ts, [v])
    }

    const sorted = Array.from(byTs.keys()).sort((a, b) => a - b)
    for (const ts of sorted) {
      arrays[0].push(ts)
      for (let i = 1; i < seriesCount; i++) arrays[i].push(null)
      for (const v of byTs.get(ts)!) {
        const idx = tagIndex.get(v.tag)
        if (idx !== undefined) {
          arrays[idx][arrays[idx].length - 1] = v.value as number | null
        }
      }
    }

    pointCount = sorted.length
    // Trim from start if needed
    if (pointCount > maxPoints) {
      const excess = pointCount - maxPoints
      for (let i = 0; i < seriesCount; i++) arrays[i].splice(0, excess)
      pointCount = maxPoints
    }

    data.value = arrays
  }

  function clear() {
    const seriesCount = tagNames.value.length + 1
    const arrays: (number | null)[][] = new Array(seriesCount)
    for (let i = 0; i < seriesCount; i++) arrays[i] = []
    data.value = arrays
    pointCount = 0
  }

  return {
    tagNames,
    data,
    setTags,
    push,
    loadHistory,
    clear,
  }
}
