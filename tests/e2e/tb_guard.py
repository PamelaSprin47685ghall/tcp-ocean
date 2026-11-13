#!/usr/bin/env python3
"""Own only resources recorded by this run. Cleanup verifies identity, not just PID files.

Adapted from the v2.2 testbed guard (tb_guard.py). Manifest lives in /run/tbtun-e2e,
root-owned. A run records the (st_dev, st_ino) of each namespace it created and the
start-time of processes it started; down refuses to touch anything whose identity
no longer matches. Existing namespaces are never adopted or deleted by `begin`.
"""
import fcntl
import json
import os
from pathlib import Path
import signal
import select
import stat
import subprocess
import sys
import tempfile
import uuid

BASE = Path('/run/tbtun-e2e')
BOOT = Path('/proc/sys/kernel/random/boot_id')
NETNS = Path('/var/run/netns')


def identity(path):
    value = path.stat()
    return [value.st_dev, value.st_ino]


def process_start(pid, proc=Path('/proc')):
    # comm may contain spaces and parentheses. Fields after its final ')' start at state.
    fields = (proc / str(pid) / 'stat').read_text().rsplit(')', 1)[1].split()
    return fields[19], int(fields[1]), fields[0]


class Scope:
    def __init__(self, base=BASE, netns=NETNS, boot=None):
        self.base = base
        self.netns = netns
        self.boot = boot if boot is not None else BOOT.read_text().strip()
        self.file = base / 'run.json'

    def load(self):
        if self.file.is_symlink():
            raise RuntimeError('Refuse a symlink manifest')
        state = json.loads(self.file.read_text())
        if state.get('version') != 1 or state.get('boot') != self.boot:
            raise RuntimeError('Manifest version or boot differs; no resource touched')
        return state

    def save(self, state):
        self.base.mkdir(parents=True, exist_ok=True)
        with tempfile.NamedTemporaryFile(mode='w', dir=self.base, delete=False) as out:
            json.dump(state, out)
            out.flush()
            os.fsync(out.fileno())
            name = out.name
        os.replace(name, self.file)

    def begin(self, names):
        if self.file.exists() or self.file.is_symlink():
            raise RuntimeError('An owned run exists; use netns.sh down explicitly first')
        if len(names) != 3 or len(set(names)) != 3:
            raise RuntimeError('Expected three distinct namespace names')
        for name in names:
            if not name or not name[0].isalpha() or any(
                    c not in 'abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_-'
                    for c in name):
                raise RuntimeError('Invalid namespace name')
            if (self.netns / name).exists() or (self.netns / name).is_symlink():
                raise RuntimeError('Namespace already exists; refusing to reuse or delete: ' + name)
        self.save(dict(version=1, boot=self.boot, token=uuid.uuid4().hex,
                       names=list(names), namespaces={}, processes={}, work=None))

    def record_namespace(self, name):
        state = self.load()
        if name not in state['names'] or name in state['namespaces']:
            raise RuntimeError('Namespace is not a new member of this run')
        state['namespaces'][name] = identity(self.netns / name)
        self.save(state)

    def record_work(self, work):
        state = self.load()
        state['work'] = work
        self.save(state)

    def check(self, complete=True):
        state = self.load()
        if complete and set(state['namespaces']) != set(state['names']):
            raise RuntimeError('Run is incomplete')
        for name, expected in state['namespaces'].items():
            path = self.netns / name
            if path.is_symlink() or not path.exists() or identity(path) != expected:
                raise RuntimeError('Namespace ownership changed; refusing: ' + name)
        return state

    def record_process(self, role, pid, parent):
        state = self.load()
        if role not in ('wanem',) or role in state['processes']:
            raise RuntimeError('Invalid or already recorded process role')
        started, actual_parent, status = process_start(pid)
        if actual_parent != parent or status == 'Z':
            raise RuntimeError('Process is not the just-created child')
        state['processes'][role] = dict(pid=pid, start=started)
        self.save(state)

    def signal_process(self, role, sig, wait=False):
        state = self.load()
        record = state['processes'].get(role)
        if record is None:
            return
        pid = record['pid']
        fd = None
        try:
            # pidfd prevents check-then-kill from signalling a recycled PID.
            fd = os.pidfd_open(pid, 0)
            started, _, status = process_start(pid)
            if started != record['start']:
                raise RuntimeError('PID was reused; no signal sent')
            if status != 'Z':
                signal.pidfd_send_signal(fd, sig)
                if wait:
                    poller = select.poll()
                    poller.register(fd, select.POLLIN)
                    if not poller.poll(2000):
                        signal.pidfd_send_signal(fd, signal.SIGKILL)
                        if not poller.poll(2000):
                            raise RuntimeError('Owned process did not exit; manifest retained')
        except ProcessLookupError:
            pass
        except FileNotFoundError:
            pass
        finally:
            if fd is not None:
                os.close(fd)
        if wait:
            state['processes'].pop(role, None)
            self.save(state)


def protected_base():
    if os.geteuid() != 0:
        raise RuntimeError('Run the testbed explicitly as root')
    BASE.mkdir(mode=0o700, exist_ok=True)
    info = BASE.lstat()
    if not stat.S_ISDIR(info.st_mode) or info.st_uid != 0 or stat.S_IMODE(info.st_mode) != 0o700:
        raise RuntimeError('Runtime directory must be a root-owned real directory, mode 0700')
    if not hasattr(os, 'pidfd_open') or not hasattr(signal, 'pidfd_send_signal'):
        raise RuntimeError('Python/Linux pidfd support is required for safe process signalling')
    probe = os.pidfd_open(os.getpid(), 0)
    os.close(probe)


def main(args):
    command, *args = args
    if command == 'status':
        # May be called unprivileged. A root-only runtime dir must not crash the caller.
        try:
            if BASE.joinpath('run.json').exists():
                print('owned-run')
                return 0
        except OSError:
            print('no-run')
            return 0
        print('no-run')
        return 0
    protected_base()
    scope = Scope()
    if command == 'begin':
        scope.begin(args)
    elif command == 'record-ns':
        scope.record_namespace(args[0])
    elif command == 'record-work':
        scope.record_work(args[0])
    elif command == 'record-process':
        scope.record_process(args[0], int(args[1]), int(args[2]))
    elif command == 'check':
        state = scope.check(complete=args[0] != 'partial' if args else True)
        for name in state['namespaces']:
            print(name)
    elif command == 'down':
        if not scope.file.exists():
            print('No owned run; no namespace or process touched')
            return 0
        state = scope.check(complete=False)
        scope.signal_process('wanem', signal.SIGTERM, wait=True)
        # After identity check, kill leftover processes in owned namespaces so
        # `ip netns del` can succeed. Only the names in the manifest are touched.
        state = scope.check(complete=False)
        for name, expected in list(state['namespaces'].items()):
            if identity(scope.netns / name) != expected:
                raise RuntimeError('Namespace changed during cleanup: ' + name)
            try:
                listed = subprocess.check_output(['ip', 'netns', 'pids', name], text=True)
            except subprocess.CalledProcessError:
                listed = ''
            for pid in listed.split():
                try:
                    fd = os.pidfd_open(int(pid), 0)
                    try:
                        signal.pidfd_send_signal(fd, signal.SIGTERM)
                    finally:
                        os.close(fd)
                except (ProcessLookupError, FileNotFoundError, PermissionError, ValueError):
                    pass
        for name, expected in list(scope.check(complete=False)['namespaces'].items()):
            if identity(scope.netns / name) != expected:
                raise RuntimeError('Namespace changed during cleanup: ' + name)
            subprocess.run(['ip', 'netns', 'del', name], check=True)
            latest = scope.load()
            latest['namespaces'].pop(name)
            scope.save(latest)
        scope.file.unlink()
        print('Removed only the namespaces and processes recorded for this run')
    else:
        raise RuntimeError('Unknown guard operation')
    return 0


if __name__ == '__main__':
    try:
        sys.exit(main(sys.argv[1:]))
    except (RuntimeError, OSError, ValueError, KeyError, json.JSONDecodeError) as error:
        print('testbed guard: ' + str(error), file=sys.stderr)
        sys.exit(2)
