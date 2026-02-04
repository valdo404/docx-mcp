/**
 * WordService - Wrapper around Office.js Word API
 *
 * Provides a clean interface for:
 * - Reading document content with element IDs
 * - Applying patches from the LLM
 * - Tracking changes via events
 */

import type { DocumentContent, DocumentElement, SelectionInfo, LlmPatch } from '../types';

// Simple ID generator (8-char hex)
let idCounter = 0;
function generateId(): string {
  idCounter++;
  return (Date.now() + idCounter).toString(16).toUpperCase().slice(-8);
}

// Map to store element IDs (persisted per session)
const elementIdMap = new Map<string, string>();

export class WordService {
  private isInitialized = false;

  /**
   * Initialize the Word API context.
   */
  async initialize(): Promise<void> {
    if (this.isInitialized) return;

    await Office.onReady();
    this.isInitialized = true;
    console.log('[WordService] Initialized');
  }

  /**
   * Get the full document content with element IDs.
   */
  async getDocumentContent(): Promise<DocumentContent> {
    await this.initialize();

    return Word.run(async (context) => {
      const body = context.document.body;
      body.load('text');

      const paragraphs = body.paragraphs;
      paragraphs.load('items');

      await context.sync();

      // Load each paragraph's details
      const elements: DocumentElement[] = [];

      for (let i = 0; i < paragraphs.items.length; i++) {
        const para = paragraphs.items[i];
        para.load(['text', 'style', 'isListItem']);
      }

      await context.sync();

      for (let i = 0; i < paragraphs.items.length; i++) {
        const para = paragraphs.items[i];
        const text = para.text.trim();

        // Skip empty paragraphs
        if (!text) continue;

        // Generate or retrieve stable ID
        const contentKey = `${i}:${text.slice(0, 50)}`;
        let id = elementIdMap.get(contentKey);
        if (!id) {
          id = generateId();
          elementIdMap.set(contentKey, id);
        }

        // Determine type based on style
        const styleName = para.style?.toLowerCase() || '';
        let type: DocumentElement['type'] = 'paragraph';

        if (styleName.includes('heading') || styleName.startsWith('titre')) {
          type = 'heading';
        } else if (para.isListItem) {
          type = 'list';
        }

        elements.push({
          id,
          type,
          text,
          index: i,
          style: para.style,
        });
      }

      return {
        text: body.text,
        elements,
        selection: await this.getSelectionInternal(context),
      };
    });
  }

  /**
   * Get the current selection.
   */
  async getSelection(): Promise<SelectionInfo | undefined> {
    await this.initialize();

    return Word.run(async (context) => {
      return this.getSelectionInternal(context);
    });
  }

  private async getSelectionInternal(context: Word.RequestContext): Promise<SelectionInfo | undefined> {
    const selection = context.document.getSelection();
    selection.load('text');

    try {
      await context.sync();

      if (!selection.text || !selection.text.trim()) {
        return undefined;
      }

      return {
        text: selection.text,
        start_index: 0, // Office.js doesn't give us character indices easily
        end_index: selection.text.length,
      };
    } catch {
      return undefined;
    }
  }

  /**
   * Apply a single LLM patch to the document.
   */
  async applyPatch(patch: LlmPatch): Promise<{ success: boolean; error?: string }> {
    await this.initialize();

    try {
      await Word.run(async (context) => {
        const body = context.document.body;

        switch (patch.op) {
          case 'add':
            await this.applyAdd(context, body, patch);
            break;

          case 'replace':
            await this.applyReplace(context, body, patch);
            break;

          case 'remove':
            await this.applyRemove(context, body, patch);
            break;

          case 'replace_text':
            await this.applyReplaceText(context, body, patch);
            break;

          case 'move':
            // Move is complex - implement as remove + add
            console.warn('[WordService] Move operation not fully implemented');
            break;

          default:
            throw new Error(`Unknown patch operation: ${patch.op}`);
        }

        await context.sync();
      });

      return { success: true };
    } catch (error) {
      const message = error instanceof Error ? error.message : String(error);
      console.error('[WordService] Patch failed:', message);
      return { success: false, error: message };
    }
  }

  /**
   * Apply multiple patches in sequence.
   */
  async applyPatches(patches: LlmPatch[]): Promise<{ applied: number; errors: string[] }> {
    const errors: string[] = [];
    let applied = 0;

    for (const patch of patches) {
      const result = await this.applyPatch(patch);
      if (result.success) {
        applied++;
      } else if (result.error) {
        errors.push(result.error);
      }
    }

    return { applied, errors };
  }

  /**
   * Insert text at the current cursor position.
   */
  async insertTextAtCursor(text: string): Promise<void> {
    await this.initialize();

    await Word.run(async (context) => {
      const selection = context.document.getSelection();
      selection.insertText(text, Word.InsertLocation.replace);
      await context.sync();
    });
  }

  /**
   * Insert text at the end of the document.
   */
  async appendText(text: string): Promise<void> {
    await this.initialize();

    await Word.run(async (context) => {
      const body = context.document.body;
      body.insertText(text, Word.InsertLocation.end);
      await context.sync();
    });
  }

  /**
   * Register a handler for paragraph changes.
   */
  async onParagraphChanged(handler: () => void): Promise<() => void> {
    await this.initialize();

    // Note: Word.js paragraph events are in preview
    // For now, we'll use polling as a fallback
    const interval = setInterval(handler, 2000);

    return () => clearInterval(interval);
  }

  // --- Private patch application methods ---

  private async applyAdd(
    context: Word.RequestContext,
    body: Word.Body,
    patch: LlmPatch
  ): Promise<void> {
    const value = patch.value as { type?: string; text?: string; level?: number } | undefined;
    if (!value?.text) {
      throw new Error('Add operation requires value.text');
    }

    // Parse path to get index
    const index = this.parseIndexFromPath(patch.path);

    if (index !== null && index === 0) {
      // Insert at beginning
      body.insertParagraph(value.text, Word.InsertLocation.start);
    } else {
      // Insert at end (simplification - proper index handling would need more work)
      const para = body.insertParagraph(value.text, Word.InsertLocation.end);

      // Apply heading style if specified
      if (value.type === 'heading' && value.level) {
        para.style = `Heading ${value.level}`;
      }
    }

    await context.sync();
  }

  private async applyReplace(
    context: Word.RequestContext,
    body: Word.Body,
    patch: LlmPatch
  ): Promise<void> {
    const value = patch.value as { text?: string } | undefined;
    if (!value?.text) {
      throw new Error('Replace operation requires value.text');
    }

    // Parse path to find the paragraph
    const index = this.parseIndexFromPath(patch.path);
    const id = this.parseIdFromPath(patch.path);

    const paragraphs = body.paragraphs;
    paragraphs.load('items');
    await context.sync();

    let targetPara: Word.Paragraph | null = null;

    if (index !== null && index < paragraphs.items.length) {
      targetPara = paragraphs.items[index];
    } else if (id) {
      // Find by ID (search through our map)
      for (let i = 0; i < paragraphs.items.length; i++) {
        const para = paragraphs.items[i];
        para.load('text');
        await context.sync();

        const contentKey = `${i}:${para.text.trim().slice(0, 50)}`;
        if (elementIdMap.get(contentKey) === id) {
          targetPara = para;
          break;
        }
      }
    }

    if (targetPara) {
      // Clear and insert new text
      targetPara.clear();
      targetPara.insertText(value.text, Word.InsertLocation.start);
    } else {
      throw new Error(`Could not find element at path: ${patch.path}`);
    }
  }

  private async applyRemove(
    context: Word.RequestContext,
    body: Word.Body,
    patch: LlmPatch
  ): Promise<void> {
    const index = this.parseIndexFromPath(patch.path);

    const paragraphs = body.paragraphs;
    paragraphs.load('items');
    await context.sync();

    if (index !== null && index < paragraphs.items.length) {
      const para = paragraphs.items[index];
      para.delete();
    } else {
      throw new Error(`Could not find element at path: ${patch.path}`);
    }
  }

  private async applyReplaceText(
    context: Word.RequestContext,
    body: Word.Body,
    patch: LlmPatch
  ): Promise<void> {
    const value = patch.value as { find?: string; replace?: string } | undefined;
    if (!value?.find || value.replace === undefined) {
      throw new Error('replace_text requires value.find and value.replace');
    }

    // Use Word's search and replace
    const searchResults = body.search(value.find, { matchCase: false, matchWholeWord: false });
    searchResults.load('items');
    await context.sync();

    if (searchResults.items.length > 0) {
      // Replace first occurrence
      searchResults.items[0].insertText(value.replace, Word.InsertLocation.replace);
    }
  }

  // --- Path parsing helpers ---

  private parseIndexFromPath(path: string): number | null {
    // Match patterns like /body/paragraph[0] or /body/children/0
    const indexMatch = path.match(/\[(\d+)\]/) || path.match(/\/(\d+)$/);
    if (indexMatch) {
      return parseInt(indexMatch[1], 10);
    }
    return null;
  }

  private parseIdFromPath(path: string): string | null {
    // Match patterns like /body/paragraph[id='ABC123']
    const idMatch = path.match(/\[id='([^']+)'\]/);
    if (idMatch) {
      return idMatch[1];
    }
    return null;
  }
}

// Export singleton instance
export const wordService = new WordService();
