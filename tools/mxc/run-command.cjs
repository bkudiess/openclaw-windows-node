#!/usr/bin/env node
/**
 * tools/mxc/run-command.cjs — productized runner for OpenClaw MxcCommandRunner.
 *
 * Reads a single JSON request from stdin describing a system.run invocation
 * plus the SandboxPolicy to apply. Spawns wxc-exec via @microsoft/mxc-sdk's
 * spawnSandboxFromConfig({ usePty: false }) so stdout / stderr stay separate
 * and the exit code is reliable. Writes a single JSON envelope to stdout on
 * completion; node-side errors go to stderr.
 *
 * Wire request (matches BridgeRequest in OneShotAppContainerExecutor.cs):
 *   {
 *     "capabilityCommand": "system.run",
 *     "args": { command: "...", shell: "powershell"|"cmd"|"pwsh", args?: [], ... },
 *     "policy": { version, filesystem, network, ui, timeoutMs },
 *     "cwd": "...", "env": {...}, "timeoutMs": 30000,
 *     "wxcExecPath": "...optional override..."
 *   }
 *
 * Wire response (matches BridgeResponse):
 *   { exitCode, stdout, stderr, timedOut, durationMs, containmentTag }
 *
 * Slice 1 — system.run only. Other capabilities follow the same envelope shape
 * with capabilityCommand set appropriately and structuredResult populated.
 */

const {
  createConfigFromPolicy,
  spawnSandboxFromConfig,
  getAvailableToolsPolicy,
  getTemporaryFilesPolicy,
} = require('@microsoft/mxc-sdk');
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');

const MAX_OUTPUT_BYTES = 4 * 1024 * 1024; // mirrors C# DefaultMaxOutputBytes

async function main() {
  const req = await readJsonFromStdin();

  // Args specific to system.run. Other capabilities will have their own shapes.
  const args = req.args ?? {};
  const command = typeof args.command === 'string' ? args.command : '';
  const shell = typeof args.shell === 'string' ? args.shell : 'powershell';
  const argv = Array.isArray(args.args) ? args.args : [];

  if (!command) {
    return emit(failResponse(-1, 'Missing required arg: command', startTime));
  }

  const startTime = Date.now();

  // Compose host-discovered tool/temp paths into the policy supplied by C#.
  const tools = getAvailableToolsPolicy(process.env, { containerType: 'appcontainer' });
  const temp = getTemporaryFilesPolicy(process.env);

  const policy = mergePolicy(req.policy, tools, temp);

  let config;
  try {
    config = createConfigFromPolicy(policy, 'process');
  } catch (e) {
    return emit(failResponse(-1, `Policy invalid: ${e.message}`, startTime));
  }

  // Build the shell command line. Quote the inner command for the chosen shell.
  config.process.commandLine = buildShellCommandLine(shell, command, argv);
  if (req.cwd) config.process.cwd = req.cwd;
  if (req.env && typeof req.env === 'object') {
    config.process.env = Object.entries(req.env).map(([k, v]) => `${k}=${v}`);
  }
  config.process.timeout = req.timeoutMs > 0 ? req.timeoutMs : 30000;

  // CRITICAL: usePty:false — the @microsoft/mxc-sdk default uses node-pty which
  // conflates stdout/stderr and rounds exit codes through PTY signals. Per
  // rubber-duck #5 we want LocalCommandRunner-equivalent semantics.
  const spawnOptions = {
    usePty: false,
    debug: false,
  };
  if (req.wxcExecPath) {
    spawnOptions.executablePath = req.wxcExecPath;
  }

  let child;
  try {
    child = spawnSandboxFromConfig(config, spawnOptions);
  } catch (e) {
    return emit(failResponse(-1, `spawnSandboxFromConfig failed: ${e.message}`, startTime));
  }

  let stdout = '';
  let stderr = '';
  let stdoutBytes = 0;
  let stderrBytes = 0;
  let truncated = false;

  child.stdout?.on('data', (chunk) => {
    const text = chunk.toString();
    if (stdoutBytes + text.length > MAX_OUTPUT_BYTES) {
      stdout += text.substring(0, MAX_OUTPUT_BYTES - stdoutBytes);
      stdoutBytes = MAX_OUTPUT_BYTES;
      truncated = true;
    } else {
      stdout += text;
      stdoutBytes += text.length;
    }
  });
  child.stderr?.on('data', (chunk) => {
    const text = chunk.toString();
    if (stderrBytes + text.length > MAX_OUTPUT_BYTES) {
      stderr += text.substring(0, MAX_OUTPUT_BYTES - stderrBytes);
      stderrBytes = MAX_OUTPUT_BYTES;
      truncated = true;
    } else {
      stderr += text;
      stderrBytes += text.length;
    }
  });

  const exitCode = await new Promise((resolve) => {
    child.on('close', (code) => resolve(code ?? -1));
    child.on('error', (err) => {
      stderr += `\n[bridge] spawn error: ${err.message}`;
      resolve(-1);
    });
  });

  if (truncated) {
    stderr += '\n[bridge] output truncated at 4 MiB cap';
  }

  emit({
    exitCode,
    stdout,
    stderr,
    timedOut: false,
    durationMs: Date.now() - startTime,
    containmentTag: 'mxc',
  });
}

function mergePolicy(callerPolicy, tools, temp) {
  const fs0 = callerPolicy?.filesystem ?? {};
  return {
    version: callerPolicy?.version ?? '0.4.0-alpha',
    filesystem: {
      readonlyPaths: dedupe([
        ...(fs0.readonlyPaths ?? []),
        ...(tools?.readonlyPaths ?? []),
      ]),
      readwritePaths: dedupe([
        ...(fs0.readwritePaths ?? []),
        ...(temp?.readwritePaths ?? []),
      ]),
      deniedPaths: fs0.deniedPaths ?? [],
      clearPolicyOnExit: fs0.clearPolicyOnExit ?? true,
    },
    network: callerPolicy?.network ?? { allowOutbound: false, allowLocalNetwork: false },
    ui: callerPolicy?.ui ?? { allowWindows: false, clipboard: 'none', allowInputInjection: false },
    timeoutMs: callerPolicy?.timeoutMs,
  };
}

function dedupe(arr) {
  return Array.from(new Set(arr.filter(Boolean)));
}

function buildShellCommandLine(shell, command, argv) {
  const isCmd = shell.toLowerCase() === 'cmd';
  const argsSuffix = (argv && argv.length > 0)
    ? ' ' + argv.map((a) => quoteArg(a, isCmd)).join(' ')
    : '';
  if (isCmd) {
    return `cmd.exe /C ${command}${argsSuffix}`;
  }
  if (shell.toLowerCase() === 'pwsh') {
    return `pwsh.exe -NoProfile -NonInteractive -Command ${command}${argsSuffix}`;
  }
  return `powershell.exe -NoProfile -NonInteractive -Command ${command}${argsSuffix}`;
}

function quoteArg(arg, isCmd) {
  // Minimal quoting; matches OpenClaw.Shared/ShellQuoting semantics for cmd
  // (double-quote with escaped inner quotes) and PowerShell (single quotes).
  if (isCmd) {
    return `"${String(arg).replace(/"/g, '""')}"`;
  }
  return `'${String(arg).replace(/'/g, "''")}'`;
}

function readJsonFromStdin() {
  return new Promise((resolve, reject) => {
    const chunks = [];
    process.stdin.on('data', (c) => chunks.push(c));
    process.stdin.on('end', () => {
      try {
        resolve(JSON.parse(Buffer.concat(chunks).toString('utf8')));
      } catch (e) {
        reject(e);
      }
    });
    process.stdin.on('error', reject);
  });
}

function emit(response) {
  process.stdout.write(JSON.stringify(response));
}

function failResponse(exitCode, errorMessage, startTime = Date.now()) {
  return {
    exitCode,
    stdout: '',
    stderr: errorMessage,
    timedOut: false,
    durationMs: Math.max(0, Date.now() - startTime),
    containmentTag: 'mxc',
  };
}

main().catch((err) => {
  process.stdout.write(JSON.stringify({
    exitCode: -1,
    stdout: '',
    stderr: `[bridge] unhandled error: ${err && err.message ? err.message : String(err)}`,
    timedOut: false,
    durationMs: 0,
    containmentTag: 'mxc',
  }));
  process.exit(0); // exit 0 so the host always sees our envelope, not a Node crash
});
