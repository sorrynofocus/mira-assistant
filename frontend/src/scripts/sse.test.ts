import { describe, expect, it } from 'vitest';
import { SseParser, readSse, type SseEvent } from './sse';

function streamOf(chunks: Uint8Array[]): ReadableStream<Uint8Array> {
  return new ReadableStream({
    start(controller) {
      for (const chunk of chunks) controller.enqueue(chunk);
      controller.close();
    },
  });
}

async function collect(body: ReadableStream<Uint8Array>): Promise<SseEvent[]> {
  const events: SseEvent[] = [];
  for await (const event of readSse(body)) events.push(event);
  return events;
}

const wire =
  'event: response.started\ndata: {"type":"response.started"}\n\n' +
  'event: response.delta\ndata: {"data":{"text":"Héllo 😀 世界"}}\n\n' +
  'event: response.completed\ndata: {"type":"response.completed"}\n\n';

describe('SseParser', () => {
  it('parses complete events', () => {
    const events = new SseParser().push(wire);
    expect(events.map((e) => e.event)).toEqual(['response.started', 'response.delta', 'response.completed']);
    expect(JSON.parse(events[1].data).data.text).toBe('Héllo 😀 世界');
  });

  it('produces the same events however the text is split', () => {
    const expected = new SseParser().push(wire);
    for (let size = 1; size <= 7; size++) {
      const parser = new SseParser();
      const events: SseEvent[] = [];
      for (let i = 0; i < wire.length; i += size) events.push(...parser.push(wire.slice(i, i + size)));
      expect(events).toEqual(expected);
    }
  });

  it('handles CRLF and CR line endings, including a CRLF split across chunks', () => {
    const parser = new SseParser();
    const events = [
      ...parser.push('event: a\r'),
      ...parser.push('\ndata: one\r'),
      ...parser.push('\n\r'),
      ...parser.push('\nevent: b\rdata: two\r\r\n'),
    ];
    expect(events).toEqual([
      { event: 'a', data: 'one' },
      { event: 'b', data: 'two' },
    ]);
  });

  it('joins multi-line data, ignores comments, and defaults the event name', () => {
    const events = new SseParser().push(': keep-alive\ndata: first\ndata: second\n\n');
    expect(events).toEqual([{ event: 'message', data: 'first\nsecond' }]);
  });

  it('discards an event that is not terminated by a blank line', () => {
    const parser = new SseParser();
    expect(parser.push('event: x\ndata: partial\n')).toEqual([]);
    parser.end();
    expect(parser.push('\n')).toEqual([]);
  });
});

describe('readSse', () => {
  it('decodes UTF-8 characters split across byte chunks', async () => {
    const bytes = new TextEncoder().encode(wire);
    const chunks: Uint8Array[] = [];
    for (let i = 0; i < bytes.length; i++) chunks.push(bytes.slice(i, i + 1));

    const events = await collect(streamOf(chunks));
    expect(events).toEqual(new SseParser().push(wire));
    expect(JSON.parse(events[1].data).data.text).toBe('Héllo 😀 世界');
  });
});
