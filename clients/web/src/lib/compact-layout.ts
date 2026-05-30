import type { CompactLayout, TagValue } from '@/types/bridge'

/**
 * Tracks compact layouts per source and decodes batch messages.
 */
export class CompactLayoutTracker {
  private layouts = new Map<number, CompactLayout>() // layoutId → layout

  setLayout(layout: CompactLayout) {
    // Remove old layout for same source
    for (const [id, existing] of this.layouts) {
      if (existing.source === layout.source) {
        this.layouts.delete(id)
      }
    }
    this.layouts.set(layout.layoutId, layout)
  }

  getLayout(source: string): CompactLayout | undefined {
    for (const layout of this.layouts.values()) {
      if (layout.source === source) return layout
    }
    return undefined
  }

  /**
   * Decode a compact batch message into TagValue array.
   * Message format: { type:"d", l:layoutId, ts:string, m:msgId, v:(number|null)[] }
   */
  decode(msg: { l: number; ts: string; m: number; v: (number | null)[] }): TagValue[] {
    const layout = this.layouts.get(msg.l)
    if (!layout) return []

    const values: TagValue[] = []
    for (let i = 0; i < msg.v.length; i++) {
      if (msg.v[i] === null || msg.v[i] === undefined) continue
      values.push({
        source: layout.source,
        tag: layout.tags[i],
        kind: 'telemetry',
        value: msg.v[i],
        timestamp: msg.ts,
        msgId: msg.m,
      })
    }
    return values
  }

  clear() {
    this.layouts.clear()
  }
}
