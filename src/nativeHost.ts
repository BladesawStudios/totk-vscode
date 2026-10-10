import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
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
 * The C# host that runs the bridge commands, or undefined when it is switched off (`TKVSC.useNativeHost`)
 * or not built for this platform. A packaged extension carries it under `bin/host/<rid>/`; a checkout
 * has the build output of `host/`.
 */
export function resolveNativeHost(extensionPath: string): string | undefined {
    if (!vscode.workspace.getConfiguration('TKVSC').get<boolean>('useNativeHost', true)) {
        logger.info('host: native host disabled by TKVSC.useNativeHost');
        return undefined;
    }

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

    logger.info('host: no native host found; using the Python bridge only');
    return undefined;
}
