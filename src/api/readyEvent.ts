import * as vscode from 'vscode';

/**
 * Event emitter that replays for listeners registered after {@link fire}.
 * Required because dependent addons activate only after TKVSC `activate()` resolves.
 */
export class TkvscReadyEmitter {
    private _fired = false;
    private readonly _emitter = new vscode.EventEmitter<void>();

    readonly event: vscode.Event<void> = (listener, thisArgs, disposables?) => {
        const subscription = this._emitter.event(listener, thisArgs, disposables);
        if (this._fired) {
            void Promise.resolve().then(() => listener.call(thisArgs));
        }
        return subscription;
    };

    fire(): void {
        if (!this._fired) {
            this._fired = true;
            this._emitter.fire();
        }
    }

    get fired(): boolean {
        return this._fired;
    }

    dispose(): void {
        this._emitter.dispose();
    }
}
