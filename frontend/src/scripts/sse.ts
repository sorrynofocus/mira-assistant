export interface SseEvent {
  event: string;
  data: string;
}

export interface ChatEnvelope<T = Record<string, unknown>> {
  v: number;
  requestId: string;
  conversationId: string;
  type: string;
  timestamp: string;
  data: T;
}

// Incremental parser for the SSE wire format. Chunks may split lines, events, or CRLF pairs anywhere.
export class SseParser {
  #buffer = '';
  #event = '';
  #data: string[] = [];

  push(chunk: string): SseEvent[] {
    this.#buffer += chunk;
    const events: SseEvent[] = [];
    for (;;) {
      const match = /\r\n|\r|\n/.exec(this.#buffer);
      if (!match) break;
      // A CR at the very end may be the first half of a CRLF that continues in the next chunk.
      if (match[0] === '\r' && match.index === this.#buffer.length - 1) break;

      const line = this.#buffer.slice(0, match.index);
      this.#buffer = this.#buffer.slice(match.index + match[0].length);
      const event = this.#processLine(line);
      if (event) events.push(event);
    }
    return events;
  }

  // Per the SSE spec, an event without its terminating blank line is discarded at end of stream.
  end(): void {
    this.#buffer = '';
    this.#event = '';
    this.#data = [];
  }

  #processLine(line: string): SseEvent | null {
    if (line === '') {
      if (this.#data.length === 0) {
        this.#event = '';
        return null;
      }
      const event = { event: this.#event || 'message', data: this.#data.join('\n') };
      this.#event = '';
      this.#data = [];
      return event;
    }

    if (line.startsWith(':')) return null;

    const colon = line.indexOf(':');
    const field = colon < 0 ? line : line.slice(0, colon);
    let value = colon < 0 ? '' : line.slice(colon + 1);
    if (value.startsWith(' ')) value = value.slice(1);

    if (field === 'event') this.#event = value;
    else if (field === 'data') this.#data.push(value);
    return null;
  }
}

// Decodes a fetch body as UTF-8 (multi-byte characters may span chunks) and yields complete events.
export async function* readSse(body: ReadableStream<Uint8Array>): AsyncGenerator<SseEvent> {
  const reader = body.getReader();
  const decoder = new TextDecoder();
  const parser = new SseParser();
  try {
    for (;;) {
      const { value, done } = await reader.read();
      if (done) break;
      // stream: true holds back an incomplete multi-byte sequence until the next chunk.
      yield* parser.push(decoder.decode(value, { stream: true }));
    }
    yield* parser.push(decoder.decode());
    parser.end();
  } finally {
    reader.releaseLock();
  }
}
