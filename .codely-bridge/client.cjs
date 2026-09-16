// Codely Bridge client helper: persistent TCP client for the Unity Editor bridge.
// Speaks NDJSON on stdin/stdout with its parent process.
const fs = require('node:fs')
const path = require('node:path')
const net = require('node:net')

const WORKSPACE = process.env.CODELY_WORKSPACE || process.cwd()
const HEARTBEAT_MS = 20000

function findConfig() {
  const found = []
  const probe = (dir) => {
    const p = path.join(dir, 'Temp', '.com-unity-codely.json')
    if (fs.existsSync(p)) found.push(p)
  }
  probe(WORKSPACE)
  probe(path.join(WORKSPACE, 'UnityProject'))
  const walk = (dir, depth) => {
    if (depth > 3 || found.length) return
    let entries = []
    try { entries = fs.readdirSync(dir, { withFileTypes: true }) } catch { return }
    for (const e of entries) {
      if (found.length) return
      if (!e.isDirectory()) continue
      if (e.name === 'Library' || e.name === 'node_modules' || e.name === '.git' || e.name === 'Temp') continue
      const next = path.join(dir, e.name)
      probe(next)
      walk(next, depth + 1)
    }
  }
  walk(WORKSPACE, 0)
  if (found.length) return found[0]
  // Fall back to searching upward, so any cwd inside the Unity project still works.
  let dir = path.resolve(WORKSPACE)
  for (let depth = 0; depth < 6; depth++) {
    dir = path.dirname(dir)
    if (!dir || dir === path.dirname(dir)) break
    probe(dir)
    probe(path.join(dir, 'UnityProject'))
    if (found.length) return found[0]
  }
  return null
}

function emit(obj) {
  try { process.stdout.write(JSON.stringify(obj) + '\n') } catch { /* parent gone */ }
}

let cfgPath = null
let socket = null
let connected = false
let connecting = null
let handshakeTimer = null
let buffer = Buffer.alloc(0)
let serverVersion = 1
let port = -1
let pending = new Map()
let seq = 0
let lastConnectError = null

function frame(payload) {
  const body = Buffer.isBuffer(payload) ? payload : Buffer.from(String(payload), 'utf8')
  const head = Buffer.allocUnsafe(8)
  head.writeBigUInt64BE(BigInt(body.length), 0)
  return Buffer.concat([head, body])
}

function failAll(reason) {
  for (const [, w] of pending) { clearTimeout(w.timer); w.reject(new Error(reason)) }
  pending.clear()
}

function handleFrame(text) {
  let json = null
  try { json = JSON.parse(text) } catch { return }
  if (!json || typeof json !== 'object') return
  if (typeof json.notification_type === 'string') {
    emit({ kind: 'notification', notification_type: json.notification_type, payload: json.payload === undefined ? null : json.payload })
    return
  }
  if (json.success === true && json.message === 'pong') return
  const rid = typeof json.request_id === 'string' ? json.request_id : null
  let waiter = null
  if (rid && pending.has(rid)) { waiter = pending.get(rid); pending.delete(rid) }
  else if (pending.size > 0) { const key = pending.keys().next().value; waiter = pending.get(key); pending.delete(key) }
  if (waiter) { clearTimeout(waiter.timer); waiter.resolve(json) }
}

function readFrames(chunk) {
  buffer = Buffer.concat([buffer, chunk])
  while (buffer.length >= 8) {
    const len = Number(buffer.readBigUInt64BE(0))
    if (len === 0) { buffer = buffer.subarray(8); continue }
    if (len > 64 * 1024 * 1024) { buffer = Buffer.alloc(0); return }
    if (buffer.length < 8 + len) return
    const payload = buffer.subarray(8, 8 + len)
    buffer = buffer.subarray(8 + len)
    handleFrame(payload.toString('utf8'))
  }
}

function settleConnect(error) {
  const current = connecting
  connecting = null
  if (handshakeTimer) { clearTimeout(handshakeTimer); handshakeTimer = null }
  if (!current) return
  if (error) current.reject(error)
  else current.resolve()
}

function onData(chunk) {
  if (connected) { readFrames(chunk); return }
  buffer = Buffer.concat([buffer, chunk])
  if (!buffer.includes(10)) return
  const nl = buffer.indexOf(10)
  const line = buffer.subarray(0, nl).toString('ascii').trim()
  buffer = buffer.subarray(nl + 1)
  const matched = line.match(/SERVER_VERSION=(\d+)/)
  serverVersion = matched ? Number.parseInt(matched[1], 10) : 1
  if (!line.includes('WELCOME UNITY-TCP') || !line.includes('FRAMING=1')) {
    settleConnect(new Error('unexpected handshake from Unity bridge: ' + line))
    return
  }
  const rootMatch = line.match(/PROJECT_ROOT=(\S+)/)
  const sock = socket
  connected = true
  lastConnectError = null
  try {
    if (serverVersion >= 2) sock.write(frame('CLIENT_VERSION=2'))
    sock.write(frame('PLATFORM=dsh'))
  } catch (e) {
    connected = false
    settleConnect(e)
    return
  }
  emit({ kind: 'status', status: 'connected', port, serverVersion, projectRoot: rootMatch ? rootMatch[1] : null })
  settleConnect(null)
  if (buffer.length) readFrames(Buffer.alloc(0))
}

function readConfig() {
  const p = cfgPath || findConfig()
  if (!p) throw new Error('no .com-unity-codely.json found under ' + WORKSPACE + ' — is the Unity project open?')
  cfgPath = p
  const parsed = JSON.parse(fs.readFileSync(p, 'utf8'))
  if (typeof parsed.unity_port !== 'number' || parsed.unity_port < 1) throw new Error('invalid unity_port in ' + p)
  return parsed
}

function connectOne(timeoutMs) {
  const cfg = readConfig()
  port = cfg.unity_port
  const host = cfg.unity_host || '127.0.0.1'
  buffer = Buffer.alloc(0)
  const sock = net.connect({ host, port })
  sock.setNoDelay(true)
  socket = sock
  const promise = new Promise((resolve, reject) => {
    connecting = { resolve, reject }
    handshakeTimer = setTimeout(() => settleConnect(new Error('handshake timeout with Unity bridge at ' + host + ':' + port)), timeoutMs || 8000)
  })
  sock.on('data', onData)
  sock.on('error', (e) => { if (!connected) settleConnect(e) })
  sock.on('close', () => {
    const wasConnected = connected
    connected = false
    if (socket === sock) socket = null
    settleConnect(new Error('socket closed during handshake'))
    failAll('Unity bridge connection closed (Unity may be compiling or reloading)')
    if (wasConnected) emit({ kind: 'status', status: 'disconnected', port })
  })
  return promise
}

async function ensureConnected(totalMs) {
  const deadline = Date.now() + (totalMs || 15000)
  let attempt = 0
  let lastError = null
  while (Date.now() < deadline) {
    if (connected && socket) return
    if (connecting) {
      await new Promise((r) => setTimeout(r, 50))
      continue
    }
    try { await connectOne(Math.min(8000, Math.max(1000, deadline - Date.now()))); return } catch (e) {
      lastError = e
      lastConnectError = e
      connected = false
      attempt++
      await new Promise((r) => setTimeout(r, Math.min(2000, 250 * 2 ** attempt)))
    }
  }
  if (connected) return
  throw lastConnectError || lastError || new Error('could not connect to Unity bridge')
}

async function call(type, params, timeoutMs) {
  await ensureConnected(20000)
  const id = 'dsh-' + (++seq)
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => { pending.delete(id); reject(new Error('timeout waiting for ' + type + ' (request_id=' + id + ')')) }, timeoutMs || 120000)
    pending.set(id, { resolve, reject, timer })
    try { socket.write(frame(JSON.stringify({ type, params: params || {}, request_id: id }))) }
    catch (e) { clearTimeout(timer); pending.delete(id); reject(e) }
  })
}

let lineBuf = ''
process.stdin.setEncoding('utf8')
process.stdin.on('data', (chunk) => {
  lineBuf += chunk
  let idx
  while ((idx = lineBuf.indexOf('\n')) >= 0) {
    const line = lineBuf.slice(0, idx).trim()
    lineBuf = lineBuf.slice(idx + 1)
    if (!line) continue
    let msg = null
    try { msg = JSON.parse(line) } catch { continue }
    if (msg.type === 'ping') {
      if (connected && socket) emit({ kind: 'result', id: msg.id, ok: true, response: { success: true, message: 'connected', port, serverVersion } })
      else ensureConnected(15000).then(
        () => emit({ kind: 'result', id: msg.id, ok: true, response: { success: true, message: 'connected', port, serverVersion } }),
        (e) => emit({ kind: 'result', id: msg.id, ok: false, error: String((e && e.message) || e) }),
      )
      continue
    }
    call(msg.type, msg.params, msg.timeoutMs).then(
      (response) => emit({ kind: 'result', id: msg.id, ok: true, response }),
      (error) => emit({ kind: 'result', id: msg.id, ok: false, error: String((error && error.message) || error) }),
    )
  }
})
process.stdin.on('end', () => process.exit(0))

emit({ kind: 'status', status: 'starting', workspace: WORKSPACE, configPath: findConfig() })

// Keep the link warm and reconnect after Unity domain reloads.
setInterval(() => {
  if (!connected) ensureConnected(15000).catch(() => {})
}, HEARTBEAT_MS)

process.on('SIGTERM', () => process.exit(0))
process.on('SIGINT', () => process.exit(0))
