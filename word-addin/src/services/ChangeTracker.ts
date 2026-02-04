/**
 * ChangeTracker - Detects user changes and sends them to backend as logical patches.
 *
 * Uses polling + debouncing to detect changes without overwhelming the system.
 * Computes a diff between snapshots and reports semantic changes.
 */

import type { DocumentContent, UserChangeReport, LogicalChange } from '../types';
import { wordService } from './WordService';

interface ChangeTrackerConfig {
  backendUrl: string;
  sessionId: string;
  debounceMs: number;
  pollIntervalMs: number;
  enabled: boolean;
}

type ChangeHandler = (changes: LogicalChange[]) => void;

export class ChangeTracker {
  private config: ChangeTrackerConfig;
  private lastSnapshot: DocumentContent | null = null;
  private pollTimer: number | null = null;
  private debounceTimer: number | null = null;
  private changeHandlers: ChangeHandler[] = [];
  private isPolling = false;

  constructor(config: Partial<ChangeTrackerConfig> = {}) {
    this.config = {
      backendUrl: 'http://localhost:5300',
      sessionId: this.generateSessionId(),
      debounceMs: 500,
      pollIntervalMs: 2000,
      enabled: true,
      ...config,
    };
  }

  /**
   * Get the current session ID.
   */
  get sessionId(): string {
    return this.config.sessionId;
  }

  /**
   * Start tracking changes.
   */
  async start(): Promise<void> {
    if (this.pollTimer) return;

    console.log('[ChangeTracker] Starting with session:', this.config.sessionId);

    // Take initial snapshot
    this.lastSnapshot = await wordService.getDocumentContent();

    // Start polling
    this.pollTimer = window.setInterval(() => {
      this.checkForChanges();
    }, this.config.pollIntervalMs);
  }

  /**
   * Stop tracking changes.
   */
  stop(): void {
    if (this.pollTimer) {
      clearInterval(this.pollTimer);
      this.pollTimer = null;
    }
    if (this.debounceTimer) {
      clearTimeout(this.debounceTimer);
      this.debounceTimer = null;
    }
    console.log('[ChangeTracker] Stopped');
  }

  /**
   * Register a handler for detected changes.
   */
  onChanges(handler: ChangeHandler): () => void {
    this.changeHandlers.push(handler);
    return () => {
      this.changeHandlers = this.changeHandlers.filter((h) => h !== handler);
    };
  }

  /**
   * Force check for changes (useful after LLM applies patches).
   */
  async forceCheck(): Promise<void> {
    // Clear debounce
    if (this.debounceTimer) {
      clearTimeout(this.debounceTimer);
      this.debounceTimer = null;
    }

    // Update snapshot without reporting changes
    this.lastSnapshot = await wordService.getDocumentContent();
  }

  /**
   * Get the current document snapshot.
   */
  async getCurrentSnapshot(): Promise<DocumentContent> {
    return wordService.getDocumentContent();
  }

  // --- Private methods ---

  private async checkForChanges(): Promise<void> {
    if (!this.config.enabled || this.isPolling) return;

    this.isPolling = true;

    try {
      const currentSnapshot = await wordService.getDocumentContent();

      // Quick check: has the text changed?
      if (this.lastSnapshot && currentSnapshot.text === this.lastSnapshot.text) {
        return;
      }

      // Debounce the change processing
      if (this.debounceTimer) {
        clearTimeout(this.debounceTimer);
      }

      this.debounceTimer = window.setTimeout(() => {
        this.processChanges(currentSnapshot);
      }, this.config.debounceMs);
    } catch (error) {
      console.error('[ChangeTracker] Error checking for changes:', error);
    } finally {
      this.isPolling = false;
    }
  }

  private async processChanges(currentSnapshot: DocumentContent): Promise<void> {
    if (!this.lastSnapshot) {
      this.lastSnapshot = currentSnapshot;
      return;
    }

    // Report to backend
    const report: UserChangeReport = {
      session_id: this.config.sessionId,
      before: this.lastSnapshot,
      after: currentSnapshot,
    };

    try {
      const response = await fetch(`${this.config.backendUrl}/api/changes/report`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(report),
      });

      if (response.ok) {
        const result = await response.json();
        const changes = result.changes as LogicalChange[];

        if (changes.length > 0) {
          console.log('[ChangeTracker] Detected changes:', result.summary);

          // Notify handlers
          for (const handler of this.changeHandlers) {
            try {
              handler(changes);
            } catch (error) {
              console.error('[ChangeTracker] Handler error:', error);
            }
          }
        }
      }
    } catch (error) {
      console.error('[ChangeTracker] Failed to report changes:', error);
    }

    // Update snapshot
    this.lastSnapshot = currentSnapshot;
  }

  private generateSessionId(): string {
    return Math.random().toString(36).slice(2, 14);
  }
}

// Export singleton instance
export const changeTracker = new ChangeTracker();
