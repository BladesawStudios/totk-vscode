import * as vscode from 'vscode';

const FONT_FILE_PATTERN = /\.(?:bfotf|bfttf|ttf|otf)(?:\.zs)?$/i;

export function isFontFilePath(filePath: string): boolean {
    return FONT_FILE_PATTERN.test(filePath.replace(/\\/g, '/'));
}

export function isTotkEncryptedFontTarget(filePath: string): boolean {
    return /\.(?:bfotf|bfttf)(?:\.zs)?$/i.test(filePath.replace(/\\/g, '/'));
}

export const FONT_IMPORT_FILTERS: Record<string, string[]> = {
    Fonts: ['ttf', 'otf', 'bfotf', 'bfttf'],
};
