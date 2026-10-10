import * as fs from 'fs';
import * as path from 'path';
import { logger } from './logger';

const EXECUTABLE = process.platform === 'win32' ? 'tkvsc-host.exe' : 'tkvsc-host';

/** The .NET runtime identifier of this machine, as the host is published per platform. */
function runtimeIdentifier(): string | undefined {
    const arch = process.arch === 'x64' ? 'x64' : process.arch === 'arm64' ? 'arm64' : undefined;
    if (!arch) {
        return undefined;
    }
    switch (process.platform) {
        case 'win32':
            return `win-${arch}`;
        case 'linux':
            return `linux-${arch}`;
        case 'darwin':
            return `osx-${arch}`;
        default:
            return undefined;
    }
}

/**
 * The C# host that runs the bridge commands, or undefined when none is built for this platform. A packaged
 * extension carries it under `bin/host/<rid>/`; a checkout has the build output of `host/`.
 */
export function resolveNativeHost(extensionPath: string): string | undefined {
    const rid = runtimeIdentifier();
    const candidates = [
        ...(rid ? [path.join(extensionPath, 'bin', 'host', rid, EXECUTABLE)] : []),
        path.join(extensionPath, 'host', 'src', 'TkvscHost', 'bin', 'Release', 'net10.0', EXECUTABLE),
        path.join(extensionPath, 'host', 'src', 'TkvscHost', 'bin', 'Debug', 'net10.0', EXECUTABLE),
    ];

    for (const candidate of candidates) {
        if (fs.existsSync(candidate)) {
            logger.info(`host: using ${candidate}`);
            return candidate;
        }
    }

    logger.error('host: no tkvsc-host found for this platform');
    return undefined;
}
