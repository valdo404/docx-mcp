/**
 * LlmClient - Handles communication with the LLM backend via SSE.
 *
 * Streams patches from Claude and applies them to Word in real-time.
 */

import type { LlmEditRequest, LlmStreamEvent, LlmPatch, DocumentContent } from '../types';
import { wordService } from './WordService';
import { changeTracker } from './ChangeTracker';

interface LlmClientConfig {
  backendUrl: string;
  autoApplyPatches: boolean;
  onContent?: (content: string) => void;
  onPatch?: (patch: LlmPatch, applied: boolean) => void;
  onDone?: (stats: LlmStreamEvent['stats']) => void;
  onError?: (error: string) => void;
}

export class LlmClient {
  private config: LlmClientConfig;
  private abortController: AbortController | null = null;
  private pendingPatches: LlmPatch[] = [];
  private fullContent = '';

  constructor(config: Partial<LlmClientConfig> = {}) {
    this.config = {
      backendUrl: 'http://localhost:5300',
      autoApplyPatches: true,
      ...config,
    };
  }

  /**
   * Send an instruction to the LLM and stream patches back.
   */
  async streamEdit(instruction: string): Promise<void> {
    // Cancel any existing stream
    this.cancel();

    this.abortController = new AbortController();
    this.pendingPatches = [];
    this.fullContent = '';

    // Get current document state
    const document = await wordService.getDocumentContent();

    const request: LlmEditRequest = {
      session_id: changeTracker.sessionId,
      instruction,
      document,
      recent_changes: [], // Will be filled by backend from stored history
    };

    console.log('[LlmClient] Starting stream for:', instruction.slice(0, 50));

    try {
      const response = await fetch(`${this.config.backendUrl}/api/llm/stream`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(request),
        signal: this.abortController.signal,
      });

      if (!response.ok) {
        throw new Error(`HTTP ${response.status}: ${await response.text()}`);
      }

      if (!response.body) {
        throw new Error('No response body');
      }

      await this.processStream(response.body);
    } catch (error) {
      if (error instanceof Error && error.name === 'AbortError') {
        console.log('[LlmClient] Stream cancelled');
        return;
      }

      const message = error instanceof Error ? error.message : String(error);
      console.error('[LlmClient] Stream error:', message);
      this.config.onError?.(message);
    }
  }

  /**
   * Cancel the current stream.
   */
  cancel(): void {
    if (this.abortController) {
      this.abortController.abort();
      this.abortController = null;
    }
  }

  /**
   * Get pending patches that haven't been applied yet.
   */
  getPendingPatches(): LlmPatch[] {
    return [...this.pendingPatches];
  }

  /**
   * Apply all pending patches to the document.
   */
  async applyPendingPatches(): Promise<{ applied: number; errors: string[] }> {
    const patches = [...this.pendingPatches];
    this.pendingPatches = [];

    const result = await wordService.applyPatches(patches);

    // Update change tracker to avoid detecting our own changes
    await changeTracker.forceCheck();

    return result;
  }

  /**
   * Get the full content streamed so far.
   */
  getFullContent(): string {
    return this.fullContent;
  }

  // --- Private methods ---

  private async processStream(body: ReadableStream<Uint8Array>): Promise<void> {
    const reader = body.getReader();
    const decoder = new TextDecoder();
    let buffer = '';

    try {
      while (true) {
        const { done, value } = await reader.read();

        if (done) {
          break;
        }

        buffer += decoder.decode(value, { stream: true });

        // Process complete SSE events
        const events = this.parseSSEEvents(buffer);
        buffer = events.remaining;

        for (const event of events.complete) {
          await this.handleEvent(event);
        }
      }
    } finally {
      reader.releaseLock();
    }
  }

  private parseSSEEvents(buffer: string): { complete: SSEEvent[]; remaining: string } {
    const events: SSEEvent[] = [];
    const lines = buffer.split('\n');
    let currentEvent: Partial<SSEEvent> = {};
    let remaining = '';

    for (let i = 0; i < lines.length; i++) {
      const line = lines[i];

      // Check if we have an incomplete event at the end
      if (i === lines.length - 1 && !line.endsWith('\n') && buffer.indexOf('\n\n') === -1) {
        remaining = lines.slice(i).join('\n');
        break;
      }

      if (line.startsWith('event:')) {
        currentEvent.type = line.slice(6).trim();
      } else if (line.startsWith('data:')) {
        currentEvent.data = line.slice(5).trim();
      } else if (line === '' && currentEvent.type && currentEvent.data) {
        events.push(currentEvent as SSEEvent);
        currentEvent = {};
      }
    }

    return { complete: events, remaining };
  }

  private async handleEvent(sse: SSEEvent): Promise<void> {
    let event: LlmStreamEvent;

    try {
      event = JSON.parse(sse.data) as LlmStreamEvent;
    } catch {
      console.warn('[LlmClient] Failed to parse SSE data:', sse.data);
      return;
    }

    switch (event.type) {
      case 'content':
        if (event.content) {
          this.fullContent += event.content;
          this.config.onContent?.(event.content);
        }
        break;

      case 'patch':
        if (event.patch) {
          console.log('[LlmClient] Received patch:', event.patch.op, event.patch.path);

          if (this.config.autoApplyPatches) {
            const result = await wordService.applyPatch(event.patch);
            this.config.onPatch?.(event.patch, result.success);

            if (!result.success) {
              console.warn('[LlmClient] Patch failed:', result.error);
            }
          } else {
            this.pendingPatches.push(event.patch);
            this.config.onPatch?.(event.patch, false);
          }
        }
        break;

      case 'done':
        console.log('[LlmClient] Stream complete:', event.stats);
        this.config.onDone?.(event.stats);

        // Update change tracker after all patches applied
        await changeTracker.forceCheck();
        break;

      case 'error':
        console.error('[LlmClient] Server error:', event.error);
        this.config.onError?.(event.error ?? 'Unknown error');
        break;
    }
  }
}

interface SSEEvent {
  type: string;
  data: string;
}

// Export factory function
export function createLlmClient(config?: Partial<LlmClientConfig>): LlmClient {
  return new LlmClient(config);
}
