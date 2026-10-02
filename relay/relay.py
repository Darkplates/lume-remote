"""Lume's opaque TCP rendezvous relay. Python 3.10+, standard library only.

The relay pairs sockets. TLS is negotiated end to end by the Windows apps.
Never log room identifiers, invitations, operator tokens, screen contents, or session secrets.

Header: ``LUME1 <H|V> <32 hex room>[ <token>]\n``. The optional token is an operator
secret. When the relay is started with one (``LUME_RELAY_TOKEN`` or ``--token-file``),
host registrations must carry it; viewers join an existing room by its 128-bit
identifier and need no token. Without a configured token the relay behaves as before.
"""
import argparse
import asyncio
import contextlib
import hmac
import ipaddress
import os
import re
import signal
import socket

HEADER = re.compile(rb"LUME1 ([HV]) ([a-fA-F0-9]{32})(?: ([A-Za-z0-9._~-]{16,64}))?\n")
TOKEN = re.compile(r"[A-Za-z0-9._~-]{16,64}")
TOKEN_ENV = "LUME_RELAY_TOKEN"


def source_key(address):
    """Groups a source for per-source limits: IPv4 (and IPv4-mapped IPv6) by address,
    other IPv6 by /64, the smallest block one subscriber usually controls."""
    if not isinstance(address, str):
        return address
    try:
        ip = ipaddress.ip_address(address.split("%", 1)[0])
    except ValueError:
        return address
    if ip.version == 6:
        if ip.ipv4_mapped is not None:
            return str(ip.ipv4_mapped)
        return str(ipaddress.ip_network(f"{ip}/64", strict=False))
    return str(ip)


def valid_token(token):
    return token is None or (isinstance(token, str) and TOKEN.fullmatch(token) is not None)


class Relay:
    def __init__(self, max_connections=512, max_per_address=64, idle_timeout=600, host_wait_timeout=600,
                 max_waiting_per_address=4, token=None):
        if not valid_token(token):
            raise ValueError("The operator token must be 16-64 characters: letters, digits, '.', '_', '~' or '-'")
        self.waiting = {}
        # Seconds without bytes in either direction before a paired stream is closed.
        # Healthy sessions exchange heartbeats every few seconds. 0 disables it.
        self.idle_timeout = idle_timeout
        # Seconds an unpaired host may hold its room. The Windows host re-registers. 0 = unlimited.
        self.host_wait_timeout = host_wait_timeout
        # Unpaired host rooms one source may hold at once, so it cannot fill the room table.
        self.max_waiting_per_address = max_waiting_per_address
        # Optional operator secret required for host registration. Never logged.
        self.token = token.encode() if token else None
        self.connections = 0
        self.max_connections = max_connections
        # One source address must not be able to occupy every slot.
        self.max_per_address = max_per_address
        self.per_address = {}
        self.tasks = set()
        self.writers = set()

    async def handle(self, reader, writer):
        peer = writer.get_extra_info("peername")
        address = source_key(peer[0] if isinstance(peer, tuple) and peer else None)
        if self.connections >= self.max_connections or self.per_address.get(address, 0) >= self.max_per_address:
            writer.write(b"\x03")
            with contextlib.suppress(Exception):
                await writer.drain()
            writer.close()
            return
        self.connections += 1
        self.per_address[address] = self.per_address.get(address, 0) + 1
        self.writers.add(writer)
        task = asyncio.current_task()
        self.tasks.add(task)
        room = None
        entry = None
        raw_socket = writer.get_extra_info("socket")
        if raw_socket:
            raw_socket.setsockopt(socket.SOL_SOCKET, socket.SO_KEEPALIVE, 1)
            raw_socket.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        try:
            header = await asyncio.wait_for(reader.readuntil(b"\n"), 10)
            match = HEADER.fullmatch(header)
            if not match:
                writer.write(b"\x03")
                await writer.drain()
                return
            role, room, token = match.groups()
            room = room.lower()
            if role == b"H":
                if (
                    room in self.waiting
                    or not self.authorized(token)
                    or self.waiting_from(address) >= self.max_waiting_per_address
                ):
                    writer.write(b"\x03")
                    await writer.drain()
                    return
                pair = asyncio.get_running_loop().create_future()
                entry = (reader, writer, pair, address)
                self.waiting[room] = entry
                writer.write(b"\x01")
                await writer.drain()
                # Waiting hosts send no TLS bytes until a viewer has paired.
                disconnected = asyncio.create_task(reader.read(1))
                try:
                    done, _ = await asyncio.wait(
                        [pair, disconnected], timeout=self.host_wait_timeout or None, return_when=asyncio.FIRST_COMPLETED
                    )
                    # No await separates this check from the wait, so a viewer cannot pair in between.
                    if disconnected in done or not pair.done():
                        return
                    disconnected.cancel()
                    with contextlib.suppress(asyncio.CancelledError):
                        await disconnected
                    peer_reader, peer_writer, released = pair.result()
                    try:
                        writer.write(b"\x01")
                        peer_writer.write(b"\x01")
                        await asyncio.gather(writer.drain(), peer_writer.drain())
                        loop = asyncio.get_running_loop()
                        activity = [loop.time()]
                        forward = asyncio.create_task(self.pipe(reader, peer_writer, activity))
                        backward = asyncio.create_task(self.pipe(peer_reader, writer, activity))
                        try:
                            await self.until_closed_or_idle(forward, backward, activity)
                        finally:
                            forward.cancel()
                            backward.cancel()
                            await asyncio.gather(forward, backward, return_exceptions=True)
                    finally:
                        if not released.done():
                            released.set_result(None)
                finally:
                    disconnected.cancel()
                    with contextlib.suppress(asyncio.CancelledError):
                        await disconnected
                    if pair.done() and not pair.cancelled():
                        _, _, released = pair.result()
                        if not released.done():
                            released.set_result(None)
            else:
                waiting = self.waiting.pop(room, None)
                if waiting is None or waiting[2].done():
                    writer.write(b"\x02")
                    await writer.drain()
                    return
                released = asyncio.get_running_loop().create_future()
                waiting[2].set_result((reader, writer, released))
                await released
        except (asyncio.IncompleteReadError, asyncio.LimitOverrunError, asyncio.TimeoutError, TimeoutError, ConnectionError, OSError):
            pass
        finally:
            if room is not None and entry is not None and self.waiting.get(room) is entry:
                self.waiting.pop(room, None)
            self.connections -= 1
            remaining = self.per_address.get(address, 1) - 1
            if remaining > 0:
                self.per_address[address] = remaining
            else:
                self.per_address.pop(address, None)
            self.writers.discard(writer)
            self.tasks.discard(task)
            writer.close()
            with contextlib.suppress(Exception):
                await writer.wait_closed()

    def authorized(self, token):
        """Host registrations need the operator token when one is configured."""
        if self.token is None:
            return True
        return token is not None and hmac.compare_digest(token, self.token)

    def waiting_from(self, address):
        return sum(1 for entry in self.waiting.values() if entry[3] == address)

    async def until_closed_or_idle(self, forward, backward, activity):
        """Returns when either direction ends or no bytes moved either way for idle_timeout."""
        loop = asyncio.get_running_loop()
        while True:
            timeout = None
            if self.idle_timeout:
                timeout = activity[0] + self.idle_timeout - loop.time()
                if timeout <= 0:
                    return
            done, _ = await asyncio.wait([forward, backward], timeout=timeout, return_when=asyncio.FIRST_COMPLETED)
            if done:
                return

    @staticmethod
    async def pipe(reader, writer, activity=None):
        loop = asyncio.get_running_loop()
        while data := await reader.read(65536):
            if activity is not None:
                activity[0] = loop.time()
            writer.write(data)
            await writer.drain()
            if activity is not None:
                activity[0] = loop.time()

    async def close(self):
        for writer in list(self.writers):
            writer.close()
        for task in list(self.tasks):
            task.cancel()
        await asyncio.gather(*list(self.tasks), return_exceptions=True)


async def serve(args):
    relay = Relay(args.max_connections, args.max_per_address, args.idle_timeout, args.host_wait_timeout,
                  args.max_waiting_per_address, args.token)
    server = await asyncio.start_server(relay.handle, args.bind, args.port, limit=128)
    print(f"Lume relay listening on {args.bind}:{args.port}. End-to-end TLS stays inside the paired stream."
          + (" Host registration requires the operator token." if args.token else ""), flush=True)
    try:
        async with server:
            await server.serve_forever()
    finally:
        await relay.close()


def main():
    parser = argparse.ArgumentParser(description="Self-hosted Lume TCP relay. No third-party dependencies.")
    parser.add_argument("--bind", default="127.0.0.1", help="Default is local-only. Use 0.0.0.0 explicitly for a reachable server.")
    parser.add_argument("--port", type=int, default=24817)
    parser.add_argument("--max-connections", type=int, default=512, help="Operational resource guard, not a licence limit.")
    parser.add_argument("--max-per-address", type=int, default=64, help="Connections allowed from one source address at a time.")
    parser.add_argument("--idle-timeout", type=float, default=600, help="Close a paired stream after this many seconds without bytes in either direction. 0 disables it.")
    parser.add_argument("--host-wait-timeout", type=float, default=600, help="Seconds an unpaired host may wait for a viewer before it must re-register. 0 means unlimited.")
    parser.add_argument("--max-waiting-per-address", type=int, default=4, help="Unpaired host rooms one source address (IPv6: one /64) may hold at a time.")
    parser.add_argument("--token-file", help=f"File holding an optional operator token required for host registration. The {TOKEN_ENV} environment variable is used when this is absent.")
    args = parser.parse_args()
    if not 1 <= args.port <= 65535 or args.max_connections < 2 or args.max_per_address < 2 or args.max_waiting_per_address < 1:
        parser.error("Invalid port or connection capacity")
    if not (0 <= args.idle_timeout < float("inf") and 0 <= args.host_wait_timeout < float("inf")):
        parser.error("Timeouts must be finite and zero or positive")
    if args.token_file:
        try:
            with open(args.token_file, encoding="ascii") as handle:
                args.token = handle.read().strip()
        except (OSError, UnicodeDecodeError):
            parser.error("The token file could not be read")
    else:
        args.token = os.environ.get(TOKEN_ENV, "").strip()
    args.token = args.token or None
    if not valid_token(args.token):
        parser.error("The operator token must be 16-64 characters: letters, digits, '.', '_', '~' or '-'")
    try:
        asyncio.run(serve(args))
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
