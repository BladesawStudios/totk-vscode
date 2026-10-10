// Builds a platform-specific VSIX: the C# host for one platform plus the bundled extension.
//
//   node scripts/package-vsix.js [<vsce target>] [--out <dir>]
//
// <vsce target> is one of win32-x64, linux-x64, darwin-x64, darwin-arm64 (default: this machine).
// It publishes only that platform's host into bin/host and passes --target to vsce, so the VSIX holds one ~76 MB program.
const { execFileSync } = require('child_process');
const fs = require('fs');
const path = require('path');

const root = path.resolve(__dirname, '..');
const RID_BY_TARGET = {
    'win32-x64': 'win-x64',
    'linux-x64': 'linux-x64',
    'darwin-x64': 'osx-x64',
    'darwin-arm64': 'osx-arm64',
};

function localTarget() {
    const arch = process.arch === 'arm64' ? 'arm64' : 'x64';
    return `${process.platform}-${arch}`;
}

const positional = process.argv.slice(2).filter((a, i, all) => !a.startsWith('--') && all[i - 1] !== '--out');
const target = positional[0] ?? localTarget();
const rid = RID_BY_TARGET[target];
if (!rid) {
    console.error(`Unknown target "${target}". Use one of: ${Object.keys(RID_BY_TARGET).join(', ')}`);
    process.exit(1);
}
const outAt = process.argv.indexOf('--out');
const outDir = path.resolve(root, outAt >= 0 ? process.argv[outAt + 1] : 'out');

const run = (command, args) => execFileSync(command, args, { cwd: root, stdio: 'inherit' });

fs.rmSync(path.join(root, 'bin', 'host'), { recursive: true, force: true });
run(process.execPath, ['scripts/build-host.js', '--rid', rid]);

fs.mkdirSync(outDir, { recursive: true });
const version = JSON.parse(fs.readFileSync(path.join(root, 'package.json'), 'utf8')).version;
const vsix = path.join(outDir, `totk-vscode-${target}-${version}.vsix`);
// vsce runs the prepublish script (type check, lint, production bundle) itself.
run(process.execPath, [require.resolve('@vscode/vsce/vsce'), 'package', '--target', target, '--out', vsix]);
console.log(`wrote ${path.relative(root, vsix)}`);
