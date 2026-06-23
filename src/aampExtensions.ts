import { getFormatRegistry } from './formatRegistry';

/** @deprecated Builtin formats load via {@link initFormatRegistry} / {@link initAddonRegistries}. */
export function initAampExtensions(extensionPath: string): void {
    getFormatRegistry().initBuiltin(extensionPath);
}

export function getAampExtensions(): Set<string> {
    return getFormatRegistry().getAampExtensions();
}

export function isAampExtension(filePath: string): boolean {
    return getFormatRegistry().isAampExtension(filePath);
}
