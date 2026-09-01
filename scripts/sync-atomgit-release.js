#!/usr/bin/env node

const { Readable } = require('node:stream')

const dryRun = ['1', 'true', 'yes'].includes((process.env.DRY_RUN || '').trim().toLowerCase())
function validateEnvironment() {
  const requiredEnv = [
    'GITHUB_TOKEN',
    'GITHUB_OWNER',
    'GITHUB_REPO',
    'RELEASE_TAG',
    'ATOMGIT_OWNER',
    'ATOMGIT_REPO',
  ]
  if (!dryRun) requiredEnv.push('ATOMGIT_TOKEN')

  const missing = requiredEnv.filter((name) => !process.env[name])
  if (missing.length > 0) {
    throw new Error(`missing env: ${missing.join(', ')}`)
  }
}

const GITHUB_API_BASE = 'https://api.github.com'
const ATOMGIT_API_BASE = (process.env.ATOMGIT_API_BASE || 'https://api.atomgit.com').replace(
  /\/+$/,
  ''
)
const githubOwner = process.env.GITHUB_OWNER
const githubRepo = process.env.GITHUB_REPO
const releaseTag = process.env.RELEASE_TAG
const atomgitOwner = process.env.ATOMGIT_OWNER
const atomgitRepo = process.env.ATOMGIT_REPO

function formatBytes(bytes) {
  if (!Number.isFinite(bytes) || bytes < 0) return 'unknown'

  const units = ['B', 'KB', 'MB', 'GB']
  let value = bytes
  let unitIndex = 0
  while (value >= 1024 && unitIndex < units.length - 1) {
    value /= 1024
    unitIndex += 1
  }

  return `${value.toFixed(value >= 10 || unitIndex === 0 ? 0 : 1)} ${units[unitIndex]}`
}

function createProgressTracker(kind, name, totalBytes) {
  let lastPercent = -1
  let lastLoggedBytes = 0

  return (processedBytes) => {
    if (!Number.isFinite(processedBytes)) return

    if (Number.isFinite(totalBytes) && totalBytes > 0) {
      const percent = Math.min(100, Math.floor((processedBytes / totalBytes) * 100))
      if (percent > lastPercent) {
        lastPercent = percent
        console.log(
          `[atomgit-sync] ${kind} ${name}: ${percent}% (${formatBytes(processedBytes)}/${formatBytes(totalBytes)})`
        )
      }
      return
    }

    const progressStep = 5 * 1024 * 1024
    if (processedBytes - lastLoggedBytes >= progressStep) {
      lastLoggedBytes = processedBytes
      console.log(`[atomgit-sync] ${kind} ${name}: ${formatBytes(processedBytes)}`)
    }
  }
}

function redactUrl(rawUrl) {
  const url = new URL(rawUrl)
  if (url.searchParams.has('access_token')) url.searchParams.set('access_token', '***')
  return url.toString()
}

async function requestJson(url, options = {}, { allow404 = false } = {}) {
  const response = await fetch(url, options)
  if (allow404 && response.status === 404) return null

  if (!response.ok) {
    const text = await response.text()
    if (allow404) {
      try {
        const payload = JSON.parse(text)
        const message = String(payload?.error_message || payload?.message || '')
        if (message.includes('Release Not Found') || message.includes('404')) return null
      } catch {
        // 保留原始 HTTP 错误。
      }
    }
    throw new Error(
      `${options.method || 'GET'} ${redactUrl(url)} failed: ${response.status} ${text}`
    )
  }

  if (response.status === 204) return null
  return response.json()
}

function githubHeaders(extra = {}) {
  return {
    Accept: 'application/vnd.github+json',
    Authorization: `Bearer ${process.env.GITHUB_TOKEN}`,
    'X-GitHub-Api-Version': '2022-11-28',
    ...extra,
  }
}

function atomgitHeaders(extra = {}) {
  return {
    Accept: 'application/json',
    Authorization: `Bearer ${process.env.ATOMGIT_TOKEN}`,
    'PRIVATE-TOKEN': process.env.ATOMGIT_TOKEN,
    ...extra,
  }
}

function buildAtomGitApiUrl(path, query = {}) {
  const url = new URL(`${ATOMGIT_API_BASE}${path}`)
  if (process.env.ATOMGIT_TOKEN) {
    url.searchParams.set('access_token', process.env.ATOMGIT_TOKEN)
  }
  for (const [key, value] of Object.entries(query)) {
    if (value == null || value === '') continue
    url.searchParams.set(key, String(value))
  }
  return url.toString()
}

async function getGithubRelease() {
  const url = `${GITHUB_API_BASE}/repos/${encodeURIComponent(githubOwner)}/${encodeURIComponent(githubRepo)}/releases/tags/${encodeURIComponent(releaseTag)}`
  return requestJson(url, { headers: githubHeaders() })
}

async function getAtomGitRelease() {
  if (dryRun) return null

  const url = buildAtomGitApiUrl(
    `/api/v5/repos/${encodeURIComponent(atomgitOwner)}/${encodeURIComponent(atomgitRepo)}/releases/${encodeURIComponent(releaseTag)}`
  )
  return requestJson(url, { headers: atomgitHeaders() }, { allow404: true })
}

function buildReleasePayload(release) {
  return {
    tag_name: release.tag_name,
    target_commitish: release.target_commitish,
    name: release.name,
    body: release.body || '',
    draft: Boolean(release.draft),
    prerelease: Boolean(release.prerelease),
  }
}

async function createOrUpdateAtomGitRelease(githubRelease) {
  const existing = await getAtomGitRelease()
  const payload = buildReleasePayload(githubRelease)

  if (dryRun) {
    console.log(
      `[atomgit-sync][dry-run] would ${existing ? 'update' : 'create'} release ${releaseTag}`
    )
    return { ...payload, assets: [] }
  }

  if (!existing) {
    const url = buildAtomGitApiUrl(
      `/api/v5/repos/${encodeURIComponent(atomgitOwner)}/${encodeURIComponent(atomgitRepo)}/releases`
    )
    const created = await requestJson(url, {
      method: 'POST',
      headers: atomgitHeaders({ 'Content-Type': 'application/json' }),
      body: JSON.stringify(payload),
    })
    console.log(`[atomgit-sync] created release ${releaseTag}`)
    return created
  }

  const url = buildAtomGitApiUrl(
    `/api/v5/repos/${encodeURIComponent(atomgitOwner)}/${encodeURIComponent(atomgitRepo)}/releases/${encodeURIComponent(releaseTag)}`
  )
  const updated = await requestJson(url, {
    method: 'PATCH',
    headers: atomgitHeaders({ 'Content-Type': 'application/json' }),
    body: JSON.stringify(payload),
  })
  console.log(`[atomgit-sync] updated release ${releaseTag}`)
  return updated
}

function readUploadUrlPayload(payload) {
  if (!payload) return null
  if (typeof payload === 'string') return payload
  if (typeof payload.upload_url === 'string') return payload.upload_url
  if (typeof payload.url === 'string') return payload.url
  if (typeof payload.href === 'string') return payload.href
  return null
}

function normalizeUploadHeaders(headers) {
  if (!headers || typeof headers !== 'object') return {}

  const normalized = {}
  for (const [key, value] of Object.entries(headers)) {
    if (value != null) normalized[key] = String(value)
  }
  return normalized
}

async function getAtomGitUploadTarget(fileName) {
  if (dryRun) {
    return {
      url: 'https://example.invalid/upload',
      headers: { 'Content-Type': 'application/octet-stream' },
    }
  }

  const url = buildAtomGitApiUrl(
    `/api/v5/repos/${encodeURIComponent(atomgitOwner)}/${encodeURIComponent(atomgitRepo)}/releases/${encodeURIComponent(releaseTag)}/upload_url`,
    { file_name: fileName }
  )
  const payload = await requestJson(url, { headers: atomgitHeaders() })
  const uploadUrl = readUploadUrlPayload(payload)
  if (!uploadUrl) {
    throw new Error(`unexpected AtomGit upload_url payload: ${JSON.stringify(payload)}`)
  }

  return {
    url: uploadUrl,
    headers: normalizeUploadHeaders(payload.headers),
  }
}

async function downloadGithubAsset(asset) {
  const response = await fetch(asset.url, {
    headers: githubHeaders({ Accept: 'application/octet-stream' }),
    redirect: 'follow',
  })
  if (!response.ok) {
    throw new Error(`download GitHub asset ${asset.name} failed: ${response.status}`)
  }

  const totalBytes = Number(response.headers.get('content-length') || asset.size || 0)
  const reportProgress = createProgressTracker('download', asset.name, totalBytes)
  const reader = response.body?.getReader()
  if (!reader) {
    const buffer = Buffer.from(await response.arrayBuffer())
    reportProgress(buffer.byteLength)
    return buffer
  }

  const chunks = []
  let processedBytes = 0
  while (true) {
    const { done, value } = await reader.read()
    if (done) break
    const chunk = Buffer.from(value)
    chunks.push(chunk)
    processedBytes += chunk.byteLength
    reportProgress(processedBytes)
  }
  return Buffer.concat(chunks)
}

async function uploadAtomGitAsset(uploadTarget, asset, buffer) {
  if (dryRun) {
    console.log(
      `[atomgit-sync][dry-run] would upload ${asset.name} (${formatBytes(buffer.byteLength)})`
    )
    return
  }

  const reportProgress = createProgressTracker('upload', asset.name, buffer.byteLength)
  const headers = {
    ...uploadTarget.headers,
    'Content-Length': String(buffer.byteLength),
  }
  if (!headers['Content-Type'] && !headers['content-type']) {
    headers['Content-Type'] = asset.content_type || 'application/octet-stream'
  }

  let processedBytes = 0
  const body = Readable.from(
    (function* chunks() {
      const chunkSize = 1024 * 1024
      for (let offset = 0; offset < buffer.byteLength; offset += chunkSize) {
        const chunk = buffer.subarray(offset, Math.min(offset + chunkSize, buffer.byteLength))
        processedBytes += chunk.byteLength
        reportProgress(processedBytes)
        yield chunk
      }
    })()
  )

  const response = await fetch(uploadTarget.url, {
    method: 'PUT',
    headers,
    body,
    duplex: 'half',
  })
  if (response.ok || response.status === 409) {
    console.log(
      response.status === 409
        ? `[atomgit-sync] asset exists, skipped: ${asset.name}`
        : `[atomgit-sync] uploaded asset: ${asset.name}`
    )
    return
  }

  const text = await response.text()
  throw new Error(`PUT upload failed for ${asset.name}: ${response.status} ${text}`)
}

function listExistingAssetNames(release) {
  if (!release || !Array.isArray(release.assets)) return new Set()
  return new Set(release.assets.map((asset) => asset?.name).filter(Boolean))
}

async function syncAssets(githubRelease, atomgitRelease) {
  if (!Array.isArray(githubRelease.assets) || githubRelease.assets.length === 0) {
    console.log('[atomgit-sync] no GitHub release assets, skip upload')
    return
  }

  const existingNames = listExistingAssetNames(atomgitRelease)
  for (const asset of githubRelease.assets) {
    if (existingNames.has(asset.name)) {
      console.log(`[atomgit-sync] asset already present, skipped: ${asset.name}`)
      continue
    }

    const uploadTarget = await getAtomGitUploadTarget(asset.name)
    if (dryRun) {
      console.log(
        `[atomgit-sync][dry-run] would sync ${asset.name} (${formatBytes(asset.size)})`
      )
      continue
    }

    console.log(`[atomgit-sync] downloading asset: ${asset.name}`)
    const buffer = await downloadGithubAsset(asset)
    await uploadAtomGitAsset(uploadTarget, asset, buffer)
  }
}

async function main() {
  validateEnvironment()
  const githubRelease = await getGithubRelease()
  console.log(`[atomgit-sync] loaded GitHub release ${githubRelease.tag_name}`)

  const atomgitRelease = await createOrUpdateAtomGitRelease(githubRelease)
  await syncAssets(githubRelease, atomgitRelease)
  console.log(`[atomgit-sync] sync completed for ${releaseTag}`)
}

if (require.main === module) {
  main().catch((error) => {
    console.error(`[atomgit-sync] ${error.stack || error.message}`)
    process.exit(1)
  })
}

module.exports = {
  buildReleasePayload,
  createProgressTracker,
  formatBytes,
  listExistingAssetNames,
  normalizeUploadHeaders,
  readUploadUrlPayload,
  redactUrl,
  validateEnvironment,
}
