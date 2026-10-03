#!/usr/bin/env python3
"""Scripted OpenAI-compatible chat-completions server for end-to-end checks (streaming and not)."""
import json, sys, time, http.server, socketserver
PORT = int(sys.argv[1])
calls = 0
def decide(messages):
    last = messages[-1]
    if last.get('role') == 'tool':
        content = last.get('content') or ''
        if isinstance(content, list): content = ' '.join(c.get('text', '') for c in content)
        first = content.split('\n')[0]
        return {'text': f"Done. The tool said: **{first}**\n\nHere is `inline code` and a block:\n```bash\necho done\n```\n- one\n- two"}
    text = last.get('content') or ''
    if isinstance(text, list): text = ' '.join(c.get('text', '') for c in text)
    if 'shell' in text: return {'tool': ('shell', {'command': 'echo hello from the sandbox'})}
    if 'list' in text: return {'tool': ('list', {'path': '.'})}
    if 'edit' in text: return {'tool': ('write', {'path': 'notes.txt', 'content': 'line one\nline two\n'})}
    if 'slow' in text:
        time.sleep(8)
    return {'text': f"You said: {text.strip()[:200]}. This is a fairly long answer so that the transcript has to wrap it across more than one line of the terminal window."}
class H(http.server.BaseHTTPRequestHandler):
    protocol_version = 'HTTP/1.1'
    def log_message(self, *a): pass
    def do_GET(self):
        body = json.dumps({'data': [{'id': 'mock', 'object': 'model'}]}).encode()
        self.send_response(200); self.send_header('Content-Type', 'application/json'); self.send_header('Content-Length', str(len(body))); self.end_headers(); self.wfile.write(body)
    def do_POST(self):
        global calls
        n = int(self.headers.get('Content-Length', 0)); req = json.loads(self.rfile.read(n))
        calls += 1
        d = decide(req['messages'])
        stream = req.get('stream', False)
        usage = {'prompt_tokens': 1200 + 100 * len(req['messages']), 'completion_tokens': 42, 'total_tokens': 1242}
        if not stream:
            msg = {'role': 'assistant', 'content': d.get('text')}
            if 'tool' in d:
                msg['tool_calls'] = [{'id': f'call_{calls}', 'type': 'function', 'function': {'name': d['tool'][0], 'arguments': json.dumps(d['tool'][1])}}]
            body = json.dumps({'id': f'c{calls}', 'object': 'chat.completion', 'created': 0, 'model': 'mock', 'choices': [{'index': 0, 'message': msg, 'finish_reason': 'tool_calls' if 'tool' in d else 'stop'}], 'usage': usage}).encode()
            self.send_response(200); self.send_header('Content-Type', 'application/json'); self.send_header('Content-Length', str(len(body))); self.end_headers(); self.wfile.write(body); return
        self.send_response(200); self.send_header('Content-Type', 'text/event-stream'); self.send_header('Transfer-Encoding', 'chunked'); self.end_headers()
        def send(obj):
            data = ('data: ' + (obj if isinstance(obj, str) else json.dumps(obj)) + '\n\n').encode()
            self.wfile.write(f'{len(data):x}\r\n'.encode() + data + b'\r\n'); self.wfile.flush()
        base = {'id': f'c{calls}', 'object': 'chat.completion.chunk', 'created': 0, 'model': 'mock'}
        if 'tool' in d:
            name, args = d['tool']
            send({**base, 'choices': [{'index': 0, 'delta': {'role': 'assistant', 'tool_calls': [{'index': 0, 'id': f'call_{calls}', 'type': 'function', 'function': {'name': name, 'arguments': json.dumps(args)}}]}, 'finish_reason': None}]})
            send({**base, 'choices': [{'index': 0, 'delta': {}, 'finish_reason': 'tool_calls'}]})
        else:
            words = d['text'].split(' ')
            for i, w in enumerate(words):
                send({**base, 'choices': [{'index': 0, 'delta': {'content': (w if i == 0 else ' ' + w)}, 'finish_reason': None}]})
                time.sleep(0.03)
            send({**base, 'choices': [{'index': 0, 'delta': {}, 'finish_reason': 'stop'}]})
        send({**base, 'choices': [], 'usage': usage})
        send('[DONE]')
        self.wfile.write(b'0\r\n\r\n'); self.wfile.flush()
class S(socketserver.ThreadingMixIn, http.server.HTTPServer): daemon_threads = True
S(('127.0.0.1', PORT), H).serve_forever()
