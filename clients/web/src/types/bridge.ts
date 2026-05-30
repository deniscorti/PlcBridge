export interface Source {
  id: string
  tags: string[]
  buffer?: BufferInfo
}

export interface BufferInfo {
  oldestTs: string
  newestTs: string
  chunkCount: number
  totalRecords: number
  oldestMsgId: number
  newestMsgId: number
}

export interface TagValue {
  source: string
  tag: string
  kind?: 'telemetry' | 'event' | 'alarm'
  value: unknown
  timestamp: string
  msgId: number
}

export interface ChunkInfo {
  chunkId: string
  source: string
  fromTs: string
  toTs: string
  firstMsgId: number
  lastMsgId: number
  recordCount: number
  state: 'Open' | 'Sealed'
  quality: 'Live' | 'Full' | 'Loaded'
}

export interface CompactLayout {
  source: string
  layoutId: number
  tags: string[]
}

export type Quality = 'live' | 'full' | 'loaded' | 'mixed' | 'none'

export type ConnectionState = 'disconnected' | 'connecting' | 'connected'
