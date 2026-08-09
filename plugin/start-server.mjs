#!/usr/bin/env node
// Launch the TcKit MCP server. stdout is the MCP JSON-RPC channel, so ONLY the
// server may write to it; every diagnostic here goes to stderr.
//
// Server resolution order:
//   1. TCKIT_SERVER_EXE      - explicit override / offline pre-placement
//   2. cached prebuilt for this plugin version
//   3. build from source, if the .NET 8 SDK is present (the contributor path)
//   4. download the matching self-contained release binary from GitHub, cache it
// If none of those work, print how to fix it and exit non-zero.
//
// This is JavaScript rather than PowerShell because a plugin's .mcp.json carries a single
// `command` for every platform, with no OS conditional. PowerShell is not present on a stock
// Linux box, so a `powershell` command made the plugin Windows-only. Node is what Claude Code
// itself runs on. Where node is not on PATH, set TCKIT_SERVER_EXE and the launcher is bypassed.

import { spawn, spawnSync } from 'node:child_process';
import { createWriteStream } from 'node:fs';
import { chmod, mkdir, readFile, rename, rm, stat } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { homedir } from 'node:os';
import { dirname, join } from 'node:path';
import { Readable } from 'node:stream';
import { pipeline } from 'node:stream/promises';
import { fileURLToPath } from 'node:url';

const err = (message) => process.stderr.write(`${message}\n`);

const pluginRoot = dirname(fileURLToPath(import.meta.url));
const repoRoot = join(pluginRoot, '..');

const isWindows = process.platform === 'win32';

/** The release asset for this host, or null where we publish none. */
function assetName() {
  if (process.arch !== 'x64') {
    return null;
  }

  if (isWindows) {
    return 'tckit-server-win-x64.exe';
  }

  return process.platform === 'linux' ? 'tckit-server-linux-x64' : null;
}

/**
 * Where a downloaded binary is kept. Per-user and per-version, so upgrading the plugin fetches
 * a matching server rather than silently reusing the previous one.
 */
function cacheDir(version) {
  const base = isWindows
    ? process.env.LOCALAPPDATA ?? join(homedir(), 'AppData', 'Local')
    : process.env.XDG_CACHE_HOME ?? join(homedir(), '.cache');

  return join(base, 'tckit', 'bin', version);
}

async function exists(path) {
  try {
    await stat(path);
    return true;
  } catch {
    return false;
  }
}

/** The version pins which release asset we fetch; read it from the plugin manifest. */
async function pluginVersion() {
  try {
    const manifest = await readFile(join(pluginRoot, '.claude-plugin', 'plugin.json'), 'utf8');
    return JSON.parse(manifest).version ?? '0.0.0';
  } catch {
    return '0.0.0';
  }
}

function hasDotnetSdk() {
  const probe = spawnSync('dotnet', ['--version'], { stdio: 'ignore' });
  return probe.status === 0;
}

/**
 * Build from source. The server multi-targets, so the framework has to be named: the windows
 * flavour carries the COM lanes, the plain net8.0 flavour is what runs anywhere else.
 */
async function buildFromSource() {
  const project = join(repoRoot, 'dotnet', 'src', 'TcKit.Server', 'TcKit.Server.csproj');
  if (!(await exists(project))) {
    return null;
  }

  const framework = isWindows ? 'net8.0-windows' : 'net8.0';
  const built = join(
    repoRoot, 'dotnet', 'src', 'TcKit.Server', 'bin', 'Release', framework,
    isWindows ? 'TcKit.Server.exe' : 'TcKit.Server');

  err('TcKit: .NET 8 SDK found; building server from source (incremental)...');
  const build = spawnSync(
    'dotnet',
    ['build', project, '-c', 'Release', '-f', framework, '--nologo', '-v', 'q'],
    { stdio: ['ignore', 'pipe', 'pipe'], encoding: 'utf8' });

  // Build chatter must not reach stdout, which belongs to the JSON-RPC channel.
  for (const stream of [build.stdout, build.stderr]) {
    if (stream) {
      err(stream.trimEnd());
    }
  }

  if (build.status === 0 && (await exists(built))) {
    return built;
  }

  err('TcKit: build failed; falling back to the prebuilt download.');
  return null;
}

async function sha256(path) {
  const hash = createHash('sha256');
  await pipeline((await import('node:fs')).createReadStream(path), hash);
  return hash.digest('hex');
}

/** Download the self-contained release binary, verifying the published checksum when present. */
async function download(version, asset, target) {
  const base = `https://github.com/georgeturneruk/tckit/releases/download/v${version}`;
  err(`TcKit: no local server available; downloading prebuilt v${version} (~75 MB, one time)...`);

  const temporary = `${target}.download`;
  try {
    await mkdir(dirname(target), { recursive: true });

    const response = await fetch(`${base}/${asset}`, { redirect: 'follow' });
    if (!response.ok || !response.body) {
      throw new Error(`HTTP ${response.status} fetching ${asset}`);
    }

    await pipeline(Readable.fromWeb(response.body), createWriteStream(temporary));

    let expected = null;
    try {
      const sums = await fetch(`${base}/${asset}.sha256`, { redirect: 'follow' });
      if (sums.ok) {
        expected = (await sums.text()).trim().split(/\s+/)[0].toLowerCase();
      }
    } catch {
      // A missing checksum asset is not fatal; the download still stands.
    }

    if (expected) {
      const actual = await sha256(temporary);
      if (actual !== expected) {
        await rm(temporary, { force: true });
        throw new Error(`checksum mismatch (expected ${expected}, got ${actual})`);
      }
    }

    await rename(temporary, target);
    if (!isWindows) {
      // A downloaded file is not executable on POSIX until it is said to be.
      await chmod(target, 0o755);
    }

    err(`TcKit: cached at ${target}`);
    return target;
  } catch (problem) {
    await rm(temporary, { force: true });
    err(`TcKit: download failed: ${problem.message}`);
    return null;
  }
}

async function resolveServer() {
  const override = process.env.TCKIT_SERVER_EXE;
  if (override && (await exists(override))) {
    return override;
  }

  const version = await pluginVersion();
  const asset = assetName();
  const cached = asset ? join(cacheDir(version), asset) : null;

  if (cached && (await exists(cached))) {
    return cached;
  }

  if (hasDotnetSdk()) {
    const built = await buildFromSource();
    if (built) {
      return built;
    }
  }

  if (!asset) {
    err(`TcKit: no prebuilt server is published for ${process.platform}-${process.arch}.`);
    return null;
  }

  return download(version, asset, cached);
}

const server = await resolveServer();

if (!server) {
  const version = await pluginVersion();
  err('');
  err('TcKit: could not obtain the server. Pick one:');
  err('  - Install the .NET 8 SDK, then relaunch: https://dotnet.microsoft.com/download/dotnet/8.0');
  err(`  - Or download the server from https://github.com/georgeturneruk/tckit/releases/tag/v${version}`);
  err('    and set the TCKIT_SERVER_EXE environment variable to its full path.');
  process.exit(1);
}

// stdio is inherited so the server owns the JSON-RPC channel directly; this process only waits.
const child = spawn(server, process.argv.slice(2), { stdio: 'inherit' });

for (const signal of ['SIGINT', 'SIGTERM']) {
  process.on(signal, () => child.kill(signal));
}

child.on('error', (problem) => {
  err(`TcKit: failed to start ${server}: ${problem.message}`);
  process.exit(1);
});

child.on('exit', (code, signal) => {
  process.exit(signal ? 1 : code ?? 0);
});
