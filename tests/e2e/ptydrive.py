#!/usr/bin/env python3
"""Run a command in a pty, feed keystrokes on a schedule, and print the emulated screen.
usage: ptydrive.py COLSxROWS 'step;step;...' -- cmd args
steps: 'w:SECONDS' wait, 's:TEXT' send text (python escapes), 'p' print screen, 'pc' print screen with colours of row cells"""
import os, pty, sys, time, select, struct, fcntl, termios, signal, pyte, codecs
size, steps = sys.argv[1], sys.argv[2]
cmd = sys.argv[sys.argv.index('--')+1:]
cols, rows = map(int, size.split('x'))
screen = pyte.Screen(cols, rows); stream = pyte.ByteStream(screen)
pid, fd = pty.fork()
if pid == 0:
    os.environ['TERM'] = os.environ.get('TERM_OVERRIDE', 'xterm-256color')
    os.execvp(cmd[0], cmd)
fcntl.ioctl(fd, termios.TIOCSWINSZ, struct.pack('HHHH', rows, cols, 0, 0))
raw = open(os.environ.get('PTY_LOG', '/dev/null'), 'ab')
def pump(t):
    end = time.time() + t
    while True:
        left = end - time.time()
        if left <= 0: return
        r, _, _ = select.select([fd], [], [], left)
        if fd in r:
            try: data = os.read(fd, 65536)
            except OSError: return
            if not data: return
            raw.write(data); stream.feed(data)
def show():
    print('+' + '-'*cols + '+')
    for line in screen.display: print('|' + line + '|')
    print('+' + '-'*cols + '+', flush=True)
for step in steps.split(';;'):
    if step.startswith('w:'): pump(float(step[2:]))
    elif step.startswith('s:'): os.write(fd, codecs.decode(step[2:], 'unicode_escape').encode('utf-8')); pump(0.3)
    elif step == 'p': show()
    elif step.startswith('fg:'):
        # print the fg colour and bold of the cells of a row
        r = int(step[3:]); print(' '.join(f"{c.data}:{c.fg}{'*' if c.bold else ''}" for c in screen.buffer[r].values() if c.data.strip())[:600])
pump(1.0)
for sig in (signal.SIGTERM, signal.SIGKILL):
    try: os.kill(pid, sig)
    except ProcessLookupError: pass
    for _ in range(30):
        done, status = os.waitpid(pid, os.WNOHANG)
        if done: break
        time.sleep(0.1)
    if done: break
    print('still running after', sig, flush=True)
print('exit', status)
