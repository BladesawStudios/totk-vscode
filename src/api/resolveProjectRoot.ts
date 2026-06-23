import type { TkvscTreeItemLike } from './types';
import { resolveProjectRootFromTreeItem as resolveFromAdapters } from '../projectAdapters/registry';

function asTreeItem(item: unknown): TkvscTreeItemLike | undefined {
    if (!item || typeof item !== 'object') {
        return undefined;
    }
    const candidate = item as TkvscTreeItemLike;
    if (!candidate.resourceUri?.fsPath) {
        return undefined;
    }
    return candidate;
}

/**
 * Extract a project folder path from a projects-tree context-menu item.
 * Supports the context values addons typically hook (`archiveRoot`, etc.).
 */
export function resolveProjectRoot(item: unknown): string | undefined {
    const treeItem = asTreeItem(item);
    if (!treeItem?.resourceUri) {
        return undefined;
    }

    return resolveFromAdapters(treeItem.contextValue, treeItem.resourceUri.fsPath);
}
