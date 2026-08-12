import * as vscode from 'vscode';

export interface TkprojContributor {
    Author: string;
    Contribution: string;
}

export interface TkprojData {
    Mod: {
        Name: string;
        Author: string;
        Version: string;
        Description: string;
        Id: string;
        Contributors: TkprojContributor[];
        Dependencies: string[];
        Thumbnail?: { ThumbnailPath?: string };
    };
    Flags?: Record<string, unknown>;
    [key: string]: unknown;
}

/** Author written into new `.tkproj` files, from `TKVSC.defaultProjectAuthor`. */
export function getDefaultProjectAuthor(): string {
    const configured = vscode.workspace
        .getConfiguration('TKVSC')
        .get<string>('defaultProjectAuthor', '')
        .trim();
    return configured || 'Unknown';
}

export function createDefaultTkprojData(projectName = 'New Project'): TkprojData {
    return {
        Mod: {
            Name: projectName,
            Author: getDefaultProjectAuthor(),
            Version: '1.0.0',
            Description: '',
            Id: generateUlidNumber(),
            Contributors: [],
            Dependencies: [],
        },
        Flags: {
            TrackRemovedRsDbEntries: false,
        },
    };
}

export function generateUlidNumber(): string {
    let id = '';
    for (let i = 0; i < 26; i++) {
        id += Math.floor(Math.random() * 10).toString();
    }
    if (id === '00000000000000000000000001') {
        return generateUlidNumber();
    }
    return id;
}
