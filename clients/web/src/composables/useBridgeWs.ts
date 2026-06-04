import { ref, readonly } from 'vue'
import type { ConnectionState } from '@/types/bridge'

export type MessageHandler = (data: Record<string, unknown>) => void

const ws = ref<WebSocket | null>(null)
const state = ref<ConnectionState>('disconnected')
const rttMs = ref(0)

let url = ''
let apiKey = ''
let handlers: MessageHandler[] = []
let reconnectTimer: ReturnType<typeof setTimeout> | null = null
let reconnectDelay = 1000
let pingTimer: ReturnType<typeof setInterval> | null = null
let pingTs = 0
let msgId = 0

function nextId(prefix: string) {
  return `${prefix}-${++msgId}`
}

type PendingResolve = (data: Record<string, unknown>) => void
const pendingRequests = new Map<string, PendingResolve>()

function connect(bridgeUrl: string, key: string) {
  url = bridgeUrl
  apiKey = key
  doConnect()
}

function doConnect() {
  if (ws.value && ws.value.readyState <= WebSocket.OPEN) return
  state.value = 'connecting'

  const fullUrl = `${url}${url.includes('?') ? '&' : '?'}apiKey=${apiKey}`
  const socket = new WebSocket(fullUrl)

  socket.onopen = () => {
    state.value = 'connected'
    reconnectDelay = 1000
    ws.value = socket
    startHeartbeat()
  }

  socket.onmessage = (ev) => {
    try {
      const data = JSON.parse(ev.data) as Record<string, unknown>

      // Pong → measure RTT
      if (data.type === 'pong') {
        if (pingTs > 0) rttMs.value = Date.now() - pingTs
        return
      }

      // Correlated response
      if (data.type === 'response' && data.id) {
        const resolve = pendingRequests.get(data.id as string)
        if (resolve) {
          pendingRequests.delete(data.id as string)
          resolve(data)
          return
        }
      }

      // Dispatch to all handlers
      for (const h of handlers) h(data)
    } catch { /* malformed JSON */ }
  }

  socket.onclose = () => {
    state.value = 'disconnected'
    ws.value = null
    stopHeartbeat()
    scheduleReconnect()
  }

  socket.onerror = () => {
    socket.close()
  }
}

function scheduleReconnect() {
  if (reconnectTimer) return
  reconnectTimer = setTimeout(() => {
    reconnectTimer = null
    reconnectDelay = Math.min(reconnectDelay * 2, 30000)
    doConnect()
  }, reconnectDelay)
}

function startHeartbeat() {
  stopHeartbeat()
  pingTimer = setInterval(() => {
    if (ws.value?.readyState === WebSocket.OPEN) {
      pingTs = Date.now()
      ws.value.send(JSON.stringify({ op: 'ping' }))
    }
  }, 10000)
}

function stopHeartbeat() {
  if (pingTimer) { clearInterval(pingTimer); pingTimer = null }
}

function send(msg: Record<string, unknown>) {
  if (ws.value?.readyState === WebSocket.OPEN) {
    ws.value.send(JSON.stringify(msg))
  }
}

/**
 * Send a request and wait for the correlated response.
 */
function request(msg: Record<string, unknown>, timeoutMs = 10000): Promise<Record<string, unknown>> {
  const id = msg.id as string || nextId('req')
  msg.id = id
  return new Promise((resolve, reject) => {
    pendingRequests.set(id, resolve)
    send(msg)
    setTimeout(() => {
      if (pendingRequests.delete(id)) {
        reject(new Error(`Request ${id} timed out`))
      }
    }, timeoutMs)
  })
}

function onMessage(handler: MessageHandler) {
  handlers.push(handler)
  return () => {
    handlers = handlers.filter((h) => h !== handler)
  }
}

function disconnect() {
  if (reconnectTimer) { clearTimeout(reconnectTimer); reconnectTimer = null }
  stopHeartbeat()
  ws.value?.close()
  ws.value = null
  state.value = 'disconnected'
}

export function useBridgeWs() {
  return {
    state: readonly(state),
    rttMs: readonly(rttMs),
    connect,
    disconnect,
    send,
    request,
    nextId,
    onMessage,
  }
}
