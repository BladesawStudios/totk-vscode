// Builds a platform-specific VSIX: the C# host for one platform plus the bundled extension.
//
//   node scripts/package-vsix.js [<vsce target> | --all] [--out <dir>]
//
// <vsce target> is one of win32-x64, linux-x64, darwin-x64, darwin-arm64 (default: this machine); --all does each.
// The version inside the VSIX gets the short commit hash appended (1.0.1-abc1234), so a dev build installs over the
// last one without an uninstall. The file name keeps the plain version. --release leaves the version as it is.
// It publishes only that platform's host into bin/host and passes --target to vsce, so each VSIX holds one ~76 MB program.
// After --all, bin/host is rebuilt for this machine so the extension still runs from the checkout.
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

const all = process.argv.includes('--all');
const positional = process.argv.slice(2).filter((a, i, args) => !a.startsWith('--') && args[i - 1] !== '--out');
const targets = all ? Object.keys(RID_BY_TARGET) : [positional[0] ?? localTarget()];
for (const target of targets) {
    if (!RID_BY_TARGET[target]) {
        console.error(`Unknown target "${target}". Use one of: ${Object.keys(RID_BY_TARGET).join(', ')}`);
        process.exit(1);
    }
}
const outAt = process.argv.indexOf('--out');
const outDir = path.resolve(root, outAt >= 0 ? process.argv[outAt + 1] : 'out');
const release = process.argv.includes('--release');

const run = (command, args) => execFileSync(command, args, { cwd: root, stdio: 'inherit' });
const manifestPath = path.join(root, 'package.json');
const manifestText = fs.readFileSync(manifestPath, 'utf8');
const version = JSON.parse(manifestText).version;
fs.mkdirSync(outDir, { recursive: true });

function commitSuffix() {
    try {
        const sha = execFileSync('git', ['rev-parse', '--short', 'HEAD'], { cwd: root, encoding: 'utf8' }).trim();
        // A semver pre-release part that is all digits can't start with 0, so prefix those like git describe does.
        return /^\d+$/.test(sha) ? `g${sha}` : sha;
    } catch {
        console.warn('git rev-parse failed, so the build keeps the plain version');
        return undefined;
    }
}

// vsce reads the version from package.json, so it is rewritten for the build and put back byte for byte after.
const suffix = release ? undefined : commitSuffix();
const restoreManifest = () => fs.writeFileSync(manifestPath, manifestText);
if (suffix) {
    const buildVersion = `${version}-${suffix}`;
    fs.writeFileSync(manifestPath, manifestText.replace(/("version"\s*:\s*")[^"]*(")/, `$1${buildVersion}$2`));
    process.on('SIGINT', () => { restoreManifest(); process.exit(130); });
    console.log(`building version ${buildVersion}`);
}

try {
    for (const target of targets) {
        fs.rmSync(path.join(root, 'bin', 'host'), { recursive: true, force: true });
        run(process.execPath, ['scripts/build-host.js', '--rid', RID_BY_TARGET[target]]);

        const vsix = path.join(outDir, `totk-vscode-${target}-${version}.vsix`);
        // vsce runs the prepublish script (type check, lint, production bundle) itself.
        run(process.execPath, [require.resolve('@vscode/vsce/vsce'), 'package', '--target', target, '--out', vsix]);
        console.log(`wrote ${path.relative(root, vsix)}`);
    }
} finally {
    if (suffix) {
        restoreManifest();
    }
}

if (all) {
    fs.rmSync(path.join(root, 'bin', 'host'), { recursive: true, force: true });
    run(process.execPath, ['scripts/build-host.js']);
}
