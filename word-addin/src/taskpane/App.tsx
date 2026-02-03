import * as React from 'react';
import { useState, useEffect, useCallback, useRef } from 'react';
import {
  makeStyles,
  tokens,
  Button,
  Textarea,
  Card,
  CardHeader,
  Text,
  Badge,
  Spinner,
  Divider,
  Switch,
  Tooltip,
} from '@fluentui/react-components';
import {
  SendRegular,
  StopRegular,
  SettingsRegular,
  DocumentRegular,
  CheckmarkCircleRegular,
  ErrorCircleRegular,
  ArrowSyncRegular,
} from '@fluentui/react-icons';

import { wordService } from '../services/WordService';
import { changeTracker } from '../services/ChangeTracker';
import { createLlmClient, LlmClient } from '../services/LlmClient';
import type { LlmPatch, LogicalChange } from '../types';

const useStyles = makeStyles({
  container: {
    display: 'flex',
    flexDirection: 'column',
    height: '100%',
    padding: tokens.spacingHorizontalM,
    gap: tokens.spacingVerticalM,
  },
  header: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
  },
  title: {
    fontWeight: tokens.fontWeightSemibold,
    fontSize: tokens.fontSizeBase400,
  },
  content: {
    flex: 1,
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
    overflowY: 'auto',
  },
  inputArea: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
  },
  buttonRow: {
    display: 'flex',
    gap: tokens.spacingHorizontalS,
  },
  message: {
    padding: tokens.spacingVerticalS,
    borderRadius: tokens.borderRadiusMedium,
  },
  userMessage: {
    backgroundColor: tokens.colorBrandBackground2,
    alignSelf: 'flex-end',
    maxWidth: '85%',
  },
  assistantMessage: {
    backgroundColor: tokens.colorNeutralBackground3,
    alignSelf: 'flex-start',
    maxWidth: '85%',
  },
  patchCard: {
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke1}`,
  },
  patchSuccess: {
    borderLeftColor: tokens.colorPaletteGreenBorder1,
    borderLeftWidth: '3px',
  },
  patchFailed: {
    borderLeftColor: tokens.colorPaletteRedBorder1,
    borderLeftWidth: '3px',
  },
  changeItem: {
    padding: tokens.spacingVerticalXS,
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground2,
  },
  statusBar: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    padding: tokens.spacingVerticalXS,
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
  },
  settings: {
    padding: tokens.spacingVerticalM,
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
  },
});

interface Message {
  id: string;
  role: 'user' | 'assistant';
  content: string;
  patches?: { patch: LlmPatch; success: boolean }[];
  timestamp: Date;
}

export const App: React.FC = () => {
  const styles = useStyles();
  const [messages, setMessages] = useState<Message[]>([]);
  const [input, setInput] = useState('');
  const [isLoading, setIsLoading] = useState(false);
  const [showSettings, setShowSettings] = useState(false);
  const [autoApply, setAutoApply] = useState(true);
  const [trackChanges, setTrackChanges] = useState(true);
  const [recentChanges, setRecentChanges] = useState<LogicalChange[]>([]);
  const [connectionStatus, setConnectionStatus] = useState<'connected' | 'disconnected'>('disconnected');

  const llmClientRef = useRef<LlmClient | null>(null);
  const contentScrollRef = useRef<HTMLDivElement>(null);

  // Initialize services
  useEffect(() => {
    const init = async () => {
      try {
        await wordService.initialize();
        setConnectionStatus('connected');

        // Set up change tracking
        if (trackChanges) {
          changeTracker.onChanges((changes) => {
            setRecentChanges((prev) => [...changes, ...prev].slice(0, 10));
          });
          changeTracker.start();
        }
      } catch (error) {
        console.error('Failed to initialize:', error);
        setConnectionStatus('disconnected');
      }
    };

    init();

    return () => {
      changeTracker.stop();
    };
  }, [trackChanges]);

  // Scroll to bottom when messages change
  useEffect(() => {
    if (contentScrollRef.current) {
      contentScrollRef.current.scrollTop = contentScrollRef.current.scrollHeight;
    }
  }, [messages]);

  const handleSend = useCallback(async () => {
    if (!input.trim() || isLoading) return;

    const userMessage: Message = {
      id: Date.now().toString(),
      role: 'user',
      content: input.trim(),
      timestamp: new Date(),
    };

    setMessages((prev) => [...prev, userMessage]);
    setInput('');
    setIsLoading(true);

    // Create assistant message placeholder
    const assistantId = (Date.now() + 1).toString();
    const assistantMessage: Message = {
      id: assistantId,
      role: 'assistant',
      content: '',
      patches: [],
      timestamp: new Date(),
    };
    setMessages((prev) => [...prev, assistantMessage]);

    // Create LLM client with handlers
    llmClientRef.current = createLlmClient({
      autoApplyPatches: autoApply,
      onContent: (content) => {
        setMessages((prev) =>
          prev.map((m) =>
            m.id === assistantId ? { ...m, content: m.content + content } : m
          )
        );
      },
      onPatch: (patch, success) => {
        setMessages((prev) =>
          prev.map((m) =>
            m.id === assistantId
              ? { ...m, patches: [...(m.patches || []), { patch, success }] }
              : m
          )
        );
      },
      onDone: () => {
        setIsLoading(false);
        llmClientRef.current = null;
      },
      onError: (error) => {
        setMessages((prev) =>
          prev.map((m) =>
            m.id === assistantId
              ? { ...m, content: m.content + `\n\n**Error:** ${error}` }
              : m
          )
        );
        setIsLoading(false);
        llmClientRef.current = null;
      },
    });

    try {
      await llmClientRef.current.streamEdit(userMessage.content);
    } catch (error) {
      console.error('Stream failed:', error);
      setIsLoading(false);
    }
  }, [input, isLoading, autoApply]);

  const handleCancel = useCallback(() => {
    if (llmClientRef.current) {
      llmClientRef.current.cancel();
      llmClientRef.current = null;
      setIsLoading(false);
    }
  }, []);

  const handleKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault();
      handleSend();
    }
  };

  if (showSettings) {
    return (
      <div className={styles.container}>
        <div className={styles.header}>
          <Text className={styles.title}>📎 Doccy Settings</Text>
          <Button
            appearance="subtle"
            icon={<DocumentRegular />}
            onClick={() => setShowSettings(false)}
          />
        </div>

        <div className={styles.settings}>
          <Switch
            checked={autoApply}
            onChange={(_, data) => setAutoApply(data.checked)}
            label="Auto-apply patches"
          />
          <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>
            When enabled, LLM patches are applied to the document immediately.
            When disabled, you can review patches before applying.
          </Text>

          <Divider />

          <Switch
            checked={trackChanges}
            onChange={(_, data) => {
              setTrackChanges(data.checked);
              if (data.checked) {
                changeTracker.start();
              } else {
                changeTracker.stop();
              }
            }}
            label="Track user changes"
          />
          <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>
            When enabled, your edits are tracked and sent to the LLM as context
            (logical patches).
          </Text>

          <Divider />

          <Text size={200}>
            Session ID: <code>{changeTracker.sessionId}</code>
          </Text>
        </div>
      </div>
    );
  }

  return (
    <div className={styles.container}>
      {/* Header */}
      <div className={styles.header}>
        <Text className={styles.title}>📎 Doccy</Text>
        <div style={{ display: 'flex', gap: '4px', alignItems: 'center' }}>
          <Badge
            appearance="filled"
            color={connectionStatus === 'connected' ? 'success' : 'danger'}
            size="small"
          />
          <Button
            appearance="subtle"
            icon={<SettingsRegular />}
            onClick={() => setShowSettings(true)}
          />
        </div>
      </div>

      {/* Recent user changes */}
      {recentChanges.length > 0 && (
        <Card size="small">
          <CardHeader
            header={
              <Text size={200} weight="semibold">
                Your Recent Changes
              </Text>
            }
            action={
              <Tooltip content="Clear history" relationship="label">
                <Button
                  appearance="subtle"
                  size="small"
                  icon={<ArrowSyncRegular />}
                  onClick={() => setRecentChanges([])}
                />
              </Tooltip>
            }
          />
          <div style={{ maxHeight: '80px', overflowY: 'auto' }}>
            {recentChanges.slice(0, 3).map((change, i) => (
              <div key={i} className={styles.changeItem}>
                {change.description}
              </div>
            ))}
          </div>
        </Card>
      )}

      {/* Messages */}
      <div className={styles.content} ref={contentScrollRef}>
        {messages.length === 0 && (
          <div style={{ textAlign: 'center', padding: '20px', color: tokens.colorNeutralForeground3 }}>
            <div style={{ fontSize: '48px', marginBottom: '8px' }}>📎</div>
            <Text block weight="semibold" style={{ marginBottom: '8px' }}>
              Hi! I'm Doccy!
            </Text>
            <Text block>
              It looks like you're writing a document. Would you like help with that?
            </Text>
          </div>
        )}

        {messages.map((message) => (
          <div
            key={message.id}
            className={`${styles.message} ${
              message.role === 'user' ? styles.userMessage : styles.assistantMessage
            }`}
          >
            <Text>{message.content || (isLoading && message.role === 'assistant' ? '...' : '')}</Text>

            {/* Patches */}
            {message.patches && message.patches.length > 0 && (
              <div style={{ marginTop: '8px' }}>
                <Divider />
                <Text size={200} weight="semibold" style={{ marginTop: '4px' }}>
                  Patches ({message.patches.length})
                </Text>
                {message.patches.map((p, i) => (
                  <Card
                    key={i}
                    size="small"
                    className={`${styles.patchCard} ${
                      p.success ? styles.patchSuccess : styles.patchFailed
                    }`}
                    style={{ marginTop: '4px' }}
                  >
                    <div style={{ display: 'flex', alignItems: 'center', gap: '4px' }}>
                      {p.success ? (
                        <CheckmarkCircleRegular style={{ color: tokens.colorPaletteGreenForeground1 }} />
                      ) : (
                        <ErrorCircleRegular style={{ color: tokens.colorPaletteRedForeground1 }} />
                      )}
                      <Text size={200}>
                        <code>{p.patch.op}</code> {p.patch.path}
                      </Text>
                    </div>
                  </Card>
                ))}
              </div>
            )}
          </div>
        ))}

        {isLoading && (
          <div style={{ display: 'flex', alignItems: 'center', gap: '8px', padding: '8px' }}>
            <Spinner size="tiny" />
            <Text size={200}>Doccy is thinking...</Text>
          </div>
        )}
      </div>

      {/* Input */}
      <div className={styles.inputArea}>
        <Textarea
          placeholder="Ask Doccy anything... (e.g., 'Make this paragraph more concise')"
          value={input}
          onChange={(_, data) => setInput(data.value)}
          onKeyDown={handleKeyDown}
          disabled={isLoading}
          resize="vertical"
          style={{ minHeight: '60px' }}
        />
        <div className={styles.buttonRow}>
          {isLoading ? (
            <Button
              appearance="secondary"
              icon={<StopRegular />}
              onClick={handleCancel}
            >
              Cancel
            </Button>
          ) : (
            <Button
              appearance="primary"
              icon={<SendRegular />}
              onClick={handleSend}
              disabled={!input.trim()}
            >
              Send
            </Button>
          )}
        </div>
      </div>

      {/* Status bar */}
      <div className={styles.statusBar}>
        <Text size={100}>
          {trackChanges ? 'Tracking changes' : 'Change tracking off'}
        </Text>
        <Text size={100}>
          {autoApply ? 'Auto-apply on' : 'Manual apply'}
        </Text>
      </div>
    </div>
  );
};
