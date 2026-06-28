/** Keep in sync with python/archive_resolve.py decrypt_bfttf */

const ENCRYPTED_MAGICS = new Map<number, number>([
    [0xd99b871a, 2785117442],
    [0x36f81a1e, 1231165446],
    [0xf368dec1, 2364726489],
]);

const OPEN_FONT_MAGICS = [
    0x4f54544f, // OTTO
    0x00010000, // TTF
    0x774f4646, // wOFF
    0x774f4632, // wOF2
    0x74727565, // true
    0x74746366, // ttcf
];

const POSSIBLE_MAGICS = OPEN_FONT_MAGICS;

function readU32Be(data: Uint8Array, offset: number): number {
    return (
        (data[offset] << 24) |
        (data[offset + 1] << 16) |
        (data[offset + 2] << 8) |
        data[offset + 3]
    );
}

export function isOpenFont(data: Uint8Array): boolean {
    if (data.length < 4) {
        return false;
    }
    return OPEN_FONT_MAGICS.includes(readU32Be(data, 0));
}

export function decryptBfttf(data: Uint8Array): Uint8Array {
    if (data.length <= 8) {
        return data;
    }

    const magic = readU32Be(data, 0);
    const baseKey = ENCRYPTED_MAGICS.get(magic);
    if (baseKey === undefined) {
        return data;
    }

    const firstChunk = readU32Be(data, 8);
    const fileSizeVal = data.length - 8;
    const derivedKey = baseKey ^ fileSizeVal;

    let keyVal: number;
    if (POSSIBLE_MAGICS.includes(firstChunk ^ derivedKey)) {
        keyVal = derivedKey;
    } else if (POSSIBLE_MAGICS.includes(firstChunk ^ baseKey)) {
        keyVal = baseKey;
    } else {
        keyVal = baseKey;
        for (const possibleMagic of POSSIBLE_MAGICS) {
            if (((firstChunk ^ possibleMagic) ^ baseKey) < 0x0fffffff) {
                keyVal = firstChunk ^ possibleMagic;
                break;
            }
        }
    }

    const out = new Uint8Array(data.length - 8);
    const keyBytes = new Uint8Array(4);
    new DataView(keyBytes.buffer).setUint32(0, keyVal >>> 0, false);
    for (let i = 8; i < data.length; i++) {
        out[i - 8] = data[i] ^ keyBytes[i % 4];
    }
    return out;
}

export function isTotkFontPath(fsPath: string): boolean {
    const lower = fsPath.replace(/\\/g, '/').toLowerCase();
    return /\.(?:bfotf|bfttf)(?:\.zs)?$/i.test(lower);
}

/** Decrypt TotK font containers when needed; no-op for plain OpenType/TrueType bytes. */
export function prepareFontBytes(data: Uint8Array, fsPath?: string): Uint8Array {
    if (isOpenFont(data)) {
        return data;
    }

    const decrypted = decryptBfttf(data);
    if (isOpenFont(decrypted)) {
        return decrypted;
    }

    if (fsPath && isTotkFontPath(fsPath)) {
        return decrypted;
    }

    return data;
}

export function getFontFaceCss(data: Uint8Array, base64Font: string): string {
    let mime = 'font/otf';
    let format = 'opentype';
    if (data.length >= 4) {
        const magic = readU32Be(data, 0);
        if (magic === 0x00010000) {
            mime = 'font/ttf';
            format = 'truetype';
        } else if (magic === 0x774f4646) {
            mime = 'font/woff';
            format = 'woff';
        } else if (magic === 0x774f4632) {
            mime = 'font/woff2';
            format = 'woff2';
        }
    }

    return `@font-face {
            font-family: 'PreviewFont';
            src: url(data:${mime};base64,${base64Font}) format('${format}');
        }`;
}

export function getFontFormatLabel(fsPath: string, data: Uint8Array): string {
    const lower = fsPath.replace(/\\/g, '/').toLowerCase();
    if (lower.endsWith('.bfotf') || lower.endsWith('.bfttf')) {
        return 'TotK Font';
    }
    if (lower.endsWith('.ttf')) {
        return 'TrueType Font';
    }
    if (lower.endsWith('.otf')) {
        return 'OpenType Font';
    }
    if (data.length >= 4) {
        const magic = readU32Be(data, 0);
        if (magic === 0x00010000) {
            return 'TrueType Font';
        }
        if (magic === 0x4f54544f) {
            return 'OpenType Font';
        }
    }
    return 'Font';
}
