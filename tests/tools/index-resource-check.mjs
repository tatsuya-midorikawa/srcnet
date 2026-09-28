import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { createReadStream } from 'node:fs';
import { mkdir, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { cpus, platform, release, totalmem } from 'node:os';
import { basename, join, resolve } from 'node:path';
import { spawn } from 'node:child_process';
import { performance } from 'node:perf_hooks';

const [baselinePath, candidatePath, fileArgument = '1000', symbolArgument = '64', runArgument = '3', reportArgument = 'artifacts/p1-resource-check'] = process.argv.slice(2);
assert(baselinePath && candidatePath, 'Expected baseline executable and candidate executable paths');
const fileCount = Number(fileArgument);
const symbolsPerFile = Number(symbolArgument);
const repetitions = Number(runArgument);
assert(Number.isInteger(fileCount) && fileCount > 0 && fileCount <= 100000);
assert(Number.isInteger(symbolsPerFile) && symbolsPerFile > 0 && symbolsPerFile <= 4096);
assert(Number.isInteger(repetitions) && repetitions > 0 && repetitions <= 30);
const reportDirectory = resolve(reportArgument);
await mkdir(reportDirectory, { recursive: true });
const working = await mkdtemp(join(reportDirectory, 'work-'));
const source = join(working, 'source');
await mkdir(source);
const corpusHash = createHash('sha256');
for (let fileIndex = 0; fileIndex < fileCount; fileIndex++) {
  const name = `source_${String(fileIndex).padStart(6, '0')}_\u65e5\u672c\u8a9e_\u4e2d\u6587_\ud55c\uad6d\uc5b4.c`;
  const lines = [`#ifdef CONFIG_P1_${fileIndex % 31}`];
  for (let symbolIndex = 0; symbolIndex < symbolsPerFile; symbolIndex++) {
    lines.push(`int function_${fileIndex}_${symbolIndex}(void) { return dependency_${fileIndex}(${symbolIndex}); }`);
  }
  lines.push('#endif', '');
  const content = lines.join('\n');
  corpusHash.update(name).update('\0').update(content);
  await writeFile(join(source, name), content);
}

function execute(executable, arguments_, timed = false) {
  return new Promise((resolveResult, reject) => {
    const macTime = timed && platform() === 'darwin';
    const command = macTime ? '/usr/bin/time' : executable;
    const args = macTime ? ['-l', executable, ...arguments_] : arguments_;
    const started = performance.now();
    const child = spawn(command, args, { stdio: ['ignore', 'pipe', 'pipe'], timeout: 300000 });
    let stdout = '';
    let stderr = '';
    child.stdout.setEncoding('utf8').on('data', data => { stdout += data; });
    child.stderr.setEncoding('utf8').on('data', data => { stderr += data; });
    child.on('error', reject);
    child.on('close', (code, signal) => resolveResult({ code, signal, stdout, stderr, elapsedMs: performance.now() - started }));
  });
}

async function logicalChecksums(output, manifest) {
  const result = {};
  for (const segment of manifest.segments.filter(segment => !segment.name.includes('.part-'))) {
    const parts = manifest.segments.filter(part => part.name === segment.name || part.name.startsWith(`${segment.name}.part-`));
    parts.sort((left, right) => left.name < right.name ? -1 : left.name > right.name ? 1 : 0);
    const hash = createHash('sha256');
    for (const part of parts) {
      for await (const bytes of createReadStream(join(output, part.name))) hash.update(bytes);
    }
    result[basename(segment.name)] = hash.digest('hex');
  }
  return result;
}

const records = [];
let expectedCounts;
let expectedChecksums;
try {
  for (let iteration = 0; iteration <= repetitions; iteration++) {
    for (const [label, executable] of [['baseline', resolve(baselinePath)], ['candidate', resolve(candidatePath)]]) {
      const output = join(working, `${label}-${iteration}`);
      const args = ['index', source, '--out', output, '--repo', 'p1-resource-check', '--tier', '2', '--jobs', '4', '--json'];
      if (label === 'candidate') args.push('--progress=always', '--memory-limit=4GiB');
      const measured = await execute(executable, args, true);
      await writeFile(join(reportDirectory, `${label}-${iteration}.stderr.txt`), measured.stderr);
      assert(measured.code === 0 || measured.code === 4, measured.stderr + measured.stdout);
      const indexed = JSON.parse(measured.stdout);
      assert.equal(indexed.complete, true);
      const verified = await execute(executable, ['verify', '--out', output, '--json']);
      assert.equal(verified.code, 0, verified.stderr + verified.stdout);
      const manifest = JSON.parse(await readFile(join(output, 'manifest.json'), 'utf8'));
      const checksums = await logicalChecksums(output, manifest);
      expectedCounts ??= manifest.counts;
      expectedChecksums ??= checksums;
      assert.deepEqual(manifest.counts, expectedCounts);
      assert.deepEqual(checksums, expectedChecksums);
      const rss = measured.stderr.match(/(\d+)\s+maximum resident set size/);
      const resources = measured.stderr.match(/resources peakRss=(\d+) temporaryBytes=(\d+) allocatedBytes=(\d+)/);
      const record = {
        label, iteration, warmup: iteration === 0, elapsedMs: measured.elapsedMs,
        peakRssBytes: rss ? Number(rss[1]) : resources ? Number(resources[1]) : null,
        peakRssSource: rss ? 'time -l' : resources ? 'Process.PeakWorkingSet64' : 'not measured',
        userSeconds: Number(measured.stderr.match(/([\d.]+)\s+user/)?.[1] ?? NaN) || null,
        systemSeconds: Number(measured.stderr.match(/([\d.]+)\s+sys/)?.[1] ?? NaN) || null,
        allocatedBytes: resources ? Number(resources[3]) : null,
        peakTemporaryBytes: resources ? Number(resources[2]) : null,
        artifactBytes: manifest.segments.reduce((sum, segment) => sum + segment.byteLength, 0),
        exitCode: measured.code,
      };
      records.push(record);
      console.log(JSON.stringify(record));
      await rm(output, { recursive: true });
    }
  }
} finally {
  const quantile = (values, fraction) => values.toSorted((left, right) => left - right)[Math.ceil(values.length * fraction) - 1] ?? null;
  const summary = {};
  for (const label of ['baseline', 'candidate']) {
    const measured = records.filter(record => record.label === label && !record.warmup);
    const elapsed = measured.map(record => record.elapsedMs);
    summary[label] = {
      samples: measured.length, p50Ms: quantile(elapsed, 0.5), p95Ms: quantile(elapsed, 0.95), p99Ms: quantile(elapsed, 0.99),
      maxRssBytes: Math.max(0, ...measured.map(record => record.peakRssBytes ?? 0)),
    };
  }
  const report = {
    environment: { platform: platform(), release: release(), architecture: process.arch, cpu: cpus()[0]?.model, memoryBytes: totalmem(), node: process.version },
    workload: { files: fileCount, symbolsPerFile, sourceSha256: corpusHash.digest('hex'), cache: 'warm; OS caches not purged', jobs: 4, tier: 2 },
    counts: expectedCounts, logicalChecksums: expectedChecksums, records, summary,
    unmeasured: ['Windows unless run there', 'cold cache', 'baseline allocation and temporary disk', 'GC counts and handles', 'Linux corpus acceptance'],
  };
  await writeFile(join(reportDirectory, 'results.json'), JSON.stringify(report, null, 2) + '\n');
  await rm(working, { recursive: true });
}
