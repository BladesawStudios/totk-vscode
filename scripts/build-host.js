// Publishes the C# host (host/src/TkvscHost) as a self-contained program into bin/host/<rid>, where the extension
// looks for it (src/nativeHost.ts). With no arguments it builds for this machine.
//
//   node scripts/build-host.js                 this machine
//   node scripts/build-host.js --rid linux-x64 one platform
//   node scripts/build-host.js --all           win-x64, linux-x64, osx-x64, osx-arm64
//
// It needs the .NET SDK (10) and the format libraries beside the repository (see host/Directory.Build.props,
// or pass --lib-root <folder>).
const { execFileSync } = require('child_process');
const fs = require('fs');
const path = require('path');

const root = path.resolve(__dirname, '..');
const project = path.join(root, 'host', 'src', 'TkvscHost', 'TkvscHost.csproj');
const ALL = ['win-x64', 'linux-x64', 'osx-x64', 'osx-arm64'];

function localRid() {
    const arch = process.arch === 'arm64' ? 'arm64' : 'x64';
    if (process.platform === 'win32') {
        return `win-${arch}`;
    }
    if (process.platform === 'darwin') {
        return `osx-${arch}`;
    }
    return `linux-${arch}`;
}

function option(name) {
    const at = process.argv.indexOf(name);
    return at >= 0 ? process.argv[at + 1] : undefined;
}

const rids = process.argv.includes('--all') ? ALL : [option('--rid') ?? localRid()];
const libRoot = option('--lib-root');

for (const rid of rids) {
    const out = path.join(root, 'bin', 'host', rid);
    fs.rmSync(out, { recursive: true, force: true });
    console.log(`host: publishing ${rid} to ${path.relative(root, out)}`);
    execFileSync(
        'dotnet',
        [
            'publish', project,
            '-c', 'Release',
            '-r', rid,
            '--self-contained', 'true',
            '-p:PublishSingleFile=true',
            '-p:IncludeNativeLibrariesForSelfExtract=true',
            '-p:DebugType=None',
            '-p:DebugSymbols=false',
            ...(libRoot ? [`-p:LibRoot=${libRoot}`] : []),
            '-o', out,
        ],
        { stdio: 'inherit' },
    );
}
