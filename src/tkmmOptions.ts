/**
 * @deprecated Use {@link TkmmProjectAdapter} via {@link detectProjectAdapter} / project adapter registry.
 */
import * as path from 'path';
import type * as vscode from 'vscode';
import { getBuiltinTkmmAdapter } from './projectAdapters/tkmmAdapter';
import type { ProjectOptionRef } from './projectAdapters/types';

export type TkmmOptionRef = ProjectOptionRef;

const tkmm = () => getBuiltinTkmmAdapter();

export function getActiveTkmmOption(
    context: vscode.ExtensionContext,
    projectRoot: string,
): TkmmOptionRef | undefined {
    return tkmm().getActiveOption(context, projectRoot);
}

export async function setActiveTkmmOption(
    context: vscode.ExtensionContext,
    projectRoot: string,
    group?: string,
    option?: string,
): Promise<void> {
    await tkmm().setActiveOption(context, projectRoot, group, option);
}

export async function listTkmmOptionGroups(projectRoot: string): Promise<string[]> {
    return tkmm().listOptionGroups(projectRoot);
}

export async function listTkmmOptions(projectRoot: string, group: string): Promise<string[]> {
    return tkmm().listOptions(projectRoot, group);
}

export async function createTkmmOptionGroup(projectRoot: string, groupName: string): Promise<string> {
    await tkmm().createOptionGroup(projectRoot, groupName);
    return path.join(projectRoot, 'options', groupName);
}

export async function createTkmmOption(
    projectRoot: string,
    groupName: string,
    optionName: string,
): Promise<string> {
    await tkmm().createOption(projectRoot, groupName, optionName);
    return path.join(projectRoot, 'options', groupName, optionName);
}

export async function askForTkmmOption(
    projectRoot: string,
): Promise<TkmmOptionRef | 'BASE_PROJECT' | 'BACK' | undefined> {
    return tkmm().askForOption(projectRoot);
}
