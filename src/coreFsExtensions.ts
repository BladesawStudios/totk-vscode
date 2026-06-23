import * as fs from 'fs';
import { getFormatRegistry } from './formatRegistry';

export function initCoreFsExtensions(extensionPath: string): void {
    getFormatRegistry().initBuiltin(extensionPath);
}

export function getHandlerType(filePath: string): string | undefined {
    return getFormatRegistry().getHandlerType(filePath);
}

export function isCoreExtension(filePath: string): boolean {
    return getFormatRegistry().isCoreExtension(filePath);
}

export function getCoreExtensions(): Record<string, string> {
    return getFormatRegistry().getCoreExtensionMap();
}
