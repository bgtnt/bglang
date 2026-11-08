import { readFileSync, statSync } from 'node:fs';
import { join, posix } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('../', import.meta.url));
const { files } = JSON.parse(readFileSync(join(root, 'packaging/public-source-allowlist.v1.json'), 'utf8'));
const exported = new Set(files);
const markdown = files.filter(file => file.endsWith('.md'));
const inlineLink = /(?<!\\)\]\(\s*(?:<([^<>]*)>|((?:\\[()]|\([^()]*\)|[^\s()])+))(?:[ \t]+(?:"[^"]*"|'[^']*'|\([^()]*\)))?[ \t]*\)/g;

function isPublicTarget(markdownFile, destination) {
  if (/^(?:https?:|mailto:|#)/i.test(destination)) return true;
  const path = destination.split(/[?#]/, 1)[0];
  if (!path) return true;

  let decoded;
  try {
    decoded = decodeURIComponent(path.replace(/\\([()])/g, '$1'));
  } catch {
    return false;
  }
  if (decoded.startsWith('/') || /[\\:\0]/.test(decoded)) return false;
  const target = posix.join(posix.dirname(markdownFile), decoded).replace(/\/$/, '');
  if (target === '..' || target.startsWith('../')) return false;

  const item = statSync(join(root, target), { throwIfNoEntry: false });
  if (exported.has(target)) return item?.isFile() === true;
  return item?.isDirectory() === true && (target === '.' || files.some(file => file.startsWith(target + '/')));
}

for (const file of markdown) {
  const lines = readFileSync(join(root, file), 'utf8').split(/\r?\n/);
  let fence;
  for (const [index, line] of lines.entries()) {
    const marker = /^ {0,3}(\x60{3,}|~{3,})(.*)$/.exec(line);
    if (fence) {
      if (marker && marker[1][0] === fence[0] && marker[1].length >= fence.length && !marker[2].trim()) {
        fence = undefined;
      }
      continue;
    }
    if (marker) {
      fence = marker[1];
      continue;
    }

    // Check relative inline destinations in the public export, omitting Markdown code spans.
    const prose = line.replace(/(?<!\x60)(\x60+)(?!\x60).*?(?<!\x60)\1(?!\x60)/g, '');
    for (const link of prose.matchAll(inlineLink)) {
      const destination = link[1] ?? link[2];
      if (!isPublicTarget(file, destination)) {
        console.error(file + ':' + (index + 1) + ': ' + destination);
        process.exitCode = 1;
      }
    }
  }
}

if (!process.exitCode) console.log('Relative links checked in ' + markdown.length + ' public Markdown files.');
